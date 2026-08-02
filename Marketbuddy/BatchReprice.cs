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
    /// One-click "relist every listed slot of the current retainer at
    /// (lowest market price - undercut)" batch engine.
    ///
    /// Headless: market data is requested via InfoProxyItemSearch.RequestData()
    /// and captured through the IMarketBoard.OfferingsReceived event; prices are
    /// applied via InventoryManager.SetRetainerMarketPrice(). RetainerSell /
    /// ItemSearchResult windows are never opened and no native UI is touched.
    /// No hooks, no packet forgery, no memory patches; a stuck market query is
    /// handled by timeout + a capped-backoff retry (up to MaxAttempts tries,
    /// backoff escalating fast then capped low), then the slot is skipped.
    ///
    /// Strictly manual: runs only when the user clicks the button while a
    /// retainer's sell list (RetainerSellList) is open, or when QuickLister
    /// hands it a single just-listed slot to price. Cancellable at any time
    /// via the cancel button or ESC; closing the sell list also aborts.
    /// </summary>
    internal sealed unsafe class BatchReprice : IRetainerBatchEngine, IDisposable
    {
        // Pacing / safety constants. Requests are throttled and retried with
        // backoff instead of patching the client's "please wait" throttle path.
        //
        // 🔑 送出節流的真實形狀（以及它為什麼是 send→send 而不是 data→send）整段寫在
        // MarketRequestGate 的類別註解裡，那裡才是唯一真值來源，這裡不重複。
        // 摘要：以「上一次送出」起算、基準 2000 ms，單發拒絕當成一次便宜的重試吸收掉，
        // 只有拒絕成群或持續率超過 20% 才動間隔，之後自動衰減回 2000。
        // ⚠️ 基準 2026-08-03 由 1700 提高到 2000：實機 341 次真值請求顯示 1700 的拒絕率
        // 是 23.5% 而 ≥2000 是 0%，換算**有效間隔**（間隔÷(1−拒絕率)）反而是 2000 比較快。
        //
        // 🔴 2026-08-02 重新量測的回應延遲（把每一筆 REQUEST 配對到後續同道具的封包，
        // 不受我們自己的逾時截斷，n=639 次有回應的請求，涵蓋 .14 與 .15 兩段）：
        //     REQ → 第一個封包：p50 128 / p90 469 / p95 1213 / p99 1802 / **max 1955 ms**
        //     REQ → HISTORY   ：p50 129 / p90 491 / max 1955 ms
        //     REQ → OFFERINGS ：p50 476 / p90 765 / max 1989 ms
        //     HISTORY → OFFERINGS：p50 312 / p99 410 / max 432 ms（n=866，>500 ms 掛零）
        //
        // ⚠️ 舊註解寫的「最慢 861 ms」只成立於 16:32–16:36 那段（.14，max 909 ms）。
        // 19:17–19:36 那段（.15）有一條到 ~1.95 秒的長尾：4.25% 的請求第一個封包超過
        // 1000 ms、3.36% 超過 1500 ms。所以舊的 NoResponseDeadlineMs=1000 會把
        // **約 5% 真實但比較慢的回應誤判成「被伺服器吞掉」**，然後去撴寬閘門——
        // 那正是 MarketRequestGate 註解裡描述的永久卡死路徑。
        //
        // 新門檻取 2500 ms：全部 639 筆量到的回應沒有任何一筆超過 2000 ms，2500 ms
        // 比實測最大值多 28% 餘裕（而且 .16 的閘門會讓我們查得比量測當時更密，
        // 伺服器延遲有機會更差一點，餘裕留寬一點）。
        //
        // 另外，「判定被吞掉」不再等於「立刻去撴寬閘門」：真正的判決延到**要送重試的
        // 那一刻**才下（見 SlotPhase.Request）。中間只要那件道具的資料落進
        // MarketDataCache（遲到的答案就是這樣被撿回來的），我們連重試都不會送，
        // 閘門自然也不會被那次「其實沒被吞」的請求汙染。
        //
        // 🔑 2026-08-02（v7.20.0.18 探針實測，83 次請求）：**這個門檻已經退居保險絲。**
        // 探針證明台服的拒絕**有封包**（`errorCode = 0x70000003`，送出後 229 ms 就到），
        // 所以「被拒絕」現在由 MarketRequestResultProbe 直接判，不再靠這個 2500 ms 的猜測。
        // 它剩下的兩個用途都是退路：探針解不出位址而停用時、以及封包真的完全沒到時。
        // ⚠️ 因此**不能調低**（要蓋住實測 1955 ms 的回應長尾），也不必調高。
        private const int NoResponseDeadlineMs = 2500; // fuse only: nothing at all by now => probably swallowed (verdict deferred to retry time)
        private const int ResponseTimeoutMs = 3500;    // hard cap per attempt once *something* did arrive (safety net; must stay above NoResponseDeadlineMs + EmptyResultGraceMs)
        private const int RetryBackoffBaseMs = 300;    // backoff before retry N is min(N * this, RetryBackoffCapMs)...
        private const int RetryBackoffCapMs = 1200;    // ...never escalating past this (the gate adds its own spacing on top)
        private const int MaxAttempts = 8;           // 1 initial attempt + up to 7 retries per slot
        // ⚠️ 這個看門狗一到期是**整批中止**（TickTaskQueue.Update → Abort），不是只放棄這一格，
        // 所以它必須確實蓋得住最壞情況。每次嘗試最壞 = max(閘門 4000, 退避 1200) + ResponseTimeoutMs
        // 3500 ≈ 7500 ms，×8 次 ≈ 60 秒——剛好等於舊值，等於沒有餘裕。逾時門檻調高之後同步
        // 拉到 90 秒（約 50% 餘裕）。
        private const int SlotWatchdogSeconds = 90;  // hard per-slot watchdog (queue-level safety net; sized for MaxAttempts attempts plus gate spacing and backoffs, with margin)
        private const int EmptyResultGraceMs = 1000; // history seen + this long with no offerings => nothing on sale (measured HISTORY->OFFERINGS max is 432 ms, so this is 2.3x the observed worst case)

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
            /// <summary>Diagnostics only: when this slot was first ticked, for total-elapsed reporting.</summary>
            public DateTime StartedAt = DateTime.MinValue;
            /// <summary>send→send gap of the attempt currently in flight; fed back to the gate when it turns out to have been swallowed.</summary>
            public double SendGapMs = -1;
            /// <summary>
            /// 上一次嘗試什麼封包都沒收到，暫定判為「被吞掉」。判決刻意延後到真的要送重試
            /// 的那一刻才交給閘門——因為在那之前遲到的答案還可能落進 MarketDataCache 把
            /// 這次重試整個省掉，那就代表它根本沒被吞，不該拿去撴寬閘門。
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

        // Per-slot record of what the batch actually changed, for the live sell
        // list overlay (display only - see LiveSellList). Keyed by market slot.
        private readonly Dictionary<short, (uint OldPrice, uint NewPrice, DateTime At)> recentChanges = new();
        private ulong recentChangesRetainerId;

        /// <summary>Read-only view of the prices this session changed, so the overlay can flag them. Display only.</summary>
        public IReadOnlyDictionary<short, (uint OldPrice, uint NewPrice, DateTime At)> RecentChanges => recentChanges;

        /// <summary>Retainer the <see cref="RecentChanges"/> marks belong to; the overlay ignores them for anybody else.</summary>
        public ulong RecentChangesRetainerId => recentChangesRetainerId;

        // ---------------------------------------------------------------
        // Temporary instrumentation (2026-08-02).
        //
        // Measured from the live log: every fresh market query costs a flat
        // ~6.0s (was ~7.5s before the backoff was shortened), and the two
        // populations differ by exactly the change in RetryBackoff - which
        // proves the FULL OfferingsTimeoutMs is burned on attempt 1 of every
        // query, and that attempt 2 then answers in ~0.5s. Exactly one query
        // per batch (the first) is fast.
        //
        // What that does NOT tell us is WHY attempt 1 never completes. Two
        // candidates produce identical timing and need opposite fixes:
        //   (a) the request never reaches the server (client-side market
        //       throttle) - no offerings packet arrives at all; or
        //   (b) the packet DOES arrive and one of the guards in
        //       OnOfferingsReceived drops it (RequestId collision, or an
        //       ItemId mismatch).
        // These logs are Information level on purpose: the user's log level
        // filters out DBG/VRB, and Dalamud's own marketboard packet tracing
        // is Verbose, so it is invisible in their captures.
        // Grep tag: MBDIAG
        private const string Diag = "[MBDIAG]";
        private DateTime lastDataReceivedAt = DateTime.MinValue;

        /// <summary>
        /// 這一批（＝這一個雇員的一輪，或 QuickLister 的單件）開始的時間。
        /// 只用來替 CACHE-HIT 標出 within-batch / cross-batch，沒有行為作用。
        /// </summary>
        private DateTime batchStartedAt = DateTime.MinValue;

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

        public bool IsRunning => queue.IsRunning;
        public int TotalSlots { get; private set; }
        public int ProcessedSlots { get; private set; }
        public int RepricedCount { get; private set; }
        public int SkippedCount { get; private set; }
        public int DelistedCount { get; private set; }
        public int FailedCount { get; private set; }
        public string CurrentItemName { get; private set; } = string.Empty;

        /// <summary>
        /// 重掛永遠是 false：它撞到目的地容器滿的時候只讓**這一件**失敗然後繼續跑，
        /// 不會整輪停手，所以沒有「因為空間而停」這種結束方式。
        /// </summary>
        public bool StoppedForSpace => false;

        /// <summary>
        /// 此刻正在處理的那一格的市場容器索引，閒置時 -1。**純顯示用**
        /// （<see cref="LiveSellList"/> 靠它把那一列亮起來），沒有任何行為作用。
        ///
        /// 它在 <see cref="TickSlot"/> 一進來就設好、直到下一格接手才變，
        /// 所以整段「送出 → 等回應 → 定價 → 完成」都指著同一格，不會只閃一下；
        /// 快取命中（根本沒送請求）那條路徑也一樣會經過這裡。
        /// 快速上架的單件定價走的是 <see cref="StartQuickReprice"/> → <see cref="BeginBatch"/>，
        /// 也就是同一條 TickSlot，所以兩條路徑都涵蓋得到。
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

            // 🔴 `MarketItemCount` 是**僱員結構上的計數器**，不是即時的出售品容器內容，
            // 它會落後於我們自己剛剛掛上去的東西。實機 log 抓到的時序是決定性的：
            //   03:26:36.720  engine refused: 這名僱員沒有上架中的物品 (queued=1, head='厚土大斧')
            //   03:26:36.731  厚土大斧：已上架（暫掛上限價），開始比價定價…
            // 也就是這個閘門說「沒有東西」的時間點，比我們自己在**市場容器裡實際看到**
            // 那件道具還早 11 毫秒。結果是剛掛上去的道具卡在 999999999 沒被定價。
            //
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
        /// Runs the full pipeline (compare via cache/request, undercut,
        /// thresholds, delist) for one just-quick-listed slot that is parked at
        /// the price cap. Returns false when the engine cannot start right now.
        /// </summary>
        /// <summary>
        /// 上一次 <see cref="StartQuickReprice"/> 被拒絕的原因，供 QuickLister 記進診斷 log。
        /// 這裡原本把 CanStart 的 reason 丟掉（<c>out _</c>），導致「排進佇列 30 秒沒人接手」
        /// 在 log 裡毫無線索——2026-08-02 實機遇到時只能靠推理。
        /// </summary>
        public string LastStartRefusalReason { get; private set; } = string.Empty;

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
            return true;
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

        private void BeginBatch(List<SlotJob> jobs)
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

            TotalSlots = jobs.Count;
            ProcessedSlots = 0;
            RepricedCount = 0;
            SkippedCount = 0;
            DelistedCount = 0;
            FailedCount = 0;
            CurrentItemName = string.Empty;
            CurrentSlot = -1;
            CurrentBatchRetainerId = ActiveRetainerId();
            batchStartedAt = DateTime.UtcNow;
            lastAcceptedRequestId = int.MinValue;
            offeringsPending = false;
            offeringsReceived = false;
            historySeen = false;

            // Hold AutoRetainer off for the duration of this user-triggered
            // run; every exit path (completion, cancel, abort, dispose) goes
            // through ResetRequestState/ForceRelease so it is always restored.
            AutoRetainerBridge.AcquireSuppression("batch reprice");
            suppressionHeld = true;

            foreach (var job in jobs)
                queue.Enqueue(job.Name, TimeSpan.FromSeconds(SlotWatchdogSeconds), () => TickSlot(job));
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
                    // Fresh cached market data for this item skips the whole
                    // request/wait pipeline (and the request throttle).
                    //
                    // 這一步刻意排在退避與閘門檢查**之前**：一次逾時之後遲到的答案會落進
                    // MarketDataCache，下一個 tick 就在這裡被撿回來，於是那次重試根本不會
                    // 送出去（實測 63 次重試裡有 18 次，答案在我們重問之前就已經到了）。
                    if (MarketDataCache.TryGet(job.ItemId, conf.MarketDataCacheSeconds, out var cachedListings,
                            out var cacheAgeMs))
                    {
                        // 🔑 scope 讓事後分得出「這一輪自己剛查過」與「上一輪／上一個雇員留下來的」。
                        // 使用者的實際流程是反覆補滿同一個雇員再換下一個，跨輪次的命中才是
                        // 1800 秒 TTL 真正的價值所在，而單一批次內的樣本永遠看不到它。
                        var batchAgeMs = batchStartedAt == DateTime.MinValue
                            ? -1
                            : (now - batchStartedAt).TotalMilliseconds;
                        var scope = batchAgeMs < 0 || cacheAgeMs > batchAgeMs ? "cross-batch" : "within-batch";
                        Log.Information(
                            $"{Diag} CACHE-HIT item={job.ItemId} '{job.Name}' n={cachedListings.Count} " +
                            $"ageMs={cacheAgeMs:F0} scope={scope} batchAgeMs={batchAgeMs:F0} attempt={job.Attempt} " +
                            $"(no request sent; ttl={conf.MarketDataCacheSeconds}s)");
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
                    // ⚠️ 必須在下面的 IsReady 之前呼叫：舊版把它放在 SlotPhase.Request，
                    // 那時閘門檢查早就通過了，所以撴寬對「這一次重試」完全無效——
                    // 實機 .19 拒絕於 send-gap 1719 ms，重試仍然以 send-gap 1718 ms 送出。
                    // ⚠️ 舊註解在這裡加了一句「順帶證明了間隔不是拒絕的原因」——**那句已被推翻**。
                    // 那只是單一一次配對，而 341 次真值請求顯示 1700 ms 的拒絕率是 23.5%、
                    // ≥2000 ms 是 0%。詳見 MarketRequestGate 的類別註解。
                    if (job.RefusalPending)
                    {
                        job.RefusalPending = false;
                        MarketRequestGate.NoteRefused(job.RefusalGapMs);
                    }

                    // 🔑 從「上一次**送出**」起算（見 MarketRequestGate）。舊版從「上一次
                    // 收到資料」起算，在前一件是「沒人在賣」的道具時會讓時間戳停在再上一件，
                    // 於是 send→send 只隔 1.1～1.4 秒就撞牆 —— 實機三次逾時全是這個形狀。
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
                    // 這裡不要重複，否則撴寬又會晚一拍。

                    job.Attempt++;

                    // 🔎 2026-08-02 鑑識：台服 InfoProxyItemSearch 的 vf10 (`EndRequest`) 位元組是
                    // `C2 00 00` —— **空函式**。舊註解寫的「reset any dangling request state」
                    // 描述的效果從來不存在。真的要重設只能自己寫這兩個欄位，而遊戲自己在
                    // ProcessRequestResult 的尾段做的也正是這件事（`mov [rbx+0x4810], ebp` 與
                    // vf13 的 `mov [rcx+0x10], 0`），所以偏移與寫法都有二進位佐證。
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
                    MarketRequestGate.NoteRequestSent(now);
                    job.WaitStart = now;
                    if (job.StartedAt == DateTime.MinValue)
                        job.StartedAt = now;

                    var sent = proxy->RequestData();
                    var cachedAgeMs = MarketDataCache.AgeMsOf(job.ItemId);
                    Log.Information(
                        $"{Diag} REQUEST item={job.ItemId} '{job.Name}' attempt={job.Attempt} " +
                        $"RequestData={sent} sendGapMs={job.SendGapMs:F0} gate={MarketRequestGate.IntervalMs} " +
                        $"msSinceLastData={MsSince(lastDataReceivedAt):F0} " +
                        $"cache={(cachedAgeMs < 0 ? "miss" : $"stale({cachedAgeMs:F0}ms)")}");

                    if (!sent)
                    {
                        offeringsPending = false;
                        if (job.Attempt >= MaxAttempts)
                        {
                            Fail(job, "market data request could not be sent".Loc());
                            return TickTaskResult.Done;
                        }

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
                    // 這是唯一一個不必猜的訊號源：實機量到拒絕在送出後 229 ms 就到，
                    // 而逾時要 2502 ms 才判得出來。取一次就消費掉，所以放在最前面。
                    if (!job.ProbeAnswered && MarketRequestResultProbe.TryTakeResult(job.ItemId, out var verdict))
                    {
                        job.ProbeAnswered = true;
                        job.ProbeRefused = verdict.Refused;
                        // listingCount 是跨所有分頁的**總**筆數；為 0 時客戶端不會送續頁請求，
                        // 所以 offerings 封包永遠不會來（反編譯證實，見探針的類別註解）。
                        job.ProbeEmpty = !verdict.Refused && verdict.ListingCount == 0;
                        Log.Information(
                            $"{Diag} VERDICT item={job.ItemId} '{job.Name}' attempt={job.Attempt} " +
                            $"refused={verdict.Refused} errorCode=0x{verdict.ErrorCode:X} " +
                            $"listingCount={verdict.ListingCount} " +
                            $"afterMs={(now - job.WaitStart).TotalMilliseconds:F0}");
                    }

                    if (offeringsReceived)
                    {
                        Log.Information(
                            $"{Diag} SLOT-DONE item={job.ItemId} '{job.Name}' via=offerings " +
                            $"attempts={job.Attempt} waitMs={(now - job.WaitStart).TotalMilliseconds:F0} " +
                            $"totalMs={(now - job.StartedAt).TotalMilliseconds:F0}");
                        MarketRequestGate.NoteAccepted();
                        // 掛單資料已經由 MarketDataCache 的被動處理器存起來了，這裡不必再存。
                        job.Phase = SlotPhase.Apply;
                        return TickTaskResult.Continue;
                    }

                    // ① 伺服器**明確拒絕**了這一次查詢。以前只能靠「什麼都沒來 + 逾時 2500 ms」
                    //    去猜，現在有真值，所以立刻走重試流程 —— 實機那一次省下 2.27 秒。
                    // 🔴 這裡**不**直接呼叫 RequestData()：重送一律回到 Throttle，由既有的
                    //    Request 階段經 MarketRequestGate 送出，否則 NoteRequestSent 會被繞過。
                    if (job.ProbeRefused)
                    {
                        Log.Information(
                            $"{Diag} REFUSED item={job.ItemId} '{job.Name}' attempt={job.Attempt} " +
                            $"afterMs={(now - job.WaitStart).TotalMilliseconds:F0} (server verdict, not a timeout)");
                        offeringsPending = false;

                        // 判決仍然延到真的要重送的那一刻才交給閘門（見 SlotPhase.Request）：
                        // 中間若有遲到的答案落進 MarketDataCache，那次重試會整個被省掉，
                        // 也就不該拿這次拒絕去撴寬閘門。
                        job.RefusalPending = true;
                        job.RefusalGapMs = job.SendGapMs;

                        if (job.Attempt >= MaxAttempts)
                        {
                            Fail(job, "the server refused the market query".Loc());
                            return TickTaskResult.Done;
                        }

                        job.NotBefore = now.AddMilliseconds(RetryBackoffFor(job.Attempt));
                        job.Phase = SlotPhase.Throttle;
                        return TickTaskResult.Continue;
                    }

                    // ② 「沒人在賣」的兩條認定路徑，快的那條優先：
                    //   • job.ProbeEmpty —— 伺服器直說 listingCount == 0。反編譯證實客戶端在
                    //     這個情況下**不送續頁請求**，所以 offerings 封包永遠不會來，
                    //     等 EmptyResultGraceMs 是在等一個保證不會發生的事件。
                    //   • historySeen + 寬限 —— 探針沒掛上（IsInstalled == false）或訊號被別的
                    //     查詢蓋掉時的退路，行為與 .18 之前完全相同。
                    if (job.ProbeEmpty || (historySeen && (now - historySeenAt).TotalMilliseconds >= EmptyResultGraceMs))
                    {
                        Log.Information(
                            $"{Diag} SLOT-DONE item={job.ItemId} '{job.Name}' " +
                            $"via={(job.ProbeEmpty ? "verdict(empty)" : "history-grace(empty)")} " +
                            $"attempts={job.Attempt} totalMs={(now - job.StartedAt).TotalMilliseconds:F0}");
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
                    //   (b) history 來了、offerings 沒來 → 請求送到了，是伺服器/網路慢
                    //       → 間隔沒有問題，加長它只會白白拖慢每一件。
                    // (a) 判得比 (b) 早：實測第一個封包最遲 1955 ms 會出現，門檻取 2500 ms。
                    //
                    // 🔑 NoResponseDeadlineMs 現在是**純保險絲**，不再是主要判準。
                    // 探針上線後，「伺服器拒絕」由 errorCode 直接判（實機 229 ms），這條逾時
                    // 只剩下兩個用途：(1) 探針解不出位址而停用時的退路；
                    // (2) 真的連 ProcessRequestResult 都沒被呼叫（封包完全沒到）的情況。
                    // 所以門檻**不能調低**——它要蓋住實測 1955 ms 的回應長尾，2500 ms 留 28% 餘裕。
                    // 反過來說也不必調高：真正需要快速反應的情境已經被探針接走了。
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
                        {
                            Fail(job, "no market data received (timed out)".Loc());
                            return TickTaskResult.Done;
                        }

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

        private void ApplySlot(SlotJob job)
        {
            var cacheTag = job.FromCache ? " " + "(cached price)".Loc() : string.Empty;

            if (captured.Count == 0)
            {
                HandleNoListings(job, cacheTag);
                return;
            }

            IEnumerable<(uint Price, bool IsHq, ulong RetainerId)> eligible = captured;
            if (job.IsHq && conf.BatchCompareHqOnly && captured.Any(l => l.IsHq))
                eligible = captured.Where(l => l.IsHq);

            var lowest = eligible.MinBy(l => l.Price);
            if (ownRetainerIds.Contains(lowest.RetainerId))
            {
                if (!job.QuickListed)
                {
                    Skip(job, "[Marketbuddy] ??: your own listing is already the lowest (?? gil)".Loc(job.Name, lowest.Price) + cacheTag);
                    return;
                }

                // The quick-listed slot is parked at the price cap and must
                // never stay there: match our own lowest listing instead of
                // undercutting ourselves.
                FinishPricing(job, lowest.Price, cacheTag,
                    "[Marketbuddy] ??: matched your own lowest listing at ?? gil".Loc(job.Name, lowest.Price) + cacheTag);
                return;
            }

            // Same undercut maths as the manual flow: float multiplication for
            // percent mode (never integer division), then clamp.
            var target = conf.UndercutUsePercent
                ? (long)(lowest.Price * (1f - conf.UndercutPercent / 100f))
                : lowest.Price - (long)conf.UndercutPrice;
            var newPrice = (uint)Math.Clamp(target, Configuration.MIN_PRICE, Configuration.MAX_PRICE);
            FinishPricing(job, newPrice, cacheTag, null);
        }

        /// <summary>
        /// No market listings for this item. Normal batch slots just keep their
        /// price; quick-listed slots are parked at the price cap and must never
        /// be left there silently - fall back to the user's minimum price when
        /// one is configured, otherwise warn loudly and count as failed.
        /// </summary>
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

            if (conf.BatchMinPrice > 0)
            {
                ChatGui.Print("[Marketbuddy] ??: no one is selling this item, using your minimum price ?? gil".Loc(job.Name, conf.BatchMinPrice) + cacheTag);
                FinishPricing(job, (uint)conf.BatchMinPrice, cacheTag, null);
                return;
            }

            ProcessedSlots++;
            FailedCount++;
            Log.Warning($"BatchReprice: quick-listed slot {job.Slot} ({job.Name}) has no market data; still listed at the price cap");
            ChatGui.PrintError("[Marketbuddy] ??: no market data - still listed at the price cap (??), set a price manually!".Loc(job.Name, Configuration.MAX_PRICE));
        }

        /// <summary>Slot re-validation, delist thresholds, then the actual price update.</summary>
        private void FinishPricing(SlotJob job, uint newPrice, string cacheTag, string? successMessage)
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
                        "[Marketbuddy] ??: market net ?? < NPC ?? gil, delisted".Loc(job.Name, netUnit, job.VendorUnitPrice) + cacheTag);
                    return;
                }
            }

            if (conf.BatchMinPrice > 0 && newPrice < (uint)conf.BatchMinPrice)
            {
                DelistSlot(job, inventoryManager, slot,
                    "[Marketbuddy] ??: target price ?? below your minimum ??, delisted".Loc(job.Name, newPrice, conf.BatchMinPrice) + cacheTag);
                return;
            }

            var current = inventoryManager->GetRetainerMarketPrice(job.Slot);
            if (current == newPrice)
            {
                Skip(job, "[Marketbuddy] ??: already at ?? gil".Loc(job.Name, newPrice) + cacheTag);
                return;
            }

            inventoryManager->SetRetainerMarketPrice(job.Slot, newPrice);
            NoteChange(job.Slot, (uint)current, newPrice);
            ProcessedSlots++;
            RepricedCount++;
            ChatGui.Print(successMessage ?? "[Marketbuddy] ??: ?? → ?? gil".Loc(job.Name, current, newPrice) + cacheTag);
        }

        private void OnOfferingsReceived(IMarketBoardCurrentOfferings offerings)
        {
            // Diagnostics: log EVERY offerings packet and the branch it takes.
            // This is the measurement that separates "the request never went
            // out" from "the answer arrived and we threw it away".
            // ⚠️ 這個處理器**只**負責「我們正在等的那一件」的快通道。把每一筆封包都存進
            // 快取是 MarketDataCache 自己那個獨立的訂閱做的事——包括下面每一條 dropped
            // 分支所丟掉的封包，它們一樣都已經被存起來了，只是不會在這裡被採用。
            var listings = offerings.ItemListings;
            Log.Information(
                $"{Diag} OFFERINGS reqId={offerings.RequestId} count={listings.Count} " +
                $"firstItem={(listings.Count > 0 ? listings[0].ItemId : 0)} " +
                $"pendingItem={pendingItemId} pending={offeringsPending} " +
                $"lastAcceptedReqId={lastAcceptedRequestId}");

            if (!offeringsPending)
            {
                Log.Information($"{Diag} OFFERINGS dropped: no request pending");
                return;
            }

            if (listings.Count > 0)
            {
                // Later pages of a batch we already consumed share its RequestId.
                if (offerings.RequestId == lastAcceptedRequestId)
                {
                    Log.Information($"{Diag} OFFERINGS dropped: RequestId == lastAcceptedRequestId ({offerings.RequestId})");
                    return;
                }

                // Stale response for a previously requested item: ignore.
                if (listings[0].ItemId != pendingItemId)
                {
                    Log.Information($"{Diag} OFFERINGS dropped: itemId mismatch (got {listings[0].ItemId}, want {pendingItemId})");
                    return;
                }
            }

            // An empty response is a definitive "nothing on sale" and is always
            // accepted while a request is pending: it cannot be a later page of
            // an earlier batch (those always carry entries), it cannot be
            // attributed by ItemId, and we only ever have a single request in
            // flight. Deliberately no RequestId check for it either, so a
            // non-incrementing RequestId can never make us drop it.

            lastAcceptedRequestId = offerings.RequestId;
            captured.Clear();
            foreach (var listing in listings)
                captured.Add((listing.PricePerUnit, listing.IsHq, listing.RetainerId));

            offeringsPending = false;
            offeringsReceived = true;
            lastDataReceivedAt = DateTime.UtcNow;
            Log.Information($"{Diag} OFFERINGS accepted: {captured.Count} listings for item {pendingItemId}");
        }

        private void OnHistoryReceived(IMarketBoardHistory history)
        {
            Log.Information(
                $"{Diag} HISTORY item={history.ItemId} pendingItem={pendingItemId} " +
                $"pending={offeringsPending} alreadySeen={historySeen}");

            if (!offeringsPending || historySeen)
                return;
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

        /// <summary>Market tax percent for the active retainer's city; falls back to the configured constant.</summary>
        private uint CurrentTaxPercent()
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

        private void DelistSlot(SlotJob job, InventoryManager* inventoryManager, InventoryItem* slot, string chatMessage)
        {
            var quantity = (uint)Math.Max(1, slot->Quantity);

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

                result = inventoryManager->MoveFromRetainerMarketToPlayerInventory(
                    InventoryType.RetainerMarket, (ushort)job.Slot, quantity);
                destinationTag = " " + "(moved to your inventory)".Loc();
            }

            Log.Information(
                $"BatchReprice: delist slot {job.Slot} ({job.Name}) qty {quantity}, move returned {result}");

            // 🔴 這裡原本**完全不看回傳值**：不管遊戲收不收，一律 DelistedCount++ 並印
            // 「已下架」。也就是說僱員／玩家背包放不下時，畫面上會說下架成功，
            // 東西卻還掛在市場上——「撞到限制卻印成正常結束」的同一類謊。
            // 回傳值的意義（離線反編譯 TC 7.20 客戶端取得）整理在 BatchDelist 的常數區，
            // 那裡是唯一真值來源；這裡只要知道 0 才是成功。
            if (result != BatchDelist.MoveOk)
            {
                // ⚠️ 錯誤字串必須跟著目的地走：兩支取回函式的回傳碼數值相同，但
                // 0x19／0x1C 講的是不同的容器（遊戲自己印的 LogMessage 就分兩套）。
                Fail(job, BatchDelist.DescribeMoveError(result, conf.DelistToRetainerInventory));
                return;
            }

            ForgetChange(job.Slot);
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
            // 每一輪（每個雇員 / 每件快速上架）印一次閘門軌跡：這是事後判斷
            // 「往下探 → 成功還是被拒 → 收斂到多少」唯一不必翻 68 行 REQUEST 的入口。
            MarketRequestGate.LogSummary("batch finished");
            ResetRequestState();
            ChatGui.Print("[Marketbuddy] Relist finished: ?? repriced, ?? skipped, ?? delisted, ?? failed"
                .Loc(RepricedCount, SkippedCount, DelistedCount, FailedCount));
            HintAboutStaleSellList();
            BatchFinished?.Invoke();
        }

        /// <summary>
        /// Prices are written straight into the retainer's market container, so the game's
        /// own sell list never redraws - it keeps showing whatever it showed when it opened.
        /// The plugin can draw a live list instead (see <see cref="LiveSellList"/>), but that
        /// is opt-in, so point the player at it once per session when it is off and something
        /// actually changed. One line, once - deliberately not every batch.
        /// </summary>
        private static bool staleListHintShown;

        private void HintAboutStaleSellList()
        {
            if (staleListHintShown || conf.LiveSellListOverlay)
                return;
            if (RepricedCount == 0 && DelistedCount == 0)
                return;

            staleListHintShown = true;
            ChatGui.Print(
                "[Marketbuddy] The game's sell list does not redraw itself, so it still shows the old prices - the new ones are already on the server. Turn on \"Live sell list\" in /mbuddy to see them right away."
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

            // ⚠️ 台服的 vf10 (`EndRequest`) 是空函式（`C2 00 00`），所以**它自己什麼都不重設**
            // —— 舊註解「reset any dangling request state」描述的效果從來不存在。
            // 真正的重設是下面兩行；遊戲自己在 ProcessRequestResult 尾段做的也是同兩個欄位。
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
