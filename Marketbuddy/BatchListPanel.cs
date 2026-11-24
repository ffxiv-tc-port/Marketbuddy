using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Marketbuddy.Common;

namespace Marketbuddy
{
    /// <summary>
    /// 出售品視窗旁重掛面板裡的「批次上架」一段。開始是一段式（與快速上架一致）：清單上先列出每件從市場快取估的價，
    /// 真正掛出去的價在按下之後逐件查價才定。常看的放列上（件數、進度、結果數），原因放滑鼠提示。
    /// 道具清單是固定高度、可捲動的區塊（背包滿時上百列也不把面板撐出畫面）。
    /// </summary>
    internal sealed class BatchListPanel
    {
        /// <summary>道具清單一次看得到的列數；超過就捲動。</summary>
        internal const int MaxVisibleRows = 10;

        /// <summary>上一次結果的滑鼠提示最多列幾件被跳過的道具。</summary>
        internal const int MaxTooltipLines = 15;

        private readonly BatchLister lister;
        private TextSlot<(int Current, int Total, string Name)> progressText;
        private TextSlot<(int Listed, int Vendor, int Skipped)> resultText;
        private TextSlot<int> startLabel;
        private TextSlot<(string Result, string Reason)> lastRunText;

        // 欄寬：候選清單換一份（約每秒）、排除清單變了或字型大小變了才重量；量的是全部列，捲動時欄位不跳。
        private IReadOnlyList<BatchLister.Candidate>? layoutFor;
        private int layoutRevision = -1;
        private float layoutFontSize = -1f;
        private float nameColumnWidth;
        private float estimateColumnWidth;
        private float toggleColumnWidth;
        private int excludedInList;

        private readonly BatchListSummaryView summaryView = new("mbbatchlist");

        private int skippedTipSerial = -1;
        private int skippedTipCount = -1;
        private string skippedTip = string.Empty;

        // 其他僱員的空位：每秒最多重讀一次（列舉僱員與組字串會配置）。
        private const int PeersRefreshMs = 1000;
        private DateTime peersReadUtc = DateTime.MinValue;
        private string peersLine = string.Empty;
        private string peersTip = string.Empty;
        private TextSlot<(int Used, int Capacity)> slotsText;

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
            var full = DrawMarketSlots();
            DrawStartButton(full);
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

        /// <summary>
        /// 「這名僱員市場 n/20」與其他僱員的空位；回傳這名僱員確知已滿。
        /// 讀法與開始後的「市場已滿」判斷相同；讀不到畫「?」、不擋開始。
        /// </summary>
        private bool DrawMarketSlots()
        {
            var known = lister.TryReadThisRetainerMarket(out var used, out var capacity);
            var full = known && MarketSlots.KnownFull(used, capacity);
            RefreshPeers(known ? capacity : RetainerMarketPrice.SlotCount);

            var text = slotsText.Get((known ? used : -1, capacity), static k => k.Used < 0
                ? "This retainer's market: ?".Loc()
                : "This retainer's market: ??/??".Loc(k.Used, k.Capacity));
            if (full)
                ImGui.TextColored(ImGuiColors.DalamudOrange, text);
            else if (known)
                ImGui.TextUnformatted(text);
            else
                Grey(text);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(known
                    ? peersTip
                    : "This retainer's market can't be read yet, so the start button is not held back.".Loc() + "\n" + peersTip);

            if (full && peersLine.Length > 0)
            {
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGuiEx.FontPx(360f));
                ImGui.TextUnformatted(peersLine);
                ImGui.PopTextWrapPos();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(peersTip);
            }

            return full;
        }

        private void RefreshPeers(int capacity)
        {
            var now = DateTime.UtcNow;
            if ((now - peersReadUtc).TotalMilliseconds < PeersRefreshMs)
                return;
            peersReadUtc = now;
            var peers = RetainerSlotsReader.Read(BatchReprice.ActiveRetainerId());
            peersLine = MarketSlots.PeersLine(peers, capacity);
            peersTip = MarketSlots.PeersTooltip(peers, capacity, now);
            if (peers != null && !RetainerSlotsReader.SnapshotsUsable)
                peersTip += "\n" + "Other retainers are only known once Marketbuddy has seen their sell list with \"Record what disappears from your retainers' listings\" on.".Loc();
        }

