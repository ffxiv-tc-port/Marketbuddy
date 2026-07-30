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
    /// <summary>
    /// "Relist all retainers" tour: from the retainer list, visits every
    /// retainer that has market listings, opens its sell list, runs the
    /// single-retainer BatchReprice engine (sharing its price cache), then
    /// leaves and moves on.
    ///
    /// Navigation follows the TC-production-proven AutoRetainer recipes:
    /// RetainerList Callback(2, index), SelectString menu entries matched via
    /// Lumina Addon sheet text (row 2380 = "sell items", row 2383 = "quit"),
    /// Talk dialogues advanced with the standard click. No strings are
    /// hardcoded. Strictly manual trigger; cancellable at any time (button or
    /// ESC); every navigation step has a watchdog so a stall aborts the tour
    /// instead of hanging.
    /// </summary>
    internal sealed unsafe class MultiRetainerReprice : IDisposable
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
        }

        private sealed class RetainerContext
        {
            public bool SkipRemainingSteps;
            public bool BatchStarted;
        }

        private readonly MarketGuiEventHandler gui;
        private readonly BatchReprice engine;
        private readonly TickTaskQueue queue = new();

        private DateTime lastUiAction = DateTime.MinValue;
        private DateTime lastTalkClick = DateTime.MinValue;
        private bool engineBatchAborted;
        private string engineAbortReason = string.Empty;
        private bool aborting;

        // Aggregated stats for the final summary.
        private int totalRepriced, totalSkipped, totalDelisted, totalFailed, retainersDone;

        public bool IsRunning => queue.IsRunning;
        public int TotalRetainers { get; private set; }
        public int CurrentRetainerNumber { get; private set; }
        public string CurrentRetainerName { get; private set; } = string.Empty;

        public MultiRetainerReprice(MarketGuiEventHandler gui, BatchReprice engine)
        {
            this.gui = gui;
            this.engine = engine;
            queue.Aborted += OnQueueAborted;
            queue.Completed += OnQueueCompleted;
            engine.BatchAborted += OnEngineBatchAborted;
            Framework.Update += OnFrameworkUpdate;
        }

        public void Dispose()
        {
            Framework.Update -= OnFrameworkUpdate;
            engine.BatchAborted -= OnEngineBatchAborted;
            queue.Aborted -= OnQueueAborted;
            queue.Completed -= OnQueueCompleted;
            if (queue.IsRunning)
                queue.Abort("plugin unloading");
        }

        public bool CanStart(out string reason)
        {
            reason = string.Empty;
            if (IsRunning || engine.IsRunning)
                return false;
            if (IPCManager.IsLocked)
            {
                reason = "locked via IPC by another plugin".Loc();
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

        public void Start()
        {
            if (!CanStart(out var reason))
            {
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
            TotalRetainers = targets.Count;
            CurrentRetainerNumber = 0;
            CurrentRetainerName = string.Empty;
            totalRepriced = totalSkipped = totalDelisted = totalFailed = retainersDone = 0;
            aborting = false;

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

            ChatGui.Print("[Marketbuddy] Relisting all retainers: ?? to visit...".Loc(targets.Count));
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
                    Log.Debug("MultiRetainerReprice: sell entry not found in SelectString yet");
            }

            return TickTaskResult.Continue;
        }

        private TickTaskResult TickRunBatch(RetainerTarget target, RetainerContext ctx)
        {
            if (ctx.SkipRemainingSteps)
                return TickTaskResult.Done;

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

            if (engineBatchAborted)
            {
                // The engine already reported the reason; stop the whole tour.
                Abort(engineAbortReason.Length > 0 ? engineAbortReason : "batch aborted".Loc());
                return TickTaskResult.Continue; // queue is already cleared by Abort
            }

            retainersDone++;
            totalRepriced += engine.RepricedCount;
            totalSkipped += engine.SkippedCount;
            totalDelisted += engine.DelistedCount;
            totalFailed += engine.FailedCount;
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

        private void OnQueueAborted(string reason)
        {
            CurrentRetainerName = string.Empty;
            ChatGui.PrintError(
                "[Marketbuddy] Tour cancelled: ?? (?? retainer(s) done: ?? repriced, ?? skipped, ?? delisted, ?? failed)"
                    .Loc(reason, retainersDone, totalRepriced, totalSkipped, totalDelisted, totalFailed));
        }

        private void OnQueueCompleted()
        {
            CurrentRetainerName = string.Empty;
            ChatGui.Print(
                "[Marketbuddy] All retainers done: ?? visited, ?? repriced, ?? skipped, ?? delisted, ?? failed"
                    .Loc(retainersDone, totalRepriced, totalSkipped, totalDelisted, totalFailed));
        }
    }
}
