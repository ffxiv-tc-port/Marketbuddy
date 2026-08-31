using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Marketbuddy.Common;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    /// <summary>巡迴到每個僱員身上要做的事。導航完全相同，只有中間那一段不一樣。</summary>
    internal enum TourMode
    {
        /// <summary>全僱員重掛（<see cref="BatchReprice"/>）。</summary>
        Reprice,

        /// <summary>全僱員下架（<see cref="BatchDelist"/>）。</summary>
        Delist,
    }

    /// <summary>
    /// 全僱員巡迴：從僱員選單出發，逐一拜訪有掛單的僱員，開它的出售品視窗，
    /// 跑單僱員引擎（重掛或下架，見 <see cref="TourMode"/>），然後離開換下一個。
    ///
    /// Navigation follows the TC-production-proven AutoRetainer recipes:
    /// RetainerList Callback(2, index), SelectString menu entries matched via
    /// Lumina Addon sheet text (row 2380 = "sell items", row 2383 = "quit"),
    /// Talk dialogues advanced with the standard click. No strings are
    /// hardcoded. Strictly manual trigger; cancellable at any time (button or
    /// ESC); every navigation step has a watchdog so a stall aborts the tour
    /// instead of hanging.
    ///
    /// 🔴 兩種模式**共用同一個佇列與同一份導航**，所以「重掛巡迴」與「下架巡迴」
    /// 在結構上不可能同時跑，<see cref="IsRunning"/> 同時就是兩者的互斥閘。
    /// </summary>
    internal sealed unsafe class MultiRetainerTour : IDisposable
    {
        private const int NavActionThrottleMs = 500; // min gap between fired UI actions
        private const int TalkThrottleMs = 250;      // min gap between Talk advances
        private const int NavStepWatchdogSeconds = 20;
        private const int BatchWatchdogSeconds = 600;

        /// <summary>
        /// 一名僱員。<c>internal</c> 是為了讓設定視窗的「跳過哪些僱員」清單能沿用
        /// <see cref="EnumerateRetainers"/> 這同一份來源，而不是另外寫一份枚舉。
        /// </summary>
        internal sealed class RetainerTarget
        {
            public required uint SortedIndex;
            public required ulong RetainerId;
            public required string Name;

            /// <summary>這名僱員的掛單數（遊戲自己的計數），用來回報「還剩幾件沒下架」。</summary>
            public required int Listed;
        }

        private sealed class RetainerContext
        {
            public bool SkipRemainingSteps;
            public bool BatchStarted;
        }

        private readonly MarketGuiEventHandler gui;
        private readonly BatchReprice repriceEngine;
        private readonly BatchDelist delistEngine;
        private readonly TickTaskQueue queue = new();

        private DateTime lastUiAction = DateTime.MinValue;
        private DateTime lastTalkClick = DateTime.MinValue;
        private bool suppressionHeld;
        private bool engineBatchAborted;
        private string engineAbortReason = string.Empty;
        private bool aborting;

        /// <summary>
        /// 這一趟巡迴是不是因為**目的地容器滿了**而停的。
        /// 🔑 收回玩家背包時這是常態不是意外（140 格 vs 最多 180 件），
        /// 所以結束語必須跟「出事了」分開講，否則使用者每次都會以為壞掉。
        /// </summary>
        private bool tourStoppedForSpace;

        /// <summary>
        /// 這一趟下架巡迴的目的地快照（true = 各僱員自己的物品欄）。
        /// 引擎自己也各自抄一份，這裡抄是為了讓**巡迴層**的結束語講對容器。
        ///
        /// ⚠️ 已知的取捨：目的地是僱員物品欄時，「某一名僱員的物品欄滿了」其實**不代表
        /// 下一名也滿**（每名僱員的物品欄各自獨立），照理可以跳過去繼續跑。但目前撞到
        /// 滿一律停整趟——這是收回玩家背包時的正確行為（共用同一個背包，去下一個也只會
        /// 立刻再滿一次），沿用到僱員目的地只是保守，不會做錯事，只是可能提早收工。
        /// 改成「跳過這名、繼續下一名」要動到中止流程本身，不在這次的範圍內。
        /// </summary>
        private bool tourToRetainerInventory;

        /// <summary>
        /// 巡迴開跑當下玩家背包的空格數，用來在收工時算出總共佔掉幾格。
        /// 目的地是僱員物品欄時是 -1（＝不量、不報，見 <see cref="BagSlotsUsed"/>）。
        /// </summary>
        private int freeBagSlotsAtStart;

        // Aggregated stats for the final summary.
        private int totalRepriced, totalSkipped, totalDelisted, totalFailed, retainersDone;

        public bool IsRunning => queue.IsRunning;

        /// <summary>
        /// 這一趟巡迴是被<b>外部驅動器</b>（<see cref="MultiCharacterTour"/> 的多角色輪）
        /// 帶起來的嗎。true 時本層的「巡迴跑完了」那一聲塔塔露<b>不響</b>——
        /// 使用者明確要求多角色輪只在**全部角色都跑完**時響一聲。
        /// </summary>
        /// <remarks>
        /// 形狀刻意與 <see cref="BatchReprice.ExternalDriverActive"/> 相同：純通知閘門，
        /// 不改任何巡迴行為，null（沒有外部驅動器）時一切照舊。
        /// </remarks>
        internal Func<bool>? ExternalDriverActive;

        /// <summary>巡迴正常跑完（<see cref="OnQueueCompleted"/> 的尾巴）。</summary>
        public event Action? TourCompleted;

        /// <summary>巡迴中止，附原因（<see cref="OnQueueAborted"/> 的尾巴）。</summary>
        public event Action<string>? TourAborted;

        /// <summary>這一趟總共重掛幾件（給多角色輪累加整輪總數用）。</summary>
        public int TotalRepriced => totalRepriced;

        /// <summary>目前（或最近一次）跑的是哪一種巡迴。UI 靠它決定要畫哪一種進度。</summary>
        public TourMode Mode { get; private set; } = TourMode.Reprice;

        public int TotalRetainers { get; private set; }
        public int CurrentRetainerNumber { get; private set; }
        public string CurrentRetainerName { get; private set; } = string.Empty;

        /// <summary>巡迴此刻正在驅動的單僱員引擎（依 <see cref="Mode"/>）。</summary>
        public IRetainerBatchEngine ActiveEngine => EngineFor(Mode);

        private IRetainerBatchEngine EngineFor(TourMode mode) =>
            mode == TourMode.Delist ? delistEngine : repriceEngine;

        public MultiRetainerTour(MarketGuiEventHandler gui, BatchReprice repriceEngine, BatchDelist delistEngine)
        {
            this.gui = gui;
            this.repriceEngine = repriceEngine;
            this.delistEngine = delistEngine;
            queue.Aborted += OnQueueAborted;
            queue.Completed += OnQueueCompleted;
            repriceEngine.BatchAborted += OnEngineBatchAborted;
            delistEngine.BatchAborted += OnEngineBatchAborted;
            // 純通知用的抑制閘：巡迴會逐個僱員各跑一次重掛引擎，不擋的話九個僱員就會叫
            // 九次「塔塔露誇獎」。整輪的那一聲由本類別的 OnQueueCompleted 負責。
            repriceEngine.ExternalDriverActive = () => IsRunning;
            Framework.Update += OnFrameworkUpdate;
        }

        public void Dispose()
        {
            Framework.Update -= OnFrameworkUpdate;
            repriceEngine.ExternalDriverActive = null;
            delistEngine.BatchAborted -= OnEngineBatchAborted;
            repriceEngine.BatchAborted -= OnEngineBatchAborted;
            queue.Aborted -= OnQueueAborted;
            queue.Completed -= OnQueueCompleted;
            if (queue.IsRunning)
                queue.Abort("plugin unloading");
            ReleaseSuppressionIfHeld();
        }

        public bool CanStart(TourMode mode, out string reason)
        {
            reason = string.Empty;
            // 兩個引擎都要空著：出售品視窗那顆單僱員按鈕、快速上架的單件定價、
            // 以及另一種模式的巡迴，任何一個在跑都不能再開一輪。
            if (IsRunning || repriceEngine.IsRunning || delistEngine.IsRunning)
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

            if (!gui.IsRetainerListOpen)
            {
                reason = "retainer list is not open".Loc();
                return false;
            }

            var retainerManager = RetainerManager.Instance();
            if (retainerManager == null || !retainerManager->IsReady)
            {
                reason = "retainer data not ready".Loc();
                return false;
            }

            var applySkipList = mode == TourMode.Delist;
            if (CollectTargets(applySkipList).Count == 0)
            {
                // 🔑 「大家都沒東西掛」跟「有東西掛但全被你的跳過名單擋掉了」是**兩件事**，
                // 講成同一句會讓使用者去找一個根本不存在的問題。名單擋掉的時候要指名是名單。
                reason = applySkipList && CollectTargets(false).Count > 0
                    ? "every retainer with listings is on your skip list".Loc()
                    : "no retainer has market listings".Loc();
                return false;
            }

            return true;
        }

        public void Start(TourMode mode)
        {
            if (!CanStart(mode, out var reason))
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

            var sellEntryText = GetAddonSheetText(2380);  // 出售（玩家所持物品）
            var quitEntryText = GetAddonSheetText(2383);  // 讓僱員返回
            if (sellEntryText.Length == 0 || quitEntryText.Length == 0)
            {
                ChatGui.PrintError("[Marketbuddy] Cannot start: ??".Loc("Addon sheet text unavailable"));
                return;
            }

            var isDelist = mode == TourMode.Delist;
            var targets = CollectTargets(isDelist);
            // 被使用者的跳過名單擋掉的僱員數（重掛巡迴一律 0，名單不作用在它身上）。
            var skippedRetainers = isDelist ? CollectTargets(false).Count - targets.Count : 0;
            Mode = mode;
            TotalRetainers = targets.Count;
            CurrentRetainerNumber = 0;
            CurrentRetainerName = string.Empty;
            totalRepriced = totalSkipped = totalDelisted = totalFailed = retainersDone = 0;
            aborting = false;
            tourStoppedForSpace = false;

            // 目的地在開跑當下定案，跟每個引擎自己抄的那份是同一個來源、同一個時點。
            tourToRetainerInventory = mode == TourMode.Delist && Configuration.GetOrLoad().DelistToRetainerInventory;

            // ⚠️ 只有「收回玩家背包」才量得到有意義的總量：玩家背包整趟都是同一個容器，
            // 開跑數一次、收工數一次就是總佔用。目的地換成僱員物品欄時**量不到**——
            // 我們一次只看得到當下這名僱員的物品欄，把不同僱員的空格數相減毫無意義。
            // 所以那種情況直接不量（-1 = 收工時不報這個數字），**不編一個看起來像
            // 那麼回事的數**。
            var inventoryManager = InventoryManager.Instance();
            freeBagSlotsAtStart = inventoryManager == null || tourToRetainerInventory
                ? -1
                : (int)inventoryManager->GetEmptySlotsInBag();

            var number = 0;
            foreach (var target in targets)
            {
                var thisNumber = ++number;
                var ctx = new RetainerContext();
                queue.Enqueue($"select {target.Name}", TimeSpan.FromSeconds(NavStepWatchdogSeconds),
                    () => TickSelectRetainer(target, ctx, thisNumber));
                queue.Enqueue($"enter sell list {target.Name}", TimeSpan.FromSeconds(NavStepWatchdogSeconds),
                    () => TickEnterSellList(ctx, sellEntryText));
                queue.Enqueue($"batch {target.Name}", TimeSpan.FromSeconds(BatchWatchdogSeconds),
                    () => TickRunBatch(target, ctx));
                queue.Enqueue($"leave {target.Name}", TimeSpan.FromSeconds(NavStepWatchdogSeconds),
                    () => TickLeaveRetainer(ctx, quitEntryText));
            }

            // Hold AutoRetainer off for the whole tour; released on completion,
            // cancel, abort and dispose alike.
            AutoRetainerBridge.AcquireSuppression("multi-retainer tour");
            suppressionHeld = true;

            ChatGui.Print(mode == TourMode.Delist
                ? "[Marketbuddy] Delisting all retainers: ?? to visit...".Loc(targets.Count)
                : "[Marketbuddy] Relisting all retainers: ?? to visit...".Loc(targets.Count));

            // ⚠️ 名單擋掉人的時候要當場說幾個，而且是在**開跑訊息旁邊**：
            // 「怎麼少跑了一個僱員」是這個功能最可能被誤認成 bug 的地方。
            // 名單空著（預設）時這一行不會出現。
            if (skippedRetainers > 0)
                ChatGui.Print("[Marketbuddy] ?? retainer(s) skipped: on your skip list.".Loc(skippedRetainers));
        }

        public void CancelByButton() => Abort("cancelled by user".Loc());

        /// <summary>
        /// 這個角色目前所有可用的僱員（**不看有沒有掛單**）。
        /// 巡迴的目標清單與設定視窗的「跳過哪些僱員」都從這一份長出來，所以兩邊
        /// 看到的僱員集合不可能分岔。
        ///
        /// ⚠️ 只在 <c>IsReady</c> 時回傳資料：設定視窗隨時可以開（包括還沒登入時），
        /// 沒有這個閘門就會去問一份還沒載入的僱員表。既有呼叫端（<see cref="CanStart"/>）
        /// 本來就已經在外面檢查過 <c>IsReady</c>，所以這一行對它們是 no-op。
        /// 🔴 回傳的是抄好的值，**不留任何原生指標**。
        /// </summary>
        internal static List<RetainerTarget> EnumerateRetainers()
        {
            var list = new List<RetainerTarget>();
            var retainerManager = RetainerManager.Instance();
            if (retainerManager == null || !retainerManager->IsReady)
                return list;

            var count = retainerManager->GetRetainerCount();
            for (var i = 0u; i < count; i++)
            {
                var retainer = retainerManager->GetRetainerBySortedIndex(i);
                if (retainer == null || retainer->RetainerId == 0 || !retainer->Available)
                    continue;
                list.Add(new RetainerTarget
                {
                    SortedIndex = i,
                    RetainerId = retainer->RetainerId,
                    Name = retainer->NameString,
                    Listed = retainer->MarketItemCount,
                });
            }

            return list;
        }

        /// <summary>
        /// 這名僱員在不在「下架巡迴要跳過」的名單裡。
        /// 名單是空的（預設）時 <c>Count &gt; 0</c> 直接短路，連查都不查。
        /// </summary>
        internal static bool IsSkippedByUser(ulong retainerId)
        {
            var skip = Configuration.GetOrLoad().DelistTourSkipRetainers;
            return skip.Count > 0 && skip.Contains(retainerId);
        }

        /// <summary>
        /// 這一趟要拜訪的僱員：有掛單的，扣掉使用者指名跳過的。
        /// </summary>
        /// <param name="applySkipList">
        /// 套不套使用者的「跳過名單」。
        /// 🔴 **只有下架巡迴傳 true。** 重掛巡迴一律傳 false——被保護的僱員手上正是
        /// 那些高價品，重掛也跳過的話它們就再也不會跟著市場調價，等於為了保護反而
        /// 讓它們永遠掛在過時的價格上。
        /// </param>
        private static List<RetainerTarget> CollectTargets(bool applySkipList)
        {
            var targets = new List<RetainerTarget>();
            foreach (var retainer in EnumerateRetainers())
            {
                if (retainer.Listed == 0)
                    continue;
                if (applySkipList && IsSkippedByUser(retainer.RetainerId))
                    continue;
                targets.Add(retainer);
            }

            return targets;
        }

        /// <summary>
        /// 現在有幾名僱員身上有掛單（不套用任何跳過名單）。
        /// 🔑 給多角色輪判斷「這個角色是不是根本沒東西要重掛」用：那是<b>完成</b>不是失敗，
        /// 而 <see cref="CanStart"/> 只給得出一句已經在地化的原因字串，比對字串會很脆。
        /// </summary>
        internal static int CountRetainersWithListings() => CollectTargets(false).Count;

        private static string GetAddonSheetText(uint rowId)
        {
            // ⚠️ 全名：Lumina 的 Addon 表與 System.Action 在這個檔裡跟 using 撞名。
            var sheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Addon>();
            if (sheet != null && sheet.TryGetRow(rowId, out var row))
                return row.Text.ExtractText();
            return string.Empty;
        }

        private void OnFrameworkUpdate(IFramework framework)
        {
            if (!queue.IsRunning)
                return;

            // Tour-level guard rails. While the per-retainer batch runs, the
            // engine applies its own guards; an engine abort surfaces through
            // OnEngineBatchAborted and stops the tour.
            var engine = ActiveEngine;
            if (!engine.IsRunning)
            {
                if (Keys[VirtualKey.ESCAPE])
                {
                    Abort("ESC pressed".Loc());
                    return;
                }

                if (IPCManager.IsLocked)
                {
                    Abort("locked via IPC by another plugin".Loc());
                    return;
                }

                // A price adjustment window we never open means the user (or
                // another plugin) started interacting mid-tour: stand down.
                if (Commons.GetUnitBase("RetainerSell") != null)
                {
                    Abort("manual price adjustment detected".Loc());
                    return;
                }

                // AutoRetainer mutual exclusion (cached at 1 Hz by the bridge);
                // while the engine runs, its own check covers this.
                if (AutoRetainerBridge.IsBusy)
                {
                    Abort("AutoRetainer became busy".Loc());
                    return;
                }
            }

            queue.Update();
        }

        private TickTaskResult TickSelectRetainer(RetainerTarget target, RetainerContext ctx, int number)
        {
            CurrentRetainerNumber = number;
            CurrentRetainerName = target.Name;

            var retainerManager = RetainerManager.Instance();
            var retainer = retainerManager == null ? null : retainerManager->GetRetainerBySortedIndex(target.SortedIndex);
            if (retainer == null || retainer->RetainerId != target.RetainerId || retainer->MarketItemCount == 0)
            {
                // Retainer list changed since the tour started: skip this one.
                ctx.SkipRemainingSteps = true;
                ChatGui.Print("[Marketbuddy] ??: nothing to do anymore, skipped".Loc(target.Name));
                return TickTaskResult.Done;
            }

            var retainerList = AddonHelpers.GetReadyAddon("RetainerList");
            if (retainerList == null)
                return TickTaskResult.Continue; // watchdog aborts if it never appears

            if (!ThrottleUiAction())
                return TickTaskResult.Continue;

            AddonHelpers.FireRetainerListSelect(retainerList, target.SortedIndex);
            return TickTaskResult.Done;
        }

        private TickTaskResult TickEnterSellList(RetainerContext ctx, string sellEntryText)
        {
            if (ctx.SkipRemainingSteps)
                return TickTaskResult.Done;

            if (gui.IsRetainerSellListOpen)
                return TickTaskResult.Done;

            if (TryAdvanceTalk())
                return TickTaskResult.Continue;

            var selectString = AddonHelpers.GetReadyAddon("SelectString");
            if (selectString != null && ThrottleUiAction())
            {
                if (!AddonHelpers.TrySelectStringEntry(selectString, sellEntryText))
                    Log.Debug("MultiRetainerTour: sell entry not found in SelectString yet");
            }

            return TickTaskResult.Continue;
        }

        private TickTaskResult TickRunBatch(RetainerTarget target, RetainerContext ctx)
        {
            if (ctx.SkipRemainingSteps)
                return TickTaskResult.Done;

            var engine = ActiveEngine;
            if (!ctx.BatchStarted)
            {
                engineBatchAborted = false;
                engineAbortReason = string.Empty;
                engine.Start();
                if (!engine.IsRunning)
                {
                    // Could not start (e.g. nothing listed after all): count as
                    // visited with no work and move on to leaving.
                    ChatGui.Print("[Marketbuddy] ??: batch did not start, moving on".Loc(target.Name));
                    retainersDone++;
                    return TickTaskResult.Done;
                }

                ctx.BatchStarted = true;
                return TickTaskResult.Continue;
            }

            if (engine.IsRunning)
                return TickTaskResult.Continue;

            // 🔴 引擎停了就先把它已經做掉的事計進總數，**中止的情況也一樣**。
            // 舊版只在成功路徑累加，所以「跑到第 3 個僱員撞到僱員背包滿而停手」的
            // 摘要會把前面兩個僱員真的處理掉的件數講成 0——那正是「撞到上限卻
            // 印得像沒事」的同一類謊。retainersDone 則刻意不加：這名僱員確實沒跑完。
            totalRepriced += engine.RepricedCount;
            totalSkipped += engine.SkippedCount;
            totalDelisted += engine.DelistedCount;
            totalFailed += engine.FailedCount;

            if (engineBatchAborted)
            {
                // 引擎是「因為目的地滿了」停的還是「出錯」停的，決定整趟巡迴的結束語。
                // 兩種都停整趟：收回玩家背包時這是正確的（共用同一個背包，去下一個也
                // 只會立刻再滿一次）；收回僱員物品欄時則是保守做法，見
                // tourToRetainerInventory 的說明。
                tourStoppedForSpace = engine.StoppedForSpace;
                // The engine already reported the reason; stop the whole tour.
                Abort(engineAbortReason.Length > 0 ? engineAbortReason : "batch aborted".Loc());
                return TickTaskResult.Continue; // queue is already cleared by Abort
            }

            retainersDone++;
            return TickTaskResult.Done;
        }

        private TickTaskResult TickLeaveRetainer(RetainerContext ctx, string quitEntryText)
        {
            // Runs even for skipped retainers if navigation already began; for
            // fully skipped ones every check below just falls through to Done.
            if (TryAdvanceTalk())
                return TickTaskResult.Continue;

            if (gui.IsRetainerSellListOpen)
            {
                var sellList = AddonHelpers.GetReadyAddon("RetainerSellList");
                if (sellList != null && ThrottleUiAction())
                    AddonHelpers.FireIntCallback(sellList, -1);
                return TickTaskResult.Continue;
            }

            var selectString = AddonHelpers.GetReadyAddon("SelectString");
            if (selectString != null)
            {
                if (ThrottleUiAction() && !AddonHelpers.TrySelectStringEntry(selectString, quitEntryText))
                    AddonHelpers.FireIntCallback(selectString, -1);
                return TickTaskResult.Continue;
            }

            if (gui.IsRetainerListOpen && AddonHelpers.GetReadyAddon("RetainerList") != null)
                return TickTaskResult.Done;

            return TickTaskResult.Continue;
        }

        private bool TryAdvanceTalk()
        {
            var talk = AddonHelpers.GetReadyAddon("Talk");
            if (talk == null)
                return false;

            if ((DateTime.UtcNow - lastTalkClick).TotalMilliseconds >= TalkThrottleMs)
            {
                lastTalkClick = DateTime.UtcNow;
                AddonHelpers.ClickTalk(talk);
            }

            return true;
        }

        private bool ThrottleUiAction()
        {
            if ((DateTime.UtcNow - lastUiAction).TotalMilliseconds < NavActionThrottleMs)
                return false;
            lastUiAction = DateTime.UtcNow;
            return true;
        }

        private void Abort(string reason)
        {
            if (aborting)
                return;
            aborting = true;
            try
            {
                queue.Abort(reason);
            }
            finally
            {
                aborting = false;
            }
        }

        private void OnEngineBatchAborted(string reason)
        {
            if (!queue.IsRunning)
                return;
            engineBatchAborted = true;
            engineAbortReason = reason;
        }

        private void ReleaseSuppressionIfHeld()
        {
            if (!suppressionHeld)
                return;
            suppressionHeld = false;
            AutoRetainerBridge.ReleaseSuppression();
        }

        /// <summary>
        /// 現在還有幾名僱員、共幾件掛單沒下架（每次重新數，不留狀態）。
        ///
        /// ⚠️ 其他僱員只能用 <c>MarketItemCount</c>（僱員結構上的計數器）——巡迴本來就是
        /// 靠它挑目標的，所以這裡沿用同一個來源不會多出新的不一致。但**我們剛剛動過的
        /// 那名僱員**風險最高（那個計數器已知會落後於容器實況），而她正好是我們此刻
        /// 站著的人，市場容器就在手邊，所以那一名改用實際容器內容數。
        /// </summary>
        /// <param name="applySkipList">
        /// 跟巡迴本身用同一套規則。這個數字後面接的是「再按一次就接著跑」，所以
        /// 被跳過名單擋掉的僱員必須一起排除——否則那句話承諾的是一個永遠達不到的 0。
        /// ⚠️ 單價門檻只在**我們此刻站著的那一名**僱員身上算得出來（其他人的價格看不到），
        /// 所以其餘僱員仍然只能用 <c>MarketItemCount</c>，那是既有的已知取捨。
        /// </param>
        private static (int Retainers, int Items) CountRemaining(bool applySkipList)
        {
            var targets = CollectTargets(applySkipList);
            var activeId = ActiveRetainerId();
            var aboveUnitPrice = Math.Max(0, Configuration.GetOrLoad().DelistAboveUnitPrice);
            var items = 0;
            foreach (var t in targets)
                items += t.RetainerId == activeId && activeId != 0
                    ? BatchDelist.CountRemainingListed(aboveUnitPrice)
                    : t.Listed;
            return (targets.Count, items);
        }

        private static ulong ActiveRetainerId()
        {
            var retainerManager = RetainerManager.Instance();
            var active = retainerManager == null ? null : retainerManager->GetActiveRetainer();
            return active == null ? 0 : active->RetainerId;
        }

        /// <summary>
        /// 整趟巡迴佔掉玩家背包幾格；量不到就回 null，**不編數字**。
        /// 目的地是僱員物品欄時 <see cref="freeBagSlotsAtStart"/> 已經是 -1，所以這裡
        /// 自然回 null——那個數字在那種情況下量不到（理由見 Start 裡的說明）。
        /// </summary>
        private int? BagSlotsUsed()
        {
            var inventoryManager = InventoryManager.Instance();
            if (inventoryManager == null || freeBagSlotsAtStart < 0)
                return null;
            var used = freeBagSlotsAtStart - (int)inventoryManager->GetEmptySlotsInBag();
            return used < 0 ? null : used;
        }

        private void OnQueueAborted(string reason)
        {
            ReleaseSuppressionIfHeld();
            CurrentRetainerName = string.Empty;

            // 🔑 「目的地滿了」是這個功能**預期中**的結束方式，不是故障。
            // 用一般訊息（不是紅字）、講清楚已完成多少與還剩多少、並明說再按一次就接著跑。
            // ⚠️ 刻意**不做**「還可以下架幾件」的預估：道具會併堆疊，空格數換算件數
            // 一定不準，給一個會騙人的數字比不給還糟。
            // ⚠️ 要講對是哪個容器滿了——使用者要去清的地方不一樣。
            if (tourStoppedForSpace)
            {
                var (retainersLeft, itemsLeft) = CountRemaining(Mode == TourMode.Delist);
                ChatGui.Print((tourToRetainerInventory
                        ? "[Marketbuddy] A retainer's inventory is full - stopped here, this is not an error. ?? item(s) delisted so far; ?? item(s) across ?? retainer(s) still listed. Make room in that retainer's inventory and press the button again to carry on where it left off."
                        : "[Marketbuddy] Bags are full - stopped here, this is not an error. ?? item(s) delisted so far; ?? item(s) across ?? retainer(s) still listed. Clear space and press the button again to carry on where it left off.")
                    .Loc(totalDelisted, itemsLeft, retainersLeft));
                TourAborted?.Invoke(reason);
                return;
            }

            // 🔴 停手原因照實帶出來，而且**永遠**跟著已完成的件數一起講：
            // 「撞到限制卻印成正常結束」是我們踩過的雷，這裡不會重蹈。
            ChatGui.PrintError(Mode == TourMode.Delist
                ? "[Marketbuddy] Delist tour stopped: ?? (?? retainer(s) done: ?? delisted, ?? failed)"
                    .Loc(reason, retainersDone, totalDelisted, totalFailed)
                : "[Marketbuddy] Tour cancelled: ?? (?? retainer(s) done: ?? repriced, ?? skipped, ?? delisted, ?? failed)"
                    .Loc(reason, retainersDone, totalRepriced, totalSkipped, totalDelisted, totalFailed));
            if (AutoRetainerBridge.IsBusy)
            {
                AutoRetainerBridge.ArmAvailabilityNotice();
                ChatGui.Print("[Marketbuddy] Wait for AutoRetainer to finish, then press the button again.".Loc());
            }

            TourAborted?.Invoke(reason);
        }

        private void OnQueueCompleted()
        {
            ReleaseSuppressionIfHeld();
            CurrentRetainerName = string.Empty;
            if (Mode != TourMode.Delist)
            {
                ChatGui.Print("[Marketbuddy] All retainers done: ?? visited, ?? repriced, ?? skipped, ?? delisted, ?? failed"
                    .Loc(retainersDone, totalRepriced, totalSkipped, totalDelisted, totalFailed));

                // 純通知，零行為：整輪重掛跑完才響這一聲（途中每個僱員各自的批次收尾被
                // BatchReprice.ExternalDriverActive 擋掉了）。下架巡迴不響——那不是「重掛跑完」。
                // ⚠️ 再上面還有一層：多角色輪在驅動時這一聲也不響，改由它在**全部角色跑完**
                //    的時候響一次（使用者明確要求途中每一角完成都不要響）。
                // 🔴 這裡在 Framework.Update → queue.Update() 的鏈上（OnFrameworkUpdate），是主執行緒。
                if (ExternalDriverActive?.Invoke() != true)
                    TataruPraiseIPC.TryPraise("全僱員重掛巡迴完成");
                TourCompleted?.Invoke();
                return;
            }

            // 🔑 「佔掉幾格」是這個功能的**產出**不只是副作用：使用者要的就是把散在各個
            // 僱員身上的同款道具併成堆疊，而「下架 N 件只佔掉 M 格」正是合併的證據。
            // 這是實際量到的（開跑前後各數一次空格），不是推估——量不到就不講。
            //
            // ⚠️ 上面那段只在收回**玩家背包**時成立。目的地是各僱員自己的物品欄時，
            // 跨僱員合併根本不會發生，而且我們也量不到跨僱員的總量——那種情況
            // BagSlotsUsed() 回 null，於是自動走下面那句不帶數字的版本。
            var used = BagSlotsUsed();
            ChatGui.Print(used is null
                ? "[Marketbuddy] All retainers delisted: ?? visited, ?? delisted, ?? failed"
                    .Loc(retainersDone, totalDelisted, totalFailed)
                : "[Marketbuddy] All retainers delisted: ?? visited, ?? delisted, ?? failed (?? bag slot(s) used)"
                    .Loc(retainersDone, totalDelisted, totalFailed, used.Value));

            // ⚠️ 單價門檻在整趟裡總共留下幾件。totalSkipped 一路是各僱員 BatchDelist
            // 累加上來的（門檻停用時每一名都是 0），所以門檻沒開時這一行不會出現。
            if (totalSkipped > 0)
                ChatGui.Print("[Marketbuddy] ?? listing(s) kept across the tour: unit price not above ?? gil."
                    .Loc(totalSkipped, Configuration.GetOrLoad().DelistAboveUnitPrice.ToString("N0")));

            TourCompleted?.Invoke();
        }
    }
}
