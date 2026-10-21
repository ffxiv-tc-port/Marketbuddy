using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Marketbuddy.Common;

namespace Marketbuddy
{
    /// <summary>
    /// 出售品視窗旁重掛面板裡的「批次上架」一段。開始是一段式（與快速上架一致）：清單上先列出每件從市場快取估的價，
    /// 真正掛出去的價在按下之後逐件查價才定。常看的放列上（件數、進度、結果數），原因放滑鼠提示。
    /// </summary>
    internal sealed class BatchListPanel
    {
        private readonly BatchLister lister;
        private TextSlot<(int Current, int Total, string Name)> progressText;
        private TextSlot<(int Listed, int Vendor, int Skipped)> resultText;
        private TextSlot<int> startLabel;

        public BatchListPanel(BatchLister lister)
        {
            this.lister = lister;
        }

        public void Draw()
        {
            lister.MarkViewed();
            ImGui.Separator();
            ImGui.TextUnformatted("Batch listing".Loc());
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Tick items and press the button: each stack is priced one at a time with the same market check as the relist, then put up for sale through the game's own right-click \"Put Up for Sale\" at that price. Anything that would sell below the NPC price or below your minimum is not listed - from your own bags it is added to the \"Sell to the shop\" list instead (nothing is sold until you press that button). Items nobody is selling are skipped. ESC, Cancel, a right-click or any confirm key stops it."
                        .Loc());

            if (lister.IsRunning)
            {
                DrawProgress();
                return;
            }

            if (!lister.IsAvailable)
            {
                Grey("Not available: the right-click menu hook did not load".Loc());
                return;
            }

            Grey(lister.CandidateSource == BatchListSource.RetainerBags
                ? "From: this retainer's inventory (your ticks are remembered for \"Replenish all retainers\")".Loc()
                : "From: your bags".Loc());
            DrawStartButton();
            DrawCandidates();
            DrawLastResult();
        }

        private void DrawProgress()
        {
            var current = Math.Min(lister.FinishedJobs + 1, lister.TotalJobs);
            ImGui.TextUnformatted(progressText.Get((current, lister.TotalJobs, lister.CurrentItemName),
                static k => "Listing ??/??: ??".Loc(k.Current, k.Total, k.Name)));
            ImGui.SameLine();
            if (ImGui.Button(DrawText.Concat("Cancel".Loc(), "##mbbatchlistcancel")))
                lister.CancelByButton();

            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextUnformatted(ResultLine());
            if (lister.WaitingFor.Length > 0)
                ImGui.TextUnformatted(lister.WaitingFor);
            ImGui.PopStyleColor();
        }

        private string ResultLine() => resultText.Get((lister.ListedCount, lister.VendorQueuedCount, lister.SkippedCount),
            static k => "?? listed, ?? added to the shop list, ?? skipped".Loc(k.Listed, k.Vendor, k.Skipped));

        private void DrawStartButton()
        {
            var stacks = lister.TickedStacks();
            var canStart = lister.CanStart(lister.CandidateSource, false, out var reason);
            if (canStart && stacks == 0)
            {
                canStart = false;
                reason = "nothing is ticked to list".Loc();
            }

            // AutoRetainer 是唯一的阻擋時照樣讓人按，按下去會說明原因（與重掛按鈕相同）。
            var disabled = !canStart && !AutoRetainerBridge.IsBusy;
            if (disabled)
                ImGui.BeginDisabled();
            if (ImGui.Button(startLabel.Get(stacks, static n => "List ?? ticked stack(s)".Loc(n) + "##mbbatchliststart")))
                lister.Start(lister.TickedPicks(), lister.CandidateSource, false);
            if (disabled)
            {
                ImGui.EndDisabled();
                if (!string.IsNullOrEmpty(reason) && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    using var tooltipAlpha = new ImRaii.Style().Push(ImGuiStyleVar.Alpha, 1f);
                    ImGui.SetTooltip(reason);
                }
            }
            else if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "Starts right away. Each stack is checked again right before it goes up; the price shown in the list is only an estimate from recent market data."
                        .Loc());
            }
        }

        private void DrawCandidates()
        {
            var candidates = lister.Candidates;
            if (!ImGui.TreeNode(DrawText.Concat(DrawText.Loc("What gets listed (??)", candidates.Count), "##mbbatchlistitems")))
                return;

            if (candidates.Count == 0)
                Grey(lister.CandidateWindowOpen
                    ? "Nothing here can be put up for sale".Loc()
                    : "Open your inventory or the retainer's inventory".Loc());

            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                var ticked = lister.IsTicked(c);
                if (ImGui.Checkbox($"##mbbatchlist{c.Key.ItemId}{(c.Key.Hq ? "h" : "n")}", ref ticked))
                    lister.SetTicked(c, ticked);
                ImGui.SameLine();
                var estimate = c.Estimate > 0 ? "about ?? gil".Loc(c.Estimate.ToString("N0")) : "?";
                var line = "?? x?? (?? stack(s))  ??".Loc(c.Name, c.Quantity, c.Slots.Count, estimate);
                if (c.Estimate > 0 && !c.Protected)
                    ImGui.TextUnformatted(line);
                else
                    Grey(c.Protected ? line + "  " + "(on a craft list)".Loc() : line);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(c.Estimate > 0
                        ? "Estimate from market data seen in the last few minutes; the real price is checked when it is its turn.".Loc()
                        : "? = no recent market data; the price is checked when it is its turn, and it is skipped if nobody is selling it.".Loc());
            }

            if (candidates.Count > 0 && ImGui.SmallButton(DrawText.Concat("Untick all".Loc(), "##mbbatchlistclear")))
                lister.ClearTicks();
            ImGui.TreePop();
        }

        private void DrawLastResult()
        {
            if (lister.End == BatchListEnd.None)
                return;
            Grey("Last run: ??".Loc(ResultLine()) +
                 (lister.EndReason.Length > 0 ? " - " + lister.EndReason : string.Empty));
            if (lister.Skipped.Count == 0 || !ImGui.IsItemHovered())
                return;

            var lines = new List<string>(lister.Skipped.Count);
            foreach (var (name, reason) in lister.Skipped)
                lines.Add($"{name}: {reason}");
            ImGui.SetTooltip(string.Join("\n", lines));
        }

        private static void Grey(string text)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextUnformatted(text);
            ImGui.PopStyleColor();
        }
    }
}
