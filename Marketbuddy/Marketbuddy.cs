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

        internal LiveSellList LiveSellList { get; private set; }

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

                // Must run before the UI is constructed or the command registered, as those
                // resolve their .Loc() text once at construction time.
                Localization.Init(pluginInterface.AssemblyLocation.DirectoryName);

                AutoRetainerBridge.Init();

                // Passive global market data cache. Must come up before the
                // engines so nothing that arrives during startup is lost; it
                // only ever writes to a dictionary and never drives anything.
                MarketDataCache.Init();

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

                MultiTour = new MultiRetainerTour(MarketGuiEventHandler, BatchReprice, BatchDelist);

                // 多角色重掛：只是訂閱 AutoRetainer 的事件並等待；預設沒有武裝，
                // 使用者不去按那顆按鈕的話這個物件什麼都不會做。
                MultiCharTour = new MultiCharacterTour(MarketGuiEventHandler, MultiTour);

                ManualRequery = new ManualRequery(MarketGuiEventHandler);

                // Display only: draws a live copy of the retainer's listings next to the
                // game's own (which never redraws itself after a headless reprice).
                LiveSellList = new LiveSellList(MarketGuiEventHandler, BatchReprice);

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
                // 但舊的 Dispose() 一開頭就無防護地存取 PluginInterface / PluginUi 等欄位,
                // 初始化半途失敗時那些欄位是 null,第一個 NullReferenceException 就會讓後面的
                // QuickLister?.Dispose() 與 requestResultProbe?.Dispose() 永遠跑不到,
                // detour 於是活過外掛卸載 —— 下一次遊戲呼叫該函式就跳進已卸載的組件。
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
            Safe(() => LiveSellList?.Dispose());
            Safe(() => QuickLister?.Dispose());
            Safe(() => ManualRequery?.Dispose());
            // 🔴 必須排在 MultiTour 之前：它會取消進行中的巡迴，而且要在那之後
            //    才把 AutoRetainer 的控制權還回去（AR 端的等待沒有時限）。
            Safe(() => MultiCharTour?.Dispose());
            Safe(() => MultiTour?.Dispose());
            Safe(() => BatchDelist?.Dispose());
            Safe(() => BatchReprice?.Dispose());
            Safe(() => MarketGuiEventHandler?.Dispose());
            Safe(() => requestResultProbe?.Dispose());
            Safe(() => MarketDataCache.Shutdown());
            // Last: must run after every engine released its reference so a
            // leftover suppression can never survive an unload.
            Safe(() => AutoRetainerBridge.Shutdown());
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
            if (command == commandName)
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