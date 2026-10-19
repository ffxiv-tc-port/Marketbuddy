using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Network.Structures;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;
using Marketbuddy.Common;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    /// <summary>
    /// 一件「查不到行情、刻意停在上限價」的掛單。
    /// 🔑 判準與 <c>HandleNoListings</c> 寫進 log 的那一行是<b>同一次判定</b>，這裡只是把它留下來。
    /// </summary>
    /// <param name="Slot">僱員市場容器的格號。</param>
    /// <param name="ItemId">道具 id。</param>
    /// <param name="IsHq">這一格是不是優質品。</param>
    /// <param name="Name">已經帶好優質圖示的顯示名稱。</param>
    internal readonly record struct NeedsPricingEntry(short Slot, uint ItemId, bool IsHq, string Name);

    /// <summary>
    /// One-click "relist every listed slot of the current retainer at
    /// (lowest market price - undercut)" batch engine.
    /// No hooks, no packet forgery, no memory patches.
    /// <para>Strictly manual: runs only when the user clicks the button while a
    /// retainer's sell list (RetainerSellList) is open, or when QuickLister
    /// hands it a single just-listed slot to price. Cancellable at any time
    /// via the cancel button or ESC; closing the sell list also aborts.</para>
    /// </summary>
    internal sealed unsafe class BatchReprice : IRetainerBatchEngine, IDisposable
    {
        // Pacing / safety constants. Requests are throttled and retried with
        // backoff instead of patching the client's "please wait" throttle path.
        // 🔑 送出節流的真實形狀（以及它為什麼是 send→send 而不是 data→send）整段寫在
        // MarketRequestGate 的類別註解裡，那裡才是唯一真值來源，這裡不重複。
        // ⚠️ NoResponseDeadlineMs 是探針失效時的保險絲：**不能調低**（要蓋住實測 1955 ms 的回應長尾），也不必調高。
        private const int NoResponseDeadlineMs = 2500; // fuse only: nothing at all by now => probably swallowed (verdict deferred to retry time)
        private const int ResponseTimeoutMs = 3500;    // hard cap per attempt once *something* did arrive (safety net; must stay above NoResponseDeadlineMs + EmptyResultGraceMs)
        private const int RetryBackoffBaseMs = 300;    // backoff before retry N is min(N * this, RetryBackoffCapMs)...
        private const int RetryBackoffCapMs = 1200;    // ...never escalating past this (the gate adds its own spacing on top)
        private const int MaxAttempts = 8;           // 1 initial attempt + up to 7 retries per slot
        // ⚠️ 這個看門狗一到期是**整批中止**（TickTaskQueue.Update → Abort），不是只放棄這一格，
        // 所以它必須確實蓋得住最壞情況。每次嘗試最壞 = max(閘門 4000, 退避 1200) + ResponseTimeoutMs
        // 3500 ≈ 7500 ms，×8 次 ≈ 60 秒；90 秒＝約 50% 餘裕。
        private const int SlotWatchdogSeconds = 90;  // hard per-slot watchdog (queue-level safety net; sized for MaxAttempts attempts plus gate spacing and backoffs, with margin)
        private const int EmptyResultGraceMs = 1000; // history seen + this long with no offerings => nothing on sale (measured HISTORY->OFFERINGS max is 432 ms, so this is 2.3x the observed worst case)
        private const int ForeignOfferingsReportSeconds = 60; // 「不是我們在等的」掛單封包最多幾秒記一次累計筆數

        /// <summary>
        /// 「以歷史最近賣出價重掛」時，一格最多等 Universalis 這麼久。
        /// 逾時之後<b>不是失敗</b>：那一格落回既有的市場比價流程，也就是它原本的定價方式。
        /// 🔴 必須明顯小於 <see cref="SlotWatchdogSeconds"/>：那個看門狗一到期是<b>整批中止</b>，
        /// 而「網路慢」不該讓整輪重掛死掉。
        /// </summary>
        private const int LastSoldWaitMs = 25000;

        /// <summary>Backoff before retry attempt N: escalates fast, then caps low - never the multi-second climb of a classic exponential backoff.</summary>
        private static int RetryBackoffFor(int attempt) => Math.Min(RetryBackoffBaseMs * attempt, RetryBackoffCapMs);

        private enum SlotPhase
        {
            Throttle,
            Request,
            WaitOfferings,
            Apply
        }

        private sealed class SlotJob
        {
            public required short Slot;
            public required uint ItemId;
            public required bool IsHq;
            public required string Name;
            public required uint VendorUnitPrice;
            public int Attempt;
            public bool FromCache;
            public bool QuickListed;
            public SlotPhase Phase = SlotPhase.Throttle;
            public DateTime NotBefore = DateTime.MinValue;
            public DateTime WaitStart;
            /// <summary>
            /// 這一格開始等 Universalis 回答「歷史最近賣出價」的時間；
            /// <see cref="DateTime.MinValue"/>＝還沒開始等。等太久就放棄那個定價方式、
            /// 落回既有的市場比價流程（見 <see cref="LastSoldWaitMs"/>）。
            /// </summary>
            public DateTime LastSoldWaitStart = DateTime.MinValue;

            /// <summary>
            /// 「歷史最近賣出價」這條路對這一格已經有結論了（用了它，或放棄改走市場比價）。
            /// 🔴 少了這個旗標，<see cref="SlotPhase.Throttle"/> 會在等閘門的每一個 tick 重跑一次
            /// 判斷，於是「改用市場比價」那行 Information 會每幀寫一次——log 被洗掉是靜默的損失。
            /// </summary>
            public bool LastSoldDone;
            /// <summary>Diagnostics only: when this slot was first ticked, for total-elapsed reporting.</summary>
            public DateTime StartedAt = DateTime.MinValue;
            /// <summary>send→send gap of the attempt currently in flight; fed back to the gate when it turns out to have been swallowed.</summary>
            public double SendGapMs = -1;
            /// <summary>
            /// 上一次嘗試什麼封包都沒收到，暫定判為「被吞掉」。判決刻意延後到真的要送重試
            /// 的那一刻才交給閘門——因為在那之前遲到的答案還可能落進 MarketDataCache 把
            /// 這次重試整個省掉，那就代表它根本沒被吞，不該拿去撐寬閘門。
            /// </summary>
            public bool RefusalPending;
            /// <summary>暫定被吞掉的那次請求，距離前一次送出的實際毫秒數。</summary>
            public double RefusalGapMs = -1;
            /// <summary>
            /// 這一次嘗試已經從 <see cref="MarketRequestResultProbe"/> 收到伺服器的答覆了。
            /// 每次送出新請求時重設（見 <see cref="SlotPhase.Request"/>）。
            /// </summary>
            public bool ProbeAnswered;
            /// <summary>伺服器明確拒絕了這一次查詢（<c>errorCode != 0</c>）。真值，不是逾時推測。</summary>
            public bool ProbeRefused;
            /// <summary>伺服器明確回答「零掛售」（<c>errorCode == 0 &amp;&amp; listingCount == 0</c>）。</summary>
            public bool ProbeEmpty;

            // 成交價候選（S）的原始觀測。
            // 🔑 這裡刻意只存**原始資料**，不存算好的候選：異常低價保護要拿「這一格目前的
            // 掛售價」當退路基準，而那個值要在真的要定價的那一刻（SlotPhase.Apply）才讀，
            // 不然它與最終決策之間會隔著整條市場查詢的時間窗。

            /// <summary>成交單價；<b>-1＝沒有可用的成交紀錄</b>（不是 0）。</summary>
            public long SaleUnitPrice = -1;

            /// <summary>那筆成交的時間；<see cref="DateTime.MinValue"/>＝不知道。</summary>
            public DateTime SaleAtUtc = DateTime.MinValue;

            /// <summary>那筆成交本身是優質品嗎（可能與這一格的品質不同——那就是「忽略優質」的意思）。</summary>
            public bool SaleHq;

            /// <summary>那筆成交發生在哪個世界；空＝回應裡沒帶世界。</summary>
            public string SaleWorld = string.Empty;

            /// <summary>
            /// 異常低價保護在成交價這一側要用的同業基準（＝本世界目前最低掛售價）；-1＝不知道。
            /// </summary>
            public long SalePeerBaseline = -1;

            // ---------------------------------------------------------------
            // 巡檢補位（板上最低價 L 的第三來源）。

            /// <summary>
            /// 遊戲內查詢整個失敗之後，改用的巡檢記錄裡家世界那一列的最低價；-1＝沒有。
            /// </summary>
            public long SurveyLowest = -1;

            /// <summary>上面那一列是什麼時候掃到的。</summary>
            public DateTime SurveyAtUtc = DateTime.MinValue;
        }

        private readonly MarketGuiEventHandler gui;
        private readonly TickTaskQueue queue = new();

        // Market data capture state. Only one request is ever in flight.
        private readonly List<(uint Price, bool IsHq, ulong RetainerId)> captured = new();
        private bool offeringsPending;
        private bool offeringsReceived;
        private bool historySeen;
        private DateTime historySeenAt;
        private uint pendingItemId;
        private int lastAcceptedRequestId = int.MinValue;
        private bool suppressionHeld;

        // 「不是我們在等的」掛單封包計數，見 NoteForeignOfferings。只在封包處理器裡碰。
        private int foreignOfferings;
        private DateTime foreignOfferingsReportedAt;

        // Per-slot record of what the batch actually changed, for the live sell
        // list overlay (display only - see LiveSellList). Keyed by market slot.
        private readonly Dictionary<short, (uint OldPrice, uint NewPrice, DateTime At)> recentChanges = new();
        private ulong recentChangesRetainerId;

        /// <summary>Read-only view of the prices this session changed, so the overlay can flag them. Display only.</summary>
        public IReadOnlyDictionary<short, (uint OldPrice, uint NewPrice, DateTime At)> RecentChanges => recentChanges;

        /// <summary>Retainer the <see cref="RecentChanges"/> marks belong to; the overlay ignores them for anybody else.</summary>
        public ulong RecentChangesRetainerId => recentChangesRetainerId;

        // 眼前這位僱員身上「查不到行情、刻意停在上限價」的那幾件。
        // 🔴 只在 framework 執行緒讀寫；格號是每位僱員各自編的，換人就整份作廢
        //    （與 recentChanges 同一個理由）。
        private readonly List<NeedsPricingEntry> needsPricing = new();
        private ulong needsPricingRetainerId;
        private string needsPricingRetainerName = string.Empty;

        /// <summary>那幾件的唯讀檢視，給重掛面板的結果區顯示與手動下架用。</summary>
        public IReadOnlyList<NeedsPricingEntry> NeedsPricing => needsPricing;

        /// <summary>上面那份清單屬於哪位僱員（只給人看）；空＝不知道。</summary>
        public string NeedsPricingRetainerName => needsPricingRetainerName;

        // Grep tag: MBDIAG
        private const string Diag = "[MBDIAG]";
        private DateTime lastDataReceivedAt = DateTime.MinValue;

        /// <summary>
        /// 這一批（＝這一個僱員的一輪，或 QuickLister 的單件）開始的時間。
        /// 只用來替 CACHE-HIT 標出 within-batch / cross-batch，沒有行為作用。
        /// </summary>
        private DateTime batchStartedAt = DateTime.MinValue;

        /// <summary>這一批已經講過一次「拿不到歷史賣出價，改用市場比價」了嗎。每批重設。</summary>
        private bool lastSoldFallbackWarned;

        private static double MsSince(DateTime t) =>
            t == DateTime.MinValue ? -1 : (DateTime.UtcNow - t).TotalMilliseconds;

        // Live market tax rates, cached opportunistically from the
        // TaxRatesReceived event; conf.MarketTaxPercent is the fallback.
        private IMarketTaxRates? taxRates;

        // Market data is cached globally by MarketDataCache: every offerings
        // packet the client sees is stored there, no matter which plugin (or
        // the player themselves) asked for it. This engine only reads from it.

        // Batch state (for UI / summary).
        private HashSet<ulong> ownRetainerIds = new();

        /// <summary>Fired when a batch runs to completion (all slots processed).</summary>
        public event System.Action? BatchFinished;

        /// <summary>Fired when a batch is cancelled or aborted, with the reason.</summary>
        public event System.Action<string>? BatchAborted;

        /// <summary>
        /// 「現在是不是有別人（＝<see cref="MultiRetainerTour"/>）在驅動這具引擎」的查詢器。
        /// </summary>
        /// <remarks>
        /// 🔴 只給<b>收尾通知</b>用，不影響任何重掛行為。巡迴會逐個僱員各跑一次這具引擎，
        /// 每一次都會走到 <see cref="OnQueueCompleted"/>；沒有這道閘，九個僱員就會響九次。
        /// </remarks>
        internal Func<bool>? ExternalDriverActive;

        /// <summary>
        /// 這一輪是不是 <see cref="StartQuickReprice"/> 開的（快速上架後替<b>單一格</b>定價）。
        /// </summary>
        /// <remarks>
        /// 📌 只給收尾通知用。快速上架是「每上架一件就跑一次」的高頻背景動作，
        /// 把它當成「重掛跑完」會變成每件商品響一次。
        /// </remarks>
        private bool quickRepriceBatch;

        /// <summary>
        /// 這一輪是不是「只下架」批次（見 <see cref="StartDelistNeedsPricing"/>）。
        /// 📌 只影響收尾那句話與通知，不影響任何下架行為。
        /// </summary>
        private bool delistOnlyBatch;

        /// <summary>連續兩次取回之間的最小間隔，形狀比照 <c>BatchDelist</c>。</summary>
        private const int DelistThrottleMs = 250;

        private DateTime lastDelistFiredAt = DateTime.MinValue;

        public bool IsRunning => queue.IsRunning;

        /// <summary>
        /// 這一輪是不是「只下架」批次（<see cref="StartDelistNeedsPricing"/> 開的）。
        /// 📌 純顯示用：進度列與收尾措辭。不影響任何下架或定價行為。
        /// </summary>
        public bool IsDelistOnlyBatch => delistOnlyBatch;

        public int TotalSlots { get; private set; }
        public int ProcessedSlots { get; private set; }
        public int RepricedCount { get; private set; }
        public int SkippedCount { get; private set; }
        public int DelistedCount { get; private set; }
        public int FailedCount { get; private set; }

        /// <summary>
        /// 查不到比價資料、因此**刻意**留在上限價等使用者手動定價的格數。
        /// 🔑 這不是失敗：沒有參考價的時候不亂猜一個價掛出去，正是它該做的事。
        /// </summary>
        public int NeedsPricingCount { get; private set; }

        public string CurrentItemName { get; private set; } = string.Empty;

        /// <summary>
        /// 重掛永遠是 false：它撞到目的地容器滿的時候只讓**這一件**失敗然後繼續跑，
        /// 不會整輪停手，所以沒有「因為空間而停」這種結束方式。
        /// </summary>
        public bool StoppedForSpace => false;

        /// <summary>
        /// 此刻正在處理的那一格的市場容器索引，閒置時 -1。**純顯示用**
        /// （<see cref="LiveSellList"/> 靠它把那一列亮起來），沒有任何行為作用。
        /// </summary>
        public short CurrentSlot { get; private set; } = -1;

        /// <summary>
        /// <see cref="CurrentSlot"/> 屬於哪個僱員。格號是**每個僱員各自**編的，
        /// 少了這道比對，僱員 B 的第 3 格會繼承僱員 A 第 3 格的高亮
        /// （<see cref="RecentChangesRetainerId"/> 存在的理由完全相同）。
        /// </summary>
        public ulong CurrentBatchRetainerId { get; private set; }

        private Configuration conf => Configuration.GetOrLoad();

        public BatchReprice(MarketGuiEventHandler gui)
        {
            this.gui = gui;
            queue.Aborted += OnQueueAborted;
            queue.Completed += OnQueueCompleted;
            MarketBoard.OfferingsReceived += OnOfferingsReceived;
            MarketBoard.HistoryReceived += OnHistoryReceived;
            MarketBoard.TaxRatesReceived += OnTaxRatesReceived;
            Framework.Update += OnFrameworkUpdate;
        }

        public void Dispose()
        {
            Framework.Update -= OnFrameworkUpdate;
            MarketBoard.TaxRatesReceived -= OnTaxRatesReceived;
            MarketBoard.HistoryReceived -= OnHistoryReceived;
            MarketBoard.OfferingsReceived -= OnOfferingsReceived;
            // Detach handlers first so an unload-time abort stays silent.
            queue.Aborted -= OnQueueAborted;
            queue.Completed -= OnQueueCompleted;
            if (queue.IsRunning)
                queue.Abort("plugin unloading");
            ReleaseSuppressionIfHeld();
        }

        public bool CanStart(out string reason) => CanStart(out reason, requireListedItems: true);

        /// <param name="requireListedItems">
        /// 是否要求「這名僱員身上有掛單」。整批重掛必須要求（它要自己去掃有哪些格子），
        /// 但**已經指名某一格**的呼叫端要傳 false：它手上的資訊比僱員結構上那個
        /// 會落後的計數器更新也更具體。詳見下面該檢查處的說明。
        /// </param>
        public bool CanStart(out string reason, bool requireListedItems)
        {
            reason = string.Empty;
            if (IsRunning)
                return false;
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

            // 跨世界價格巡檢與重掛共用同一個 InfoProxyItemSearch 請求槽，兩者不能同時跑。
            if (gui.Survey?.IsRunning == true)
            {
                reason = "A cross-world price survey is running".Loc();
                return false;
            }

            if (gui.VendorSeller?.IsRunning == true)
            {
                reason = "selling to the shop is in progress".Loc();
                return false;
            }

            if (!gui.IsRetainerSellListOpen)
            {
                reason = "retainer sell list is not open".Loc();
                return false;
            }

            // A RetainerSell window that QuickLister itself opened to list the
            // next item at the price cap is not a manual override - it must
            // not block starting this item's own reprice (that is the whole
            // point of queueing quick-lists instead of serializing them
            // behind engine.IsRunning). See QuickLister.IsCapListingInFlight.
            if (Commons.GetUnitBase("RetainerSell") != null && gui.QuickLister?.IsCapListingInFlight != true)
            {
                reason = "close the price adjustment window first".Loc();
                return false;
            }

            var retainerManager = RetainerManager.Instance();
            var active = retainerManager == null ? null : retainerManager->GetActiveRetainer();
            if (active == null)
            {
                reason = "no active retainer".Loc();
                return false;
            }

            // 🔴 MarketItemCount 是**僱員結構上的計數器**，不是即時的出售品容器內容，會落後於我們自己剛剛掛上去的東西。
            // 🔑 呼叫端如果已經指名了某一格（快速上架的單件定價就是），它手上的資訊
            // 比這個計數器新也比它具體——StartQuickReprice 會直接去讀那一格確認
            // ItemId != 0，那才是這個引擎真正要操作的東西。這種時候不該讓落後的
            // 計數器否決它，所以 requireListedItems 給 false。
            if (requireListedItems && active->MarketItemCount == 0)
            {
                reason = "this retainer has nothing listed".Loc();
                return false;
            }

            return true;
        }

        public void Start()
        {
            if (!CanStart(out var reason))
            {
                if (AutoRetainerBridge.IsBusy)
                {
                    AutoRetainerBridge.ArmAvailabilityNotice();
                    ChatGui.PrintError("[Marketbuddy] AutoRetainer is running - this action was skipped. You will be told when it finishes.".Loc());
                    return;
                }

                if (reason.Length > 0)
                    ChatGui.PrintError("[Marketbuddy] Cannot start: ??".Loc(reason));
                return;
            }

            var inventoryManager = InventoryManager.Instance();
            var retainerManager = RetainerManager.Instance();
            if (inventoryManager == null || retainerManager == null)
                return;
            var container = inventoryManager->GetInventoryContainer(InventoryType.RetainerMarket);
            if (container == null)
                return;

            // Snapshot every listed slot of the active retainer. Pointers are
            // null-checked before any dereference.
            var jobs = new List<SlotJob>();
            for (var i = 0; i < container->Size; i++)
            {
                var slot = inventoryManager->GetInventorySlot(InventoryType.RetainerMarket, i);
                if (slot == null || slot->ItemId == 0)
                    continue;
                jobs.Add(CreateSlotJob(slot));
            }

            if (jobs.Count == 0)
            {
                ChatGui.PrintError("[Marketbuddy] Cannot start: ??".Loc("this retainer has nothing listed".Loc()));
                return;
            }

            BeginBatch(jobs);

            var active = retainerManager->GetActiveRetainer();
            var retainerName = active == null ? string.Empty : active->NameString;
            ChatGui.Print("[Marketbuddy] Relisting ?? item(s) (retainer: ??)...".Loc(jobs.Count, retainerName));
        }

        /// <summary>
        /// 上一次 <see cref="StartQuickReprice"/> 被拒絕的原因，供 QuickLister 記進診斷 log。
        /// </summary>
        public string LastStartRefusalReason { get; private set; } = string.Empty;

        /// <summary>
        /// Runs the full pipeline (compare via cache/request, undercut,
        /// thresholds, delist) for one just-quick-listed slot that is parked at
        /// the price cap. Returns false when the engine cannot start right now.
        /// </summary>
        public bool StartQuickReprice(short slotIndex)
        {
            // requireListedItems: false —— 我們已經指名了 slotIndex，而且下面就會去讀
            // 那一格確認它真的有東西。那比僱員結構上會落後的 MarketItemCount 更權威。
            if (!CanStart(out var reason, requireListedItems: false))
            {
                LastStartRefusalReason = reason;
                return false;
            }

            var inventoryManager = InventoryManager.Instance();
            var slot = inventoryManager == null
                ? null
                : inventoryManager->GetInventorySlot(InventoryType.RetainerMarket, slotIndex);
            if (slot == null || slot->ItemId == 0)
            {
                LastStartRefusalReason = slot == null
                    ? $"market slot {slotIndex} unavailable"
                    : $"market slot {slotIndex} is empty";
                return false;
            }

            LastStartRefusalReason = string.Empty;

            var job = CreateSlotJob(slot);
            job.QuickListed = true;
            BeginBatch([job]);
            // BeginBatch 先把它清成 false，所以這一行要在後面。只影響收尾通知，不影響定價流程。
            quickRepriceBatch = true;
            return true;
        }

        /// <summary>
        /// 把眼前這位僱員身上「查不到行情、停在上限價」的那幾件下架收回。
        /// 🔴 只有使用者按下按鈕（而且通過二次確認）才會走到這裡：重掛流程自己永遠不呼叫它。
        /// </summary>
        /// <returns>真的排進去的件數；0＝沒東西可做，或引擎現在不能開始（原因印在聊天視窗）。</returns>
        public int StartDelistNeedsPricing()
        {
            // requireListedItems: false —— 格號已經指名了，下面逐格確認它真的有東西。
            if (!CanStart(out var reason, requireListedItems: false))
            {
                if (AutoRetainerBridge.IsBusy)
                {
                    AutoRetainerBridge.ArmAvailabilityNotice();
                    ChatGui.PrintError("[Marketbuddy] AutoRetainer is running - this action was skipped. You will be told when it finishes.".Loc());
                    return 0;
                }

                if (reason.Length > 0)
                    ChatGui.PrintError("[Marketbuddy] Cannot start: ??".Loc(reason));
                return 0;
            }

            var jobs = new List<SlotJob>();
            foreach (var entry in needsPricing)
            {
                var slot = GetMarketSlot(entry.Slot);
                if (slot != null && slot->ItemId == entry.ItemId)
                    jobs.Add(CreateSlotJob(slot));
            }

            if (jobs.Count == 0)
            {
                ChatGui.PrintError("[Marketbuddy] Cannot start: ??"
                    .Loc("nothing here is parked at the price cap any more".Loc()));
                return 0;
            }

            lastDelistFiredAt = DateTime.MinValue;
            BeginBatch(jobs, delistOnly: true);
            ChatGui.Print("[Marketbuddy] Taking ?? item(s) with no market data off the board...".Loc(jobs.Count));
            return jobs.Count;
        }

        /// <summary>
        /// 「只下架」批次的一格：不查價、不改價，交給 <see cref="DelistSlot"/>。
        /// </summary>
        /// <remarks>
        /// 🔴 送出前重新確認「同一格、同一件、價格還停在上限」三項：取回一件之後市場容器
        /// 會不會壓縮格號我們離線證不了，三項全對才動手，最壞情況因此是「沒下架」而不是「下錯東西」。
        /// </remarks>
        private TickTaskResult TickDelistOnly(SlotJob job)
        {
            CurrentItemName = job.Name;
            CurrentSlot = job.Slot;

            // 與 BatchDelist 同一個節奏：不要把整批取回一次全丟給伺服器。
            if ((DateTime.UtcNow - lastDelistFiredAt).TotalMilliseconds < DelistThrottleMs)
                return TickTaskResult.Continue;

            var inventoryManager = InventoryManager.Instance();
            var slot = GetMarketSlot(job.Slot);
            if (inventoryManager == null || slot == null || slot->ItemId != job.ItemId ||
                inventoryManager->GetRetainerMarketPrice(job.Slot) < Configuration.MAX_PRICE)
            {
                Skip(job, "[Marketbuddy] ??: no longer parked at the price cap, left alone".Loc(job.Name));
                return TickTaskResult.Done;
            }

            lastDelistFiredAt = DateTime.UtcNow;
            DelistSlot(job, inventoryManager, slot,
                "[Marketbuddy] ??: no market data, delisted".Loc(job.Name));
            return TickTaskResult.Done;
        }

        private static SlotJob CreateSlotJob(InventoryItem* slot)
        {
            var itemSheet = DataManager.GetExcelSheet<Item>();
            var name = $"#{slot->ItemId}";
            var priceLow = 0u;
            if (itemSheet != null && itemSheet.TryGetRow(slot->ItemId, out var row))
            {
                name = row.Name.ExtractText();
                priceLow = row.PriceLow;
            }

            var isHq = (slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
            if (isHq)
                name += $" {(char)SeIconChar.HighQuality}";

            // NPC vendors pay PriceLow for NQ and PriceLow+1 for HQ (verified
            // against CriticalCommonLib's production SellToVendorPrice; the
            // old "HQ = +10%" rule is long gone). PriceLow 0 = unsellable.
            var vendorUnitPrice = priceLow == 0 ? 0u : isHq ? priceLow + 1 : priceLow;

            return new SlotJob
            {
                Slot = slot->Slot, ItemId = slot->ItemId, IsHq = isHq, Name = name,
                VendorUnitPrice = vendorUnitPrice
            };
        }

        private void BeginBatch(List<SlotJob> jobs) => BeginBatch(jobs, delistOnly: false);

        /// <param name="delistOnly">
        /// true＝這一批<b>不查價、不改價</b>，每一格只走 <see cref="DelistSlot"/>。
        /// </param>
        private void BeginBatch(List<SlotJob> jobs, bool delistOnly)
        {
            // All of our own retainers: if the lowest listing is ours (any
            // retainer), never undercut ourselves.
            ownRetainerIds = new HashSet<ulong>();
            var retainerManager = RetainerManager.Instance();
            if (retainerManager != null)
            {
                foreach (var retainer in retainerManager->Retainers)
                {
                    if (retainer.RetainerId != 0)
                        ownRetainerIds.Add(retainer.RetainerId);
                }
            }

            // 預設是「一般重掛」；快速上架那條路徑在 BeginBatch 回來之後自己標回 true。
            quickRepriceBatch = false;
            delistOnlyBatch = delistOnly;
            TotalSlots = jobs.Count;
            ProcessedSlots = 0;
            RepricedCount = 0;
            SkippedCount = 0;
            DelistedCount = 0;
            FailedCount = 0;
            NeedsPricingCount = 0;
            CurrentItemName = string.Empty;
            CurrentSlot = -1;
            CurrentBatchRetainerId = ActiveRetainerId();

            // 這一批要重新判定的格子先從「停在上限價」清單移掉；真的還是查不到行情時
            // HandleNoListings 會把它加回來。下架批次不動它——下架沒成功的那幾件還在板上。
            if (!delistOnly)
            {
                foreach (var job in jobs)
                    ForgetNeedsPricing(job.Slot);
            }
            batchStartedAt = DateTime.UtcNow;
            lastSoldFallbackWarned = false;

            // 「以歷史最近賣出價重掛」開著時，整批的道具一次排進 Universalis 查詢佇列
            // （一個 HTTP 請求就問完一整位僱員）。⚠️ 這只是預取，不改變任何觸發時機：
            // 使用者沒按按鈕的話 BeginBatch 根本不會被呼叫。
            if (conf.RelistUseLastSoldPrice)
            {
                var itemIds = new HashSet<uint>();
                foreach (var job in jobs)
                    itemIds.Add(job.ItemId);
                RequestLastSoldPrices(itemIds);
            }

            // 巡檢補位要用的記憶體索引。🔴 讀檔在執行緒池上，這裡只是把它踢起來；
            // 已經讀過就是一次 Interlocked 比較。⚠️ 它是**補位**，所以來不及讀完也只是
            // 退回「查詢失敗就算失敗」的舊行為，不會讓任何一格拿到錯的價。
            if (conf.RelistSurveyFallbackHours > 0)
                PriceSurveySnapshot.EnsureLoaded();
            lastAcceptedRequestId = int.MinValue;
            offeringsPending = false;
            offeringsReceived = false;
            historySeen = false;

            // Hold AutoRetainer off for the duration of this user-triggered
            // run; every exit path (completion, cancel, abort, dispose) goes
            // through ResetRequestState/ForceRelease so it is always restored.
            AutoRetainerBridge.AcquireSuppression("batch reprice");
            suppressionHeld = true;

            // 🔴 看門狗一到期是**整批中止**，所以它必須蓋得住最壞情況。開了「歷史最近賣出價」
            //    的時候，一格最壞是「等 Universalis 等到 LastSoldWaitMs 逾時，然後才從頭跑完整條
            //    市場比價」——那兩段是相加的，不加上去的話網路慢會讓整輪重掛被整批砍掉。
            var watchdog = TimeSpan.FromSeconds(SlotWatchdogSeconds) +
                           (conf.RelistUseLastSoldPrice
                               ? TimeSpan.FromMilliseconds(LastSoldWaitMs)
                               : TimeSpan.Zero);
            foreach (var job in jobs)
            {
                var tick = delistOnly
                    ? new Func<TickTaskResult>(() => TickDelistOnly(job))
                    : new Func<TickTaskResult>(() => TickSlot(job));
                queue.Enqueue(job.Name, watchdog, tick);
            }
        }

        private void ReleaseSuppressionIfHeld()
        {
            if (!suppressionHeld)
                return;
            suppressionHeld = false;
            AutoRetainerBridge.ReleaseSuppression();
        }

        public void CancelByButton() => Cancel("cancelled by user".Loc());

        private void Cancel(string reason) => queue.Abort(reason);

        private void OnFrameworkUpdate(IFramework framework)
        {
            PruneNeedsPricing();

            if (!queue.IsRunning)
                return;

            // Global guard rails, evaluated every tick while the batch runs.
            if (!gui.IsRetainerSellListOpen)
            {
                Cancel("the retainer sell list was closed".Loc());
                return;
            }

            if (Keys[VirtualKey.ESCAPE])
            {
                Cancel("ESC pressed".Loc());
                return;
            }

            if (IPCManager.IsLocked)
            {
                Cancel("locked via IPC by another plugin".Loc());
                return;
            }

            // Same carve-out as CanStart: a RetainerSell window opened by
            // QuickLister listing a *different* item at the price cap while
            // this job's headless reprice runs in the background is not a
            // manual intervention and must not abort this run.
            if (Commons.GetUnitBase("RetainerSell") != null && gui.QuickLister?.IsCapListingInFlight != true)
            {
                Cancel("manual price adjustment detected".Loc());
                return;
            }

            // AutoRetainer mutual exclusion (cached at 1 Hz by the bridge): if
            // it starts driving retainers mid-batch, we stand down immediately.
            if (AutoRetainerBridge.IsBusy)
            {
                Cancel("AutoRetainer became busy".Loc());
                return;
            }

            queue.Update();
        }

        private TickTaskResult TickSlot(SlotJob job)
        {
            CurrentItemName = job.Name;
            // 顯示用：一進來就指向這一格，直到下一格接手（見 CurrentSlot 的說明）。
            CurrentSlot = job.Slot;
            var now = DateTime.UtcNow;

            switch (job.Phase)
            {
                case SlotPhase.Throttle:
                    // 成交價候選（S）先解出來，但**不定價**：定價規則是「成交價與板上最低價
                    // 取低者」，所以這一格照樣要往下走市場查詢流程去拿板上的最低價。
                    // ⚠️ 排在最前面是因為它要等一個 HTTP 回應，而那段時間正好可以與
                    //    MarketRequestGate 的間隔重疊。
                    if (conf.RelistUseLastSoldPrice && !job.LastSoldDone &&
                        TickSaleCandidate(job, now) is { } saleWaitResult)
                        return saleWaitResult;

                    // Fresh cached market data for this item skips the whole
                    // request/wait pipeline (and the request throttle).
                    // 這一步刻意排在退避與閘門檢查**之前**：一次逾時之後遲到的答案會落進
                    // MarketDataCache，下一個 tick 就在這裡被撿回來，於是那次重試根本不會送出去。
                    if (MarketDataCache.TryGet(job.ItemId, conf.MarketDataCacheSeconds, out var cachedListings,
                            out var cacheAgeMs))
                    {
                        // 🔑 scope 讓事後分得出「這一輪自己剛查過」與「上一輪／上一個僱員留下來的」。
                        // 跨輪次的命中才是長 TTL 真正的價值所在，而單一批次內的樣本看不到它。
                        var batchAgeMs = batchStartedAt == DateTime.MinValue
                            ? -1
                            : (now - batchStartedAt).TotalMilliseconds;
                        var scope = batchAgeMs < 0 || cacheAgeMs > batchAgeMs ? "cross-batch" : "within-batch";
                        Log.Debug(
                            $"{Diag} CACHE-HIT item={job.ItemId} '{job.Name}' n={cachedListings.Count} " +
                            $"ageMs={cacheAgeMs:F0} scope={scope} batchAgeMs={batchAgeMs:F0} attempt={job.Attempt} " +
                            $"(no request sent; ttl={conf.MarketDataCacheSeconds}s)");
                        LogQuerySummary(job, $"cache({scope})", cachedListings.Count, now);
                        captured.Clear();
                        captured.AddRange(cachedListings);
                        job.FromCache = true;
                        // 這次嘗試其實有答案，只是遲到 —— 不能拿去指控閘門。
                        job.RefusalPending = false;
                        job.Phase = SlotPhase.Apply;
                        return TickTaskResult.Continue;
                    }

                    if (now < job.NotBefore)
                        return TickTaskResult.Continue;

                    // 🔴 上一次真的被吞掉/被拒絕的**判決點**。走到這裡代表已經過了退避、
                    // 也確認快取裡沒有遲到的答案可用，現在真的要再問一次同一件事。
                    // ⚠️ 必須在下面的 IsReady 之前呼叫：放在閘門檢查之後才呼叫，撐寬對「這一次重試」完全無效。
                    if (job.RefusalPending)
                    {
                        job.RefusalPending = false;
                        MarketRequestGate.NoteRefused(job.RefusalGapMs);
                    }

                    // 🔑 從「上一次**送出**」起算（見 MarketRequestGate）：從「上一次收到資料」起算的話，
                    // 前一件是「沒人在賣」的道具時時間戳會停在再上一件，
                    // 於是 send→send 間隔不足就撞牆。
                    if (!MarketRequestGate.IsReady(now))
                        return TickTaskResult.Continue;

                    job.Phase = SlotPhase.Request;
                    return TickTaskResult.Continue;

                case SlotPhase.Request:
                {
                    var slot = GetMarketSlot(job.Slot);
                    if (slot == null || slot->ItemId != job.ItemId)
                    {
                        Skip(job, "[Marketbuddy] ??: slot changed, skipped".Loc(job.Name));
                        return TickTaskResult.Done;
                    }

                    var proxy = GetItemSearchProxy();
                    if (proxy == null)
                    {
                        Fail(job, "InfoProxyItemSearch unavailable");
                        return TickTaskResult.Done;
                    }

                    // ⚠️ 拒絕的判決已經在 SlotPhase.Throttle 下過了（刻意在閘門檢查之前），
                    // 這裡不要重複，否則撐寬又會晚一拍。

                    job.Attempt++;

                    // 為什麼要清：從這裡到伺服器回覆之間，SearchItemId 已經是**新**道具，
                    // 但 ListingCount/EntryCount 還是**上一件**的 —— 這段期間任何讀 proxy 的
                    // 消費者（含遊戲自己的市場面板）會把舊清單當成新道具的清單。
                    // EndRequest() 仍然照呼叫：台服是空函式所以零成本，改版變回實作時自動生效。
                    proxy->EndRequest();
                    proxy->ListingCount = 0;
                    proxy->EntryCount = 0;

                    proxy->SearchItemId = job.ItemId;
                    captured.Clear();
                    offeringsReceived = false;
                    historySeen = false;
                    pendingItemId = job.ItemId;
                    offeringsPending = true;
                    job.ProbeAnswered = false;
                    job.ProbeRefused = false;
                    job.ProbeEmpty = false;
                    // 丟掉還留在探針槽裡的舊答覆，這樣取到的一定是這一次請求之後才到的。
                    MarketRequestResultProbe.ArmForRequest();
                    job.SendGapMs = MarketRequestGate.MsSinceLastRequest(now);
                    var waitReason = MarketRequestGate.NoteRequestSent(now, job.ItemId);
                    job.WaitStart = now;
                    if (job.StartedAt == DateTime.MinValue)
                        job.StartedAt = now;

                    var sent = proxy->RequestData();
                    var cachedAgeMs = MarketDataCache.AgeMsOf(job.ItemId);
                    // 每一次送出都有一筆，是 log 的大宗 -> Debug。摘要由 QUERY 那一行負責。
                    Log.Debug(
                        $"{Diag} REQUEST item={job.ItemId} '{job.Name}' attempt={job.Attempt} " +
                        $"RequestData={sent} sendGapMs={job.SendGapMs:F0} gate={MarketRequestGate.IntervalMs} " +
                        $"msSinceLastData={MsSince(lastDataReceivedAt):F0} " +
                        $"cache={(cachedAgeMs < 0 ? "miss" : $"stale({cachedAgeMs:F0}ms)")}{waitReason}");

                    if (!sent)
                    {
                        offeringsPending = false;
                        if (job.Attempt >= MaxAttempts)
                            return GiveUpOnMarketQuery(
                                job, "market data request could not be sent".Loc(), "send-failed");

                        job.NotBefore = now.AddMilliseconds(RetryBackoffFor(job.Attempt));
                        job.Phase = SlotPhase.Throttle;
                        return TickTaskResult.Continue;
                    }

                    job.Phase = SlotPhase.WaitOfferings;
                    return TickTaskResult.Continue;
                }

                case SlotPhase.WaitOfferings:
                {
                    // 🔑 伺服器對這一次查詢的**真實答覆**（見 MarketRequestResultProbe）。
                    // 這是唯一一個不必猜的訊號源（拒絕在送出後約 0.2 秒就到，逾時要 2.5 秒才判得出來）。
                    // 取一次就消費掉，所以放在最前面。
                    if (!job.ProbeAnswered && MarketRequestResultProbe.TryTakeResult(job.ItemId, out var verdict))
                    {
                        job.ProbeAnswered = true;
                        job.ProbeRefused = verdict.Refused;
                        // listingCount 是跨所有分頁的**總**筆數；為 0 時客戶端不會送續頁請求，
                        // 所以 offerings 封包永遠不會來。
                        job.ProbeEmpty = !verdict.Refused && verdict.ListingCount == 0;
                        // 探針對每一次查價都會給一筆答覆 -> Debug；被拒絕的情形由下面的
                        // REFUSED 那一行以 Information 保留（那是異常，不是常態）。
                        Log.Debug(
                            $"{Diag} VERDICT item={job.ItemId} '{job.Name}' attempt={job.Attempt} " +
                            $"refused={verdict.Refused} errorCode=0x{verdict.ErrorCode:X} " +
                            $"listingCount={verdict.ListingCount} " +
                            $"afterMs={(now - job.WaitStart).TotalMilliseconds:F0}");
                    }

                    if (offeringsReceived)
                    {
                        Log.Debug(
                            $"{Diag} SLOT-DONE item={job.ItemId} '{job.Name}' via=offerings " +
                            $"attempts={job.Attempt} waitMs={(now - job.WaitStart).TotalMilliseconds:F0} " +
                            $"totalMs={(now - job.StartedAt).TotalMilliseconds:F0}");
                        LogQuerySummary(job, "offerings", captured.Count, now);
                        MarketRequestGate.NoteAccepted();
                        // 掛單資料已經由 MarketDataCache 的被動處理器存起來了，這裡不必再存。
                        job.Phase = SlotPhase.Apply;
                        return TickTaskResult.Continue;
                    }

                    // ① 伺服器**明確拒絕**了這一次查詢：有真值，不必等逾時，立刻走重試流程。
                    // 🔴 這裡**不**直接呼叫 RequestData()：重送一律回到 Throttle，由既有的
                    //    Request 階段經 MarketRequestGate 送出，否則 NoteRequestSent 會被繞過。
                    if (job.ProbeRefused)
                    {
                        Log.Information(
                            $"{Diag} REFUSED item={job.ItemId} '{job.Name}' attempt={job.Attempt} " +
                            $"afterMs={(now - job.WaitStart).TotalMilliseconds:F0} (server verdict, not a timeout)");
                        offeringsPending = false;

                        // 判決仍然延到真的要重送的那一刻才交給閘門（見 SlotPhase.Throttle）：
                        // 中間若有遲到的答案落進 MarketDataCache，那次重試會整個被省掉，
                        // 也就不該拿這次拒絕去撐寬閘門。
                        job.RefusalPending = true;
                        job.RefusalGapMs = job.SendGapMs;

                        if (job.Attempt >= MaxAttempts)
                            return GiveUpOnMarketQuery(
                                job, "the server refused the market query".Loc(), "refused");

                        job.NotBefore = now.AddMilliseconds(RetryBackoffFor(job.Attempt));
                        job.Phase = SlotPhase.Throttle;
                        return TickTaskResult.Continue;
                    }

                    // ② 「沒人在賣」的兩條認定路徑，快的那條優先：伺服器直說 listingCount == 0（job.ProbeEmpty，
                    //    這時客戶端**不送續頁請求**，offerings 封包永遠不會來），
                    //    或 history 到了之後等滿 EmptyResultGraceMs 仍然沒有 offerings。
                    if (job.ProbeEmpty || (historySeen && (now - historySeenAt).TotalMilliseconds >= EmptyResultGraceMs))
                    {
                        Log.Debug(
                            $"{Diag} SLOT-DONE item={job.ItemId} '{job.Name}' " +
                            $"via={(job.ProbeEmpty ? "verdict(empty)" : "history-grace(empty)")} " +
                            $"attempts={job.Attempt} totalMs={(now - job.StartedAt).TotalMilliseconds:F0}");
                        LogQuerySummary(
                            job, job.ProbeEmpty ? "empty(verdict)" : "empty(history-grace)", 0, now);
                        offeringsPending = false;
                        // 伺服器確實回答了這次查詢，所以就算沒有 offerings 分頁，
                        // 對節流而言這仍然算一次乾淨的請求。
                        MarketRequestGate.NoteAccepted();
                        captured.Clear();
                        // 「沒人在賣」是被動觀察推不出來的（零掛單根本不會有 offerings 封包），
                        // 只有走完上面兩條確認流程之一才敢寫進快取。
                        MarketDataCache.StoreConfirmedEmpty(job.ItemId);
                        job.Phase = SlotPhase.Apply;
                        return TickTaskResult.Continue;
                    }

                    var waitedMs = (now - job.WaitStart).TotalMilliseconds;

                    // 走到這裡代表探針沒給出答覆（沒掛上、或這次真的一個封包都沒回來）。
                    // 兩種逾時要分開，因為它們代表相反的事、需要相反的修正：
                    //   (a) 什麼封包都沒來 → 請求可能被節流吞掉了 → 間隔不夠長，要往上加。
                    //   (b) history 來了、offerings 沒來 → 請求送到了，是伺服器/網路慢 → 間隔沒有問題，加長它只會白白拖慢每一件。
                    // (a) 判得比 (b) 早：實測第一個封包最遲 1955 ms 會出現，門檻取 2500 ms。
                    var swallowed = !historySeen && waitedMs > NoResponseDeadlineMs;
                    if (swallowed || waitedMs > ResponseTimeoutMs)
                    {
                        Log.Information(
                            $"{Diag} TIMEOUT item={job.ItemId} '{job.Name}' attempt={job.Attempt} " +
                            $"historySeen={historySeen} swallowed={swallowed} waitedMs={waitedMs:F0} " +
                            $"probeInstalled={MarketRequestResultProbe.IsInstalled} probeAnswered={job.ProbeAnswered}");
                        offeringsPending = false;

                        if (swallowed)
                        {
                            job.RefusalPending = true;
                            job.RefusalGapMs = job.SendGapMs;
                        }

                        if (job.Attempt >= MaxAttempts)
                            return GiveUpOnMarketQuery(
                                job, "no market data received (timed out)".Loc(), "timeout");

                        job.NotBefore = now.AddMilliseconds(RetryBackoffFor(job.Attempt));
                        job.Phase = SlotPhase.Throttle;
                        return TickTaskResult.Continue;
                    }

                    return TickTaskResult.Continue;
                }

                case SlotPhase.Apply:
                    ApplySlot(job);
                    return TickTaskResult.Done;

                default:
                    return TickTaskResult.AbortQueue;
            }
        }

        /// <summary>
        /// 把「資料中心最近一筆成交價」這個<b>候選</b>解出來並記在這一格上。
        /// 回傳值刻意是兩態：<c>Continue</c>＝還在等 Universalis 回答；
        /// <b><c>null</c>＝這一格的成交價候選已經有結論了</b>（可能有值、可能沒有），
        /// 呼叫端要繼續往下走市場查詢流程。用 <c>bool</c> 表示不了「還在等」，
        /// 而把「還在等」誤當成「查不到」會讓第一批永遠拿不到成交價。
        /// 🔴 這一段<b>一次遊戲內市場查詢都不送</b>：價格來自 Universalis 的 HTTP API。
        /// </summary>
        private TickTaskResult? TickSaleCandidate(SlotJob job, DateTime now)
        {
            var state = LastSoldPriceSource.StateOf(job.ItemId);

            if (state is LastSoldState.Unknown or LastSoldState.Loading)
            {
                if (job.LastSoldWaitStart == DateTime.MinValue)
                {
                    job.LastSoldWaitStart = now;
                    // 沒人替這一件排過查詢（例如批次開始之後才換上來的道具）——自己補一次。
                    if (state == LastSoldState.Unknown)
                        RequestLastSoldPrices([job.ItemId]);
                }
                else if ((now - job.LastSoldWaitStart).TotalMilliseconds > LastSoldWaitMs)
                {
                    job.LastSoldDone = true;
                    WarnLastSoldFallback(job, "timed out");
                    return null;
                }

                return TickTaskResult.Continue;
            }

            job.LastSoldDone = true;

            if (state == LastSoldState.Ready &&
                LastSoldPriceSource.TryGet(job.ItemId, job.IsHq, conf.RelistLastSoldIgnoreQuality, out var sold))
            {
                job.SaleUnitPrice = sold.UnitPrice;
                job.SaleAtUtc = sold.SoldAtUtc;
                job.SaleHq = sold.Hq;
                job.SaleWorld = sold.World;

                // 🔴 異常低價保護（成交價這一側）的同業基準：有人買走了別人少打一個 0 的掛單時，那一筆成交會把我們整格拉到那個價。
                //    這裡拿「本世界目前的最低掛售價」當正常價——那是與這筆成交
                //    完全獨立的另一個觀測，整體崩盤時它會跟著低，所以不會把崩盤誤判成異常。
                //    📌 刻意**不**改用這一輪查到的板上最低價：那個值同時也是候選 L 的參考價，
                //    拿 L 去替 S 背書會讓兩個候選不再互相獨立。
                job.SalePeerBaseline =
                    LastSoldPriceSource.TryGetMinPrices(job.ItemId, job.IsHq, out var minWorld, out _)
                    && minWorld is { } minWorldPoint
                        ? minWorldPoint.UnitPrice
                        : -1L;
                return null;
            }

            // 走到這裡有四種情形（查詢失敗、沒有資料、這個品質沒有成交紀錄、成交全部來自被排除的世界），處置相同：只剩板上比價。
            //   🔴 最後一種（拉姆已停止營運那條）刻意<b>不</b>拿別的來源代打：沒有成交紀錄的往往正是稀有的東西，
            //   湊一個價出來會賤賣。所以照樣只走板上比價，並在 log 裡標明是這個原因。
            if (state == LastSoldState.Failed)
                WarnLastSoldFallback(job, "lookup failed");
            else
                Log.Information(
                    $"{Diag} LASTSOLD-MISS item={job.ItemId} '{job.Name}' state={state} " +
                    $"hq={job.IsHq} ignoreQuality={conf.RelistLastSoldIgnoreQuality} " +
                    "excludedWorldOnly=" +
                    LastSoldPriceSource.IsSaleExcluded(
                        job.ItemId, job.IsHq, conf.RelistLastSoldIgnoreQuality) +
                    "; pricing from the market board only");

            return null;
        }

        /// <summary>
        /// 「最近一次賣出」的時間；<see cref="DateTime.MinValue"/> 一律畫成 <c>?</c>，
        /// 絕不畫成某個看起來合理的日期。
        /// </summary>
        /// <remarks>📌 實作只有一份，在 <see cref="RelistPricing.FormatAt"/>。</remarks>
        internal static string FormatSoldAt(DateTime soldAtUtc) => RelistPricing.FormatAt(soldAtUtc);

        /// <summary>
        /// 🔴 只從 framework 執行緒呼叫：它會讀 <c>PlayerState</c>。
        /// </summary>
        private static void RequestLastSoldPrices(IReadOnlyCollection<uint> itemIds)
        {
            var worldId = PlayerState.ContentId == 0 ? 0u : PlayerState.CurrentWorld.RowId;
            LastSoldPriceSource.Request(worldId, itemIds);
        }

        /// <summary>
        /// 歷史賣出價這條路走不通、這一格改用市場比價時的告知。
        /// 一整批只講一次——每一格都講會把真正的改價結果洗掉。
        /// </summary>
        private void WarnLastSoldFallback(SlotJob job, string reason)
        {
            Log.Information(
                $"{Diag} LASTSOLD-FALLBACK item={job.ItemId} '{job.Name}' reason={reason}; using market pricing instead");

            if (lastSoldFallbackWarned)
                return;
            lastSoldFallbackWarned = true;
            ChatGui.PrintError(
                "[Marketbuddy] Could not get the last sale price from Universalis; those items keep their usual pricing."
                    .Loc());
        }

        /// <summary>
        /// 遊戲內市場查詢這條路走不通了（送不出去／被伺服器拒絕／逾時，而且重試次數已經用完）。
        /// 🔑 這裡<b>不直接判失敗</b>：跨世界價格巡檢已經把全世界的掛售清單掃過一遍，
        /// 家世界那一列可以補位（見 <see cref="TryUseSurveyFallback"/>）；補不到、但這一格
        /// 有成交價候選時，照樣定得出價。<b>兩條都沒有才真的是失敗。</b>
        /// </summary>
        /// <param name="reason">真的失敗時給使用者看的原因（已在地化）。</param>
        /// <param name="tag">失敗的形狀，只進 log：<c>send-failed</c>／<c>refused</c>／<c>timeout</c>。</param>
        private TickTaskResult GiveUpOnMarketQuery(SlotJob job, string reason, string tag)
        {
            var haveSurvey = TryUseSurveyFallback(job, tag);
            if (!haveSurvey && job.SaleUnitPrice <= 0)
            {
                Fail(job, reason);
                return TickTaskResult.Done;
            }

            // 🔴 這一輪沒有拿到任何掛單，所以絕不能讓上一格留下來的內容被當成這一格的行情。
            //    （Request 階段每次嘗試都會清，這裡只是把它變成無論走哪條路都成立的前提。）
            captured.Clear();
            job.FromCache = false;
            LogQuerySummary(job, haveSurvey ? $"survey({tag})" : $"sale-only({tag})", 0, DateTime.UtcNow);
            job.Phase = SlotPhase.Apply;
            return TickTaskResult.Continue;
        }

        /// <summary>
        /// 拿巡檢記錄裡<b>家世界</b>那一列當「板上別人的最低價」。
        /// </summary>
        /// <remarks>
        /// 🔴 <b>只認家世界</b>。別的世界的掛單永遠不會變成定價依據——別人在別的世界比我便宜，
        /// 不代表我在自己的市場上吃虧。（待處理清單上的 <c>survey-other</c> 是純顯示，那是另一件事。）
        /// 🔴 讀的是 <see cref="PriceSurveySnapshot"/> 這份記憶體索引，<b>不是檔案</b>：
        /// 這裡是 framework 執行緒，讀一個幾萬列的 CSV 就是掉幀。
        /// </remarks>
        private bool TryUseSurveyFallback(SlotJob job, string tag)
        {
            if (conf.RelistSurveyFallbackHours <= 0)
                return false;

            // 🔴 家世界，不是「現在站著的世界」：僱員的掛單掛在家世界的市場上。
            var homeWorldId = PlayerState.ContentId == 0 ? 0u : PlayerState.HomeWorld.RowId;
            if (homeWorldId == 0)
                return false;

            if (!PriceSurveySnapshot.TryGetLowest(job.ItemId, job.IsHq, homeWorldId, DateTime.UtcNow,
                    conf.RelistSurveyFallbackHours, out var lowest, out var atUtc))
            {
                Log.Information(
                    $"{Diag} SURVEY-MISS item={job.ItemId} '{job.Name}' hq={job.IsHq} " +
                    $"homeWorld={homeWorldId} reason={tag} maxAgeHours={conf.RelistSurveyFallbackHours} " +
                    $"indexLoaded={PriceSurveySnapshot.IsLoaded} indexCount={PriceSurveySnapshot.Count}");
                return false;
            }

            job.SurveyLowest = lowest;
            job.SurveyAtUtc = atUtc;
            Log.Information(
                $"{Diag} SURVEY-FALLBACK item={job.ItemId} '{job.Name}' hq={job.IsHq} " +
                $"lowest={lowest} at={RelistPricing.FormatAt(atUtc)} reason={tag}");
            return true;
        }

        /// <summary>
        /// 這一格的定價決策：算出 <b>L</b>（板上別人的最低價）與 <b>S</b>（資料中心最近一筆成交價）
        /// 兩個候選，<b>取低者</b>，然後交給 <see cref="FinishPricing"/>。
        /// 🔑 規則本體在 <see cref="RelistPricing"/>，那裡是唯一真值來源，而且
        /// <see cref="PendingActionsBuilder"/> 用的是<b>同一支</b>函式——清單上寫的建議價與
        /// 按下按鈕之後真的掛出去的價因此不可能分岔。這一段只負責「把輸入在正確的執行緒上取好」。
        /// </summary>
        private void ApplySlot(SlotJob job)
        {
            var cacheTag = job.FromCache ? " " + "(cached price)".Loc() : string.Empty;
            var now = DateTime.UtcNow;
            var listedPrice = CurrentListedPrice(job.Slot);
            // 🔴 快速上架的那一格停在上限價，那不是一個真的價格，絕不能當異常判定的基準。
            var ownBasis = job.QuickListed ? -1L : listedPrice;

            var undercut = conf.UndercutRuleSnapshot();
            var anomalyRule = new RelistPricing.AnomalyRule(
                conf.AnomalyGuardEnabled, conf.AnomalyGuardMinNormalPrice, conf.AnomalyGuardRatio);

            // ---------------- 候選 L：板上（家世界）別人的最低掛售價 ----------------
            long competitor = -1;
            long peerBaseline = -1;
            var ownIsLowest = false;
            var ownLowest = -1L;
            var listingSource = string.Empty;
            var listingAt = DateTime.MinValue;

            if (job.SurveyLowest > 0)
            {
                // 遊戲內查詢整個失敗，用的是巡檢掃到的那一列。
                // ⚠️ 那一列只有一個最低價、沒有原始掛單 ⇒ 同業基準拿不到（-1），
                //    異常低價保護只能退回「拿自己的現價比」（AnomalyBaseline.Own）。
                competitor = job.SurveyLowest;
                listingSource = PriceSourceTag.Survey;
                listingAt = job.SurveyAtUtc;
            }
            else if (captured.Count > 0)
            {
                competitor = RelistPricing.LowestCompetitor(
                    captured, job.IsHq, conf.BatchCompareHqOnly, ownRetainerIds,
                    out peerBaseline, out ownIsLowest, out ownLowest);
                listingSource = job.FromCache ? PriceSourceTag.Cache : PriceSourceTag.Live;
                listingAt = now;
            }

            // 🔑 板上最便宜的那一筆是我們自己掛的時候，L 一律當成「沒有」：
            //    板上沒有給我們任何「該降到多少」的新資訊，而且我們不會自己壓自己的價。
            var listing = ownIsLowest
                ? PriceCandidate.None
                : RelistPricing.FromListing(
                    competitor, peerBaseline, ownBasis, listingSource, listingAt, undercut, anomalyRule);

            // ---------------- 候選 S：資料中心最近一筆成交價 ----------------
            var sale = RelistPricing.FromSale(
                job.SaleUnitPrice, job.SaleAtUtc, job.SalePeerBaseline, ownBasis,
                now, conf.RelistLastSoldMaxAgeDays, anomalyRule);

            var decision = RelistPricing.Decide(listing, sale);
            LogPricingDecision(job, listing, sale, decision, listedPrice, ownIsLowest, competitor);

            switch (decision.Outcome)
            {
                case RelistOutcome.Hold:
                    HoldForAnomaly(job, decision.Winner.Anomaly, listedPrice,
                        ReferenceKindOf(decision.Winner), cacheTag);
                    return;

                case RelistOutcome.NoData:
                    if (ownIsLowest)
                    {
                        HandleOwnIsLowest(job, ownLowest, cacheTag);
                        return;
                    }

                    HandleNoListings(job, cacheTag);
                    return;
            }

            var target = decision.Price;

            if (ownIsLowest)
            {
                // 走到這裡代表唯一的目標價來自成交價（L 在上面已經被當成「沒有」），
                // 而成交價是與板上完全獨立的觀測 ⇒ 只往下用，絕不拿它把價格往上抬。
                if (job.QuickListed)
                {
                    // 停在上限價的那一格絕不能留在上限價上；與自家最低價齊平，除非成交價更低。
                    if (ownLowest > 0 && ownLowest < target)
                    {
                        HandleOwnIsLowest(job, ownLowest, cacheTag);
                        return;
                    }
                }
                else if (listedPrice < 0 || listedPrice <= target)
                {
                    HandleOwnIsLowest(job, ownLowest, cacheTag);
                    return;
                }
            }

            FinishPricing(job, (uint)target, cacheTag, null, SourceNoteFor(decision.Winner));
        }

        /// <summary>
        /// 板上最便宜的那一筆是我們自己掛的 ⇒ 一般格什麼都不做，
        /// 快速上架那一格與自家最低價齊平（它停在上限價上，那代表沒有人買得到）。
        /// </summary>
        private void HandleOwnIsLowest(SlotJob job, long ownLowest, string cacheTag)
        {
            if (!job.QuickListed)
            {
                Skip(job, "[Marketbuddy] ??: your own listing is already the lowest (?? gil)".Loc(
                    job.Name, ownLowest) + cacheTag);
                return;
            }

            if (ownLowest > 0)
            {
                FinishPricing(job,
                    (uint)Math.Clamp(ownLowest, Configuration.MIN_PRICE, Configuration.MAX_PRICE), cacheTag,
                    "[Marketbuddy] ??: matched your own lowest listing at ?? gil".Loc(job.Name, ownLowest)
                    + cacheTag);
                return;
            }

            HandleNoListings(job, cacheTag);
        }

        /// <summary>異常低價保護的 log 欄位 <c>refKind</c>：參考價是哪一種。</summary>
        private static string ReferenceKindOf(PriceCandidate candidate)
            => candidate.Source == PriceSourceTag.Sale
                ? $"sale@{FormatSoldAt(candidate.AtUtc)}"
                : candidate.Source.Length == 0
                    ? "listing"
                    : candidate.Source;

        /// <summary>
        /// 聊天成功訊息尾端那一句「這個價是怎麼來的」。
        /// 🔑 使用者按下去之後唯一看得到的東西就是那一行聊天訊息，而「跟板上最低價」與
        /// 「照最近成交價」是兩個完全不同的決定——不講清楚等於叫他去猜。
        /// </summary>
        private static string SourceNoteFor(PriceCandidate candidate)
        {
            if (!candidate.IsObserved)
                return string.Empty;

            var reference = candidate.Reference.ToString("N0");
            return candidate.Source switch
            {
                PriceSourceTag.Sale => " " + "(last sold ?? gil, ??)".Loc(
                    reference, FormatSoldAt(candidate.AtUtc)),
                PriceSourceTag.Survey => " " + "(lowest listing ?? gil, cross-world scan ??)".Loc(
                    reference, FormatSoldAt(candidate.AtUtc)),
                PriceSourceTag.Live or PriceSourceTag.Cache => " " + "(lowest listing ?? gil)".Loc(reference),
                _ => string.Empty,
            };
        }

        /// <summary>
        /// 一格一行的定價決策紀錄。
        /// </summary>
        /// <remarks>
        /// 🔴 要使用者回報的診斷（寫 <c>Information</c>）：它就是「事後查得到為什麼那一件掛在這個價」的那一行。
        /// 📌 <c>boardCompetitor</c> 是獨立欄位而不是從 <c>L=</c> 讀：自家掛單是最低價時 L 會被
        /// 當成「沒有」，那時候板上別人開多少仍然是判讀時必要的資訊。
        /// </remarks>
        private void LogPricingDecision(SlotJob job, PriceCandidate listing, PriceCandidate sale,
            RelistDecision decision, long listedPrice, bool ownIsLowest, long competitor)
        {
            var winner = decision.Winner.Source.Length == 0 ? "?" : decision.Winner.Source;
            var pick = decision.Outcome switch
            {
                RelistOutcome.Price => winner,
                RelistOutcome.Hold => $"hold({winner})",
                _ => "none",
            };

            Log.Information(
                $"{Diag} PRICE item={job.ItemId} '{job.Name}' hq={job.IsHq} ours={listedPrice} " +
                $"L={RelistPricing.Describe(listing)} S={RelistPricing.Describe(sale)} " +
                $"ownIsLowest={ownIsLowest} boardCompetitor={competitor} listings={captured.Count} " +
                $"pick={pick} final={(decision.Outcome == RelistOutcome.Price ? decision.Price.ToString() : "-")} " +
                $"saleMaxAgeDays={conf.RelistLastSoldMaxAgeDays} " +
                $"surveyHours={conf.RelistSurveyFallbackHours} quickListed={job.QuickListed}");
        }

        /// <summary>
        /// No market listings for this item. Normal batch slots just keep their
        /// price; quick-listed slots are parked at the price cap and are left
        /// there on purpose - with no reference price, any number we made up
        /// would be a guess. Warn loudly and count the slot as needing a price
        /// by hand.
        /// </summary>
        /// <remarks>
        /// 板上沒人賣的東西往往正是稀有的。最低價欄位的語意是「算出來的價低於它就別掛了」
        /// （見 <see cref="FinishPricing"/> 的下架守衛），不是「查不到價就拿它當價格」。
        /// </remarks>
        private void HandleNoListings(SlotJob job, string cacheTag)
        {
            if (!job.QuickListed)
            {
                // Nothing on sale is a normal situation, not a failure: quiet
                // debug log, counted as skipped.
                Log.Debug($"BatchReprice: slot {job.Slot} ({job.Name}) has no market listings, skipped");
                Skip(job, "[Marketbuddy] ??: no one is selling this item, skipped".Loc(job.Name) + cacheTag);
                return;
            }

            ProcessedSlots++;
            NeedsPricingCount++;
            Log.Warning($"BatchReprice: quick-listed slot {job.Slot} ({job.Name}) has no market data; still listed at the price cap");
            ChatGui.PrintError("[Marketbuddy] ??: no market data - still listed at the price cap (??), set a price manually!".Loc(job.Name, Configuration.MAX_PRICE));

            // 🔴 聊天訊息會被洗掉，NeedsPricingCount 每一批就歸零 —— 沒有這一行，這一格
            //    就只存在於聊天記錄裡，之後再也找不回來。
            //    這裡**只記錄**，不改任何價格。
            RecordNeedsPricing(job);
            NoteNeedsPricing(job);
        }

        /// <summary>Slot re-validation, delist thresholds, then the actual price update.</summary>
        /// <param name="successMessage">
        /// 整句取代預設的成功訊息；null＝用預設那句（「道具：舊價 → 新價」）。
        /// </param>
        /// <param name="sourceNote">
        /// 接在預設成功訊息尾端的「這個價是怎麼來的」。
        /// 🔑 <paramref name="successMessage"/> 不為 null 時<b>不會</b>用到它（那一句自己就講完了）。
        /// </param>
        private void FinishPricing(SlotJob job, uint newPrice, string cacheTag, string? successMessage,
            string sourceNote = "")
        {
            var inventoryManager = InventoryManager.Instance();
            var slot = GetMarketSlot(job.Slot);
            if (inventoryManager == null || slot == null || slot->ItemId != job.ItemId)
            {
                Skip(job, "[Marketbuddy] ??: slot changed, skipped".Loc(job.Name));
                return;
            }

            // Delist guards: if the price we are about to set is not worth
            // keeping on the market, take the item off the board instead.
            // Both features are opt-in and default to off.
            if (conf.BatchDelistBelowVendor && job.VendorUnitPrice > 0)
            {
                var taxPercent = CurrentTaxPercent();
                var netUnit = newPrice * (100L - taxPercent) / 100L;
                if (netUnit < job.VendorUnitPrice)
                {
                    DelistSlot(job, inventoryManager, slot,
                        "[Marketbuddy] ??: market net ?? < NPC ?? gil, delisted".Loc(job.Name, netUnit, job.VendorUnitPrice) + cacheTag,
                        VendorSellCause.BelowVendor);
                    return;
                }
            }

            if (conf.BatchMinPrice > 0 && newPrice < (uint)conf.BatchMinPrice)
            {
                DelistSlot(job, inventoryManager, slot,
                    "[Marketbuddy] ??: target price ?? below your minimum ??, delisted".Loc(job.Name, newPrice, conf.BatchMinPrice) + cacheTag,
                    VendorSellCause.BelowMinPrice);
                return;
            }

            var current = inventoryManager->GetRetainerMarketPrice(job.Slot);
            if (current == newPrice)
            {
                // 已經在該有的價格上 ＝ 這一格沒事了，同樣要從待處理清單上消失。
                PendingActions.ClearSlot(CurrentBatchRetainerId, job.Slot);
                Skip(job, "[Marketbuddy] ??: already at ?? gil".Loc(job.Name, newPrice) + cacheTag);
                return;
            }

            inventoryManager->SetRetainerMarketPrice(job.Slot, newPrice);
            NoteChange(job.Slot, (uint)current, newPrice);
            // 這一格處理完了，從「待處理」清單上消失。
            PendingActions.ClearSlot(CurrentBatchRetainerId, job.Slot);
            ProcessedSlots++;
            RepricedCount++;
            ChatGui.Print(successMessage
                          ?? "[Marketbuddy] ??: ?? → ?? gil".Loc(job.Name, current, newPrice)
                          + sourceNote + cacheTag);
        }

        private void OnOfferingsReceived(IMarketBoardCurrentOfferings offerings)
        {
            // ⚠️ 這個處理器**只**負責「我們正在等的那一件」的快通道。把每一筆封包都存進
            // 快取是 MarketDataCache 自己那個獨立的訂閱做的事——包括下面每一條 dropped
            // 分支所丟掉的封包，它們一樣都已經被存起來了，只是不會在這裡被採用。
            var listings = offerings.ItemListings;

            if (!offeringsPending)
            {
                // 遊戲裡任何一次掛單查詢都會走到這裡（別的外掛、使用者自己逛市場），
                // 與本次重掛無關：逐筆記會蓋掉真正的訊號，只記節流後的累計筆數。
                NoteForeignOfferings();
                return;
            }

            MarketDiag.TracePacket(
                $"{Diag} OFFERINGS reqId={offerings.RequestId} count={listings.Count} " +
                $"firstItem={(listings.Count > 0 ? listings[0].ItemId : 0)} " +
                $"pendingItem={pendingItemId} pending={offeringsPending} " +
                $"lastAcceptedReqId={lastAcceptedRequestId}");

            if (listings.Count > 0)
            {
                // Later pages of a batch we already consumed share its RequestId.
                if (offerings.RequestId == lastAcceptedRequestId)
                {
                    MarketDiag.TracePacket(
                        $"{Diag} OFFERINGS dropped: RequestId == lastAcceptedRequestId ({offerings.RequestId})");
                    return;
                }

                // Stale response for a previously requested item: ignore.
                if (listings[0].ItemId != pendingItemId)
                {
                    MarketDiag.TracePacket(
                        $"{Diag} OFFERINGS dropped: itemId mismatch (got {listings[0].ItemId}, want {pendingItemId})");
                    return;
                }
            }

            // An empty response is a definitive "nothing on sale" and is always accepted
            // while a request is pending: it cannot be a later page of an earlier batch
            // (those always carry entries), it cannot be attributed by ItemId, and only a
            // single request is ever in flight. Deliberately no RequestId check either,
            // so a non-incrementing RequestId can never make us drop it.

            lastAcceptedRequestId = offerings.RequestId;
            captured.Clear();
            foreach (var listing in listings)
                captured.Add((listing.PricePerUnit, listing.IsHq, listing.RetainerId));

            offeringsPending = false;
            offeringsReceived = true;
            lastDataReceivedAt = DateTime.UtcNow;
            MarketDiag.TracePacket($"{Diag} OFFERINGS accepted: {captured.Count} listings for item {pendingItemId}");
        }

        /// <summary>記一筆「不是我們在等的」掛單封包，節流後只寫累計筆數。</summary>
        /// <remarks>🔴 只從市場封包處理器呼叫（與這個類別其餘的封包狀態同一條執行緒）。</remarks>
        private void NoteForeignOfferings()
        {
            foreignOfferings++;
            var now = DateTime.UtcNow;
            if ((now - foreignOfferingsReportedAt).TotalSeconds < ForeignOfferingsReportSeconds)
                return;
            foreignOfferingsReportedAt = now;
            MarketDiag.TracePacket(
                $"{Diag} OFFERINGS ignored (no request pending): {foreignOfferings} since load");
        }

        private void OnHistoryReceived(IMarketBoardHistory history)
        {
            // 不是我們在等的那一筆就直接離開、不記錄（遊戲裡每次掛單查詢都會來一筆）。
            if (!offeringsPending || historySeen)
                return;

            MarketDiag.TracePacket(
                $"{Diag} HISTORY item={history.ItemId} pendingItem={pendingItemId} " +
                $"pending={offeringsPending} alreadySeen={historySeen}");

            if (history.ItemId != pendingItemId)
                return;

            historySeen = true;
            historySeenAt = DateTime.UtcNow;
        }

        private void OnTaxRatesReceived(IMarketTaxRates rates)
        {
            // The packet is a generic "result dialog"; sanity-check the values
            // before trusting it (rates are single-digit percentages).
            if (rates.ValidUntil <= DateTime.UtcNow)
                return;
            if (rates.LimsaLominsaTax > 25 || rates.GridaniaTax > 25 || rates.UldahTax > 25)
                return;
            taxRates = rates;
            Log.Debug($"BatchReprice: cached market tax rates (valid until {rates.ValidUntil:u})");
        }

        /// <summary>
        /// Market tax percent for the active retainer's city; falls back to the configured constant.
        /// ⚠️ internal（不是 private）是因為 <see cref="RetainerMarketWatcher"/> 也要用同一份稅率
        /// 去對僱員錢包的增量——兩邊各算一份的話，某一天有人改了退回值就會靜默分岔。
        /// </summary>
        internal uint CurrentTaxPercent()
        {
            var fallback = (uint)Math.Clamp(conf.MarketTaxPercent, 0, 25);
            if (taxRates == null || taxRates.ValidUntil <= DateTime.UtcNow)
                return fallback;

            var retainerManager = RetainerManager.Instance();
            var active = retainerManager == null ? null : retainerManager->GetActiveRetainer();
            if (active == null)
                return fallback;

            uint? rate = active->Town switch
            {
                RetainerManager.RetainerTown.LimsaLominsa => taxRates.LimsaLominsaTax,
                RetainerManager.RetainerTown.Gridania => taxRates.GridaniaTax,
                RetainerManager.RetainerTown.Uldah => taxRates.UldahTax,
                RetainerManager.RetainerTown.Ishgard => taxRates.IshgardTax,
                RetainerManager.RetainerTown.Kugane => taxRates.KuganeTax,
                RetainerManager.RetainerTown.Crystarium => taxRates.CrystariumTax,
                RetainerManager.RetainerTown.OldSharlayan => taxRates.SharlayanTax,
                _ => null,
            };
            return rate is null or > 25 ? fallback : rate.Value;
        }

        /// <param name="vendorCause">
        /// 重掛時的兩道自動下架才給；有值而且收回玩家背包時，這一件會記進「賣給商店」那一輪（見 <see cref="VendorSeller"/>）。
        /// </param>
        private void DelistSlot(SlotJob job, InventoryManager* inventoryManager, InventoryItem* slot, string chatMessage,
            VendorSellCause? vendorCause = null)
        {
            var quantity = (uint)Math.Max(1, slot->Quantity);
            var vendorTracked = false;

            // Destination is a user setting (player inventory by default).
            // Deliberately NO silent fallback to the other container: a full
            // destination is a reported failure and the batch moves on.
            int result;
            string destinationTag;
            if (conf.DelistToRetainerInventory)
            {
                if (!HasFreeRetainerInventorySlot(inventoryManager))
                {
                    Fail(job, "retainer inventory is full, cannot delist".Loc());
                    return;
                }

                result = inventoryManager->MoveFromRetainerMarketToRetainerInventory(
                    InventoryType.RetainerMarket, (ushort)job.Slot, quantity);
                destinationTag = " " + "(moved to retainer inventory)".Loc();
            }
            else
            {
                if (inventoryManager->GetEmptySlotsInBag() == 0)
                {
                    Fail(job, "your inventory is full, cannot delist".Loc());
                    return;
                }

                // 背包「下架前」的樣子必須在取回之前記下，取回之後東西可能已經在背包裡了。
                vendorTracked = vendorCause != null && gui.VendorSeller?.NoteBeforeDelist(job.ItemId, job.IsHq) == true;
                result = inventoryManager->MoveFromRetainerMarketToPlayerInventory(
                    InventoryType.RetainerMarket, (ushort)job.Slot, quantity);
                destinationTag = " " + "(moved to your inventory)".Loc();
            }

            Log.Information(
                $"BatchReprice: delist slot {job.Slot} ({job.Name}) qty {quantity}, move returned {result}");

            // 🔴 一定要看回傳值：不看的話僱員／玩家背包放不下時，畫面上會說下架成功，
            // 東西卻還掛在市場上。回傳值的意義整理在 BatchDelist 的常數區，
            // 那裡是唯一真值來源；這裡只要知道 0 才是成功。
            if (result != BatchDelist.MoveOk)
            {
                // ⚠️ 錯誤字串必須跟著目的地走：兩支取回函式的回傳碼數值相同，但
                // 0x19／0x1C 講的是不同的容器（遊戲自己印的 LogMessage 就分兩套）。
                Fail(job, BatchDelist.DescribeMoveError(result, conf.DelistToRetainerInventory));
                return;
            }

            // 記進「我方下架」的短期帳：僱員銷售紀錄靠它把這一格的消失跟真正的賣出分開。
            // 只是記錄，記漏了最壞的結果是那一列變成低信心（見 RetainerDelistLedger）。
            RetainerDelistLedger.Note(CurrentBatchRetainerId, job.ItemId, job.IsHq, (int)quantity);
            if (vendorTracked && vendorCause is { } cause)
                gui.VendorSeller?.NoteDelisted(job.ItemId, job.IsHq, (int)quantity, cause, job.VendorUnitPrice, job.Name);

            ForgetChange(job.Slot);
            PendingActions.ClearSlot(CurrentBatchRetainerId, job.Slot);
            ProcessedSlots++;
            DelistedCount++;
            ChatGui.Print(chatMessage + destinationTag);
        }

        /// <summary>
        /// Shared guard for interactive listings (manual flow and
        /// AutoRetainer's quick "put up for sale"): same thresholds as the
        /// batch delist logic, applied to the price about to be entered.
        /// Gated by the same opt-in settings; itemId 0 (unknown) skips the
        /// vendor comparison, the minimum-price check is item-independent.
        /// </summary>
        public bool ShouldBlockListing(uint price, uint itemId, bool isHq, out string reason)
        {
            reason = string.Empty;

            if (conf.BatchDelistBelowVendor && itemId != 0)
            {
                var itemSheet = DataManager.GetExcelSheet<Item>();
                if (itemSheet != null && itemSheet.TryGetRow(itemId, out var row) && row.PriceLow > 0)
                {
                    var vendorUnit = isHq ? row.PriceLow + 1u : row.PriceLow;
                    var netUnit = price * (100L - CurrentTaxPercent()) / 100L;
                    if (netUnit < vendorUnit)
                    {
                        reason = "market net ?? < NPC ?? gil".Loc(netUnit, vendorUnit);
                        return true;
                    }
                }
            }

            if (conf.BatchMinPrice > 0 && price < (uint)conf.BatchMinPrice)
            {
                reason = "price ?? below your minimum ??".Loc(price, conf.BatchMinPrice);
                return true;
            }

            return false;
        }

        private static bool HasFreeRetainerInventorySlot(InventoryManager* inventoryManager)
        {
            for (var type = InventoryType.RetainerPage1; type <= InventoryType.RetainerPage7; type++)
            {
                var container = inventoryManager->GetInventoryContainer(type);
                if (container == null || !container->IsLoaded)
                    continue;
                for (var i = 0; i < container->Size; i++)
                {
                    var slot = inventoryManager->GetInventorySlot(type, i);
                    if (slot != null && slot->ItemId == 0)
                        return true;
                }
            }

            return false;
        }

        private void OnQueueAborted(string reason)
        {
            MarketRequestGate.LogSummary("batch aborted");
            ResetRequestState();
            // 「只下架」批次一個價格都沒動過，印重掛的措辭會讓人以為價格被改了。
            if (delistOnlyBatch)
                ChatGui.PrintError("[Marketbuddy] Delisting cancelled: ?? (?? delisted, ?? skipped, ?? failed)"
                    .Loc(reason, DelistedCount, SkippedCount, FailedCount));
            else
                ChatGui.PrintError("[Marketbuddy] Relist cancelled: ?? (?? repriced, ?? skipped, ?? delisted, ?? failed)"
                    .Loc(reason, RepricedCount, SkippedCount, DelistedCount, FailedCount));
            if (AutoRetainerBridge.IsBusy)
            {
                AutoRetainerBridge.ArmAvailabilityNotice();
                ChatGui.Print("[Marketbuddy] Wait for AutoRetainer to finish, then press the button again.".Loc());
            }

            BatchAborted?.Invoke(reason);
        }

        private void OnQueueCompleted()
        {
            // 每一輪（每個僱員 / 每件快速上架）印一次閘門軌跡：這是事後判斷
            // 「往下探 → 成功還是被拒 → 收斂到多少」不必逐行翻 REQUEST 的入口。
            MarketRequestGate.LogSummary("batch finished");
            ResetRequestState();
            if (delistOnlyBatch)
                ChatGui.Print("[Marketbuddy] Delisting finished: ?? delisted, ?? skipped, ?? failed"
                    .Loc(DelistedCount, SkippedCount, FailedCount));
            else
                ChatGui.Print("[Marketbuddy] Relist finished: ?? repriced, ?? skipped, ?? delisted, ?? failed"
                    .Loc(RepricedCount, SkippedCount, DelistedCount, FailedCount));

            // 查不到比價資料、刻意留在上限價的那幾件：它們不是失敗，但**一定**要人工介入，
            // 所以獨立講一行；件數是 0 時整行不出現。
            if (NeedsPricingCount > 0)
                ChatGui.PrintError(
                    "[Marketbuddy] ?? item(s) have no market data and are still listed at the price cap - price them by hand."
                        .Loc(NeedsPricingCount));

            HintAboutStaleSellList();

            // 純通知，零行為：請「塔塔露誇獎」念一句「重掛跑完了」。
            // ⚠️ 兩道閘都是為了「不要洗版」：
            //   ① 巡迴在驅動時不響——那由巡迴自己在整輪收尾時響一次（MultiRetainerTour.OnQueueCompleted）。
            //   ② 快速上架的單件定價不響——那是每上架一件跑一次的高頻動作。
            // 🔴 這裡在 Framework.Update → queue.Update() 的鏈上（OnFrameworkUpdate），是主執行緒。
            if (ExternalDriverActive?.Invoke() != true && !quickRepriceBatch && !delistOnlyBatch)
                TataruPraiseIPC.TryPraise("單僱員重掛完成");

            BatchFinished?.Invoke();
        }

        /// <summary>Set once the stale-sell-list hint has been shown this session.</summary>
        private static bool staleListHintShown;

        /// <summary>
        /// Prices are written straight into the retainer's market container, so the game's
        /// own sell list never redraws - it keeps showing whatever it showed when it opened.
        /// The plugin can draw a live list instead (see <see cref="LiveSellList"/>), but that
        /// is opt-in, so point the player at it once per session when it is off and something
        /// actually changed. One line, once - deliberately not every batch.
        /// </summary>
        private void HintAboutStaleSellList()
        {
            if (staleListHintShown || conf.LiveSellListOverlay)
                return;
            if (RepricedCount == 0 && DelistedCount == 0)
                return;

            staleListHintShown = true;
            // 批次型別不是判準：一般重掛批次也可能一件都沒改價、只下架了幾件。
            ChatGui.Print(RepricedCount > 0
                ? "[Marketbuddy] The game's sell list does not redraw itself, so it still shows the old prices - the new ones are already on the server. Turn on \"Show a live sell list next to the game's one\" in /mbuddy to see them right away."
                    .Loc()
                : "[Marketbuddy] The game's sell list does not redraw itself, so the items you just delisted can still show up on it. Turn on \"Show a live sell list next to the game's one\" in /mbuddy to see what is really still listed."
                    .Loc());
        }

        /// <summary>
        /// Records a price change for the live sell list overlay (display only).
        /// Slots are per-retainer, so the marks are dropped the moment a different
        /// retainer becomes active - otherwise retainer B's slot 3 would inherit
        /// retainer A's "changed" flag.
        /// </summary>
        private void NoteChange(short slot, uint oldPrice, uint newPrice)
        {
            var retainerId = ActiveRetainerId();
            if (retainerId != recentChangesRetainerId)
            {
                recentChanges.Clear();
                recentChangesRetainerId = retainerId;
            }

            recentChanges[slot] = (oldPrice, newPrice, DateTime.UtcNow);
        }

        private void ForgetChange(short slot)
        {
            if (ActiveRetainerId() == recentChangesRetainerId)
                recentChanges.Remove(slot);
        }

        /// <summary>
        /// 把這一格記進面板上那份「停在上限價」清單。
        /// 🔴 純顯示＋手動下架的入口，<b>一個 gil 都不改</b>；換了僱員就整份作廢。
        /// </summary>
        private void NoteNeedsPricing(SlotJob job)
        {
            var retainerId = ActiveRetainerId();
            if (retainerId != needsPricingRetainerId)
            {
                needsPricing.Clear();
                needsPricingRetainerId = retainerId;
            }

            needsPricingRetainerName = RetainerNameOf(retainerId);
            ForgetNeedsPricing(job.Slot);
            needsPricing.Add(new NeedsPricingEntry(job.Slot, job.ItemId, job.IsHq, job.Name));
        }

        private void ForgetNeedsPricing(short slot)
        {
            for (var i = needsPricing.Count - 1; i >= 0; i--)
            {
                if (needsPricing[i].Slot == slot)
                    needsPricing.RemoveAt(i);
            }
        }

        /// <summary>
        /// 那份清單跟著容器走：換了僱員整份作廢，那一格的東西不在了（賣掉、下架、
        /// 換成別的）就移掉。清單空的時候整支零成本，所以每幀呼叫是安全的。
        /// </summary>
        private void PruneNeedsPricing()
        {
            if (needsPricing.Count == 0)
                return;

            if (ActiveRetainerId() != needsPricingRetainerId)
            {
                needsPricing.Clear();
                return;
            }

            for (var i = needsPricing.Count - 1; i >= 0; i--)
            {
                var slot = GetMarketSlot(needsPricing[i].Slot);
                if (slot == null || slot->ItemId != needsPricing[i].ItemId)
                    needsPricing.RemoveAt(i);
            }
        }

        /// <summary>
        /// 把這一格記進「待處理」清單的「掛在上限價」桶。
        /// 🔴 只記錄，不改任何價格；建議價留空（-1＝不知道），之後由待處理清單自己重算。
        /// </summary>
        private void RecordNeedsPricing(SlotJob job)
        {
            var inventoryManager = InventoryManager.Instance();
            var current = inventoryManager == null
                ? -1L
                : (long)inventoryManager->GetRetainerMarketPrice(job.Slot);

            PendingActions.Upsert(new PendingActionRow(
                DateTime.UtcNow, PendingActionKind.PriceCap, job.ItemId, job.IsHq,
                CurrentBatchRetainerId, RetainerNameOf(CurrentBatchRetainerId), job.Slot,
                current, -1, string.Empty, string.Empty, DateTime.MinValue, false));
        }

        /// <summary>
        /// 某一格目前的掛售單價；讀不到時 -1（＝不知道，<b>不是 0</b>）。
        /// </summary>
        /// <remarks>🔴 只從 framework 執行緒呼叫：它會解參考 <c>InventoryManager</c>。</remarks>
        private static long CurrentListedPrice(short slot)
        {
            var inventoryManager = InventoryManager.Instance();
            return inventoryManager == null ? -1L : (long)inventoryManager->GetRetainerMarketPrice(slot);
        }

        /// <summary>
        /// 判定為異常低價之後的完整處置：<b>一個 gil 都不改</b>，只記錄與告知。
        /// </summary>
        /// <param name="listedPrice">這一格目前的掛售價，只拿來顯示；-1＝讀不到。</param>
        /// <param name="referenceKind">參考價是哪一種（<c>listing</c> 或 <c>sale@時間</c>），只進 log。</param>
        private void HoldForAnomaly(SlotJob job, PriceAnomaly anomaly, long listedPrice,
            string referenceKind, string cacheTag)
        {
            Log.Information(
                $"{Diag} ANOMALY-HOLD item={job.ItemId} '{job.Name}' hq={job.IsHq} " +
                $"ours={listedPrice} refKind={referenceKind} reference={anomaly.Reference} " +
                $"normal={anomaly.NormalPrice} baseline={anomaly.Tag} ratio={anomaly.RatioText}x " +
                $"minNormal={conf.AnomalyGuardMinNormalPrice} mult={conf.AnomalyGuardRatio} " +
                $"quickListed={job.QuickListed}");

            RecordAnomaly(job, anomaly, listedPrice);

            var ours = listedPrice < 0 ? "?" : listedPrice.ToString("N0");
            var reference = anomaly.Reference.ToString("N0");
            var normal = anomaly.NormalPrice.ToString("N0");

            if (job.QuickListed)
            {
                // 快速上架的那一格本來就停在上限價：保護不改價，所以它還在上限價上，
                // 而那代表沒有人買得到 —— 必須算進「一定要人工介入」的那個數。
                ProcessedSlots++;
                NeedsPricingCount++;
                ChatGui.PrintError(
                    "[Marketbuddy] ??: ?? gil looks like a mistyped price (??x below the ?? gil that looks normal), so this is still at the price cap - price it by hand"
                        .Loc(job.Name, reference, anomaly.RatioText, normal) + cacheTag);
                return;
            }

            Skip(job,
                "[Marketbuddy] ??: kept at ?? gil - ?? gil looks like a mistyped price (??x below the ?? gil that looks normal), so it was not used"
                    .Loc(job.Name, ours, reference, anomaly.RatioText, normal) + cacheTag);
        }

        /// <summary>
        /// 把這一格記進「待處理」清單的「疑似打錯的低價」桶。
        /// 🔴 只記錄，不改任何價格。
        /// </summary>
        /// <remarks>
        /// ⚠️ 這一桶借用了兩個欄位（理由與語意寫在 <see cref="PendingActionKind.PriceAnomaly"/> 上）：
        /// <c>SuggestedPrice</c>＝被擋下來的那個可疑價，<c>SuggestionWorld</c>＝拿來比的正常價。
        /// </remarks>
        private void RecordAnomaly(SlotJob job, PriceAnomaly anomaly, long listedPrice)
        {
            PendingActions.Upsert(new PendingActionRow(
                DateTime.UtcNow, PendingActionKind.PriceAnomaly, job.ItemId, job.IsHq,
                CurrentBatchRetainerId, RetainerNameOf(CurrentBatchRetainerId), job.Slot,
                listedPrice, anomaly.Reference, anomaly.Tag,
                anomaly.NormalPrice.ToString("N0"), DateTime.UtcNow, false, EngineHeld: true));
        }

        /// <summary>
        /// 某一位僱員的名字（只給人看，任何比對一律用 <c>RetainerId</c>）。
        /// ⚠️ 只查得到<b>目前角色</b>的僱員；別的角色的僱員回空字串（＝「不知道」）。
        /// </summary>
        public static string RetainerNameOf(ulong retainerId)
        {
            if (retainerId == 0)
                return string.Empty;
            var retainerManager = RetainerManager.Instance();
            if (retainerManager == null)
                return string.Empty;
            foreach (var retainer in retainerManager->Retainers)
            {
                if (retainer.RetainerId == retainerId)
                    return retainer.NameString;
            }

            return string.Empty;
        }

        /// <summary>Content id of the retainer currently being interacted with, or 0.</summary>
        public static ulong ActiveRetainerId()
        {
            var retainerManager = RetainerManager.Instance();
            var active = retainerManager == null ? null : retainerManager->GetActiveRetainer();
            return active == null ? 0ul : active->RetainerId;
        }


        private void ResetRequestState()
        {
            ReleaseSuppressionIfHeld();
            offeringsPending = false;
            offeringsReceived = false;
            historySeen = false;
            CurrentItemName = string.Empty;
            CurrentSlot = -1;
            // 這一輪結束了，槽裡任何還沒被取走的答覆都已經無主，丟掉。
            MarketRequestResultProbe.ArmForRequest();

            var proxy = GetItemSearchProxy();
            if (proxy == null)
                return;

            // ⚠️ 台服的 vf10 (`EndRequest`) 是空函式，所以**它自己什麼都不重設**；
            // 真正的重設是下面兩行（遊戲自己在 ProcessRequestResult 尾段做的也是同兩個欄位）。
            // 呼叫仍然保留：零成本，且改版把它變回實作時會自動生效。
            proxy->EndRequest();
            proxy->ListingCount = 0;
            proxy->EntryCount = 0;
        }

        private void Skip(SlotJob job, string chatMessage)
        {
            ProcessedSlots++;
            SkippedCount++;
            ChatGui.Print(chatMessage);
        }

        /// <summary>
        /// 一次查價一行的摘要。走診斷開關（<see cref="MarketDiag"/>）：關著時不寫，開著寫 Information；
        /// 逐筆的 REQUEST／VERDICT 是細節，判讀先看這一行。
        /// </summary>
        /// <param name="via">答案是怎麼來的：cache / offerings / empty(...)。</param>
        /// <param name="n">採用的掛單筆數。</param>
        private void LogQuerySummary(SlotJob job, string via, int n, DateTime now)
        {
            // StartedAt 只有真的送出過請求才會設；快取直接命中時沒有「耗時」可言。
            var totalMs = job.StartedAt == DateTime.MinValue
                ? 0d
                : (now - job.StartedAt).TotalMilliseconds;
            MarketDiag.Trace(
                $"{Diag} QUERY item={job.ItemId} '{job.Name}' via={via} n={n} " +
                $"attempts={job.Attempt} totalMs={totalMs:F0}");
        }

        private void Fail(SlotJob job, string reason)
        {
            ProcessedSlots++;
            FailedCount++;
            Log.Warning($"BatchReprice: slot {job.Slot} ({job.Name}) failed: {reason}");
            ChatGui.PrintError("[Marketbuddy] ??: failed - ??".Loc(job.Name, reason));
        }

        private static InventoryItem* GetMarketSlot(short slotIndex)
        {
            var inventoryManager = InventoryManager.Instance();
            return inventoryManager == null
                ? null
                : inventoryManager->GetInventorySlot(InventoryType.RetainerMarket, slotIndex);
        }

        private static InfoProxyItemSearch* GetItemSearchProxy()
        {
            var infoModule = InfoModule.Instance();
            return infoModule == null
                ? null
                : (InfoProxyItemSearch*)infoModule->GetInfoProxyById(InfoProxyId.ItemSearch);
        }
    }
}
