using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
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

        private sealed class RetainerTarget
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
        /// 這一趟巡迴是不是因為**玩家背包滿了**而停的。
        /// 🔑 目的地換成玩家背包之後這是常態不是意外（140 格 vs 最多 180 件），
        /// 所以結束語必須跟「出事了」分開講，否則使用者每次都會以為壞掉。
        /// </summary>
        private bool tourStoppedForSpace;

        /// <summary>巡迴開跑當下玩家背包的空格數，用來在收工時算出總共佔掉幾格。</summary>
        private int freeBagSlotsAtStart;

        // Aggregated stats for the final summary.
        private int totalRepriced, totalSkipped, totalDelisted, totalFailed, retainersDone;

        public bool IsRunning => queue.IsRunning;

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
            Framework.Update += OnFrameworkUpdate;
        }

        public void Dispose()
        {
            Framework.Update -= OnFrameworkUpdate;
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

            if (CollectTargets().Count == 0)
            {
                reason = "no retainer has market listings".Loc();
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

            var targets = CollectTargets();
            Mode = mode;
            TotalRetainers = targets.Count;
            CurrentRetainerNumber = 0;
            CurrentRetainerName = string.Empty;
            totalRepriced = totalSkipped = totalDelisted = totalFailed = retainersDone = 0;
            aborting = false;
            tourStoppedForSpace = false;
            var inventoryManager = InventoryManager.Instance();
            freeBagSlotsAtStart = inventoryManager == null ? -1 : (int)inventoryManager->GetEmptySlotsInBag();

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
        }

        public void CancelByButton() => Abort("cancelled by user".Loc());

        private static List<RetainerTarget> CollectTargets()
        {
            var targets = new List<RetainerTarget>();
            var retainerManager = RetainerManager.Instance();
            if (retainerManager == null)
                return targets;

            var count = retainerManager->GetRetainerCount();
            for (var i = 0u; i < count; i++)
            {
                var retainer = retainerManager->GetRetainerBySortedIndex(i);
                if (retainer == null || retainer->RetainerId == 0 || !retainer->Available)
                    continue;
                if (retainer->MarketItemCount == 0)
                    continue;
                targets.Add(new RetainerTarget
                {
                    SortedIndex = i,
                    RetainerId = retainer->RetainerId,
                    Name = retainer->NameString,
                    Listed = retainer->MarketItemCount,
                });
            }

            return targets;
        }

        private static string GetAddonSheetText(uint rowId)
        {
            var sheet = DataManager.GetExcelSheet<Addon>();
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
                // 引擎是「因為背包滿了」停的還是「出錯」停的，決定整趟巡迴的結束語。
                // 兩種都要停整趟——背包滿了再去下一個僱員也只會立刻再滿一次。
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
        private static (int Retainers, int Items) CountRemaining()
        {
            var targets = CollectTargets();
            var activeId = ActiveRetainerId();
            var items = 0;
            foreach (var t in targets)
                items += t.RetainerId == activeId && activeId != 0
                    ? BatchDelist.CountRemainingListed()
                    : t.Listed;
            return (targets.Count, items);
        }

        private static ulong ActiveRetainerId()
        {
            var retainerManager = RetainerManager.Instance();
            var active = retainerManager == null ? null : retainerManager->GetActiveRetainer();
            return active == null ? 0 : active->RetainerId;
        }

        /// <summary>整趟巡迴佔掉玩家背包幾格；量不到就回 null，**不編數字**。</summary>
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

            // 🔑 「背包滿了」是這個功能**預期中**的結束方式，不是故障。
            // 用一般訊息（不是紅字）、講清楚已完成多少與還剩多少、並明說再按一次就接著跑。
            // ⚠️ 刻意**不做**「還可以下架幾件」的預估：道具會併堆疊，空格數換算件數
            // 一定不準，給一個會騙人的數字比不給還糟。
            if (tourStoppedForSpace)
            {
                var (retainersLeft, itemsLeft) = CountRemaining();
                ChatGui.Print(
                    "[Marketbuddy] Bags are full - stopped here, this is not an error. ?? item(s) delisted so far; ?? item(s) across ?? retainer(s) still listed. Clear space and press the button again to carry on where it left off."
                        .Loc(totalDelisted, itemsLeft, retainersLeft));
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
        }

        private void OnQueueCompleted()
        {
            ReleaseSuppressionIfHeld();
            CurrentRetainerName = string.Empty;
            if (Mode != TourMode.Delist)
            {
                ChatGui.Print("[Marketbuddy] All retainers done: ?? visited, ?? repriced, ?? skipped, ?? delisted, ?? failed"
                    .Loc(retainersDone, totalRepriced, totalSkipped, totalDelisted, totalFailed));
                return;
            }

            // 🔑 「佔掉幾格」是這個功能的**產出**不只是副作用：使用者要的就是把散在各個
            // 僱員身上的同款道具併成堆疊，而「下架 N 件只佔掉 M 格」正是合併的證據。
            // 這是實際量到的（開跑前後各數一次空格），不是推估——量不到就不講。
            var used = BagSlotsUsed();
            ChatGui.Print(used is null
                ? "[Marketbuddy] All retainers delisted: ?? visited, ?? delisted, ?? failed"
                    .Loc(retainersDone, totalDelisted, totalFailed)
                : "[Marketbuddy] All retainers delisted: ?? visited, ?? delisted, ?? failed (?? bag slot(s) used)"
                    .Loc(retainersDone, totalDelisted, totalFailed, used.Value));
        }
    }
}
