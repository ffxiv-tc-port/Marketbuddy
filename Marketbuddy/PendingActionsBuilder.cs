using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using static Marketbuddy.Common.Dalamud;
using Placement = Marketbuddy.PriceSurveyItemSource.PriceSurveyPlacement;

namespace Marketbuddy
{
    /// <summary>
    /// 「待處理清單」的計算與操作驅動器。
    /// 🔴 <b>零自動化</b>。這個類別做的事只有三件，每一件都要有人按過按鈕：
    /// ①重算清單（純計算，不送任何市場查詢、不改任何價格）②把某一格交給<b>既有的</b>
    /// <see cref="BatchReprice.StartQuickReprice"/> ③把某一列標成已跳過。
    /// 「巡檢跑完自動重算」那個開關也只是①——它不會、也不能改任何價格。
    /// 執行緒（形狀比照 <see cref="LiveSellList"/>）：所有遊戲狀態都在
    /// <see cref="OnFrameworkUpdate"/> 上拍成快照，<b>繪製執行緒只讀快照</b>，
    /// 按鈕只立旗標。讀檔（巡檢記錄、InventoryTools 的 inventories.csv）一律在執行緒池上，
    /// 因為那兩個檔案都會長到幾十萬 bytes。
    /// 市場快取是一個裸 <c>Dictionary</c>，只有 framework 執行緒與封包處理器碰它——
    /// 從執行緒池讀它的失敗形式不是「拿到舊值」而是<b>字典本身壞掉</b>。
    /// </summary>
    internal sealed unsafe class PendingActionsBuilder : IDisposable
    {
        /// <summary>僱員市場容器的格數上限；索引兩端都要夾。</summary>
        private const int MaxMarketSlots = 20;

        private readonly MarketGuiEventHandler gui;

        private Configuration conf => Configuration.GetOrLoad();

        /// <summary>要重算了嗎（連同「是誰要求的」，只給診斷用）。</summary>
        private string? recomputeReason;

        private Task<Prepared>? prepareTask;
        private Request prepareRequest;

        /// <summary>使用者按下「重新定價」的那一格；-1＝沒有。</summary>
        private short repriceSlot = -1;

        private uint repriceItemId;
        private bool repriceHq;

        /// <summary>待處理分頁這一幀有沒有被畫出來。畫的時候立起，framework 執行緒消費掉。</summary>
        private volatile bool uiWanted;

        /// <summary>給繪製執行緒讀的清單快照。🔴 只在 framework 執行緒上換掉整個 list 實例。</summary>
        private volatile List<PendingActionRow> snapshot = [];

        internal IReadOnlyList<PendingActionRow> Snapshot => snapshot;

        internal bool IsRecomputing => prepareTask != null;

        internal DateTime LastComputedAt { get; private set; } = DateTime.MinValue;

        /// <summary>上一次重算讀進了幾列巡檢記錄；-1＝還沒算過。</summary>
        internal int LastSurveyRowsRead { get; private set; } = -1;

        /// <summary>畫面上那一行狀態字（已在地化）。</summary>
        internal string StatusText { get; private set; } = string.Empty;

        // ---- framework 執行緒拍的快照，繪製執行緒只讀 -------------------------

        internal ulong ActiveRetainerId { get; private set; }

        internal string ActiveRetainerName { get; private set; } = string.Empty;

        /// <summary>現在按「重新定價」按得動嗎。</summary>
        internal bool RepriceReady { get; private set; }

        /// <summary>按不動的原因（已在地化）。</summary>
        internal string RepriceBlockedReason { get; private set; } = string.Empty;

        internal string HomeWorldName { get; private set; } = string.Empty;

        internal uint HomeWorldId { get; private set; }

        /// <summary>
        /// 上一次重算時，<b>記錄檔裡真的有資料、卻因為在排除清單上
        /// 而沒有被拿來當建議價</b>的那些世界（名字，已排序）。
        /// </summary>
        /// <remarks>
        /// 🔑 <b>藏了東西就要說出來</b>：少掉一個世界的建議價，
        /// 與「那件東西真的沒人在賣」在畫面上長得一模一樣。
        /// 繪製執行緒只讀它；每次重算都是<b>整份換掉</b>，不會被就地改。
        /// </remarks>
        internal IReadOnlyList<string> ExcludedWorldsInLog { get; private set; } = [];

        public PendingActionsBuilder(MarketGuiEventHandler gui)
        {
            this.gui = gui;
            Framework.Update += OnFrameworkUpdate;
        }

