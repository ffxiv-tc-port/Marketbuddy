using System;
using System.Reflection;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Logging;
using Dalamud.Plugin;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;
using Dalamud.Bindings.ImGui;
using Marketbuddy.Common;
using Marketbuddy.Structs;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    public unsafe class Marketbuddy : IDalamudPlugin
    {
        private const string commandName = "/mbuddy";

        internal PluginUI PluginUi { get; private set; }

        internal MarketGuiEventHandler MarketGuiEventHandler { get; private set; }

        internal BatchReprice BatchReprice { get; private set; }

        internal BatchDelist BatchDelist { get; private set; }

        internal MultiRetainerTour MultiTour { get; private set; }

        /// <summary>與 AutoRetainer 多開模式協作的多角色重掛驅動器。</summary>
        internal MultiCharacterTour MultiCharTour { get; private set; }

        internal QuickLister? QuickLister { get; private set; }

        internal ManualRequery ManualRequery { get; private set; }

        /// <summary>跨世界掛售價格巡檢（唯讀、只在使用者按下按鈕時才動）。</summary>
        internal PriceSurvey PriceSurvey { get; private set; }

        internal LiveSellList LiveSellList { get; private set; }

        /// <summary>
        /// 「待處理」清單的計算與操作驅動器（純計算＋把某一格交給既有的重掛引擎）。
        /// 🔴 它沒有任何自動改價路徑：巡檢跑完只會重算清單，不會動任何一格的價格。
        /// </summary>
        internal PendingActionsBuilder PendingWorklist { get; private set; }

        /// <summary>
        /// 純唯讀觀察者：看到使用者在市場板上買下東西時，告訴 GilDelta 那筆金幣是買了什麼。
        /// 🔴 零封包、零 hook、零記憶體寫入，也不影響 Marketbuddy 的任何流程。
        /// </summary>
        internal MarketPurchaseWatcher MarketPurchaseWatcher { get; private set; }

        /// <summary>
        /// 「這一趟買到哪了」：貼在市場板搜尋結果視窗旁的計數與累計金額面板。
        /// 🔴 純顯示——不買東西、不點任何一列、零封包、零 hook、零記憶體寫入。
        /// </summary>
        internal MarketBuyTally MarketBuyTally { get; private set; }

        /// <summary>
        /// 僱員銷售紀錄：比較兩次看到的僱員市場容器，把少掉的東西記成帶信心標記的事件清單。
        /// 🔴 純唯讀觀察，不下單、不改價、不下架。
        /// </summary>
        internal RetainerMarketWatcher RetainerMarketWatcher { get; private set; }

        /// <summary>
        /// 純診斷探針（只記錄、零行為變更）。失敗時為 null，其餘功能不受影響。
        /// </summary>
        private MarketRequestResultProbe? requestResultProbe;

        /// <summary>
        /// ReleaseAll() 的冪等旗標。初始化失敗路徑與 Dalamud 的 Dispose() 都會呼叫它。
        /// </summary>
        private bool released;

        // Assembly compatible with dev & published versions
        public string AssemblyLocation { get; set; } = Assembly.GetExecutingAssembly().Location;
        public string Name => "Marketbuddy";

        public Marketbuddy(IDalamudPluginInterface pluginInterface)
        {
            try
            {
                DalamudInitialize(pluginInterface);

                // 🔴 越早越好。這支會訂閱 Framework.Update 當自己的幀時鐘，而同一個外掛
                //    內部的 Framework.Update 是單一多播委派包在同一個 try/catch 裡：
                //    排在前面的處理常式擲例外，後面所有處理常式那個 tick 完全不會被呼叫。
                //    時鐘停掉 ＝ 重按守衛的逃生口不會到期。
                AddonPressGuard.Initialize();

                // Must run before the UI is constructed or the command registered, as those
                // resolve their .Loc() text once at construction time.
                Localization.Init(pluginInterface.AssemblyLocation.DirectoryName);

                AutoRetainerBridge.Init();

                // Passive global market data cache. Must come up before the
                // engines so nothing that arrives during startup is lost; it
                // only ever writes to a dictionary and never drives anything.
                MarketDataCache.Init();

                // 「歷史最近賣出價」的來源（Universalis 的 HTTP API）。Init 只是把 HttpClient
                // 準備好，🔴 本身不送出任何請求：要使用者自己開啟那個定價方式、而且站在僱員的
                // 出售品視窗前，才會有查詢排進佇列。零遊戲內市場查詢、零封包、零 hook。
                LastSoldPriceSource.Init();

                // 掛售年齡：把「這一次載入的時間」記下來，功能開始看之前就已經在架上的東西
                // 拿它當暫定起點。🔴 只在「完全沒有紀錄」時寫一次，見 RetainerListingAge。
                RetainerListingAge.Init();

                // 純被動診斷：記錄每一次市場查價結果的 (listingCount, errorCode, itemId)，
                // 然後原封不動呼叫 Original。用來判定「伺服器拒絕時到底有沒有回封包」。
                // 掛不上去只影響診斷，所以自己吞掉例外。
                try
                {
                    requestResultProbe = new MarketRequestResultProbe();
                }
                catch (Exception e)
                {
                    Log.Warning(e, "Market request result probe could not be initialized (diagnostics only)");
                }

                MarketGuiEventHandler = new MarketGuiEventHandler();

                BatchReprice = new BatchReprice(MarketGuiEventHandler);
                MarketGuiEventHandler.BatchEngine = BatchReprice;

                BatchDelist = new BatchDelist(MarketGuiEventHandler);
                MarketGuiEventHandler.DelistEngine = BatchDelist;

                // 跨世界價格巡檢：只訂閱 Framework.Update 與市場封包事件並等待。
                // 🔴 它沒有任何自動觸發來源——使用者不去按按鈕，這個物件什麼都不會做。
                //    「掃完自動換世界」也一樣：那要使用者先打開一個預設關的設定、
                //    再親手按「武裝一輪」，而且武裝狀態不存檔（載入時一律是解除的）。
                PriceSurvey = new PriceSurvey(MarketGuiEventHandler);
                MarketGuiEventHandler.Survey = PriceSurvey;

                MultiTour = new MultiRetainerTour(MarketGuiEventHandler, BatchReprice, BatchDelist);

                // 多角色重掛：只是訂閱 AutoRetainer 的事件並等待；預設沒有武裝，
                // 使用者不去按那顆按鈕的話這個物件什麼都不會做。
                MultiCharTour = new MultiCharacterTour(MarketGuiEventHandler, MultiTour);

                ManualRequery = new ManualRequery(MarketGuiEventHandler);

                // Display only: draws a live copy of the retainer's listings next to the
                // game's own (which never redraws itself after a headless reprice).
                LiveSellList = new LiveSellList(MarketGuiEventHandler, BatchReprice);

                // 待處理清單：只訂閱 Framework.Update 當自己的幀時鐘，然後等使用者按按鈕。
                // 🔴 建構本身不讀檔、不算任何東西。
                PendingWorklist = new PendingActionsBuilder(MarketGuiEventHandler);
                MarketGuiEventHandler.Pending = PendingWorklist;

                // 金幣歸因提示：只訂閱 Framework.Update 並在市場結果視窗開著時讀一個欄位。
                // 沒裝 GilDelta 時整條路徑是安靜的 no-op。
                MarketPurchaseWatcher = new MarketPurchaseWatcher(MarketGuiEventHandler);

                // 「這一趟買到哪了」：與上面那個觀察者共用同一個購買偵測（見 MarketBuyTally），
                // 自己只多讀一份掛單快照。同樣是純顯示，不會替使用者買任何東西。
                MarketBuyTally = new MarketBuyTally(MarketGuiEventHandler);
                MarketPurchaseWatcher.Tally = MarketBuyTally;

                // 僱員銷售紀錄：只訂閱 Framework.Update，並且只在「出售品」視窗開著時
                // 讀僱員的市場容器與錢包。零封包、零 hook、零記憶體寫入。
                RetainerMarketWatcher = new RetainerMarketWatcher(MarketGuiEventHandler, BatchReprice);
                MarketGuiEventHandler.SalesWatcher = RetainerMarketWatcher;

                try
                {
                    QuickLister = new QuickLister(MarketGuiEventHandler, BatchReprice, MultiTour);
                    MarketGuiEventHandler.QuickLister = QuickLister;
                }
                catch (Exception e)
                {
                    // Address resolution failure only disables quick listing;
                    // the rest of the plugin keeps working.
                    Log.Error(e, "QuickLister could not be initialized, quick listing disabled");
                }

                PluginUi = new PluginUI(this);

                Common.Dalamud.CommandManager.AddHandler(commandName, new CommandInfo(OnCommand)
                {
                    HelpMessage = "Show plugin configuration window.".Loc()
                });

                PluginInterface.UiBuilder.Draw += DrawUi;
                PluginInterface.UiBuilder.OpenConfigUi += DrawConfigUi;
                IPCManager.Init();

            }
            catch (Exception e)
            {
                if (e is not OperationCanceledException)
                    Log.Error(e, "Error loading plugin");

                // 🔴 初始化到一半失敗時,前面已經建好的元件必須全部釋放。
                // QuickLister 與 MarketRequestResultProbe 各自持有一個已 Enable 的 Hook,
                // 而且是直接用 IGameInteropProvider.HookFromAddress 建的,沒有進 Commons 的
                // HookList,所以只有它們自己的 Dispose() 會拆。
                // 這個 catch 把例外吞掉 ⇒ Dalamud 認為外掛「載入成功」,卸載時仍會呼叫 Dispose();
                ReleaseAll();
            }
        }

        public void Dispose() => ReleaseAll();

        /// <summary>
        /// 釋放所有已建立的資源。每一步各自獨立防護,任何一步失敗都不會擋住後面的步驟
        /// (關鍵是持有 hook 的 QuickLister / requestResultProbe 與 Commons.DisposeHooks 一定要跑到)。
        /// 具冪等性:初始化失敗路徑與 Dalamud 的 Dispose() 都會呼叫。
        /// 步驟順序刻意與原本的 Dispose() 逐行一致,沒有調換。
        /// </summary>
        private void ReleaseAll()
        {
            if (released)
                return;
            released = true;

            Safe(() => IPCManager.Shutdown());
            Safe(() => PluginInterface.UiBuilder.Draw -= DrawUi);
            Safe(() => PluginInterface.UiBuilder.OpenConfigUi -= DrawConfigUi);
            Safe(() => Common.Dalamud.CommandManager.RemoveHandler(commandName));
            Safe(() => PluginUi?.Dispose());
            Safe(() => RetainerMarketWatcher?.Dispose());
            Safe(() => MarketBuyTally?.Dispose());
            Safe(() => MarketPurchaseWatcher?.Dispose());
            Safe(() => PendingWorklist?.Dispose());
            Safe(() => LiveSellList?.Dispose());
            Safe(() => QuickLister?.Dispose());
            Safe(() => ManualRequery?.Dispose());
            Safe(() => PriceSurvey?.Dispose());
            // 🔴 必須排在 MultiTour 之前：它會取消進行中的巡迴，而且要在那之後
            //    才把 AutoRetainer 的控制權還回去（AR 端的等待沒有時限）。
            Safe(() => MultiCharTour?.Dispose());
            Safe(() => MultiTour?.Dispose());
            Safe(() => BatchDelist?.Dispose());
            Safe(() => BatchReprice?.Dispose());
            Safe(() => MarketGuiEventHandler?.Dispose());
            Safe(() => requestResultProbe?.Dispose());
            Safe(() => MarketDataCache.Shutdown());
            // 取消進行中的 HTTP 查詢並釋放 HttpClient。排在引擎之後：它們不再會排新的查詢。
            Safe(() => LastSoldPriceSource.Shutdown());
            // 巡檢記錄的記憶體索引。注意讀檔那個 Task 可能還在跑，Reset 只是把狀態歸零；
            // 它之後寫進去的內容會在下一次載入時被重建的索引取代（純快取，沒有副作用）。
            Safe(() => PriceSurveySnapshot.Reset());
            // Last: must run after every engine released its reference so a
            // leftover suppression can never survive an unload.
            Safe(() => AutoRetainerBridge.Shutdown());
            // 所有會按下去的模組都拆完之後才收掉重按守衛。
            Safe(() => AddonPressGuard.Shutdown());
            // 收尾:任何經由 Commons.Hook() 註冊的 hook 都在這裡拆掉。
            Safe(() => Commons.Dispose());
        }

        /// <summary>
        /// 執行單一釋放步驟;失敗只記錄不外傳,確保後續步驟(尤其是拆 hook)一定跑得到。
        /// DalamudInitialize 失敗時連 Log 都可能還是 null,所以記錄本身也要防護。
        /// </summary>
        private static void Safe(Action step)
        {
            try
            {
                step();
            }
            catch (Exception e)
            {
                try
                {
                    Log?.Error(e, "Marketbuddy: 卸載步驟失敗,略過並繼續執行其餘步驟。");
                }
                catch
                {
                    // 記錄失敗絕不能中斷卸載流程。
                }
            }
        }

        private void OnCommand(string command, string args)
        {
            if (command != commandName)
                return;

            // /mbuddy survey ＝ 直接開巡檢視窗。空參數維持一直以來的行為（開關設定視窗）。
            var argument = args.Trim();
            if (argument.Equals("survey", StringComparison.OrdinalIgnoreCase))
            {
                PluginUi.SurveyVisible = !PluginUi.SurveyVisible;
                return;
            }

            PluginUi.SettingsVisible = !PluginUi.SettingsVisible;
        }

        private void DrawUi()
        {
            PluginUi.Draw();
        }

        private void DrawConfigUi()
        {
            PluginUi.SettingsVisible = true;
        }
    }
}