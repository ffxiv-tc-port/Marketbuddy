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
    ///
    /// <para>
    /// 🔴 <b>零自動化</b>。這個類別做的事只有三件，每一件都要有人按過按鈕：
    /// ①重算清單（純計算，不送任何市場查詢、不改任何價格）②把某一格交給<b>既有的</b>
    /// <see cref="BatchReprice.StartQuickReprice"/> ③把某一列標成已跳過。
    /// 「巡檢跑完自動重算」那個開關也只是①——它不會、也不能改任何價格。
    /// </para>
    ///
    /// <para>
    /// 執行緒（形狀比照 <see cref="LiveSellList"/>）：所有遊戲狀態都在
    /// <see cref="OnFrameworkUpdate"/> 上拍成快照，<b>繪製執行緒只讀快照</b>，
    /// 按鈕只立旗標。讀檔（巡檢記錄、InventoryTools 的 inventories.csv）一律在執行緒池上，
    /// 因為那兩個檔案都會長到幾十萬 bytes。
    /// </para>
    ///
    /// <para>
    /// 🔑 <b>兩段式重算</b>：第一段在執行緒池上把兩個檔案讀成純資料，第二段回到
    /// framework 執行緒才去碰 <see cref="MarketDataCache"/>、僱員清單與 AllaganTools 的 IPC。
    /// 市場快取是一個裸 <c>Dictionary</c>，只有 framework 執行緒與封包處理器碰它——
    /// 從執行緒池讀它的失敗形式不是「拿到舊值」而是<b>字典本身壞掉</b>。
    /// </para>
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
            string? CsvPath);

        /// <summary>執行緒池那一段的產出：純資料，沒有任何遊戲指標。</summary>
        private sealed class Prepared
        {
            public required Dictionary<(uint ItemId, bool Hq, uint WorldId), PriceSurveyRow> Latest;
            public required List<Placement> CsvPlacements;
            public required int SurveyRowsRead;
            public required HashSet<uint> Worlds;
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
                PriceSurveyItemSource.InventoryToolsCsvPath());

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
            foreach (var row in rows)
            {
                worlds.Add(row.WorldId);
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
            };
        }

        /// <summary>建議價與它的出處。價格 -1＝沒有任何可用的參考價。</summary>
        private readonly record struct Suggestion(long Price, string Source, string World, DateTime At);

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
            var suggestions = new Dictionary<(uint ItemId, bool Hq), Suggestion>();
            var placed = new HashSet<(uint ItemId, bool Hq)>();
            var result = new List<PendingActionRow>();

            foreach (var placement in placements)
            {
                if (placement.RetainerId == 0 || placement.Slot < 0)
                    continue;
                var key = (placement.ItemId, placement.Hq);
                placed.Add(key);

                if (!suggestions.TryGetValue(key, out var suggestion))
                    suggestions[key] = suggestion = ResolveSuggestion(placement.ItemId, placement.Hq, request,
                        prepared, ownRetainers);

                prepared.Latest.TryGetValue((placement.ItemId, placement.Hq, request.HomeWorldId), out var homeRow);
                var kind = Classify(placement.Price, suggestion, request, homeRow);
                if (kind == null)
                    continue;

                result.Add(new PendingActionRow(now, kind.Value, placement.ItemId, placement.Hq,
                    placement.RetainerId, names.GetValueOrDefault(placement.RetainerId, string.Empty),
                    placement.Slot, placement.Price, suggestion.Price, suggestion.Source, suggestion.World,
                    suggestion.At, false));
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

                if (!suggestions.TryGetValue(key, out var suggestion))
                    suggestions[key] = suggestion = ResolveSuggestion(itemId, hq, request, prepared, ownRetainers);

                var kind = Classify(row.OurPrice, suggestion, request, row);
                if (kind == null)
                    continue;

                result.Add(new PendingActionRow(now, kind.Value, itemId, hq, 0, string.Empty, -1,
                    row.OurPrice, suggestion.Price, suggestion.Source, suggestion.World, suggestion.At, false));
            }

            return result;
        }

        /// <summary>
        /// 建議價的來源優先序（<b>這裡是唯一真值來源</b>）：
        /// <list type="number">
        ///   <item><c>live</c>：本世界的即時市場快取（<see cref="MarketDataCache"/>）。
        ///         🔴 只有人現在就在家世界時才算數——那份快取是<b>綁世界</b>的，
        ///         在別的世界讀它拿到的是別的世界的行情。</item>
        ///   <item><c>survey</c>：巡檢記錄裡<b>家世界自己</b>那一列。</item>
        ///   <item><c>survey-other</c>：巡檢記錄裡別的世界最便宜的那一列。
        ///         🔴 那是另一個市場，只能當參考——UI 必須把世界名與日期畫在列上。</item>
        /// </list>
        /// 三條都沒有就回 -1（＝不知道）。<b>絕不回 0</b>：0 在價格欄是一個合法但荒謬的值。
        /// <para>
        /// 📌 沒有 Universalis 這一條：本外掛對使用者的承諾是「不自己連任何網站」
        /// （巡檢分頁上那一句），而艦隊裡唯一有 Universalis 客戶端的 PriceInsight
        /// 沒有開任何 IPC 端點（2026-09-08 實查）。要加只能等對方開端點。
        /// </para>
        /// </summary>
        private static Suggestion ResolveSuggestion(uint itemId, bool hq, Request request, Prepared prepared,
            HashSet<ulong> ownRetainers)
        {
            // ① 本世界的即時快取。🔴 只能在 framework 執行緒上讀（裸 Dictionary）。
            if (request.CurrentWorldId == request.HomeWorldId
                && MarketDataCache.TryGet(itemId, request.CacheSeconds, out var listings, out var ageMs))
            {
                var lowest = LowestCompetitor(listings, hq, request.CompareHqOnly, ownRetainers);
                if (lowest >= 0)
                    return new Suggestion(ApplyUndercut(lowest, request), "live", request.HomeWorldName,
                        DateTime.UtcNow.AddMilliseconds(-Math.Max(0d, ageMs)));
            }

            // ② 巡檢記錄，家世界那一列。
            if (prepared.Latest.TryGetValue((itemId, hq, request.HomeWorldId), out var homeRow)
                && homeRow.Verdict == "ok" && homeRow.LowestIsOurs == 0 && homeRow.LowestForQuality >= 0)
                return new Suggestion(ApplyUndercut(homeRow.LowestForQuality, request), "survey",
                    homeRow.WorldName, homeRow.AtUtc);

            // ③ 巡檢記錄，別的世界最便宜的那一列。
            long best = -1;
            var bestWorld = string.Empty;
            var bestAt = DateTime.MinValue;
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
                if (best >= 0 && price >= best)
                    continue;
                best = price;
                bestWorld = row.WorldName;
                bestAt = row.AtUtc;
            }

            return best >= 0
                ? new Suggestion(ApplyUndercut(best, request), "survey-other", bestWorld, bestAt)
                : new Suggestion(-1, string.Empty, string.Empty, DateTime.MinValue);
        }

        /// <summary>
        /// 這一頁掛單裡<b>別人</b>的最低單價；沒有別人在賣時回 -1。
        ///
        /// <para>
        /// ⚠️ 與 <c>BatchReprice.ApplySlot</c> 的差別是刻意的：那裡取整頁最低價之後才判斷
        /// 「這個最低價是不是我自己的」，因為它要決定的是「要不要降價」；這裡要的是
        /// 「別人開多少」，所以一開始就把自家僱員的掛單排除掉。兩者都不會自己壓自己的價。
        /// </para>
        ///
        /// HQ／NQ 的取捨逐字比照重掛：這一格是優質品、使用者開著「只比優質品」、
        /// 而且這一頁真的有優質品掛單時，才只看優質品。
        /// </summary>
        private static long LowestCompetitor(List<(uint Price, bool IsHq, ulong RetainerId)> listings, bool hq,
            bool compareHqOnly, HashSet<ulong> ownRetainers)
        {
            var hqOnly = false;
            if (hq && compareHqOnly)
            {
                foreach (var listing in listings)
                {
                    if (!listing.IsHq)
                        continue;
                    hqOnly = true;
                    break;
                }
            }

            long lowest = -1;
            foreach (var listing in listings)
            {
                if (hqOnly && !listing.IsHq)
                    continue;
                if (ownRetainers.Contains(listing.RetainerId))
                    continue;
                if (lowest >= 0 && listing.Price >= lowest)
                    continue;
                lowest = listing.Price;
            }

            return lowest;
        }

        /// <summary>
        /// 把參考價換算成建議掛售價。算式與 <c>BatchReprice.ApplySlot</c> 逐字相同
        /// （百分比模式用浮點乘法，絕不用整數除法），所以清單上的估計值與真的按下去之後
        /// 引擎算出來的價是同一套規則。
        /// </summary>
        private static long ApplyUndercut(long reference, Request request)
        {
            var target = request.UsePercent
                ? (long)(reference * (1f - request.UndercutPercent / 100f))
                : reference - request.UndercutAmount;
            return Math.Clamp(target, Configuration.MIN_PRICE, Configuration.MAX_PRICE);
        }

        /// <summary>
        /// 這一格屬於哪一個桶；不需要處理時回 null。
        ///
        /// <para>
        /// 🔑 「該下架」優先於「被壓價」，因為<b>重掛引擎自己就是這個順序</b>
        /// （<c>FinishPricing</c> 的兩道下架門檻排在寫入新價之前）。清單跟著引擎走，
        /// 才不會出現「清單叫你改價、按下去卻被下架」這種互相矛盾的結果。
        /// </para>
        ///
        /// <para>
        /// ⚠️ 「該下架」刻意只認 <c>live</c>／<c>survey</c> 這兩種<b>本世界</b>的參考價：
        /// 拿別的世界的行情去建議「把東西從市場上撤下來」是不成立的。
        /// </para>
        /// </summary>
        private static PendingActionKind? Classify(long currentPrice, Suggestion suggestion, Request request,
            PriceSurveyRow homeRow)
        {
            if (request.MinPrice > 0 && suggestion.Price >= 0
                && suggestion.Source is "live" or "survey"
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
    }
}