        public void Dispose()
        {
            Framework.Update -= OnFrameworkUpdate;
        }

        // =====================================================================
        //  使用者意圖（繪製執行緒 → framework 執行緒）
        // =====================================================================

        /// <summary>待處理分頁正在被畫。只讓 framework 執行緒知道「現在需要快照」，沒有別的作用。</summary>
        internal void NoteUiVisible() => uiWanted = true;

        /// <summary>重算清單。純計算，不送任何查詢、不改任何價格。可以從任何執行緒呼叫。</summary>
        internal void RequestRecompute(string reason) => recomputeReason = reason;

        /// <summary>
        /// 把某一格交給既有的單格定價流程。<b>只立旗標</b>：真正的檢查與啟動都在
        /// framework 執行緒上，而且會先確認那一格現在真的還是這件道具。
        /// </summary>
        internal void RequestReprice(uint itemId, bool hq, short slot)
        {
            if (slot < 0 || slot >= MaxMarketSlots)
                return;
            repriceItemId = itemId;
            repriceHq = hq;
            repriceSlot = slot;
        }

        // =====================================================================
        //  幀時鐘
        // =====================================================================

        private void OnFrameworkUpdate(IFramework framework)
        {
            PendingActions.BeginLoad();
            PendingActions.PumpLoad();

            var wanted = uiWanted;
            uiWanted = false;
            if (wanted)
                UpdateUiSnapshot();

            ConsumeRepriceRequest();
            PumpRecompute();
        }

        /// <summary>
        /// 繪製執行緒要用的一切，全部在這裡拍好。
        /// 只在待處理分頁真的被畫出來的下一幀執行——沒人在看的時候不必每幀查僱員與 addon。
        /// </summary>
        private void UpdateUiSnapshot()
        {
            snapshot = PendingActions.Snapshot();

            ActiveRetainerId = BatchReprice.ActiveRetainerId();
            ActiveRetainerName = BatchReprice.RetainerNameOf(ActiveRetainerId);

            var home = PlayerState.HomeWorld;
            HomeWorldId = PlayerState.ContentId == 0 ? 0 : home.RowId;
            HomeWorldName = HomeWorldId == 0
                ? string.Empty
                : home.ValueNullable?.Name.ExtractText() ?? $"#{HomeWorldId}";

            var engine = gui.BatchEngine;
            if (engine == null)
            {
                RepriceReady = false;
                RepriceBlockedReason = "the relist engine is not available".Loc();
                return;
            }

            // requireListedItems: false —— 我們已經指名了某一格，那比僱員結構上會落後的
            // MarketItemCount 更權威（理由逐字寫在 BatchReprice.CanStart 那道閘門旁邊）。
            RepriceReady = engine.CanStart(out var why, requireListedItems: false);
            RepriceBlockedReason = RepriceReady ? string.Empty : why;
        }

