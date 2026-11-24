using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Marketbuddy.Common;

namespace Marketbuddy
{
    /// <summary>
    /// 僱員選單旁「背包上架到有空位的僱員」的「檢視／調整勾選」：不必先進某名僱員的出售清單，就能看、能改玩家背包的勾選。
    /// 與批次上架面板（玩家背包）是同一份勾選。🔴 市場紅線：這裡只改勾選，沒有開始鈕。
    /// </summary>
    internal sealed class BagTickView
    {
        private const string PopupId = "##mbbagtourtickspopup";
        private const int MaxVisibleRows = 12;

        private readonly BatchLister lister;
        private TextSlot<(int Kinds, int Stacks)> tickedText;

        // 欄寬：清單換一份（約每秒）、排除清單或字型大小變了才重量。
        private IReadOnlyList<BatchLister.Candidate>? layoutFor;
        private int layoutRevision = -1;
        private float layoutFontSize = -1f;
        private float nameColumnWidth;

        public BagTickView(BatchLister lister)
        {
            this.lister = lister;
        }

        /// <summary>畫在巡迴按鈕旁的小按鈕與它開的彈出框；每幀呼叫，順便讓玩家背包每秒重讀一次（見 <see cref="BatchLister.MarkBagViewed"/>）。</summary>
        public void Draw()
        {
            lister.MarkBagViewed();
            ImGui.SameLine();
            if (ImGui.SmallButton(DrawText.Concat("Review ticks".Loc(), "##mbbagtourticks")))
                ImGui.OpenPopup(PopupId);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "See and change which items from your bags this button would list. Same ticks as the batch listing panel; nothing starts from here."
                        .Loc());

            if (!ImGui.BeginPopup(PopupId))
                return;
            DrawContents();
            ImGui.EndPopup();
        }

        private void DrawContents()
        {
            var candidates = lister.BagCandidates;
            var editable = lister.PlayerTicksEditable;
            CountTicked(candidates, out var kinds, out var stacks);
            ImGui.TextUnformatted(tickedText.Get((kinds, stacks),
                static k => "Ticked in your bags: ?? item(s), ?? stack(s)".Loc(k.Kinds, k.Stacks)));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Only ticked items are listed, and only when you press \"List from your bags at retainers with free slots\". Items below your threshold go on the \"Sell to the shop\" list instead of being listed."
                        .Loc());

            if (candidates.Count == 0)
            {
                Grey("Nothing in your bags can be put up for sale".Loc());
                return;
            }

            if (!editable)
                ImGui.BeginDisabled();
            if (ImGui.SmallButton(DrawText.Concat("Tick all".Loc(), "##mbbagtickall")))
                lister.TickAllPlayer(candidates);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Items on a craft list or on your \"never auto-list\" list are not ticked - tick craft-list items yourself if you want to sell them.".Loc());
            ImGui.SameLine();
            if (ImGui.SmallButton(DrawText.Concat("Untick all".Loc(), "##mbbagtickclear")))
                lister.ClearPlayerTicks();
            if (!editable)
                ImGui.EndDisabled();

            UpdateLayout(candidates);
            var style = ImGui.GetStyle();
            var rowHeight = ImGui.GetFrameHeightWithSpacing();
            var scrolls = candidates.Count > MaxVisibleRows;
            var rowsShown = Math.Min(candidates.Count, MaxVisibleRows);
            var tickWidth = ImGui.GetFrameHeight();
            var gap = style.ItemSpacing.X * 2;
            var estimateWidth = ImGuiEx.FontPx(110f);
            var contentWidth = tickWidth + style.ItemSpacing.X + nameColumnWidth + gap + estimateWidth;
            var size = new Vector2(contentWidth + (style.WindowPadding.X * 2) + (scrolls ? style.ScrollbarSize : 0f),
                (rowsShown * rowHeight) + (style.WindowPadding.Y * 2));

            ImGui.BeginChild("##mbbagtickrows", size, true);
            var nameX = ImGui.GetCursorPosX() + tickWidth + style.ItemSpacing.X;
            var estimateX = nameX + nameColumnWidth + gap;
            var clipper = default(ImGuiListClipper);
            clipper.Begin(candidates.Count, rowHeight);
            try
            {
                while (clipper.Step())
                {
                    for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                        DrawRow(candidates[i], i, editable, nameX, estimateX);
                }
            }
            finally
            {
                clipper.End();
            }

            ImGui.EndChild();
        }

        private void DrawRow(BatchLister.Candidate c, int index, bool editable, float nameX, float estimateX)
        {
            ImGui.PushID(index);
            var excluded = BatchListExclusions.IsExcluded(c.Key.ItemId);
            var ticked = lister.IsPlayerTicked(c.Key);
            var disabled = excluded || !editable;
            if (disabled)
                ImGui.BeginDisabled();
            if (ImGui.Checkbox("##tick", ref ticked))
                lister.SetPlayerTicked(c.Key, ticked);
            if (disabled)
            {
                ImGui.EndDisabled();
                if (excluded && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip("On your \"never auto-list\" list: change that in the batch listing panel or in /mbuddy (Listing page).".Loc());
            }

            var grey = excluded || c.Protected || c.RecentlyBelow;
            ImGui.SameLine(nameX);
            if (grey)
                Grey(BatchListPanel.NameText(c, excluded));
            else
                ImGui.TextUnformatted(BatchListPanel.NameText(c, excluded));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(BatchListPanel.NameTooltip(c, excluded));

            ImGui.SameLine(estimateX);
            if (c.Estimate > 0)
                ImGui.TextUnformatted(DrawText.Loc("about ?? gil", DrawText.N0(c.Estimate)));
            else
                Grey("?");
            ImGui.PopID();
        }

        private void CountTicked(IReadOnlyList<BatchLister.Candidate> candidates, out int kinds, out int stacks)
        {
            kinds = 0;
            stacks = 0;
            for (var i = 0; i < candidates.Count; i++)
            {
                if (!lister.IsPlayerTicked(candidates[i].Key))
                    continue;
                kinds++;
                stacks += candidates[i].Slots.Count;
            }
        }

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
            foreach (var c in candidates)
                nameColumnWidth = MathF.Max(nameColumnWidth,
                    ImGui.CalcTextSize(BatchListPanel.NameText(c, BatchListExclusions.IsExcluded(c.Key.ItemId))).X);
        }

        private static void Grey(string text)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextUnformatted(text);
            ImGui.PopStyleColor();
        }
    }
}
