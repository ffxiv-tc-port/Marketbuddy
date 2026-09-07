using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dalamud.Game.Network.Structures;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    /// <summary>
    /// 跨世界掛售價格巡檢。
    ///
    /// <para>
    /// 「帶著自己正在賣的清單，站在某一個世界的市場前面，把每一件的行情逐一問過一遍並記下來」。
    /// 跑完一個世界就停；要換世界是**另一顆按鈕**，換到之後仍然要使用者再按一次「掃描這個世界」。
    /// </para>
    ///
    /// <para>
    /// 🔴 <b>一律手動觸發。</b>唯一的入口是巡檢視窗上的按鈕（<see cref="RequestStart"/>）。
    /// 這個類別**沒有**訂閱 AutoRetainer 的任何事件、**沒有**訂閱任何 addon 生命週期事件，
    /// 也**不會**在 <c>Framework.Update</c> 裡因為任何遊戲狀態自己開始跑
    /// （<see cref="OnFrameworkUpdate"/> 在閒置時的唯一副作用是把 <see cref="startRequested"/>
    /// 這個「使用者按了按鈕」的旗標消費掉）。
    /// </para>
    ///
    /// <para>
    /// 🔴 <b>整條路徑只讀不寫。</b>它只呼叫
    /// <c>InfoProxyItemSearch.RequestData()</c>（＝遊戲自己的市場查詢，跟玩家開市場板搜尋
    /// 送的是同一個請求）並接收 <c>IMarketBoard.OfferingsReceived</c>。
    /// 沒有 <c>SetRetainerMarketPrice</c>、沒有任何 <c>InventoryManager</c> 寫入、
    /// 沒有任何掛售／改價／下架動作，也不碰 <see cref="BatchReprice"/>／
    /// <see cref="BatchDelist"/>／<see cref="MultiCharacterTour"/> 的狀態。
    /// </para>
    ///
    /// <para>
    /// 🔴 <b>節流沿用全外掛共用的 <see cref="MarketRequestGate"/></b>（同一個 static 實例，
    /// 不另開更短的路徑）：每一件之間至少隔閘門當下的間隔，而且每一次送出都記進閘門，
    /// 所以巡檢跟批次改價、跟互動視窗的重查共用同一份「上次送出是什麼時候」。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>未經實機證實的前提</b>：本功能假設「站在市場前面、**沒有**開啟僱員出售品視窗」
    /// 時 <c>RequestData()</c> 仍然送得出去。假設不成立時的表現被刻意設計成
    /// 「只是查不到東西」而不是崩潰：不對回應解參任何指標、不假設 <c>ListingCount</c>
    /// 會被寫、逾時走既有的重試路徑；而且**第一件連續逾時 3 次就停下來**並在畫面與
    /// 記錄檔上明說「這個情境送不出查詢」，不會傻跑幾百件。
    /// </para>
    /// </summary>
    internal sealed unsafe class PriceSurvey : IDisposable
    {
        // ---- 節奏常數 ---------------------------------------------------------
        // 🔑 這些值刻意與 BatchReprice 對齊（那組是 2026-08-02/03 兩輪實機量測定下來的），
        //    只有嘗試次數放少：巡檢沒有「這一格一定要有答案」的壓力，問不到就記一筆
        //    「問不到」往下走，比反覆重試更誠實也更快。
        private const int NoResponseDeadlineMs = 2500; // 什麼封包都沒來 => 大概被吞掉了
        private const int ResponseTimeoutMs = 3500;    // 有東西來了之後的硬上限
        private const int EmptyResultGraceMs = 1000;   // history 來了、offerings 沒來 => 沒人在賣
        private const int RetryBackoffBaseMs = 300;
        private const int RetryBackoffCapMs = 1200;
        private const int MaxAttempts = 3;

        /// <summary>第一件連續這麼多次「什麼都沒回來」就判定這個情境送不出查詢，整輪停下。</summary>
        private const int FirstItemGiveUpAfter = 3;

        private const string Diag = "[MBDIAG]";

        internal enum SurveyState
        {
            Idle,
            Preparing,
            Running,
        }

        private enum ItemPhase
        {
            Throttle,
            Request,
            Wait,
        }

        private readonly MarketGuiEventHandler gui;

        // ---- 使用者按鈕交過來的意圖（唯一入口）--------------------------------
        private bool startRequested;
        private string? stopRequested;

        // ---- 準備階段 --------------------------------------------------------
        private Task<PrepareResult>? prepareTask;

        // ---- 一輪的狀態 ------------------------------------------------------
        private PriceSurveyItemList? list;
        private readonly List<uint> itemQueue = [];
        private readonly Dictionary<uint, List<PriceSurveyItem>> byItemId = new();
        private int queueIndex;
        private uint worldId;
        private string worldName = string.Empty;
        private DateTime runStartedAt;
        private int recordedRows;
        private int okCount, emptyCount, refusedCount, timeoutCount, cacheCount;
        private int skippedAlreadyDone;

        // ---- 目前這一件 -----------------------------------------------------
        private ItemPhase phase;
        private uint currentItemId;
        private int attempt;
        private DateTime notBefore;
        private DateTime waitStart;
        private double sendGapMs = -1;
        private bool probeAnswered, probeRefused, probeEmpty;
        private int firstItemNoResponseStreak;

        // ---- 換世界（Lifestream）------------------------------------------
        // 🔴 這一整組刻意與掃描完全分離：換世界不會開始掃描、掃描也不會換世界。
        //    「到了下一個世界」之後仍然要使用者自己再按一次「掃描這個世界」。
        private string? travelRequestWorld;
        private readonly List<(uint WorldId, string Name)> travelTargets = [];
        private uint travelTargetsBuiltFor = uint.MaxValue;
        private bool lifestreamMissing;

        // ---- 市場封包接收（形狀逐字比照 BatchReprice，只是不驅動任何改價）------
        private readonly List<(uint Price, bool IsHq, ulong RetainerId)> captured = [];
        private bool offeringsPending;
        private bool offeringsReceived;
        private bool historySeen;
        private DateTime historySeenAt;
        private uint pendingItemId;
        private int lastAcceptedRequestId = int.MinValue;

        private Configuration conf => Configuration.GetOrLoad();

        internal SurveyState State { get; private set; } = SurveyState.Idle;

        /// <summary>其他引擎的互斥判準：巡檢在跑的時候大家讓開。</summary>
        internal bool IsRunning => State != SurveyState.Idle;

        internal int Total => itemQueue.Count;
        internal int Processed => queueIndex;
        internal uint CurrentItemId => currentItemId;
        internal uint WorldId => worldId;
        internal string WorldName => worldName;
        internal int RecordedRows => recordedRows;
        internal int SkippedAlreadyDone => skippedAlreadyDone;
        internal string SourceLabel => list?.SourceLabel ?? string.Empty;

        /// <summary>
        /// 目前所在世界的名稱，<b>在 framework 執行緒上拍好的快照</b>。
        /// 🔴 繪製執行緒不可以自己去讀 <c>PlayerState</c>：那是原生指標解參
        /// （<c>PlayerState.Instance()</c> / 本地玩家實體），由遊戲主執行緒每幀重建，
        /// 讀到一半被換掉就是 AccessViolationException，而 AVE 在 .NET Core 是
        /// corrupted-state exception，<c>try</c>/<c>catch</c> 攔不到。所以畫面一律讀這份快照。
        /// </summary>
        internal string CurrentWorldName { get; private set; } = string.Empty;

        /// <summary>目前所在世界的 id（framework 執行緒快照，0＝還沒登入）。</summary>
        internal uint CurrentWorldId { get; private set; }

        /// <summary>已經進到遊戲世界裡了（framework 執行緒快照）。</summary>
        internal bool LoggedIn { get; private set; }

        /// <summary>畫面上那一行狀態文字；閒置時是上一輪的結果或失敗原因。</summary>
        internal string StatusText { get; private set; } = string.Empty;

        /// <summary>
        /// 上一輪停下來的原因是「這個情境送不出查詢」。畫面用它顯示那句具體的建議。
        /// </summary>
        internal bool LastRunLookedUnsupported { get; private set; }

        /// <summary>同一個資料中心裡、Lifestream 說去得了的世界（不含目前這一個）。</summary>
        internal IReadOnlyList<(uint WorldId, string Name)> TravelTargets => travelTargets;

        /// <summary>問過 Lifestream 但它不在（或 IPC 還沒好）。畫面要說的話與「沒有可去的世界」不同。</summary>
        internal bool LifestreamMissing => lifestreamMissing;

        /// <summary>上一次換世界請求的結果，顯示在按鈕旁邊。</summary>
        internal string TravelStatus { get; private set; } = string.Empty;

        internal int OkCount => okCount;
        internal int EmptyCount => emptyCount;
        internal int RefusedCount => refusedCount;
        internal int TimeoutCount => timeoutCount;
        internal int CacheCount => cacheCount;

        /// <summary>剩下的預估秒數（用閘門當下的間隔算）。還沒開始跑時 -1。</summary>
        internal double EstimatedRemainingSeconds
        {
            get
            {
                if (State != SurveyState.Running)
                    return -1;
                var remaining = itemQueue.Count - queueIndex;
                if (remaining <= 0)
                    return 0;
                return remaining * MarketRequestGate.IntervalMs / 1000.0;
            }
        }

        public PriceSurvey(MarketGuiEventHandler gui)
        {
            this.gui = gui;
            MarketBoard.OfferingsReceived += OnOfferingsReceived;
            MarketBoard.HistoryReceived += OnHistoryReceived;
            Framework.Update += OnFrameworkUpdate;
        }

        public void Dispose()
        {
            Framework.Update -= OnFrameworkUpdate;
            MarketBoard.HistoryReceived -= OnHistoryReceived;
            MarketBoard.OfferingsReceived -= OnOfferingsReceived;
            // 卸載時安靜地收手；不重設 InfoProxy 以外的任何東西。
            if (State != SurveyState.Idle)
                ResetRun();
        }

        // =====================================================================
        //  使用者意圖（UI 執行緒 → framework 執行緒）
        // =====================================================================

        /// <summary>
        /// 🔴 <b>這是本功能唯一的啟動入口</b>，只由巡檢視窗上的按鈕呼叫。
        /// 這裡只立一個旗標，真正的檢查與啟動全部發生在 framework 執行緒上。
        /// </summary>
        internal void RequestStart()
        {
            if (IsRunning)
                return;
            startRequested = true;
        }

        /// <summary>
        /// 🔴 <b>「去下一個世界」的唯一入口</b>，只由巡檢視窗上那顆按鈕呼叫，
        /// <b>按一次只換一次</b>：這裡只記下一個目的地，framework 執行緒上消費掉之後就清空，
        /// 沒有重試、沒有佇列、也沒有「到了就自動開始掃描」的串接。
        /// </summary>
        internal void RequestChangeWorld(string world)
        {
            if (IsRunning || string.IsNullOrWhiteSpace(world))
                return;
            travelRequestWorld = world;
            TravelStatus = string.Empty;
        }

        /// <summary>取消。按鈕、關視窗、以及任何讓路條件都走這裡。</summary>
        internal void RequestStop(string reason)
        {
            if (!IsRunning)
                return;
            stopRequested = reason;
        }

        /// <summary>
        /// 現在能不能開始。回 false 時 <paramref name="reason"/> 是給使用者看的一句話。
        /// </summary>
        internal bool CanStart(out string reason)
        {
            reason = string.Empty;
            if (IsRunning)
            {
                reason = "A price survey is already running".Loc();
                return false;
            }

            if (!conf.PriceSurveyEnabled)
            {
                reason = "This feature is not enabled yet".Loc();
                return false;
            }

            if (IPCManager.IsLocked)
            {
                reason = "locked via IPC by another plugin".Loc();
                return false;
            }

            if (AutoRetainerBridge.IsBusy)
            {
                reason = "AutoRetainer is busy (or MultiMode is enabled), stop it first".Loc();
                return false;
            }

            if (gui.BatchEngine?.IsRunning == true)
            {
                reason = "A relist is running".Loc();
                return false;
            }

            if (gui.DelistEngine?.IsRunning == true)
            {
                reason = "A delist is running".Loc();
                return false;
            }

            // 🔴 這個方法每一幀都被繪製執行緒呼叫（按鈕要不要變灰），所以只讀
            //    framework 執行緒拍好的快照，不直接碰 PlayerState 的原生指標。
            if (!LoggedIn || CurrentWorldId == 0)
            {
                reason = "Not logged in".Loc();
                return false;
            }

            return true;
        }

        // =====================================================================
        //  幀時鐘
        // =====================================================================

        private void OnFrameworkUpdate(IFramework framework)
        {
            UpdateWorldSnapshot();

            if (State == SurveyState.Idle)
            {
                TickIdle();
                return;
            }

            startRequested = false;

            if (stopRequested is { } reason)
            {
                stopRequested = null;
                Finish(reason, unsupported: false);
                return;
            }

            // 讓路條件：任何一個成立就立刻收手。
            if (CheckStandDown(out var standDownReason))
            {
                Finish(standDownReason, unsupported: false);
                return;
            }

            switch (State)
            {
                case SurveyState.Preparing:
                    TickPreparing();
                    return;
                case SurveyState.Running:
                    TickRunning(DateTime.UtcNow);
                    return;
            }
        }

        /// <summary>
        /// 閒置時的一格。
        /// 🔴 這裡會做的事只有三件，而且每一件都要有使用者按過按鈕才會發生：
        /// 消費「開始掃描」旗標、消費「去某個世界」旗標、以及在所在世界變了之後
        /// 重建可前往世界的清單（那只是一份下拉選單的內容，不會讓任何事情開始跑）。
        /// </summary>
        private void TickIdle()
        {
            if (CurrentWorldId != 0 && CurrentWorldId != travelTargetsBuiltFor)
                RebuildTravelTargets(CurrentWorldId);

            if (travelRequestWorld is { } destination)
            {
                travelRequestWorld = null;
                ChangeWorldOnce(destination);
            }

            // 🔴 沒有任何遊戲狀態可以讓巡檢自己開始跑：只有這個旗標。
            if (!startRequested)
                return;
            startRequested = false;
            BeginRun();
        }

        /// <summary>
        /// 重建「這裡去得了哪些世界」的下拉選單內容。
        /// 候選來自 World 表裡同一個資料中心的列，再逐一問 Lifestream 去不去得了——
        /// 🔑 刻意<b>不</b>自己維護一份世界名單，也不靠 <c>IsPublic</c>（台服八個正式世界
        /// 的 <c>IsPublic</c> 全是 false，照它篩會得到空清單）。Lifestream 才是
        /// 「這個角色現在去得了哪裡」的權威。
        /// </summary>
        private void RebuildTravelTargets(uint currentWorldId)
        {
            travelTargets.Clear();
            travelTargetsBuiltFor = currentWorldId;
            lifestreamMissing = false;

            var currentRow = PlayerState.CurrentWorld.ValueNullable;
            if (currentRow == null)
            {
                // 讀不到就當作還沒建起來，下一格再試。
                travelTargetsBuiltFor = uint.MaxValue;
                return;
            }

            var dataCentre = currentRow.Value.DataCenter.RowId;
            var sheet = DataManager.GetExcelSheet<World>();
            if (sheet == null)
                return;

            foreach (var row in sheet)
            {
                if (row.RowId == currentWorldId || row.DataCenter.RowId != dataCentre)
                    continue;
                var name = row.Name.ExtractText();
                if (name.Length == 0)
                    continue;

                var reachable = IPCManager.CanLifestreamVisit(name);
                if (reachable == null)
                {
                    // 端點不存在 ⇒ Lifestream 沒裝。不必再問剩下的世界。
                    lifestreamMissing = true;
                    travelTargets.Clear();
                    return;
                }

                if (reachable == true)
                    travelTargets.Add((row.RowId, name));
            }

            travelTargets.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        }

        /// <summary>
        /// 🔴 <b>按一次只換一次。</b>沒有重試、沒有排程，抵達之後也不會自己開始掃描。
        /// </summary>
        private void ChangeWorldOnce(string destination)
        {
            if (IPCManager.IsLocked)
            {
                TravelStatus = "Halted by another plugin via IPC".Loc();
                return;
            }

            if (AutoRetainerBridge.IsBusy)
            {
                TravelStatus = "AutoRetainer is busy (or MultiMode is enabled), stop it first".Loc();
                return;
            }

            if (IPCManager.IsLifestreamBusy())
            {
                TravelStatus = "Lifestream is busy right now.".Loc();
                return;
            }

            var accepted = IPCManager.LifestreamChangeWorld(destination);
            TravelStatus = accepted
                ? "Asked Lifestream to travel to ??. Press Scan this world again once you get there.".Loc(destination)
                : "Lifestream did not accept that request (it may be busy, or that world is not reachable right now).".Loc();
            Log.Information(
                $"[Marketbuddy] 巡檢：向 Lifestream 請求前往 {destination}，接受={accepted}。" +
                "這次呼叫只換一次世界，抵達之後不會自己開始掃描。");
        }

        /// <summary>
        /// 把「我在哪個世界、有沒有登入」拍成快照給繪製執行緒用。
        /// 🔴 只在這裡（framework 執行緒）讀 <c>PlayerState</c>。
        /// </summary>
        private void UpdateWorldSnapshot()
        {
            LoggedIn = PlayerState.ContentId != 0;
            var world = PlayerState.CurrentWorld;
            CurrentWorldId = world.RowId;
            CurrentWorldName = CurrentWorldId == 0
                ? string.Empty
                : world.ValueNullable?.Name.ExtractText() ?? $"#{CurrentWorldId}";
        }

        private bool CheckStandDown(out string reason)
        {
            reason = string.Empty;

            if (IPCManager.IsLocked)
            {
                reason = "Halted by another plugin via IPC".Loc();
                return true;
            }

            if (AutoRetainerBridge.IsBusy)
            {
                reason = "AutoRetainer started working, the survey stands down".Loc();
                return true;
            }

            if (gui.BatchEngine?.IsRunning == true || gui.DelistEngine?.IsRunning == true)
            {
                reason = "A relist or delist started, the survey stands down".Loc();
                return true;
            }

            if (!LoggedIn)
            {
                reason = "Left the game world".Loc();
                return true;
            }

            if (State == SurveyState.Running && CurrentWorldId != worldId)
            {
                reason = "The world changed, this round stops here".Loc();
                return true;
            }

            return false;
        }

        // =====================================================================
        //  準備
        // =====================================================================

        private void BeginRun()
        {
            if (!CanStart(out var why))
            {
                StatusText = "Cannot start: ??".Loc(why);
                return;
            }

            worldId = CurrentWorldId;
            worldName = CurrentWorldName;

            // 🔴 來源①②讀遊戲／IPC，只能在這裡（framework 執行緒）做。
            list = conf.PriceSurveyAllCharacters
                ? null
                : PriceSurveyItemSource.TryFromSellList(gui) ?? PriceSurveyItemSource.TryFromAllaganTools();

            var needCsvList = list == null;
            var csvPath = needCsvList ? PriceSurveyItemSource.InventoryToolsCsvPath() : null;

            // 記錄檔的讀取（續掃用的「已掃過」集合、以及「只掃被壓價的」需要的上一輪資料）
            // 一律在執行緒池上做——那個檔案會長到幾萬列。
            prepareTask = Task.Run(() => new PrepareResult(
                needCsvList ? PriceSurveyItemSource.TryFromInventoryToolsCsv(csvPath) : null,
                PriceSurveyLog.LoadAll()));

            State = SurveyState.Preparing;
            LastRunLookedUnsupported = false;
            runStartedAt = DateTime.UtcNow;
            recordedRows = 0;
            okCount = emptyCount = refusedCount = timeoutCount = cacheCount = 0;
            skippedAlreadyDone = 0;
            queueIndex = 0;
            itemQueue.Clear();
            byItemId.Clear();
            StatusText = "Building the list to survey...".Loc();
        }

        private void TickPreparing()
        {
            var task = prepareTask;
            if (task == null)
            {
                Finish("Could not build the list".Loc(), unsupported: false);
                return;
            }

            if (!task.IsCompleted)
                return;

            prepareTask = null;

            PrepareResult prepared;
            try
            {
                prepared = task.GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                Log.Information(e, "[Marketbuddy] 巡檢：準備清單時發生例外。");
                Finish("Could not build the list".Loc(), unsupported: false);
                return;
            }

            list ??= prepared.CsvList;
            if (list == null || list.Items.Count == 0)
            {
                Finish(
                    "No listed items found (needs InventoryTools/AllaganTools, or open a retainer's sell list first).".Loc(),
                    unsupported: false);
                return;
            }

            BuildQueue(prepared.History);

            if (itemQueue.Count == 0)
            {
                Finish(
                    skippedAlreadyDone > 0
                        ? "Everything on this world's list was already surveyed within the keep-for window (?? item(s)).".Loc(skippedAlreadyDone)
                        : "No item matches the current filters.".Loc(),
                    unsupported: false);
                return;
            }

            State = SurveyState.Running;
            phase = ItemPhase.Throttle;
            attempt = 0;
            firstItemNoResponseStreak = 0;
            currentItemId = itemQueue[0];
            notBefore = DateTime.MinValue;
            Log.Information(
                $"[Marketbuddy] 巡檢開始：世界 {worldName}({worldId})，{itemQueue.Count} 件，" +
                $"來源={list.SourceKey}，略過已掃 {skippedAlreadyDone} 件，閘門 {MarketRequestGate.IntervalMs} ms。");
        }

        private void BuildQueue(List<PriceSurveyRow> history)
        {
            byItemId.Clear();
            foreach (var item in list!.Items)
            {
                if (!byItemId.TryGetValue(item.ItemId, out var bucket))
                    byItemId[item.ItemId] = bucket = [];
                bucket.Add(item);
            }

            // 續掃：這個世界在保留時間內已經有結論的道具就不必再問一次。
            var alreadyDone = new HashSet<uint>();
            if (conf.PriceSurveySkipHours > 0)
            {
                var cutoff = DateTime.UtcNow.AddHours(-conf.PriceSurveySkipHours);
                foreach (var row in history)
                {
                    if (row.WorldId != worldId || row.AtUtc <= cutoff)
                        continue;
                    // 只有真的問到答案才算「掃過」；被拒絕／逾時的要再問一次。
                    if (row.Verdict is "ok" or "empty")
                        alreadyDone.Add(row.ItemId);
                }
            }

            // 「只掃被壓價的」：需要上一輪的資料才知道誰被壓價。
            HashSet<uint>? undercut = null;
            if (conf.PriceSurveyOnlyUndercut)
            {
                undercut = [];
                foreach (var row in history)
                {
                    if (row.UndercutBy > 0)
                        undercut.Add(row.ItemId);
                }
            }

            var seen = new HashSet<uint>();
            foreach (var item in list.Items)
            {
                if (!seen.Add(item.ItemId))
                    continue;
                if (undercut != null && !undercut.Contains(item.ItemId))
                    continue;
                if (alreadyDone.Contains(item.ItemId))
                {
                    skippedAlreadyDone++;
                    continue;
                }

                itemQueue.Add(item.ItemId);
            }
        }

        // =====================================================================
        //  逐件查價
        // =====================================================================

        private void TickRunning(DateTime now)
        {
            if (queueIndex >= itemQueue.Count)
            {
                Finish("Done".Loc(), unsupported: false);
                return;
            }

            currentItemId = itemQueue[queueIndex];

            switch (phase)
            {
                case ItemPhase.Throttle:
                {
                    // 快取命中就完全不必送請求。預設 0 秒（＝每一件都重新問伺服器），
                    // 因為巡檢要的就是「現在的行情」；使用者可以自己把秒數調大。
                    if (MarketDataCache.TryGet(currentItemId, conf.PriceSurveyCacheSeconds, out var cached, out _))
                    {
                        captured.Clear();
                        captured.AddRange(cached);
                        cacheCount++;
                        CompleteItem(cached.Count > 0 ? "ok" : "empty", "cache", cached.Count);
                        return;
                    }

                    if (now < notBefore)
                        return;
                    if (!MarketRequestGate.IsReady(now))
                        return;

                    phase = ItemPhase.Request;
                    return;
                }

                case ItemPhase.Request:
                {
                    var proxy = GetItemSearchProxy();
                    if (proxy == null)
                    {
                        Finish("The market query interface is unavailable (InfoProxyItemSearch)".Loc(), unsupported: false);
                        return;
                    }

                    attempt++;

                    // ⚠️ 台服的 vf10 (EndRequest) 是空函式，所以真正的重設是下面兩行。
                    //    形狀與 BatchReprice 逐字相同（那裡有完整的鑑識註解）。
                    proxy->EndRequest();
                    proxy->ListingCount = 0;
                    proxy->EntryCount = 0;

                    proxy->SearchItemId = currentItemId;
                    captured.Clear();
                    offeringsReceived = false;
                    historySeen = false;
                    pendingItemId = currentItemId;
                    offeringsPending = true;
                    probeAnswered = probeRefused = probeEmpty = false;
                    MarketRequestResultProbe.ArmForRequest();
                    sendGapMs = MarketRequestGate.MsSinceLastRequest(now);
                    MarketRequestGate.NoteRequestSent(now);
                    waitStart = now;

                    var sent = proxy->RequestData();
                    MarketDiag.Trace(
                        $"{Diag} SURVEY-REQUEST item={currentItemId} attempt={attempt} RequestData={sent} " +
                        $"sendGapMs={sendGapMs:F0} gate={MarketRequestGate.IntervalMs} world={worldId}");

                    if (!sent)
                    {
                        offeringsPending = false;
                        if (NoteNoResponseAndShouldGiveUp())
                            return;
                        if (attempt >= MaxAttempts)
                        {
                            CompleteItem("timeout", "live", -1);
                            return;
                        }

                        notBefore = now.AddMilliseconds(RetryBackoffFor(attempt));
                        phase = ItemPhase.Throttle;
                        return;
                    }

                    phase = ItemPhase.Wait;
                    return;
                }

                case ItemPhase.Wait:
                {
                    if (!probeAnswered && MarketRequestResultProbe.TryTakeResult(currentItemId, out var verdict))
                    {
                        probeAnswered = true;
                        probeRefused = verdict.Refused;
                        probeEmpty = !verdict.Refused && verdict.ListingCount == 0;
                    }

                    if (offeringsReceived)
                    {
                        MarketRequestGate.NoteAccepted();
                        firstItemNoResponseStreak = 0;
                        CompleteItem(captured.Count > 0 ? "ok" : "empty", "live", captured.Count);
                        return;
                    }

                    if (probeRefused)
                    {
                        Log.Information(
                            $"{Diag} SURVEY-REFUSED item={currentItemId} attempt={attempt} " +
                            $"afterMs={(now - waitStart).TotalMilliseconds:F0} (伺服器真值，不是逾時)");
                        offeringsPending = false;
                        firstItemNoResponseStreak = 0;
                        MarketRequestGate.NoteRefused(sendGapMs);

                        if (attempt >= MaxAttempts)
                        {
                            CompleteItem("refused", "live", -1);
                            return;
                        }

                        notBefore = now.AddMilliseconds(RetryBackoffFor(attempt));
                        phase = ItemPhase.Throttle;
                        return;
                    }

                    if (probeEmpty || (historySeen && (now - historySeenAt).TotalMilliseconds >= EmptyResultGraceMs))
                    {
                        offeringsPending = false;
                        MarketRequestGate.NoteAccepted();
                        firstItemNoResponseStreak = 0;
                        captured.Clear();
                        MarketDataCache.StoreConfirmedEmpty(currentItemId);
                        CompleteItem("empty", "live", 0);
                        return;
                    }

                    var waitedMs = (now - waitStart).TotalMilliseconds;
                    var swallowed = !historySeen && waitedMs > NoResponseDeadlineMs;
                    if (!swallowed && waitedMs <= ResponseTimeoutMs)
                        return;

                    Log.Information(
                        $"{Diag} SURVEY-TIMEOUT item={currentItemId} attempt={attempt} historySeen={historySeen} " +
                        $"swallowed={swallowed} waitedMs={waitedMs:F0} " +
                        $"probeInstalled={MarketRequestResultProbe.IsInstalled}");
                    offeringsPending = false;

                    if (swallowed)
                    {
                        MarketRequestGate.NoteRefused(sendGapMs);
                        if (NoteNoResponseAndShouldGiveUp())
                            return;
                    }
                    else
                    {
                        firstItemNoResponseStreak = 0;
                    }

                    if (attempt >= MaxAttempts)
                    {
                        CompleteItem("timeout", "live", -1);
                        return;
                    }

                    notBefore = now.AddMilliseconds(RetryBackoffFor(attempt));
                    phase = ItemPhase.Throttle;
                    return;
                }
            }
        }

        /// <summary>
        /// ⚠️ 「假設 A 不成立」的守衛：**第一件**連續 <see cref="FirstItemGiveUpAfter"/> 次
        /// 什麼都沒回來，就判定這個情境（站在市場前但沒開出售品視窗）根本送不出查詢，
        /// 整輪停下並給一句可以照做的建議 —— 而不是拿幾百件去撞同一堵牆。
        /// </summary>
        /// <returns>true＝已經收手，呼叫端要立刻 return。</returns>
        private bool NoteNoResponseAndShouldGiveUp()
        {
            if (queueIndex != 0)
                return false;

            firstItemNoResponseStreak++;
            if (firstItemNoResponseStreak < FirstItemGiveUpAfter)
                return false;

            Finish(
                "Market queries cannot be sent in this situation: the first item got no response ?? times in a row. Walk up to a market board, or open a retainer's sell list first, then try again."
                    .Loc(firstItemNoResponseStreak),
                unsupported: true);
            return true;
        }

        /// <summary>把這一件的結果寫成記錄，並前進到下一件。</summary>
        private void CompleteItem(string verdict, string source, int listingCount)
        {
            long lowestNq = -1, lowestHq = -1;
            ulong lowestNqRetainer = 0, lowestHqRetainer = 0;

            if (verdict is "ok" or "empty")
            {
                foreach (var (price, isHq, retainerId) in captured)
                {
                    if (isHq)
                    {
                        if (lowestHq < 0 || price < lowestHq)
                        {
                            lowestHq = price;
                            lowestHqRetainer = retainerId;
                        }
                    }
                    else if (lowestNq < 0 || price < lowestNq)
                    {
                        lowestNq = price;
                        lowestNqRetainer = retainerId;
                    }
                }
            }

            var knowOwners = list != null && list.OwnRetainerIds.Count > 0;
            var now = DateTime.UtcNow;
            var gate = MarketRequestGate.IntervalMs;

            if (byItemId.TryGetValue(currentItemId, out var ours))
            {
                foreach (var mine in ours)
                {
                    var lowest = mine.Hq ? lowestHq : lowestNq;
                    var lowestRetainer = mine.Hq ? lowestHqRetainer : lowestNqRetainer;
                    // 🔑 「不知道」寫 -1，不是 0：不知道最低價是不是自己的時候寫 0
                    //    等於斷言「不是我的」，那會讓比價畫面憑空生出一堆假的被壓價。
                    var lowestIsOurs = lowest < 0 || !knowOwners
                        ? -1
                        : list!.OwnRetainerIds.Contains(lowestRetainer) ? 1 : 0;

                    PriceSurveyLog.Append(new PriceSurveyRow(
                        now, worldId, worldName, mine.ItemId, mine.Hq, mine.OurPrice,
                        lowestNq, lowestHq, lowestIsOurs, listingCount, source, gate, verdict));
                    recordedRows++;
                }
            }

            switch (verdict)
            {
                case "ok":
                    okCount++;
                    break;
                case "empty":
                    emptyCount++;
                    break;
                case "refused":
                    refusedCount++;
                    break;
                default:
                    timeoutCount++;
                    break;
            }

            offeringsPending = false;
            offeringsReceived = false;
            historySeen = false;
            captured.Clear();

            queueIndex++;
            attempt = 0;
            phase = ItemPhase.Throttle;
            notBefore = DateTime.MinValue;

            if (queueIndex < itemQueue.Count)
            {
                currentItemId = itemQueue[queueIndex];
                StatusText = "Surveying: ?? / ??".Loc(queueIndex, itemQueue.Count);
            }
        }

        private void Finish(string reason, bool unsupported)
        {
            var elapsed = runStartedAt == DateTime.MinValue ? 0 : (DateTime.UtcNow - runStartedAt).TotalSeconds;
            if (State == SurveyState.Running || State == SurveyState.Preparing)
                Log.Information(
                    $"[Marketbuddy] 巡檢結束（{reason}）：世界 {worldName}({worldId})，" +
                    $"處理 {queueIndex}/{itemQueue.Count} 件，記錄 {recordedRows} 列，" +
                    $"ok={okCount} empty={emptyCount} refused={refusedCount} timeout={timeoutCount} cache={cacheCount}，" +
                    $"耗時 {elapsed:F0} 秒，閘門 {MarketRequestGate.IntervalMs} ms。");

            MarketRequestGate.LogSummary("price survey finished");
            LastRunLookedUnsupported = unsupported;
            StatusText = itemQueue.Count > 0
                ? "?? (?? / ?? item(s), ?? row(s) recorded)".Loc(reason, queueIndex, itemQueue.Count, recordedRows)
                : reason;
            ResetRun();
        }

        private void ResetRun()
        {
            State = SurveyState.Idle;
            phase = ItemPhase.Throttle;
            prepareTask = null;
            offeringsPending = false;
            offeringsReceived = false;
            historySeen = false;
            captured.Clear();
            currentItemId = 0;
            attempt = 0;

            // 這一輪結束了，探針槽裡任何還沒被取走的答覆都已經無主。
            MarketRequestResultProbe.ArmForRequest();

            var proxy = GetItemSearchProxy();
            if (proxy == null)
                return;
            proxy->EndRequest();
            proxy->ListingCount = 0;
            proxy->EntryCount = 0;
        }

        private static int RetryBackoffFor(int attemptNumber)
            => Math.Min(RetryBackoffBaseMs * attemptNumber, RetryBackoffCapMs);

        // =====================================================================
        //  市場封包（形狀比照 BatchReprice；只收自己正在等的那一件）
        // =====================================================================

        private void OnOfferingsReceived(IMarketBoardCurrentOfferings offerings)
        {
            if (!offeringsPending)
                return;

            var listings = offerings.ItemListings;
            if (listings.Count > 0)
            {
                if (offerings.RequestId == lastAcceptedRequestId)
                    return;
                if (listings[0].ItemId != pendingItemId)
                    return;
            }

            lastAcceptedRequestId = offerings.RequestId;
            captured.Clear();
            foreach (var listing in listings)
                captured.Add((listing.PricePerUnit, listing.IsHq, listing.RetainerId));

            offeringsPending = false;
            offeringsReceived = true;
        }

        private void OnHistoryReceived(IMarketBoardHistory history)
        {
            if (!offeringsPending || history.ItemId != pendingItemId)
                return;
            historySeen = true;
            historySeenAt = DateTime.UtcNow;
        }

        private static InfoProxyItemSearch* GetItemSearchProxy()
        {
            var infoModule = InfoModule.Instance();
            return infoModule == null
                ? null
                : (InfoProxyItemSearch*)infoModule->GetInfoProxyById(InfoProxyId.ItemSearch);
        }

        /// <summary>準備階段在執行緒池上算出來的兩份東西。</summary>
        private readonly record struct PrepareResult(PriceSurveyItemList? CsvList, List<PriceSurveyRow> History);
    }
}