        /// <summary>
        /// 消費「重新定價」的請求。
        /// 🔴 這裡<b>不自己改價</b>：確認那一格還是同一件道具之後，原封不動交給
        /// <see cref="BatchReprice.StartQuickReprice"/>——那條路徑會自己重新查一次行情、
        /// 套用使用者的降價設定、必要時依門檻下架。清單上那個「建議價」只是估計值。
        /// </summary>
        private void ConsumeRepriceRequest()
        {
            var slot = repriceSlot;
            if (slot < 0)
                return;
            repriceSlot = -1;

            var itemId = repriceItemId;
            var hq = repriceHq;

            var engine = gui.BatchEngine;
            if (engine == null)
            {
                StatusText = "the relist engine is not available".Loc();
                return;
            }

            var inventoryManager = InventoryManager.Instance();
            var live = inventoryManager == null
                ? null
                : inventoryManager->GetInventorySlot(InventoryType.RetainerMarket, slot);
            var liveHq = live != null && (live->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
            if (live == null || live->ItemId != itemId || liveHq != hq)
            {
                // 那一格已經不是清單上那件東西（賣掉了、下架了、換了位置）。
                // 這時候按下去會改到別的東西，所以擋下來並把那一列清掉。
                var retainerId = BatchReprice.ActiveRetainerId();
                PendingActions.ClearSlot(retainerId, slot);
                snapshot = PendingActions.Snapshot();
                StatusText = "Slot ?? no longer holds that item - the entry was removed.".Loc(slot + 1);
                Log.Information(
                    $"[Marketbuddy] 待處理：格 {slot} 已經不是道具 {itemId}（hq={hq}），該列已移除。");
                return;
            }

            if (engine.StartQuickReprice(slot))
            {
                StatusText = "Handed slot ?? to the relist engine.".Loc(slot + 1);
                return;
            }

            var reason = engine.LastStartRefusalReason;
            StatusText = reason.Length > 0
                ? "Cannot reprice right now: ??".Loc(reason)
                : "Cannot reprice right now.".Loc();
        }

        // =====================================================================
        //  重算（第一段：執行緒池讀檔；第二段：framework 執行緒組清單）
        // =====================================================================

        /// <summary>重算的輸入。全部是值，拍好之後執行緒池那一段就與遊戲狀態無關。</summary>
        private readonly record struct Request(
            uint HomeWorldId,
            string HomeWorldName,
            uint CurrentWorldId,
            int CacheSeconds,
            bool CompareHqOnly,
            bool UsePercent,
            int UndercutPercent,
            int UndercutAmount,
            int MinPrice,
            bool AnomalyEnabled,
            int AnomalyMinNormalPrice,
            int AnomalyRatio,
            string? CsvPath,
            IReadOnlySet<uint> ExcludedWorlds,
            bool UseLastSold,
            bool IgnoreQuality,
            int MaxSaleAgeDays);

        /// <summary>執行緒池那一段的產出：純資料，沒有任何遊戲指標。</summary>
        private sealed class Prepared
        {
            public required Dictionary<(uint ItemId, bool Hq, uint WorldId), PriceSurveyRow> Latest;
            public required List<Placement> CsvPlacements;
            public required int SurveyRowsRead;
            public required HashSet<uint> Worlds;

            /// <summary>世界 id → 記錄檔裡最後看到的名字；只給畫面用，比對一律用 id。</summary>
            public required Dictionary<uint, string> WorldNames;
        }

        private void PumpRecompute()
        {
            if (prepareTask == null)
            {
                if (recomputeReason is not { } reason)
                    return;
                recomputeReason = null;
                StartRecompute(reason);
                return;
            }

            var task = prepareTask;
            if (!task.IsCompleted)
                return;
            prepareTask = null;

            Prepared prepared;
            try
            {
                prepared = task.GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                Log.Information(e, "[Marketbuddy] 待處理清單：讀取重算資料時發生例外。");
                StatusText = "Could not read the survey log.".Loc();
                return;
            }

            var rows = Assemble(prepared, prepareRequest);
            ExcludedWorldsInLog = CollectExcludedWorldsInLog(prepared, prepareRequest);
            PendingActions.ReplaceComputed(rows);
            snapshot = PendingActions.Snapshot();
            LastComputedAt = DateTime.UtcNow;
            LastSurveyRowsRead = prepared.SurveyRowsRead;
            StatusText = "Recomputed: ?? entry(ies) from ?? survey row(s).".Loc(rows.Count, prepared.SurveyRowsRead);
            Log.Information(
                $"[Marketbuddy] 待處理清單重算完成：{rows.Count} 列（巡檢記錄 {prepared.SurveyRowsRead} 列，" +
                $"家世界 {prepareRequest.HomeWorldName}({prepareRequest.HomeWorldId})）。");
        }

        private void StartRecompute(string reason)
        {
            if (PlayerState.ContentId == 0)
            {
                StatusText = "Not logged in".Loc();
                return;
            }

            var home = PlayerState.HomeWorld;
            if (home.RowId == 0)
            {
                StatusText = "Not logged in".Loc();
                return;
            }

            var request = new Request(
                home.RowId,
                home.ValueNullable?.Name.ExtractText() ?? $"#{home.RowId}",
                PlayerState.CurrentWorld.RowId,
                conf.MarketDataCacheSeconds,
                conf.BatchCompareHqOnly,
                conf.UndercutUsePercent,
                conf.UndercutPercent,
                conf.UndercutPrice,
                conf.BatchMinPrice,
                // 🔴 拍成快照帶走：這一段之後會跑到執行緒池上，
                //    而設定物件是繪製執行緒在改的。
                conf.AnomalyGuardEnabled,
                conf.AnomalyGuardMinNormalPrice,
                conf.AnomalyGuardRatio,
                PriceSurveyItemSource.InventoryToolsCsvPath(),
                // 🔴 排除清單是一個裸 List，繪製執行緒（勾選框）會改它。
                //    這裡是 framework 執行緒，當場拍一份快照帶走，
                //    執行緒池那一段與組清單那一段都只讀快照。
                //    🔴 「自己掛售的那個世界」在這一步就被扣掉了（唯一實作點）。
                conf.BuildPricingExclusions(),
                // 拍成快照帶走（同上）：這三個決定「成交價這一側算不算數」。
                conf.RelistUseLastSoldPrice,
                conf.RelistLastSoldIgnoreQuality,
                conf.RelistLastSoldMaxAgeDays);

            prepareRequest = request;
            StatusText = "Recomputing...".Loc();
            Log.Information($"[Marketbuddy] 待處理清單開始重算（{reason}）。");
            // 🔴 兩個檔案都可能有幾十萬 bytes，一律在執行緒池上讀。
            prepareTask = Task.Run(() => PrepareOffThread(request));
        }

        private static Prepared PrepareOffThread(Request request)
        {
            var rows = PriceSurveyLog.LoadAll();
            var latest = new Dictionary<(uint, bool, uint), PriceSurveyRow>();
            var worlds = new HashSet<uint>();
            var worldNames = new Dictionary<uint, string>();
            foreach (var row in rows)
            {
                worlds.Add(row.WorldId);
                if (row.WorldName.Length > 0)
                    worldNames[row.WorldId] = row.WorldName;
                var key = (row.ItemId, row.Hq, row.WorldId);
                if (latest.TryGetValue(key, out var existing) && existing.AtUtc >= row.AtUtc)
                    continue;
                latest[key] = row;
            }

            return new Prepared
            {
                Latest = latest,
                CsvPlacements = PriceSurveyItemSource.ReadPlacementsFromCsv(request.CsvPath),
                SurveyRowsRead = rows.Count,
                Worlds = worlds,
                WorldNames = worldNames,
            };
        }

        /// <summary>建議價與它的出處。價格 -1＝沒有任何可用的參考價。</summary>
        /// <param name="Price">套過定價規則之後的建議掛售價。</param>
        /// <param name="Reference">算出 <paramref name="Price"/> 用的<b>原始</b>參考價；-1＝不知道。</param>
        /// <param name="PeerBaseline">
        /// 下一位賣家的最低價，異常低價保護要用（見 <see cref="PriceAnomalyGuard"/>）。
        /// 🔴 <b>只有 <c>live</c> 這條路算得出來</b>：巡檢記錄每個世界只存一個最低價、
        /// 沒有原始掛單，所以 <c>survey</c>／<c>survey-other</c> 一律 -1（＝不知道），
        /// 那兩條上的異常判定只能退回「拿自己的現價比」。
        /// </param>
        /// <param name="Held">
        /// 這個建議價被異常低價保護擋下來了（<see cref="Price"/> 因此是 -1）。
        /// </param>
        /// <param name="Anomaly">
        /// 上面那個判定的完整結果。⚠️ <b>只有 <paramref name="Held"/> 為 true 時才有意義</b>
        /// ——預設值是 <c>default</c> 而不是 <see cref="PriceAnomaly.Clear"/>（靜態欄位不能當
        /// 預設參數），兩者的 <c>IsAnomalous</c> 都是 false，但價格欄位是 0 不是 -1。
        /// </param>
        private readonly record struct Suggestion(long Price, string Source, string World, DateTime At,
            long Reference = -1, long PeerBaseline = -1, bool Held = false, PriceAnomaly Anomaly = default);

        /// <summary>
        /// 某一件道具（<b>某個品質</b>）的兩個定價候選的<b>原始觀測</b>。
        /// 🔑 刻意與「這一格目前掛多少」分開：觀測是<b>每個道具</b>算一次（同一件掛在好幾格
        /// 是常態），而異常低價保護的退路基準是<b>每一格自己</b>的現價。合在一起算的話
        /// 第一格的價格會被套到其他每一格身上，而那種錯是靜默的。
        /// </summary>
        private readonly record struct Observations(
            long ListingReference, long ListingPeer, string ListingSource, string ListingWorld,
            DateTime ListingAt,
            long SaleReference, long SalePeer, string SaleWorld, DateTime SaleAt,
            long OtherReference, string OtherSource, string OtherWorld, DateTime OtherAt);

        /// <summary>
        /// 巡檢記錄裡有資料、但被排除清單擋下來的那些世界的名字（已排序）。
        /// </summary>
        /// <remarks>
        /// 🔑 只列<b>記錄檔裡真的有</b>的世界：清單上勾著、卻從來沒掃過的
        /// 世界說出來只是雜訊。家世界不算——它從來就不受排除清單影響
        /// （見 <see cref="Observe"/>）。
        /// </remarks>
        private static List<string> CollectExcludedWorldsInLog(Prepared prepared, Request request)
        {
            var names = new List<string>();
            foreach (var worldId in prepared.Worlds)
            {
                if (worldId == request.HomeWorldId || !request.ExcludedWorlds.Contains(worldId))
                    continue;
                names.Add(prepared.WorldNames.TryGetValue(worldId, out var name) && name.Length > 0
                    ? name
                    : "#" + worldId);
            }

            names.Sort(StringComparer.Ordinal);
            return names;
        }

        /// <summary>
        /// 第二段：回到 framework 執行緒才組清單。
        /// 這裡才可以碰 <see cref="MarketDataCache"/>、僱員清單與 AllaganTools 的 IPC。
        /// </summary>
        private List<PendingActionRow> Assemble(Prepared prepared, Request request)
        {
            // 位置優先用 InventoryTools 的記錄檔（涵蓋所有角色），拿不到才退回只看得到
            // 目前角色的兩條路。位置查不到不是錯誤——那一列會以 RetainerId=0 / Slot=-1 存在，
            // UI 會把它畫成 ?，使用者仍然知道「這件被壓價了」，只是按鈕按不動。
            var placements = prepared.CsvPlacements;
            if (placements.Count == 0)
            {
                placements = PriceSurveyItemSource.ReadPlacementsFromAllaganTools();
                if (placements.Count == 0)
                    placements = PriceSurveyItemSource.ReadPlacementsFromSellList(gui);
            }

            var ownRetainers = new HashSet<ulong>();
            var names = new Dictionary<ulong, string>();
            foreach (var placement in placements)
            {
                if (placement.RetainerId != 0)
                    ownRetainers.Add(placement.RetainerId);
            }

            var retainerManager = RetainerManager.Instance();
            if (retainerManager != null)
            {
                foreach (var retainer in retainerManager->Retainers)
                {
                    if (retainer.RetainerId == 0)
                        continue;
                    ownRetainers.Add(retainer.RetainerId);
                    names[retainer.RetainerId] = retainer.NameString;
                }
            }

            var now = DateTime.UtcNow;
            // 記憶化的是**觀測**而不是建議價：同一件道具掛在好幾格是常態，而異常低價
            // 保護的退路基準是每一格自己的現價（見 BuildSuggestion）。
            var observations = new Dictionary<(uint ItemId, bool Hq), Observations>();
            var placed = new HashSet<(uint ItemId, bool Hq)>();
            var result = new List<PendingActionRow>();

            foreach (var placement in placements)
            {
                if (placement.RetainerId == 0 || placement.Slot < 0)
                    continue;
                var key = (placement.ItemId, placement.Hq);
                placed.Add(key);

                if (!observations.TryGetValue(key, out var observed))
                    observations[key] = observed = Observe(placement.ItemId, placement.Hq, request,
                        prepared, ownRetainers);

                var suggestion = BuildSuggestion(observed, placement.Price, request);
                prepared.Latest.TryGetValue((placement.ItemId, placement.Hq, request.HomeWorldId), out var homeRow);
                var kind = Classify(placement.Price, suggestion, request, homeRow, out var anomaly);
                if (kind == null)
                    continue;

                result.Add(BuildRow(now, kind.Value, placement.ItemId, placement.Hq,
                    placement.RetainerId, names.GetValueOrDefault(placement.RetainerId, string.Empty),
                    placement.Slot, placement.Price, suggestion, anomaly));
            }

            // 巡檢資料看得到、但我們完全找不到它掛在哪一格的那些。
            // 🔑 這種列刻意還是列出來：「不知道在哪」本身要看得見，不能因為按鈕按不動就藏起來。
            foreach (var ((itemId, hq, worldId), row) in prepared.Latest)
            {
                if (worldId != request.HomeWorldId)
                    continue;
                var key = (itemId, hq);
                if (placed.Contains(key))
                    continue;

                if (!observations.TryGetValue(key, out var observed))
                    observations[key] = observed = Observe(itemId, hq, request, prepared, ownRetainers);

                var suggestion = BuildSuggestion(observed, row.OurPrice, request);
                var kind = Classify(row.OurPrice, suggestion, request, row, out var anomaly);
                if (kind == null)
                    continue;

                result.Add(BuildRow(now, kind.Value, itemId, hq, 0, string.Empty, -1,
                    row.OurPrice, suggestion, anomaly));
            }

            return result;
        }

        /// <summary>
        /// 建議價的來源優先序（<b>這裡是唯一真值來源</b>）：
        /// <list type="number">
        ///   <item><b>L</b>（板上別人的最低掛售價）：
        ///     <list type="bullet">
        ///       <item><c>live</c>：本世界的即時市場快取（<see cref="MarketDataCache"/>）。
        ///             🔴 只有人現在就在家世界時才算數——那份快取是<b>綁世界</b>的，
        ///             在別的世界讀它拿到的是別的世界的行情。</item>
        ///       <item><c>survey</c>：巡檢記錄裡<b>家世界自己</b>那一列。</item>
        ///     </list>
        ///   </item>
        ///   <item><b>S</b>（<c>sale</c>）：Universalis 的「資料中心最近一次實際成交」。
        ///         ⚠️ <b>只讀已經抓回來的</b>——這裡不會發動任何 HTTP 查詢（清單重算可以由
        ///         「巡檢跑完自動重算」觸發，在那條路上發網路請求等於長出一條自動鏈）。
        ///         ⇒ 沒去過出售品清單的道具在這張清單上看不到成交價這一側，那是刻意的。</item>
        ///   <item><c>survey-other</c>：巡檢記錄裡<b>別的世界</b>最便宜的那一列。
        ///         🔴 那是另一個市場，<b>永遠不會變成 L</b>——別人在別的世界比我便宜不代表我在
        ///         自己的市場上吃虧。它只在 L 與 S 都沒有時當<b>純顯示</b>的最後估計值，
        ///         </item>
        /// </list>
        /// 全都沒有就回 -1（＝不知道）。<b>絕不回 0</b>：0 在價格欄是一個合法但荒謬的值。
        /// 🔑 <b>L 與 S 的「取低者」由 <see cref="RelistPricing.Decide"/> 決定，而那正是重掛
        /// 引擎用的同一支函式</b>——清單上寫的建議價與按下按鈕之後真的掛出去的價因此不可能分岔。
        /// </summary>
        private static Observations Observe(uint itemId, bool hq, Request request, Prepared prepared,
            HashSet<ulong> ownRetainers)
        {
            long listingRef = -1;
            long listingPeer = -1;
            var listingSource = string.Empty;
            var listingWorld = string.Empty;
            var listingAt = DateTime.MinValue;

            // ① 本世界的即時快取。🔴 只能在 framework 執行緒上讀（裸 Dictionary）。
            if (request.CurrentWorldId == request.HomeWorldId
                && MarketDataCache.TryGet(itemId, request.CacheSeconds, out var listings, out var ageMs))
            {
                var lowest = RelistPricing.LowestCompetitor(listings, hq, request.CompareHqOnly,
                    ownRetainers, out var peerBaseline, out _, out _);
                if (lowest >= 0)
                {
                    listingRef = lowest;
                    listingPeer = peerBaseline;
                    listingSource = PriceSourceTag.Live;
                    listingWorld = request.HomeWorldName;
                    listingAt = DateTime.UtcNow.AddMilliseconds(-Math.Max(0d, ageMs));
                }
            }

            // ② 巡檢記錄，家世界那一列。
            if (listingRef < 0
                && prepared.Latest.TryGetValue((itemId, hq, request.HomeWorldId), out var homeRow)
                && homeRow.Verdict == "ok" && homeRow.LowestIsOurs == 0 && homeRow.LowestForQuality >= 0)
            {
                listingRef = homeRow.LowestForQuality;
                listingSource = PriceSourceTag.Survey;
                listingWorld = homeRow.WorldName;
                listingAt = homeRow.AtUtc;
            }

            // S：資料中心最近一次實際成交。⚠️ 只讀快取，絕不在這裡發 HTTP 查詢。
            long saleRef = -1;
            long salePeer = -1;
            var saleWorld = string.Empty;
            var saleAt = DateTime.MinValue;
            if (request.UseLastSold
                && LastSoldPriceSource.TryGet(itemId, hq, request.IgnoreQuality, out var sold))
            {
                saleRef = sold.UnitPrice;
                saleAt = sold.SoldAtUtc;
                saleWorld = sold.World;
                // 異常低價保護在成交價這一側的同業基準＝本世界目前最低掛售價，
                // 與重掛引擎用的是同一個來源（理由見 BatchReprice.TickSaleCandidate）。
                salePeer = LastSoldPriceSource.TryGetMinPrices(itemId, hq, out var minWorld, out _)
                           && minWorld is { } minWorldPoint
                    ? minWorldPoint.UnitPrice
                    : -1L;
            }

            // ③ 巡檢記錄，別的世界最便宜的那一列（純顯示的最後手段）。
            long best = -1;
            var bestWorld = string.Empty;
            var bestAt = DateTime.MinValue;
            // 被排除的世界裡最便宜的那一筆。
            // 🔑 只拿來說明「為什麼沒有建議價」，絕不會變成建議價本身
            //    ——價格永遠是 -1。
            long excludedBest = -1;
            var excludedWorld = string.Empty;
            var excludedAt = DateTime.MinValue;
            foreach (var worldId in prepared.Worlds)
            {
                if (worldId == request.HomeWorldId)
                    continue;
                if (!prepared.Latest.TryGetValue((itemId, hq, worldId), out var row))
                    continue;
                if (row.Verdict != "ok" || row.LowestIsOurs != 0)
                    continue;
                var price = row.LowestForQuality;
                if (price < 0)
                    continue;
                if (request.ExcludedWorlds.Contains(worldId))
                {
                    // 排除清單上的世界：記下來只是為了讓畫面說得出原因，不參與比價。
                    if (excludedBest < 0 || price < excludedBest)
                    {
                        excludedBest = price;
                        excludedWorld = row.WorldName.Length > 0 ? row.WorldName : "#" + worldId;
                        excludedAt = row.AtUtc;
                    }

                    continue;
                }

                if (best >= 0 && price >= best)
                    continue;
                best = price;
                bestWorld = row.WorldName;
                bestAt = row.AtUtc;
            }

            // 🔑 「不知道」與「知道但故意不用」在畫面上必須分得出來。
            //    後者照樣回 -1（絕不回 0），但帶著出處，UI 才畫得出
            //    「只有某個世界有資料，而那個世界被你排除了」。
            var otherSource = best >= 0
                ? "survey-other"
                : excludedBest >= 0
                    ? "survey-excluded"
                    : string.Empty;

            return new Observations(
                listingRef, listingPeer, listingSource, listingWorld, listingAt,
                saleRef, salePeer, saleWorld, saleAt,
                best >= 0 ? best : -1,
                otherSource,
                best >= 0 ? bestWorld : excludedWorld,
                best >= 0 ? bestAt : excludedAt);
        }

        /// <summary>
        /// 把一件道具的觀測換算成<b>這一格</b>的建議價：兩候選各自過異常低價保護，再取低者。
        /// </summary>
        /// <param name="currentPrice">
        /// 這一格目前的掛售價；異常低價保護沒有同業基準時拿它當退路。-1＝不知道。
        /// </param>
        /// <remarks>
        /// 🔑 規則本體在 <see cref="RelistPricing"/>，與重掛引擎<b>共用同一支</b>。
        /// 這一段只負責「把該格自己的現價餵進去」與「兩個候選都沒有時退回純顯示的估計值」。
        /// </remarks>
        private static Suggestion BuildSuggestion(Observations observed, long currentPrice, Request request)
        {
            var undercut = new RelistPricing.UndercutRule(
                request.UsePercent, request.UndercutPercent, request.UndercutAmount);
            var anomalyRule = new RelistPricing.AnomalyRule(
                request.AnomalyEnabled, request.AnomalyMinNormalPrice, request.AnomalyRatio);

            var listing = RelistPricing.FromListing(
                observed.ListingReference, observed.ListingPeer, currentPrice,
                observed.ListingSource, observed.ListingAt, undercut, anomalyRule);
            var sale = RelistPricing.FromSale(
                observed.SaleReference, observed.SaleAt, observed.SalePeer, currentPrice,
                DateTime.UtcNow, request.MaxSaleAgeDays, anomalyRule);

            var decision = RelistPricing.Decide(listing, sale);
            switch (decision.Outcome)
            {
                case RelistOutcome.Price:
                {
                    var winner = decision.Winner;
                    var world = winner.Source == PriceSourceTag.Sale
                        ? observed.SaleWorld
                        : observed.ListingWorld;
                    return new Suggestion(decision.Price, winner.Source, world, winner.AtUtc,
                        winner.Reference, observed.ListingPeer);
                }

                case RelistOutcome.Hold:
                {
                    var winner = decision.Winner;
                    var world = winner.Source == PriceSourceTag.Sale
                        ? observed.SaleWorld
                        : observed.ListingWorld;
                    return new Suggestion(-1, winner.Source, world, winner.AtUtc,
                        winner.Reference, observed.ListingPeer, true, winner.Anomaly);
                }
            }

            // 兩個候選都沒有 ⇒ 退回純顯示的估計值。
            if (observed.OtherSource == "survey-other")
                return new Suggestion(
                    RelistPricing.ApplyUndercut(observed.OtherReference, undercut), "survey-other",
                    observed.OtherWorld, observed.OtherAt, observed.OtherReference);

            return observed.OtherSource.Length > 0
                ? new Suggestion(-1, observed.OtherSource, observed.OtherWorld, observed.OtherAt)
                : new Suggestion(-1, string.Empty, string.Empty, DateTime.MinValue);
        }

        /// <summary>
        /// 這一格屬於哪一個桶；不需要處理時回 null。
        /// 🔑 「該下架」優先於「被壓價」，因為<b>重掛引擎自己就是這個順序</b>
        /// （<c>FinishPricing</c> 的兩道下架門檻排在寫入新價之前）。清單跟著引擎走，
        /// 才不會出現「清單叫你改價、按下去卻被下架」這種互相矛盾的結果。
        /// ⚠️ 「該下架」刻意只認 <c>live</c>／<c>survey</c>／<c>sale</c> 這三種<b>引擎真的會
        /// 拿去定價</b>的參考價：
        /// 拿別的世界的行情去建議「把東西從市場上撤下來」是不成立的。
        /// </summary>
        private static PendingActionKind? Classify(long currentPrice, Suggestion suggestion, Request request,
            PriceSurveyRow homeRow, out PriceAnomaly anomaly)
        {
            // 🔴 異常低價排在最前面，而且理由與引擎逐字相同（BatchReprice.ApplySlot）：
            //    一個打錯的低價會同時長出「照它降價」與「低於最低價就該下架」兩個建議，
            //    而正確答案是兩件都不要做。判準與門檻見 PriceAnomalyGuard。
            // 判定由引擎用的同一支函式算好（RelistPricing.Decide，經 BuildSuggestion）。
            // BuildSuggestion 只對 live/survey/sale 這三種**引擎真的會拿去定價**的來源設 Held，
            // 所以 survey-other 不會憑空長出一列「已經替你擋下來了」——那裡什麼都沒發生過。
            anomaly = suggestion.Held ? suggestion.Anomaly : PriceAnomaly.Clear;
            if (anomaly.IsAnomalous)
                return PendingActionKind.PriceAnomaly;

            if (request.MinPrice > 0 && suggestion.Price >= 0
                && suggestion.Source is "live" or "survey" or PriceSourceTag.Sale
                && suggestion.Price < request.MinPrice)
                return PendingActionKind.BelowMinimum;

            // 被壓價的判準只看家世界那一列：別的世界比我便宜不代表我在自己的市場上吃虧。
            if (homeRow.Verdict != "ok" || homeRow.LowestIsOurs != 0)
                return null;
            var lowest = homeRow.LowestForQuality;
            if (lowest < 0 || currentPrice < 0 || currentPrice <= lowest)
                return null;
            return PendingActionKind.Undercut;
        }

        /// <summary>
        /// 把一列組出來。
        /// 🔴 「疑似打錯的低價」那一桶借用了兩個欄位（語意寫在
        /// <see cref="PendingActionKind.PriceAnomaly"/> 上）：<c>SuggestedPrice</c> 放的是
        /// <b>被擋下來的可疑價</b>而不是建議掛的價，<c>SuggestionWorld</c> 放的是拿來比的正常價。
        /// 這裡是唯一組出那種列的地方（引擎那側是 <c>BatchReprice.RecordAnomaly</c>），
        /// 兩處必須一致。
        /// </summary>
        private static PendingActionRow BuildRow(DateTime now, PendingActionKind kind, uint itemId, bool hq,
            ulong retainerId, string retainerName, short slot, long currentPrice,
            Suggestion suggestion, PriceAnomaly anomaly)
            => kind == PendingActionKind.PriceAnomaly
                ? new PendingActionRow(now, kind, itemId, hq, retainerId, retainerName, slot, currentPrice,
                    anomaly.Reference, anomaly.Tag, anomaly.NormalPrice.ToString("N0"), now, false)
                : new PendingActionRow(now, kind, itemId, hq, retainerId, retainerName, slot, currentPrice,
                    suggestion.Price, suggestion.Source, suggestion.World, suggestion.At, false);
    }
}