        private void DrawStartButton(bool marketFull)
        {
            var stacks = lister.TickedStacks();
            var canStart = lister.CanStart(lister.CandidateSource, false, out var reason);
            if (canStart && marketFull)
            {
                canStart = false;
                reason = "this retainer's market is full - pressing start would stop right away. Free slots of your other retainers are listed above.".Loc();
                if (Configuration.GetOrLoad().BagListTourEnabled)
                    reason += "\n" + "Or leave this retainer and press \"List from your bags at retainers with free slots\" next to the retainer list.".Loc();
            }
            else if (canStart && stacks == 0)
            {
                canStart = false;
                reason = "nothing is ticked to list".Loc();
            }

            // AutoRetainer 是唯一的阻擋時照樣讓人按，按下去會說明原因（與重掛按鈕相同）；確知已滿一律停用。
            var disabled = (!canStart && !AutoRetainerBridge.IsBusy) || marketFull;
            if (disabled)
                ImGui.BeginDisabled();
            if (ImGuiEx.MarketActionButton(startLabel.Get(stacks, static n => "List ?? ticked stack(s)".Loc(n) + "##mbbatchliststart"), "mbbatchliststart"))
                lister.Start(lister.TickedPicks(), lister.CandidateSource, false, forceRequery: ImGui.GetIO().KeyCtrl);
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
                        .Loc() + "\n" +
                    "Items whose check came out below your threshold in the last few days are not checked again (see /mbuddy); hold Ctrl while pressing to check everything again."
                        .Loc());
            }
        }

        private void DrawCandidates()
        {
            var candidates = lister.Candidates;
            if (!ImGui.TreeNode(DrawText.Concat(DrawText.Loc("What gets listed (??)", candidates.Count), "##mbbatchlistitems")))
                return;

            if (candidates.Count == 0)
            {
                Grey(lister.CandidateWindowOpen
                    ? "Nothing here can be put up for sale".Loc()
                    : "Open your inventory or the retainer's inventory".Loc());
                ImGui.TreePop();
                return;
            }

            UpdateLayout(candidates);
            DrawListToolbar();

            var style = ImGui.GetStyle();
            var rowHeight = ImGui.GetFrameHeightWithSpacing();
            var scrolls = candidates.Count > MaxVisibleRows;
            var rowsShown = Math.Min(candidates.Count, MaxVisibleRows);
            var tickWidth = ImGui.GetFrameHeight();
            var gap = style.ItemSpacing.X * 2;
            var contentWidth = tickWidth + style.ItemSpacing.X + nameColumnWidth + gap + estimateColumnWidth + gap +
                               toggleColumnWidth;
            var size = new Vector2(contentWidth + (style.WindowPadding.X * 2) + (scrolls ? style.ScrollbarSize : 0f),
                (rowsShown * rowHeight) + (style.WindowPadding.Y * 2));

            ImGui.BeginChild("##mbbatchlistrows", size, true);
            var baseX = ImGui.GetCursorPosX();
            var nameX = baseX + tickWidth + style.ItemSpacing.X;
            var estimateX = nameX + nameColumnWidth + gap;
            var toggleX = estimateX + estimateColumnWidth + gap;
            var clipper = default(ImGuiListClipper);
            clipper.Begin(candidates.Count, rowHeight);
            try
            {
                while (clipper.Step())
                {
                    for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                        DrawCandidateRow(candidates[i], i, nameX, estimateX, toggleX);
                }
            }
            finally
            {
                clipper.End();
            }

            ImGui.EndChild();
            ImGui.TreePop();
        }

        /// <summary>全部勾選／全部取消與「幾件不自動上架」，畫在清單上方（清單捲動時也看得到）。</summary>
        private void DrawListToolbar()
        {
            if (ImGui.SmallButton(DrawText.Concat("Tick all".Loc(), "##mbbatchlistall")))
                lister.TickAll();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Items on a craft list or on your \"never auto-list\" list are not ticked - tick craft-list items yourself if you want to sell them.".Loc());
            ImGui.SameLine();
            if (ImGui.SmallButton(DrawText.Concat("Untick all".Loc(), "##mbbatchlistclear")))
                lister.ClearTicks();
            if (excludedInList == 0)
                return;
            ImGui.SameLine();
            Grey(DrawText.Loc("?? never auto-listed", excludedInList));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Items you marked with \"Don't auto-list\" stay greyed out here and are skipped by batch listing, \"Replenish all retainers\" and \"Tidy up this character\". Undo it on the row, or in /mbuddy (Listing page).".Loc());
        }

        private void DrawCandidateRow(BatchLister.Candidate c, int index, float nameX, float estimateX, float toggleX)
        {
            ImGui.PushID(index);
            var excluded = BatchListExclusions.IsExcluded(c.Key.ItemId);
            var ticked = lister.IsTicked(c);
            if (excluded)
                ImGui.BeginDisabled();
            if (ImGui.Checkbox("##tick", ref ticked))
                lister.SetTicked(c, ticked);
            if (excluded)
            {
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip("Not auto-listed: press \"Allow\" on the right to tick it again.".Loc());
            }

            var grey = excluded || c.Protected || c.RecentlyBelow || c.Estimate <= 0;
            ImGui.SameLine(nameX);
            TextMaybeGrey(NameText(c, excluded), grey);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(NameTooltip(c, excluded));
            ImGui.SameLine(estimateX);
            TextMaybeGrey(EstimateText(c), grey);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(EstimateTooltip(c));

            ImGui.SameLine(toggleX);
            if (ImGui.SmallButton(excluded
                    ? DrawText.Concat("Allow".Loc(), "##mbbatchlistallow")
                    : DrawText.Concat("Don't auto-list".Loc(), "##mbbatchlistexclude")))
                BatchListExclusions.Set(c.Key.ItemId, !excluded, c.Name);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(excluded
                    ? "Lets batch listing and the replenish steps use this item again. Ticks you had for it come back.".Loc()
                    : "Batch listing, \"Replenish all retainers\" and \"Tidy up this character\" will skip this item (NQ and HQ) until you allow it again. Quick listing and listing by hand are not affected. Your existing ticks are kept.".Loc());
            ImGui.PopID();
        }

        /// <summary>名稱的滑鼠提示：近期低於門檻的多一句「為什麼沒預先勾」。</summary>
        internal static string NameTooltip(BatchLister.Candidate c, bool excluded) => c.RecentlyBelow && !excluded && !c.Protected
            ? RecentlyBelowTooltip()
            : EstimateTooltip(c);

        internal static string RecentlyBelowTooltip()
            => "A price check in the last few days came out below your threshold (or it was delisted for a low price), so pressing start will not list it: from your bags it goes on the \"Sell to the shop\" list instead. That is why it is not ticked in advance; tick it yourself if you want that.".Loc();

        private static string EstimateTooltip(BatchLister.Candidate c) => c.Estimate > 0
            ? "Estimate from market data seen in the last few minutes; the real price is checked when it is its turn.".Loc()
            : "? = no recent market data; the price is checked when it is its turn, and it is skipped if nobody is selling it.".Loc();

        internal static string NameText(BatchLister.Candidate c, bool excluded)
        {
            var line = DrawText.Loc("?? x?? (?? stack(s))", (c.Name, c.Quantity, c.Slots.Count),
                static (t, a) => t.Loc(a.Name, a.Quantity, a.Item3));
            if (excluded)
                return DrawText.Concat(line, DrawText.Concat("  ", "(never auto-listed)".Loc()));
            if (c.Protected)
                return DrawText.Concat(line, DrawText.Concat("  ", "(on a craft list)".Loc()));
            return c.RecentlyBelow ? DrawText.Concat(line, DrawText.Concat("  ", "(recently below your threshold)".Loc())) : line;
        }

        private static string EstimateText(BatchLister.Candidate c)
            => c.Estimate > 0 ? DrawText.Loc("about ?? gil", DrawText.N0(c.Estimate)) : "?";

        private void UpdateLayout(IReadOnlyList<BatchLister.Candidate> candidates)
        {
            var fontSize = ImGui.GetFontSize();
            if (ReferenceEquals(candidates, layoutFor) && layoutRevision == BatchListExclusions.Revision &&
                layoutFontSize == fontSize)
                return;
            layoutFor = candidates;
            layoutRevision = BatchListExclusions.Revision;
            layoutFontSize = fontSize;

            nameColumnWidth = 0f;
            estimateColumnWidth = 0f;
            excludedInList = 0;
            foreach (var c in candidates)
            {
                var excluded = BatchListExclusions.IsExcluded(c.Key.ItemId);
                if (excluded)
                    excludedInList++;
                nameColumnWidth = MathF.Max(nameColumnWidth, ImGui.CalcTextSize(NameText(c, excluded)).X);
                estimateColumnWidth = MathF.Max(estimateColumnWidth, ImGui.CalcTextSize(EstimateText(c)).X);
            }

            var padding = ImGui.GetStyle().FramePadding.X * 2;
            toggleColumnWidth = MathF.Max(ImGui.CalcTextSize("Allow".Loc()).X, ImGui.CalcTextSize("Don't auto-list".Loc()).X) +
                                padding;
        }

        private void DrawLastResult()
        {
            if (lister.End == BatchListEnd.None)
                return;
            Grey(lastRunText.Get((ResultLine(), lister.EndReason), static k =>
                "Last run: ??".Loc(k.Result) + (k.Reason.Length > 0 ? " - " + k.Reason : string.Empty)));
            if (lister.Skipped.Count > 0 && ImGui.IsItemHovered())
                ImGui.SetTooltip(SkippedTooltip());
            summaryView.Draw(lister.Summary);
        }

        private string SkippedTooltip()
        {
            var skipped = lister.Skipped;
            if (skippedTipSerial == lister.RunSerial && skippedTipCount == skipped.Count)
                return skippedTip;
            skippedTipSerial = lister.RunSerial;
            skippedTipCount = skipped.Count;

            var lines = new List<string>(skipped.Count);
            foreach (var (name, reason) in skipped)
                lines.Add($"{name}: {reason}");
            skippedTip = string.Join("\n",
                BatchListRules.CapLines(lines, MaxTooltipLines, static n => "... and ?? more".Loc(n)));
            return skippedTip;
        }

        private static void TextMaybeGrey(string text, bool grey)
        {
            if (grey)
                Grey(text);
            else
                ImGui.TextUnformatted(text);
        }

        private static void Grey(string text)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextUnformatted(text);
            ImGui.PopStyleColor();
        }
    }
}
