using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Marketbuddy.Common;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    /// <summary>
    /// 「即時出售品清單」——我們自己畫的僱員掛單表，貼在遊戲「出售品」視窗旁邊。
    /// 這裡的資料每一幀在 Framework.Update 上重新從 <c>InventoryManager</c> 讀進快照，
    /// 繪製時只吃快照，所以既永遠是最新的，也不會在繪製執行緒上呼叫任何遊戲函式。
    /// </summary>
    internal sealed unsafe class LiveSellList : IDisposable
    {
        /// <summary>Retainer market containers hold 20 slots; every index is bounds-checked against both ends.</summary>
        private const int MaxMarketSlots = 20;

        /// <summary>How long a repriced row keeps its "just changed" highlight.</summary>
        private static readonly TimeSpan ChangeHighlightFor = TimeSpan.FromMinutes(10);

        /// <summary>
        /// 「正在處理這一件」的列底色（半透明琥珀）。
        /// 刻意跟「已改價」那個綠色**分屬不同視覺通道**：進行中是整列底色，已改價是
        /// 單價欄的綠字。兩者可以同時出現在同一列（剛改完價、下一輪又輪到它）而不打架，
        /// 而且顏色本身也分得開——琥珀＝正在做，綠＝做完了。
        /// </summary>
        private static readonly Vector4 InProgressRowColor = new(1.00f, 0.72f, 0.15f, 0.22f);

        private readonly struct Row(short slot, string name, uint quantity, uint unitPrice)
        {
            public readonly short Slot = slot;
            public readonly string Name = name;
            public readonly uint Quantity = quantity;
            public readonly uint UnitPrice = unitPrice;

            /// <summary>這一件的「歷史最近賣出價」目前處在什麼狀態（查詢中／查不到／有值）。</summary>
            public LastSoldState SoldState { get; init; }

            /// <summary>
            /// 這一幀「歷史最近賣出價」的查詢到底有沒有在跑（＝那個定價方式開著）。
            /// 🔴 這個旗標存在的唯一理由：<see cref="LastSoldState.Unknown"/> 有<b>兩種</b>
            /// 完全不同的意思。定價方式開著時它是「這一幀剛好還沒排到」，下一幀就會變成
            /// <see cref="LastSoldState.Loading"/>；關著時它是「<b>永遠</b>不會有人去問」。
            /// </summary>
            public bool SoldLookupEnabled { get; init; }

            /// <summary>成交單價；只有 <see cref="HasSold"/> 為 true 時才有意義。</summary>
            public long SoldUnitPrice { get; init; }

            public DateTime SoldAtUtc { get; init; }

            /// <summary>那一筆成交本身是優質品還是普通品（可能與這一格掛的品質不同——那正是「忽略優質」的意思）。</summary>
            public bool SoldHq { get; init; }

            /// <summary>可為 null：<c>default(Row)</c> 與「回應裡沒帶世界」都會落在這裡，畫面上一律畫成 <c>?</c>。</summary>
            public string? SoldWorld { get; init; }

            /// <summary>
            /// 真的取到了一筆成交紀錄。
            /// ⚠️ 與 <c>SoldState == Ready</c> <b>不等價</b>：Universalis 有這件的資料，但在
            /// 「不忽略優質」的設定下這個品質剛好沒有成交紀錄時，狀態是 Ready 而這裡是 false。
            /// </summary>
            public bool HasSold { get; init; }

            /// <summary>
            /// 有成交紀錄，但能用的那幾筆全部來自被排除的世界（所以這一件不定價）。
            /// 🔑 與「從來沒賣過」<b>在列上就要分得出來</b>：處置相同、原因不同，
            /// 而只有這一種是「把那個世界勾回來就有價了」。
            /// </summary>
            public bool SoldExcluded { get; init; }

            /// <summary>按下重掛會掛出去的價格；<b>0＝算不出來</b>（絕不代表 0 gil）。</summary>
            public uint RelistPrice { get; init; }

            /// <summary>
            /// 上面那個價是<b>哪一個來源</b>贏的（見 <see cref="PriceSourceTag"/>）；空＝沒有來源。
            /// </summary>
            /// <remarks>
            /// 🔑 「跟板上最低價」與「照最近成交價」是兩個完全不同的決定，
            /// 而這一欄是使用者在按下去之前唯一分得出來的地方。
            /// </remarks>
            public string? RelistSource { get; init; }

            /// <summary>算出 <see cref="RelistPrice"/> 用的原始參考價；-1＝不知道。</summary>
            public long RelistReference { get; init; }

            /// <summary>那個參考價是什麼時候的觀測；<see cref="DateTime.MinValue"/>＝不知道。</summary>
            public DateTime RelistAtUtc { get; init; }

            /// <summary>參考價被異常低價保護擋下來了 ⇒ 按下去這一格不會動。</summary>
            public bool RelistHeld { get; init; }

            /// <summary>本世界目前的最低掛售價；<b>null＝查不到，不是 0</b>。</summary>
            public MarketPricePoint? MinWorld { get; init; }

            /// <summary>整個資料中心目前的最低掛售價；<b>null＝查不到，不是 0</b>。</summary>
            public MarketPricePoint? MinDc { get; init; }

            /// <summary>這一批東西掛在架上多久了；<b>null＝完全沒有紀錄</b>（不是 0，也不是「剛剛」）。</summary>
            public ListingAge? Age { get; init; }

            /// <summary>
            /// 這件道具（<b>這個品質</b>）在我們自己的僱員銷售紀錄裡的彙總。
            /// ⚠️ <c>IsEmpty</c> 只代表「沒有事件」，<b>不代表「沒有資料」</b>——後者由
            /// <see cref="LiveSellList.historyState"/> 決定，兩者在畫面上必須分得出來。
            /// </summary>
            public ItemSaleHistory History { get; init; }

            /// <summary>同一個道具的<b>另一個品質</b>有沒有紀錄（只給滑鼠提示補一句用）。</summary>
            public bool HistoryOtherQuality { get; init; }

            /// <summary>整堆賣完扣稅後實拿；≤0＝單價讀不到（畫 <c>?</c>）。</summary>
            public long NetTotal { get; init; }

            public uint TaxPercent { get; init; }

            /// <summary>false＝稅率是設定裡的退回值，列上標「約」。</summary>
            public bool TaxLive { get; init; }

            /// <summary>看盤資料的狀態；不是 Ready 時下面幾個看盤欄位沒有意義。</summary>
            public LastSoldState WatchState { get; init; }

            /// <summary>自己世界同品質比我便宜的件數；-1＝算不出來。</summary>
            public long Cheaper { get; init; }

            public bool CheaperLowerBound { get; init; }

            /// <summary>最近 7 天每日成交件數（這個品質、自己的世界）；NaN＝不知道。</summary>
            public double UnitsPerDay { get; init; }

            /// <summary>預估幾天賣完；NaN＝不知道，正無限大＝窗內沒有成交。</summary>
            public double DaysToSell { get; init; }

            public DateTime WatchUploadedUtc { get; init; }

            public DateTime WatchFetchedUtc { get; init; }

            /// <summary>看盤資料太舊（見 <see cref="MarketWatchMath.StaleAfter"/>）⇒ 整列淡色。</summary>
            public bool WatchStale { get; init; }
        }

        /// <summary>「賣出/下架」那一欄的資料基礎處在什麼狀態。四種在畫面上都要分得出來。</summary>
        private enum SalesHistoryState
        {
            /// <summary>記錄功能是關著的 ⇒ 沒有資料，而且不會有。</summary>
            Disabled,

            /// <summary>紀錄還在讀 ⇒ 畫「…」，不是「沒有」。</summary>
            Loading,

            /// <summary>從來沒看過這位僱員的出售品清單 ⇒ 畫「?」，<b>絕不是 0</b>。</summary>
            NoBaseline,

            /// <summary>有觀察基礎；數字可以相信，但觀察期仍然要在滑鼠提示裡講出來。</summary>
            Ready,
        }

        /// <summary>道具 id 的暫存集合，給「歷史最近賣出價」預取用；重用，不每幀配置。</summary>
        private readonly HashSet<uint> prefetchIds = [];

        /// <summary>上一次替出售品視窗裡的道具排查詢的時間。預取不必每幀做。</summary>
        private DateTime lastPrefetchAt = DateTime.MinValue;

        private static readonly TimeSpan PrefetchEvery = TimeSpan.FromSeconds(1);

        /// <summary>太舊的列的透明度（文字與數字一起淡掉；滑鼠提示照樣全亮）。</summary>
        private const float StaleRowAlpha = 0.45f;

        /// <summary>使用者按了「查日銷」；下一次 framework tick 收走並排查詢（容器只在那裡讀）。</summary>
        private volatile bool watchRequested;

        private readonly MarketGuiEventHandler gui;
        private readonly BatchReprice engine;

        // Snapshot built on the framework thread, consumed by Draw(). Nothing in the
        // draw path calls a game function or dereferences a game pointer other than the
        // addon's own plain position/size fields.
        private readonly List<Row> rows = [];
        private Vector2 nativeTopLeft;
        private Vector2 nativeSize;
        private bool haveAnchor;
        private bool containerReady;
        private ulong snapshotRetainerId;

        /// <summary>「這是哪一位僱員的掛單」——名稱＋鈴清單上的序號。
        /// 在 framework 執行緒隨快照一起算好，Draw 只讀字串。</summary>
        private string retainerLabel = string.Empty;

        // RebuildRows 每幀都跑：以下是它重用的容器與「輸入沒變就沿用」的字串，只在 framework 執行緒碰。
        private readonly HashSet<ulong> ownRetainerIds = [];

        /// <summary>道具顯示名（含優質記號），鍵＝(道具 id, 優質)；Item 表在一次載入內不變，表在不在換了就整份重來。</summary>
        private readonly Dictionary<(uint ItemId, bool Hq), string> itemLabels = [];
        private bool itemLabelsHaveSheet;

        /// <summary>上一次算出的僱員標籤與它的輸入（僱員 id、鈴清單序號、名稱原始位元組）。</summary>
        private string labelText = string.Empty;
        private ulong labelRetainerId;
        private uint labelSortedIndex = uint.MaxValue;
        private byte[] labelNameBytes = [];

        /// <summary>「賣出/下架」那一欄的資料基礎；在 framework 執行緒隨快照算好，Draw 只讀。</summary>
        private SalesHistoryState historyState = SalesHistoryState.Loading;

        /// <summary>我們是從什麼時候開始看僱員清單的（觀察起點的下界）；MinValue＝不知道。</summary>
        private DateTime historySinceUtc = DateTime.MinValue;

        private Configuration conf => Configuration.GetOrLoad();

        public LiveSellList(MarketGuiEventHandler gui, BatchReprice engine)
        {
            this.gui = gui;
            this.engine = engine;
            Framework.Update += OnFrameworkUpdate;
        }

        public void Dispose()
        {
            Framework.Update -= OnFrameworkUpdate;
        }

        private void OnFrameworkUpdate(IFramework framework)
        {
            haveAnchor = false;

            // 「歷史最近賣出價」的預取。刻意排在下面那道顯示開關**之前**：重掛引擎自己也要用
            // 這份資料，它不該取決於使用者有沒有開這個面板。
            // 🔴 這只是一個對 Universalis 的唯讀 HTTP 查詢，不送任何遊戲內市場查詢、不改任何價格。
            //    定價方式關著（預設）時整條路徑一個位元組都不會動。
            if (conf.RelistUseLastSoldPrice && gui.IsRetainerSellListOpen)
                PrefetchLastSoldPrices();

            // 看盤資料只在使用者按了「查日銷」時才問（唯讀 HTTP，不碰遊戲內市場）。
            if (watchRequested)
            {
                watchRequested = false;
                if (gui.IsRetainerSellListOpen && CollectListedItemIds())
                    LastSoldPriceSource.RequestWatch(
                        PlayerState.ContentId == 0 ? 0u : PlayerState.CurrentWorld.RowId, prefetchIds);
            }

            // 巡檢記錄的記憶體索引（市場查詢被拒絕時的補位來源），讀檔在執行緒池上。
            // 刻意在載入後第一幀就踢、不等出售品視窗：讀檔配置引發的 GC 落在開遊戲／更新插件時，
            // 不會和第一次開出售品視窗的 JIT 疊成看得見的卡頓。不必登入；檔案不存在＝0 列。
            // 已經讀過的話這一行只是一次 Interlocked 比較。
            if (conf.RelistSurveyFallbackHours > 0)
                PriceSurveySnapshot.EnsureLoaded();

            // 「這件賣掉過嗎」的彙總：讀檔與統計全部在執行緒池上，這裡只收工作。
            // 🔴 繪製路徑一個位元組的 I/O 都不做，而且只在那一欄真的會被畫出來時才推進。
            if (conf.LiveSellListOverlay && conf.LiveSellListSalesHistoryColumn &&
                conf.RetainerSalesLogEnabled && gui.IsRetainerSellListOpen)
                RetainerSalesHistory.Pump();

            if (!conf.LiveSellListOverlay || !gui.IsRetainerSellListOpen)
            {
                rows.Clear();
                return;
            }

            // Re-resolve the addon every tick; a native pointer is never kept across frames.
            var addon = Commons.GetUnitBase("RetainerSellList");
            if (addon == null || !addon->IsVisible || addon->RootNode == null ||
                addon->UldManager.LoadedState != AtkLoadState.Loaded)
            {
                rows.Clear();
                return;
            }

            // Geometry from plain modelled fields only - deliberately NOT GetScaledWidth()/
            // GetScaledHeight(), which are signature-resolved native calls we cannot verify
            // offline on the Taiwan client.
            // 只存這一幀量到的矩形（純數值）；基準點與偏移在 Draw 裡套，設定一改下一幀就生效。
            var scale = PanelPlacementMath.SafeScale(addon->Scale);
            nativeTopLeft = new Vector2(addon->X, addon->Y);
            nativeSize = new Vector2(addon->RootNode->Width * scale, addon->RootNode->Height * scale);
            haveAnchor = true;

            RebuildRows();
        }

        /// <summary>
        /// 把出售品視窗裡這一位僱員的道具排進「歷史最近賣出價」的查詢佇列。
        /// 🔴 只在 framework 執行緒上跑（它讀市場容器與 <c>PlayerState</c>）；
        /// 真正的 HTTP 在執行緒池上，見 <see cref="LastSoldPriceSource"/>。
        /// 每秒最多一次——已經有夠新答案的道具在那一側就會被濾掉，所以這裡只是省下容器掃描。
        /// </summary>
        private void PrefetchLastSoldPrices()
        {
            var now = DateTime.UtcNow;
            if (now - lastPrefetchAt < PrefetchEvery)
                return;
            lastPrefetchAt = now;

            if (!CollectListedItemIds())
                return;

            var worldId = PlayerState.ContentId == 0 ? 0u : PlayerState.CurrentWorld.RowId;
            LastSoldPriceSource.Request(worldId, prefetchIds);
        }

        /// <summary>把這位僱員掛著的道具 id 收進 <see cref="prefetchIds"/>；一件都沒有（或容器還沒載入）回 false。只在 framework 執行緒呼叫。</summary>
        private bool CollectListedItemIds()
        {
            prefetchIds.Clear();
            var inventoryManager = InventoryManager.Instance();
            var container = inventoryManager == null
                ? null
                : inventoryManager->GetInventoryContainer(InventoryType.RetainerMarket);
            if (container == null || !container->IsLoaded)
                return false;

            var slotCount = Math.Min((int)container->Size, MaxMarketSlots);
            for (var i = 0; i < slotCount; i++)
            {
                var slot = inventoryManager->GetInventorySlot(InventoryType.RetainerMarket, i);
                if (slot == null || slot->ItemId == 0)
                    continue;
                prefetchIds.Add(slot->ItemId);
            }

            return prefetchIds.Count > 0;
        }

        private void RebuildRows()
        {
            rows.Clear();
            snapshotRetainerId = BatchReprice.ActiveRetainerId();
            retainerLabel = string.Empty;

            // 名稱＋序號：序號用鈴清單的排序（GetRetainerBySortedIndex），與玩家看到的順序一致。
            // 面板只在出售品視窗開著時存在，此時 LastSelectedRetainerId 就是眼前這一位，沒有陳舊問題。
            var retainerManager = FFXIVClientStructs.FFXIV.Client.Game.RetainerManager.Instance();
            if (snapshotRetainerId != 0 && retainerManager != null && retainerManager->IsReady)
            {
                var count = retainerManager->GetRetainerCount();
                for (var i = 0u; i < count; i++)
                {
                    var retainer = retainerManager->GetRetainerBySortedIndex(i);
                    if (retainer != null && retainer->RetainerId == snapshotRetainerId)
                    {
                        retainerLabel = RetainerLabel(retainer, i);
                        break;
                    }
                }
            }

            // 「按下重掛會掛出去多少」要用的共用輸入，在迴圈外算一次。
            // 🔴 這一段純唯讀而且<b>不送任何市場查詢</b>：面板只是預告，真正的查詢在引擎那側。
            var ownRetainers = ownRetainerIds;
            OwnRetainers.Fill(ownRetainers, conf);

            var homeWorldId = PlayerState.ContentId == 0 ? 0u : PlayerState.HomeWorld.RowId;
            var undercutRule = conf.UndercutRuleSnapshot();
            var anomalyRule = conf.AnomalyRuleSnapshot();

            // 「賣出/下架」那一欄的資料基礎。🔑 這幾個判斷的唯一目的是讓
            // 「我們沒看過」與「看過但沒賣掉」在畫面上分得出來——兩者長得一模一樣，
            // 而把前者畫成 0 會讓使用者做出相反的決定。
            var showHistory = conf.LiveSellListSalesHistoryColumn;
            var history = SalesHistorySnapshot.Empty;
            historySinceUtc = DateTime.MinValue;
            if (!showHistory || !conf.RetainerSalesLogEnabled)
            {
                historyState = SalesHistoryState.Disabled;
            }
            else if (!RetainerSalesHistory.Ready || !RetainerListingAge.Loaded)
            {
                historyState = SalesHistoryState.Loading;
            }
            else
            {
                history = RetainerSalesHistory.Current;
                historySinceUtc = RetainerListingAge.EarliestSeen() ?? DateTime.MinValue;
                historyState = RetainerListingAge.FirstSeen(snapshotRetainerId) == null
                    ? SalesHistoryState.NoBaseline
                    : SalesHistoryState.Ready;
            }

            // 看盤欄位的共用輸入：稅率（這位僱員所在城市；不知道就退回設定值並標「約」）與看盤資料的世界。
            var watchOn = conf.LiveSellListWatchColumns;
            var taxLive = false;
            var taxPercent = 0u;
            if (watchOn)
                taxLive = engine.TryLiveTaxPercent(out taxPercent);
            var watchWorldId = PlayerState.ContentId == 0 ? 0u : PlayerState.CurrentWorld.RowId;
            var nowUtc = DateTime.UtcNow;

            var inventoryManager = InventoryManager.Instance();
            var container = inventoryManager == null
                ? null
                : inventoryManager->GetInventoryContainer(InventoryType.RetainerMarket);
            containerReady = container != null && container->IsLoaded;
            if (!containerReady)
                return;

            // Bounds: container->Size is the authority, but never index past the 20 slots
            // GetRetainerMarketPrice() is defined for - both ends are checked.
            var slotCount = Math.Min((int)container->Size, MaxMarketSlots);
            var itemSheet = DataManager.GetExcelSheet<Item>();

            for (var i = 0; i < slotCount; i++)
            {
                var slot = inventoryManager->GetInventorySlot(InventoryType.RetainerMarket, i);
                if (slot == null || slot->ItemId == 0)
                    continue;

                var isHq = (slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
                var name = ItemLabel(itemSheet, slot->ItemId, isHq);
                var soldState = LastSoldState.Unknown;
                var hasSold = false;
                var soldExcluded = false;
                LastSoldEntry sold = default;
                uint relistPrice = 0;
                MarketPricePoint? minWorld = null;
                MarketPricePoint? minDc = null;
                if (conf.RelistUseLastSoldPrice || conf.LiveSellListMarketColumns)
                {
                    soldState = LastSoldPriceSource.StateOf(slot->ItemId);
                    hasSold = LastSoldPriceSource.TryGet(slot->ItemId, isHq,
                        conf.RelistLastSoldIgnoreQuality, out sold);
                    // 🔴 成交價不直接等於重掛後的金額：定價規則是「成交價與板上
                    //    最低價取低者」，所以最終金額在下面那一段才算得出來。
                    if (!hasSold)
                        // 🔑 只有取不到價格時才問原因：取到了的話「某個品質被排除」
                        //    不是使用者要看的事。
                        soldExcluded = LastSoldPriceSource.IsSaleExcluded(
                            slot->ItemId, isHq, conf.RelistLastSoldIgnoreQuality);
                    LastSoldPriceSource.TryGetMinPrices(slot->ItemId, isHq, out minWorld, out minDc);
                }

                // 「按下重掛會掛出去多少」的預告：與引擎<b>共用同一支</b>定價函式
                // （<see cref="RelistPricing"/>），所以面板上的數字與真的掛出去的價是同一套規則。
                // 🔴 這裡拿不到「這一輪真的向伺服器問到的掛單」（面板不送查詢），
                //    所以 L 只能來自外掛自己的市場快取，快取沒有就退到巡檢那一列；兩者都沒有時面板顯示成交價，
                //    而引擎（它真的會去問）可能得到更低的板上價——已知落差，<b>方向只會是「實際更低」</b>。
                var relistSource = string.Empty;
                var relistReference = -1L;
                var relistAtUtc = DateTime.MinValue;
                var relistHeld = false;
                if (conf.RelistUseLastSoldPrice)
                {
                    var listedPrice = RetainerMarketPrice.TryGet(inventoryManager, i, out var listedRaw) ? (long)listedRaw : -1L;
                    long competitor = -1;
                    long boardWitness = -1;
                    long peerBaseline = -1;
                    var boardOwnIsLowest = false;
                    var listingSource = string.Empty;
                    var listingAt = DateTime.MinValue;

                    // L ① 外掛自己的市場快取。🔴 裸 Dictionary，只有 framework 執行緒能讀
                    //    ——這裡就是 framework 執行緒（OnFrameworkUpdate）。
                    if (MarketDataCache.TryPeek(slot->ItemId, conf.MarketDataCacheSeconds,
                            out var cachedListings, out var cacheAgeMs))
                    {
                        competitor = RelistPricing.LowestCompetitor(
                            cachedListings, isHq, conf.BatchCompareHqOnly, ownRetainers,
                            out peerBaseline, out boardOwnIsLowest, out _);
                        boardWitness = competitor;
                        if (boardOwnIsLowest)
                        {
                            // 與引擎同一條規則：自家掛單已經是最低時板上不提供目標價。
                            competitor = -1;
                            peerBaseline = -1;
                        }
                        else if (competitor >= 0)
                        {
                            listingSource = PriceSourceTag.Cache;
                            listingAt = DateTime.UtcNow.AddMilliseconds(-Math.Max(0d, cacheAgeMs));
                        }
                    }
                    // L ② 巡檢記錄裡家世界那一列（引擎在查詢失敗時用的那一條）。
                    else if (conf.RelistSurveyFallbackHours > 0 && homeWorldId != 0
                             && PriceSurveySnapshot.TryGetLowest(slot->ItemId, isHq, homeWorldId,
                                 DateTime.UtcNow, conf.RelistSurveyFallbackHours,
                                 out var surveyLowest, out var surveyAt))
                    {
                        competitor = surveyLowest;
                        boardWitness = surveyLowest;
                        listingSource = PriceSourceTag.Survey;
                        listingAt = surveyAt;
                    }

                    var now = DateTime.UtcNow;
                    var saleWitness = hasSold && !RelistPricing.IsSaleStale(
                        sold.SoldAtUtc, now, conf.RelistLastSoldMaxAgeDays)
                        ? sold.UnitPrice
                        : -1L;
                    // 每幀路徑：只有真的有同業基準可排除時才收集參照價。
                    var peerRef = anomalyRule.UseHistory && (peerBaseline > 0 || minWorld != null)
                        ? NewListingFallback.PeerReference(slot->ItemId, isHq, anomalyRule.Ratio,
                            conf.PriceConsensusWindowDays, now)
                        : -1L;
                    var listingCandidate = RelistPricing.FromListing(
                        competitor, peerBaseline, listedPrice, listingSource, listingAt,
                        undercutRule, anomalyRule,
                        LastSoldPriceSource.SaleConsensusOrUnknown(slot->ItemId, isHq), saleWitness, peerRef);
                    var saleCandidate = hasSold
                        ? RelistPricing.FromSale(
                            sold.UnitPrice, sold.SoldAtUtc,
                            minWorld is { } salePeer ? salePeer.UnitPrice : -1L,
                            listedPrice, now, conf.RelistLastSoldMaxAgeDays, anomalyRule,
                            LastSoldPriceSource.SaleConsensusOrUnknown(slot->ItemId, sold.Hq), boardWitness, peerRef)
                        : PriceCandidate.None;

                    var saleIsOwn = hasSold && conf.RelistSkipOwnSalePrice
                                    && OwnPriceEcho.Contains(slot->ItemId, sold.Hq, sold.UnitPrice);
                    var decision = RelistPricing.Decide(listingCandidate, saleCandidate, saleIsOwn);
                    // 與引擎同一條規則：停在上限價的格子還沒有真的價，重掛時套新上架退路（BatchReprice.ApplySlot）。
                    if (listedPrice >= Configuration.MAX_PRICE && conf.NewListingFallbackEnabled
                        && decision.Outcome != RelistOutcome.Price)
                    {
                        var inputs = NewListingFallback.Gather(slot->ItemId, isHq, conf.RelistLastSoldIgnoreQuality,
                            anomalyRule.Ratio, conf.PriceConsensusWindowDays,
                            saleCandidate.IsObserved ? saleCandidate.Reference : -1, now);
                        decision = RelistPricing.WithNewListingFallback(decision, boardOwnIsLowest,
                            RelistPricing.FallbackCandidate(inputs, undercutRule, anomalyRule.Ratio));
                    }
                    relistSource = decision.Winner.Source;
                    relistReference = decision.Winner.Reference;
                    relistAtUtc = decision.Winner.AtUtc;
                    relistHeld = decision.Outcome == RelistOutcome.Hold;
                    if (decision.Outcome == RelistOutcome.Price)
                        relistPrice = (uint)Math.Clamp(
                            decision.Price, Configuration.MIN_PRICE, Configuration.MAX_PRICE);
                }

                // 這個品質自己的紀錄；另一個品質只用來在滑鼠提示裡補一句
                // （優質與普通在市場上是兩件不同的商品，數字刻意不合併）。
                var itemHistory = default(ItemSaleHistory);
                var otherQuality = false;
                if (showHistory && historyState != SalesHistoryState.Disabled)
                {
                    itemHistory = history.Get(slot->ItemId, isHq);
                    var anyQuality = history.GetAnyQuality(slot->ItemId);
                    otherQuality = anyQuality.SoldEvents > itemHistory.SoldEvents ||
                                   anyQuality.OffBoardEvents > itemHistory.OffBoardEvents;
                }

                var quantity = (uint)Math.Max(0, slot->Quantity);
                var unitPrice = RetainerMarketPrice.TryGet(inventoryManager, i, out var rowPrice) ? (uint)rowPrice : 0u;
                var watch = watchOn
                    ? LastSoldPriceSource.WatchOf(slot->ItemId, watchWorldId, isHq)
                    : default;
                var cheaper = -1L;
                var cheaperLowerBound = false;
                var daysToSell = double.NaN;
                var watchStale = false;
                if (watch.State == LastSoldState.Ready && watch.Listings is { } watchListings)
                {
                    cheaper = MarketWatchMath.CheaperQuantity(watchListings.Prices, watchListings.Quantities,
                        unitPrice, watch.CapPrice, out cheaperLowerBound);
                    daysToSell = MarketWatchMath.DaysToSell(cheaper, quantity, watch.UnitsPerDay);
                    watchStale = MarketWatchMath.IsStale(watch.UploadedUtc, nowUtc, MarketWatchMath.StaleAfter);
                }

                rows.Add(new Row((short)i, name, quantity, unitPrice)
                {
                    NetTotal = watchOn ? MarketWatchMath.NetTotal(unitPrice, quantity, taxPercent) : -1,
                    TaxPercent = taxPercent,
                    TaxLive = taxLive,
                    WatchState = watch.State,
                    Cheaper = cheaper,
                    CheaperLowerBound = cheaperLowerBound,
                    UnitsPerDay = watch.UnitsPerDay,
                    DaysToSell = daysToSell,
                    WatchUploadedUtc = watch.UploadedUtc,
                    WatchFetchedUtc = watch.FetchedUtc,
                    WatchStale = watchStale,
                    SoldState = soldState,
                    // 🔴 與 soldState 一起抄進這一幀的快照，不要留到畫的時候才去讀
                    //    conf：那會讓「這個 Unknown 是暫時的還是永久的」與它算出來的
                    //    那一刻脫鉤。
                    SoldLookupEnabled = conf.RelistUseLastSoldPrice,
                    HasSold = hasSold,
                    SoldExcluded = soldExcluded,
                    SoldUnitPrice = hasSold ? sold.UnitPrice : 0,
                    SoldAtUtc = hasSold ? sold.SoldAtUtc : DateTime.MinValue,
                    SoldHq = hasSold && sold.Hq,
                    SoldWorld = hasSold ? sold.World : string.Empty,
                    RelistPrice = relistPrice,
                    RelistSource = relistSource,
                    RelistReference = relistReference,
                    RelistAtUtc = relistAtUtc,
                    RelistHeld = relistHeld,
                    MinWorld = minWorld,
                    MinDc = minDc,
                    Age = conf.LiveSellListMarketColumns
                        ? RetainerListingAge.Get(snapshotRetainerId, slot->ItemId, isHq)
                        : null,
                    History = itemHistory,
                    HistoryOtherQuality = otherQuality,
                });
            }
        }

        private string ItemLabel(Lumina.Excel.ExcelSheet<Item>? itemSheet, uint itemId, bool hq)
        {
            if (itemLabelsHaveSheet != (itemSheet != null))
            {
                itemLabels.Clear();
                itemLabelsHaveSheet = itemSheet != null;
            }

            if (itemLabels.TryGetValue((itemId, hq), out var cached))
                return cached;

            var name = $"#{itemId}";
            if (itemSheet != null && itemSheet.TryGetRow(itemId, out var row))
                name = row.Name.ExtractText();
            if (hq)
                name += $" {(char)SeIconChar.HighQuality}";

            if (itemLabels.Count >= 4096)
                itemLabels.Clear();
            itemLabels[(itemId, hq)] = name;
            return name;
        }

        private string RetainerLabel(RetainerManager.Retainer* retainer, uint sortedIndex)
        {
            var nameBytes = retainer->Name;
            if (retainer->RetainerId == labelRetainerId && sortedIndex == labelSortedIndex
                && nameBytes.SequenceEqual(labelNameBytes))
                return labelText;

            labelText = "?? (No.??)".Loc(retainer->NameString, sortedIndex + 1);
            labelRetainerId = retainer->RetainerId;
            labelSortedIndex = sortedIndex;
            labelNameBytes = nameBytes.ToArray();
            return labelText;
        }

        /// <summary>
        /// 這個面板與重掛面板是同一欄，位置由 <see cref="Configuration.LiveSellListAnchor"/>
        /// 決定、每幀依原生視窗這一幀的矩形重算。重掛面板的高度會變，所以疊放的起點不能寫死：
        /// <paramref name="stackEdgeY"/> 是先畫的那一塊**這一幀量到的外緣**（往下長＝下緣、往上長＝上緣），
        /// null＝那一塊這一幀沒畫，直接貼基準點。
        /// </summary>
        /// <returns>這一塊的外緣 Y（給後畫的那一塊接）；這一幀沒畫出來時回傳 null。</returns>
        public float? Draw(float? stackEdgeY = null)
        {
            if (!conf.LiveSellListOverlay || !haveAnchor)
                return null;

            var column = PanelPlacementMath.Compute(conf.LiveSellListAnchor, nativeTopLeft, nativeSize,
                conf.LiveSellListOffset, ImGuiHelpers.MainViewport.Pos);
            var placement = PanelPlacementMath.Stack(column, stackEdgeY);

            // 高度上限扣掉同一欄被另一塊吃掉的垂直空間；200 px 地板，否則重掛面板很高時這裡會被壓到看不見。
            ImGui.SetNextWindowPos(placement.Position, ImGuiCond.Always, placement.Pivot);
            ImGui.SetNextWindowSizeConstraints(
                new Vector2(220, 80),
                new Vector2(float.MaxValue, PanelPlacementMath.ListMaxHeight(nativeSize.Y, column, placement, 200f)));

            if (!ImGui.Begin("Marketbuddy_livesellist",
                    ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                    ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize |
                    ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoSavedSettings))
            {
                ImGui.End();
                return null;
            }

            if (stackEdgeY == null)
                PluginUI.SideColumnAnchorPanelSize = ImGui.GetWindowSize();
            DrawHeader();
            DrawTable();

            var edge = PanelPlacementMath.OuterEdgeY(column, ImGui.GetWindowPos(), ImGui.GetWindowSize());
            ImGui.End();
            return edge;
        }

        private void DrawHeader()
        {
            // 「（永遠是最新的）」是**說明**不是標題：掛在標題後面會把這個小面板的
            // 標題撐成兩行，白白吃掉本來要留給清單的垂直空間。移進滑鼠提示。
            ImGui.TextUnformatted("Live listings".Loc());
            if (ImGui.IsItemHovered())
                ShowTooltip("Re-read every frame - always shows the current prices".Loc());
            if (retainerLabel.Length > 0 && conf.LiveSellListShowRetainerName)
            {
                ImGui.SameLine();
                ImGui.TextColored(ImGuiColors.DalamudOrange, retainerLabel);
            }

            // 🔴 看盤資料只在這裡按了才查：不在開面板、換僱員或任何事件上自動查。
            if (conf.LiveSellListWatchColumns && (conf.LiveSellListShowCheaper || conf.LiveSellListShowDaysToSell))
            {
                ImGui.SameLine();
                if (ImGui.SmallButton(DrawText.Concat("Look up sales".Loc(), "##mblivesellistwatch")))
                    watchRequested = true;
                if (ImGui.IsItemHovered())
                    ShowTooltip(
                        "Asks Universalis, once, what is listed on your world right now and how much of each item sold there in the last 7 days, for everything this retainer has listed. Pressing it again within 30 minutes does not ask again. Read-only: nothing is listed, repriced or bought because of it."
                            .Loc());
            }

            // 與 /mbuddy「面板」分頁同一份選項（同一個方法畫），在這裡開是為了邊看邊調。
            ImGui.SameLine();
            if (ImGui.SmallButton(DrawText.Concat("Options".Loc(), "##mblivesellistopts")))
                ImGui.OpenPopup("##mblivesellistoptspopup");
            if (ImGui.IsItemHovered())
                ShowTooltip("Choose which columns are shown and where this panel sits".Loc());
            if (ImGui.BeginPopup("##mblivesellistoptspopup"))
            {
                PluginUI.DrawLiveSellListDisplayOptions(conf);
                ImGui.Separator();
                PluginUI.DrawSideColumnPositionOptions(conf, true, nativeTopLeft, nativeSize);
                ImGui.EndPopup();
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("x##mblivesellistclose"))
            {
                conf.LiveSellListOverlay = false;
                conf.Save();
            }

            if (ImGui.IsItemHovered())
                ShowTooltip("Hide this panel (re-enable it in /mbuddy)".Loc());
        }

        private void DrawTable()
        {
            if (!containerReady)
            {
                ImGui.TextUnformatted("Waiting for the retainer's data...".Loc());
                return;
            }

            if (rows.Count == 0)
            {
                ImGui.TextUnformatted("Nothing listed.".Loc());
                return;
            }

            var changes = engine.RecentChanges;
            var changesApply = snapshotRetainerId != 0 && engine.RecentChangesRetainerId == snapshotRetainerId;

            // 「此刻正在被處理的那一格」。批次重掛與快速上架的單件定價走的是同一個引擎，
            // 所以這一個判斷就同時涵蓋兩條路徑（見 BatchReprice.CurrentSlot）。
            // ⚠️ 用**格號**比對而不是道具名：同款道具拆成好幾格掛是常態，比名字會一次亮好幾列。
            var activeSlot = engine.IsRunning && snapshotRetainerId != 0 &&
                             engine.CurrentBatchRetainerId == snapshotRetainerId
                ? engine.CurrentSlot
                : (short)-1;

            // 「重掛後的金額」只有在那個定價方式開著時才有意義，關著時不占版面。
            // 道具名那一欄永遠畫：它是這一列「是哪一件」的唯一依據。
            var showRelist = conf.RelistUseLastSoldPrice && conf.LiveSellListShowRelistColumn;
            var showMarket = conf.LiveSellListMarketColumns;
            var showQty = conf.LiveSellListShowQuantity;
            var showUnit = conf.LiveSellListShowUnitPrice;
            var showTotal = conf.LiveSellListShowTotal;
            var showLowWorld = showMarket && conf.LiveSellListShowLowestWorld;
            var showLowDc = showMarket && conf.LiveSellListShowLowestDc;
            var showLastSale = showMarket && conf.LiveSellListShowLastSale;
            var showAge = showMarket && conf.LiveSellListShowListedFor;
            var showHistory = conf.LiveSellListSalesHistoryColumn;
            var showWatch = conf.LiveSellListWatchColumns;
            var showNet = showWatch && conf.LiveSellListShowNet;
            var showCheaper = showWatch && conf.LiveSellListShowCheaper;
            var showDays = showWatch && conf.LiveSellListShowDaysToSell;
            var columns = 1 + (showQty ? 1 : 0) + (showUnit ? 1 : 0) + (showTotal ? 1 : 0) +
                          (showNet ? 1 : 0) + (showCheaper ? 1 : 0) + (showDays ? 1 : 0) +
                          (showLowWorld ? 1 : 0) + (showLowDc ? 1 : 0) + (showLastSale ? 1 : 0) + (showAge ? 1 : 0) +
                          (showHistory ? 1 : 0) + (showRelist ? 1 : 0);

            if (!ImGui.BeginTable("##mblivesellisttable", columns,
                    ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
                return;

            ImGui.TableSetupColumn("Item".Loc(), ImGuiTableColumnFlags.WidthStretch);
            if (showQty)
                ImGui.TableSetupColumn("Qty".Loc());
            if (showUnit)
                ImGui.TableSetupColumn("Unit price".Loc());
            if (showTotal)
                ImGui.TableSetupColumn("Total".Loc());

            // 順序照看盤的讀法：單價 → 扣稅實拿 → 前面還排著幾件 → 幾天賣完。
            if (showNet)
                ImGui.TableSetupColumn("Net".Loc());
            if (showCheaper)
                ImGui.TableSetupColumn("Cheaper".Loc());
            if (showDays)
                ImGui.TableSetupColumn("Sells in".Loc());
            if (showLowWorld)
                ImGui.TableSetupColumn("Lowest here".Loc());
            if (showLowDc)
                ImGui.TableSetupColumn("Lowest on DC".Loc());
            if (showLastSale)
                ImGui.TableSetupColumn("Last sale".Loc());
            if (showAge)
                ImGui.TableSetupColumn("Listed for".Loc());

            // 刻意緊接在「已掛售多久」後面：「掛了 12 天／賣出 0 次」要一起讀才有意義。
            if (showHistory)
                ImGui.TableSetupColumn("Sold / off the board".Loc());

            if (showRelist)
                ImGui.TableSetupColumn("Relist at".Loc());
            ImGui.TableHeadersRow();

            foreach (var row in rows)
            {
                ImGui.TableNextRow();

                // 看盤資料太舊：整列淡色。只在看盤那兩欄有畫出來時才淡（淡色的理由就寫在那兩欄的提示裡）。
                var faded = row.WatchStale && (showCheaper || showDays);
                if (faded)
                    ImGui.PushStyleVar(ImGuiStyleVar.Alpha, StaleRowAlpha);

                // 進行中的那一列：整列底色 + 道具名改成琥珀色。
                // 目前處理中的道具**不在**這張表裡時（例如快速上架還沒把它掛上去、
                // 或引擎的目標格已經空掉）就什麼都不亮——刻意不退而求其次去比對名稱，
                // 硬找一列亮起來只會亮到錯的那一件。
                var inProgress = row.Slot == activeSlot;
                if (inProgress)
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1,
                        ImGui.ColorConvertFloat4ToU32(InProgressRowColor));

                ImGui.TableNextColumn();
                if (inProgress)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudYellow);
                    ImGui.TextUnformatted(row.Name);
                    ImGui.PopStyleColor();
                    if (ImGui.IsItemHovered())
                        ShowTooltip("Working on this one right now".Loc());
                }
                else
                {
                    ImGui.TextUnformatted(row.Name);
                    if (faded && ImGui.IsItemHovered())
                        ShowTooltip(WatchFreshness(row));
                }

                if (showQty)
                {
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(DrawText.N0(row.Quantity));
                }

                if (showUnit)
                {
                    ImGui.TableNextColumn();
                    var changed = changesApply &&
                                  changes.TryGetValue(row.Slot, out var change) &&
                                  change.NewPrice == row.UnitPrice &&
                                  DateTime.UtcNow - change.At <= ChangeHighlightFor;
                    if (changed)
                    {
                        ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.HealerGreen);
                        ImGui.TextUnformatted(DrawText.N0(row.UnitPrice));
                        ImGui.PopStyleColor();
                        if (ImGui.IsItemHovered())
                            ShowTooltip("Repriced by Marketbuddy: ?? → ??".Loc(
                                changes[row.Slot].OldPrice.ToString("N0"), row.UnitPrice.ToString("N0")));
                    }
                    else
                    {
                        ImGui.TextUnformatted(DrawText.N0(row.UnitPrice));
                    }
                }

                if (showTotal)
                {
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(DrawText.N0((long)row.UnitPrice * row.Quantity));
                }

                if (showNet)
                {
                    ImGui.TableNextColumn();
                    DrawNetCell(row);
                }

                if (showCheaper)
                {
                    ImGui.TableNextColumn();
                    DrawCheaperCell(row);
                }

                if (showDays)
                {
                    ImGui.TableNextColumn();
                    DrawDaysCell(row);
                }

                if (showLowWorld)
                {
                    ImGui.TableNextColumn();
                    DrawMinPriceCell(row, row.MinWorld, forDatacentre: false);
                }

                if (showLowDc)
                {
                    ImGui.TableNextColumn();
                    DrawMinPriceCell(row, row.MinDc, forDatacentre: true);
                }

                if (showLastSale)
                {
                    ImGui.TableNextColumn();
                    DrawLastSaleCell(row);
                }

                if (showAge)
                {
                    ImGui.TableNextColumn();
                    DrawAgeCell(row);
                }

                if (showHistory)
                {
                    ImGui.TableNextColumn();
                    DrawHistoryCell(row);
                }

                if (showRelist)
                {
                    ImGui.TableNextColumn();
                    DrawRelistCell(row);
                }

                if (faded)
                    ImGui.PopStyleVar();
            }

            ImGui.EndTable();
        }

        /// <summary>
        /// 「扣稅後實拿」：整堆賣完拿到多少。稅率不是遊戲這週告訴我們的（退回設定值）時前面標「約」。
        /// </summary>
        private static void DrawNetCell(in Row row)
        {
            if (row.NetTotal <= 0)
            {
                Grey("?");
                Tooltip("The price of this slot could not be read, so there is nothing to work out.".Loc());
                return;
            }

            ImGui.TextUnformatted(row.TaxLive ? DrawText.N0(row.NetTotal) : DrawText.N0("~".Loc(), row.NetTotal));
            if (!ImGui.IsItemHovered())
                return;

            var unitNet = MarketWatchMath.NetTotal(row.UnitPrice, 1, row.TaxPercent);
            ShowTooltip(row.TaxLive
                ? "All ?? sold at ?? gil each: you receive ?? gil after this retainer's city's market tax of ??% (?? gil per item)."
                    .Loc(row.Quantity.ToString("N0"), row.UnitPrice.ToString("N0"), row.NetTotal.ToString("N0"),
                        row.TaxPercent, unitNet.ToString("N0"))
                : "Approximate: the game has not sent this week's market tax rates yet, so this uses the ??% set in /mbuddy. All ?? sold at ?? gil each: about ?? gil (?? gil per item)."
                    .Loc(row.TaxPercent, row.Quantity.ToString("N0"), row.UnitPrice.ToString("N0"),
                        row.NetTotal.ToString("N0"), unitNet.ToString("N0")));
        }

        /// <summary>「自己世界同品質比我便宜的在售件數」；後面加 + 表示只看到前一百筆掛單、實際可能更多。</summary>
        private static void DrawCheaperCell(in Row row)
        {
            if (!DrawWatchMissing(row))
                return;

            if (row.Cheaper < 0)
            {
                Grey("?");
                Tooltip("The price of this slot could not be read, so there is nothing to compare.".Loc());
                return;
            }

            ImGui.TextUnformatted(row.CheaperLowerBound
                ? DrawText.Concat(DrawText.N0(row.Cheaper), "+")
                : DrawText.N0(row.Cheaper));
            if (!ImGui.IsItemHovered())
                return;

            var text = "On your world, ?? item(s) of this quality are listed for less than your ?? gil. Your own other retainers count too - theirs sell before this one as well."
                .Loc(row.Cheaper.ToString("N0"), row.UnitPrice.ToString("N0"));
            if (row.CheaperLowerBound)
                text += "\n" + "Universalis only returned the cheapest 100 listings, so there may be more.".Loc();
            ShowTooltip(text + "\n" + WatchFreshness(row));
        }

        /// <summary>「預估幾天賣完」＝（比我便宜的件數＋這一堆）÷ 最近 7 天每日成交件數。無條件進位到整天。</summary>
        private static void DrawDaysCell(in Row row)
        {
            if (!DrawWatchMissing(row))
                return;

            if (double.IsNaN(row.DaysToSell))
            {
                Grey("?");
                Tooltip("Not enough to go on: the price of this slot could not be read, or Universalis gave no sales figure.".Loc());
                return;
            }

            if (double.IsPositiveInfinity(row.DaysToSell))
            {
                Grey("No sales".Loc());
                if (ImGui.IsItemHovered())
                    ShowTooltip("Nothing of this quality sold on your world in the last 7 days, as far as Universalis knows, so there is no estimate.\n??"
                        .Loc(WatchFreshness(row)));
                return;
            }

            const int maxDays = 999;
            var days = row.DaysToSell < 1 ? 0 : (int)Math.Min(maxDays, Math.Ceiling(row.DaysToSell));
            var text = days == 0 ? "<1d".Loc() : DrawText.Loc("??d", days);
            if (row.CheaperLowerBound || row.DaysToSell > maxDays)
                text = DrawText.Concat(text, "+");
            ImGui.TextUnformatted(text);
            if (!ImGui.IsItemHovered())
                return;

            ShowTooltip(
                "About ?? item(s) of this quality sold per day on your world over the last 7 days. ?? listed cheaper plus your ?? = about ?? day(s) to sell out, if buyers keep coming at that rate and nobody lists below you.\n??"
                    .Loc(row.UnitsPerDay.ToString("0.#"), row.Cheaper.ToString("N0"), row.Quantity.ToString("N0"),
                        row.DaysToSell.ToString("0.#"), WatchFreshness(row)));
        }

        /// <summary>
        /// 看盤資料不是 Ready 時畫對應的灰色記號並回 false。「沒按過」「查詢中」「連不上」「沒有資料」四種都要分得出來，一個都不畫 0。
        /// </summary>
        private static bool DrawWatchMissing(in Row row)
        {
            switch (row.WatchState)
            {
                case LastSoldState.Ready:
                    return true;

                case LastSoldState.Loading:
                    Grey("...");
                    Tooltip("Asking Universalis...".Loc());
                    return false;

                case LastSoldState.Failed:
                    Grey("!");
                    Tooltip("Universalis could not be reached. Press \"Look up sales\" again in a moment.".Loc());
                    return false;

                case LastSoldState.NoData:
                    Grey("?");
                    Tooltip("Universalis has nothing on this item for your world: nobody has uploaded it yet, or it cannot be sold on the market board.".Loc());
                    return false;

                default:
                    Grey("?");
                    Tooltip("Not looked up yet. Press \"Look up sales\" at the top of this panel - nothing is asked until you do.".Loc());
                    return false;
            }
        }

        /// <summary>資料多新：Universalis 上一次有人上傳這件（自己的世界）的時間，以及我們什麼時候問的。只在滑鼠指到時組。</summary>
        private static string WatchFreshness(in Row row)
        {
            var ago = DateTime.UtcNow - row.WatchUploadedUtc;
            if (ago < TimeSpan.Zero)
                ago = TimeSpan.Zero;
            var text = "Universalis data for your world was uploaded ?? (?? ago); looked up ??."
                .Loc(BatchReprice.FormatSoldAt(row.WatchUploadedUtc), FormatSpan(ago),
                    BatchReprice.FormatSoldAt(row.WatchFetchedUtc));
            return row.WatchStale
                ? text + "\n" + "That is more than ?? hours old, so this row is faded.".Loc((int)MarketWatchMath.StaleAfter.TotalHours)
                : text;
        }

        /// <summary>
        /// 「按下重掛會掛出去的金額」那一格。
        /// 🔴 「查詢中」與「查不到」是<b>兩種不同的狀態，而且都要在列上看得見</b>：
        /// 任何一種畫成 0 或空白都會被讀成「這件賣過 0 gil」。
        /// 所以這裡一律用灰色的 <c>…</c> 與 <c>?</c>，理由放滑鼠提示。
        /// 📌 純顯示：這一格不會去改任何價格，改價一律是使用者自己按重掛按鈕。
        /// </summary>
        private static void DrawRelistCell(in Row row)
        {
            if (row.RelistPrice > 0)
            {
                ImGui.TextUnformatted(DrawText.N0(row.RelistPrice));
                if (!ImGui.IsItemHovered())
                    return;

                var reference = row.RelistReference < 0 ? "?" : row.RelistReference.ToString("N0");
                var when = BatchReprice.FormatSoldAt(row.RelistAtUtc);
                ShowTooltip(row.RelistSource switch
                {
                    PriceSourceTag.Sale =>
                        "Last sold for ?? gil (??), rounded down to ?? gil. That is lower than the cheapest listing on your own world, or there is none."
                            .Loc(reference, when, row.RelistPrice.ToString("N0")),
                    PriceSourceTag.Survey =>
                        "From the cross-world survey taken ??: the cheapest listing on your own world was ?? gil. Used because the live market data is not available."
                            .Loc(when, reference),
                    PriceSourceTag.Consensus =>
                        "The consensus of the latest sales on your data centre (?? gil, odd one-off prices left out), rounded down to 100 gil. Used when the last sale looked like an odd one-off, or when nobody else is selling this on your own world and there is no recent sale."
                            .Loc(reference),
                    PriceSourceTag.DcListing =>
                        "Nobody else is selling this on your own world and there are not enough recent sales, so this is the cheapest ordinary listing on another world of your data centre (?? gil), with your undercut applied."
                            .Loc(reference),
                    _ =>
                        "The cheapest listing on your own world that is not yours is ?? gil, with your undercut applied. That is lower than the last sale, or there is none."
                            .Loc(reference),
                });
                return;
            }

            // 🔑 「被異常低價保護擋下來」與「沒有資料」是兩件事，而且處置相同、原因相反
            //    ——畫成同一個灰色問號的話使用者無從知道「那個價其實有，只是看起來是打錯的」。
            if (row.RelistHeld)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
                ImGui.TextUnformatted("!");
                ImGui.PopStyleColor();
                Tooltip(
                    "The reference price looks like somebody dropped a zero, so this item would keep the price it has now. The pending list says what it was compared against."
                        .Loc());
                return;
            }

            switch (row.SoldState)
            {
                case LastSoldState.Unknown:
                case LastSoldState.Loading:
                    Grey("...");
                    Tooltip("Asking Universalis for the most recent sale...".Loc());
                    break;

                case LastSoldState.Failed:
                    Grey("!");
                    Tooltip("Universalis could not be reached. This item keeps its usual pricing until the lookup succeeds.".Loc());
                    break;

                default:
                    // Ready 但取不到有三種：①「不忽略優質」而這個品質沒有成交紀錄
                    // ②NoData＝完全沒有紀錄 ③有紀錄但全部來自被排除的世界。
                    // 🔴 ③ 要用不一樣的符號：三種都畫成 ? 的話，使用者無從知道
                    //    「把那個世界勾回來就有價了」。
                    if (row.SoldExcluded)
                    {
                        Grey("×");
                        Tooltip(
                            "The only sales on record for this item are on worlds you excluded, so no relist price is suggested. Untick that world in the price survey window to use them again."
                                .Loc());
                        break;
                    }

                    Grey("?");
                    Tooltip(
                        "No sale on record on the data centre, and no listing on your own world to compare against either, so relisting would leave this item alone."
                            .Loc());
                    break;
            }
        }

        /// <summary>
        /// 「目前最低價」那一格（本世界／整個資料中心各一欄）。
        /// 🔴 沒有資料一律畫成灰色的 <c>?</c>（查詢中是 <c>…</c>）——<b>絕不畫成 0</b>。
        /// 一個 0 gil 的最低價是合法但荒謬的數字，使用者會照它去決定要不要降價。
        /// ⚠️ 這一格照的是<b>這一格自己的品質</b>，與「忽略優質狀態」那個選項無關。
        /// </summary>
        private static void DrawMinPriceCell(in Row row, MarketPricePoint? point, bool forDatacentre)
        {
            if (point is { } value)
            {
                ImGui.TextUnformatted(DrawText.N0(value.UnitPrice));
                if (!ImGui.IsItemHovered())
                    return;

                var scope = forDatacentre
                    ? "the whole data centre".Loc()
                    : "this world".Loc();
                var where = string.IsNullOrEmpty(value.World) ? "?" : value.World;
                ShowTooltip(
                    "Lowest listing on ?? for this quality: ?? gil (??). Universalis data - only as fresh as the last upload."
                        .Loc(scope, value.UnitPrice.ToString("N0"), where));
                return;
            }

            DrawMissing(row,
                "Nobody has this listed there right now, as far as Universalis knows.".Loc());
        }

        /// <summary>
        /// 「最近一次成交」那一格：價格 ＋ 灰色的日期。
        /// 🔑 日期畫在<b>列上</b>而不是滑鼠提示：使用者要的就是「什麼時候賣掉的」，
        /// 一筆三個月前的成交價和昨天的成交價意義完全不同，而那件事不能藏起來。
        /// 📌 這個時間是 Universalis 記錄的<b>成交時間</b>（由玩家端上傳），
        /// 不是我們自己觀察到的「它從架上消失」的時間。
        /// </summary>
        private static void DrawLastSaleCell(in Row row)
        {
            if (!row.HasSold)
            {
                DrawMissing(row, "No sale on record for this item.".Loc(),
                    row.SoldExcluded);
                return;
            }

            ImGui.TextUnformatted(DrawText.N0(row.SoldUnitPrice));
            ImGui.SameLine();
            Grey(row.SoldAtUtc == DateTime.MinValue
                ? "?"
                : DrawText.LocalMinute(row.SoldAtUtc, "MM-dd"));
            if (!ImGui.IsItemHovered())
                return;

            var world = string.IsNullOrEmpty(row.SoldWorld) ? "?" : row.SoldWorld;
            ShowTooltip(
                "Sold for ?? gil on ?? (??, ??). This is the sale time Universalis recorded, not the moment we noticed it."
                    .Loc(row.SoldUnitPrice.ToString("N0"), BatchReprice.FormatSoldAt(row.SoldAtUtc),
                        row.SoldHq ? "HQ".Loc() : "NQ".Loc(), world));
        }

        /// <summary>
        /// 「已經掛多久了」那一格。
        /// 🔴 <b>精確值與暫定值必須看得出差別</b>：暫定值前面加上 <c>~</c>，理由寫在滑鼠提示裡。
        /// 遊戲不會告訴我們一筆掛售是什麼時候掛上去的，所以只有「我們親眼看著它出現」那一種
        /// 才是精確的；其餘是拿外掛載入時間暫定的。兩者長得一樣的話，使用者無從判斷該不該相信它。
        /// 🔴 完全沒有紀錄時是灰色的 <c>?</c>，<b>不是 0 也不是「剛剛」</b>。
        /// </summary>
        private static void DrawAgeCell(in Row row)
        {
            if (row.Age is not { } age)
            {
                Grey("?");
                Tooltip(
                    "Not recorded yet. Marketbuddy starts the clock the first time it sees this retainer's sell list."
                        .Loc());
                return;
            }

            var span = DateTime.UtcNow - age.SinceUtc;
            if (span < TimeSpan.Zero)
                span = TimeSpan.Zero;

            var text = FormatSpan(span);
            if (age.Exact)
            {
                ImGui.TextUnformatted(text);
                if (ImGui.IsItemHovered())
                    ShowTooltip("Listed since ?? (Marketbuddy watched it go up).".Loc(
                        age.SinceUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")));
                return;
            }

            Grey(DrawText.Concat("~", text));
            if (ImGui.IsItemHovered())
                ShowTooltip(
                    "Approximate: it was already listed when Marketbuddy first looked, so the clock starts from when the plugin loaded (??), not from when you actually listed it."
                        .Loc(age.SinceUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")));
        }

        /// <summary>
        /// 「這件東西以前賣掉過嗎」那一格：<c>賣出次數 / 沒賣掉就下架的次數</c>。
        /// 🔴 <b>四種「沒有數字」必須互相分得出來，而且一個都不可以畫成 0</b>：
        /// <para><list type="bullet">
        ///   <item><c>?</c>（記錄功能關著）＝沒有資料，而且不會有。</item>
        ///   <item><c>…</c>（還在讀紀錄）＝等一下就有。</item>
        ///   <item><c>?</c>（從沒看過這位僱員）＝<b>我們沒看過</b>，不是「賣不掉」。</item>
        ///   <item><c>—</c>（看過，但這件從來沒有異動）＝它就一直掛在那裡沒動過。</item>
        /// </list></para>
        /// <para>🔴 第二個數字裡<b>絕不含賣出</b>：它是「下架＋消失但對不上金幣」。信心不足的那些
        /// （<see cref="RetainerSaleConfidence.Unknown"/>）一律不算賣出，理由寫在滑鼠提示裡。</para>
        /// </summary>
        private void DrawHistoryCell(in Row row)
        {
            switch (historyState)
            {
                case SalesHistoryState.Disabled:
                    Grey("?");
                    Tooltip(
                        "\"Record what disappears from your retainers' listings\" is off, so there is nothing to count. This means \"not watched\", not \"never sold\"."
                            .Loc());
                    return;

                case SalesHistoryState.Loading:
                    Grey("...");
                    Tooltip("Reading the sales history...".Loc());
                    return;
            }

            var history = row.History;
            if (history.IsEmpty)
            {
                if (historyState == SalesHistoryState.NoBaseline)
                {
                    Grey("?");
                    Tooltip(
                        "Marketbuddy has never had this retainer's sell list open before, so nothing is known about this item yet. That is not the same as \"it never sells\"."
                            .Loc());
                    return;
                }

                Grey("—");
                if (ImGui.IsItemHovered())
                    ShowTooltip(
                        "Nothing has happened to this item in the records kept since ??: it has just been sitting on the board."
                            .Loc(FormatHistoryCoverage()));
                return;
            }

            ImGui.BeginGroup();
            if (history.SoldEvents > 0)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.HealerGreen);
                ImGui.TextUnformatted(DrawText.N0(history.SoldEvents));
                ImGui.PopStyleColor();
            }
            else
            {
                Grey("0");
            }

            ImGui.SameLine(0, 0);
            Grey(DrawText.N0(" / ", history.OffBoardEvents));
            ImGui.EndGroup();
            if (ImGui.IsItemHovered())
                ShowTooltip(BuildHistoryTooltip(row));
        }

        /// <summary>「賣出/下架」那一格的滑鼠提示。長說明放這裡，列上只留掃視得出來的兩個數字。</summary>
        private string BuildHistoryTooltip(in Row row)
        {
            var history = row.History;
            var lines = new List<string>
            {
                "Records from all your retainers since ??: sold ?? time(s), ?? item(s), ?? gil received."
                    .Loc(FormatHistoryCoverage(), history.SoldEvents, history.SoldQuantity,
                        history.SoldGil.ToString("N0")),
            };

            if (history.SoldGilIncomplete)
                lines.Add("Some of those sales have no amount, so the total is a lower bound.".Loc());

            if (history.LastSoldUtc != DateTime.MinValue)
                lines.Add("Last sold ??.".Loc(BatchReprice.FormatSoldAt(history.LastSoldUtc)));

            lines.Add(
                "Came off the board without a sale ?? time(s): ?? delisted by Marketbuddy, ?? that just went with no matching gil (deliberately never counted as sales)."
                    .Loc(history.OffBoardEvents, history.DelistedEvents, history.UnknownEvents));

            if (row.HistoryOtherQuality)
                lines.Add("The other quality of this item has records of its own; these numbers are for this quality only.".Loc());

            return string.Join("\n", lines);
        }

        /// <summary>觀察期：從什麼時候起算、到現在多久。🔑 少了它，「賣出 0 次」看不出可不可信。</summary>
        private string FormatHistoryCoverage()
        {
            if (historySinceUtc == DateTime.MinValue)
                return "?";

            var span = DateTime.UtcNow - historySinceUtc;
            if (span < TimeSpan.Zero)
                span = TimeSpan.Zero;

            var length = span.TotalDays >= 1
                ? "?? day(s)".Loc((int)span.TotalDays)
                : span.TotalHours >= 1
                    ? "?? hour(s)".Loc((int)span.TotalHours)
                    : "less than an hour".Loc();

            return "?? (??)".Loc(historySinceUtc.ToLocalTime().ToString("yyyy-MM-dd"), length);
        }

        /// <summary>把一段時間寫成「3天」「5小時」「12分」。刻意不進位到「1個月」——那太模糊。</summary>
        private static string FormatSpan(TimeSpan span)
        {
            if (span.TotalDays >= 1)
                return DrawText.Loc("??d", (int)span.TotalDays);
            if (span.TotalHours >= 1)
                return DrawText.Loc("??h", (int)span.TotalHours);
            return DrawText.Loc("??m", (int)span.TotalMinutes);
        }

        /// <summary>
        /// 「沒查過」「查詢中」「連不上」「沒有資料」「全部來自被排除的世界」五種缺席狀態的
        /// 統一畫法——五種都必須分得出來。
        /// </summary>
        /// <param name="excludedWorldOnly">
        /// 有紀錄，但能用的那幾筆全部落在排除清單上的世界。⚠️ 預設 false：
        /// 最低掛售價那兩欄刻意不套排除清單，所以它們不傳這個參數。
        /// </param>
        private static void DrawMissing(in Row row, string noDataTooltip,
            bool excludedWorldOnly = false)
        {
            switch (row.SoldState)
            {
                // 🔴 定價方式關著時 Unknown 是「終局」而不是「過程」：沒有任何一條路徑會去
                //    問 Universalis（理由寫在 Row.SoldLookupEnabled）。這裡畫「正在查…」
                //    的話，面板會永遠宣稱有一個不存在的查詢在跑。
                // 🔑 這一格與下面「問過了、但沒有紀錄」共用灰色的 ?：列上要傳達的是同一句
                //    「不知道」，而「為什麼不知道」照慣例放滑鼠提示。
                case LastSoldState.Unknown when !row.SoldLookupEnabled:
                    Grey("?");
                    Tooltip(
                        "Not looked up. These figures come from the Universalis request that the relist pricing sends, and that pricing is turned off - so nothing has been asked. Turn on 'Also price from the most recent sale, and use whichever is lower' to fill these in."
                            .Loc());
                    break;

                case LastSoldState.Unknown:
                case LastSoldState.Loading:
                    Grey("...");
                    Tooltip("Asking Universalis for the most recent sale...".Loc());
                    break;

                case LastSoldState.Failed:
                    Grey("!");
                    Tooltip("Universalis could not be reached. This item keeps its usual pricing until the lookup succeeds.".Loc());
                    break;

                default:
                    if (excludedWorldOnly)
                    {
                        Grey("×");
                        Tooltip(
                            "The only sales on record for this item are on worlds you excluded, so they are not shown. Untick that world in the price survey window to use them again."
                                .Loc());
                        break;
                    }

                    Grey("?");
                    Tooltip(noDataTooltip);
                    break;
            }
        }

        private static void Grey(string text)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextUnformatted(text);
            ImGui.PopStyleColor();
        }

        /// <summary>只給現成的字串用；要當場組的提示文字在呼叫端先判 <c>IsItemHovered</c>，沒指到就不組。</summary>
        private static void Tooltip(string text)
        {
            if (ImGui.IsItemHovered())
                ShowTooltip(text);
        }

        /// <summary>滑鼠提示一律全亮：淡色的列（看盤資料太舊）裡的說明也要讀得清楚。</summary>
        private static void ShowTooltip(string text)
        {
            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 1f);
            ImGui.SetTooltip(text);
            ImGui.PopStyleVar();
        }
    }
}
