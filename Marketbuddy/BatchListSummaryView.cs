using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;

namespace Marketbuddy
{
    /// <summary>批次上架的彙整表（已處理／還要處理兩段表格）；批次上架面板與背包上架巡迴共用。</summary>
    internal sealed class BatchListSummaryView
    {
        private readonly string id;
        private readonly string doneTableId;
        private readonly string todoTableId;

        // 每一批收尾換一份新的清單，換了才重組文字。
        private IReadOnlyList<BatchListSummaryRow>? summaryFor;
        private readonly List<string[]> summaryCells = new();
        private int summaryHandledCount;
        private int summaryPendingCount;
        private string summaryHeader = string.Empty;
        private string handledHeader = string.Empty;
        private string pendingHeader = string.Empty;

        /// <param name="id">ImGui 識別字首；同一個視窗裡兩份表要用不同的字首。</param>
        public BatchListSummaryView(string id)
        {
            this.id = id;
            doneTableId = "##" + id + "done";
            todoTableId = "##" + id + "todo";
        }

        /// <summary>沒有列時什麼都不畫。<paramref name="rows"/> 換了一份（參考不同）才重組文字。</summary>
        internal void Draw(IReadOnlyList<BatchListSummaryRow> rows)
        {
            if (rows.Count == 0)
                return;
            if (!ReferenceEquals(rows, summaryFor))
                RebuildSummary(rows);
            if (!ImGui.TreeNode(summaryHeader))
                return;

            DrawSummarySection(rows, handled: true, handledHeader, ImGuiColors.HealerGreen);
            DrawSummarySection(rows, handled: false, pendingHeader, ImGuiColors.DalamudOrange);
            ImGui.TreePop();
        }

        private void RebuildSummary(IReadOnlyList<BatchListSummaryRow> rows)
        {
            summaryFor = rows;
            summaryCells.Clear();
            int handled = 0, pending = 0;
            foreach (var row in rows)
            {
                if (row.Handled)
                    handled++;
                else
                    pending++;
                summaryCells.Add(SummaryCells(row));
            }

            summaryHandledCount = handled;
            summaryPendingCount = pending;
            summaryHeader = "Summary of the last run: ?? done, ?? still to deal with".Loc(handled, pending) +
                            "##" + id + "summary";
            handledHeader = "Done (??)".Loc(handled);
            pendingHeader = "Still to deal with (??)".Loc(pending);
        }

        private static string[] SummaryCells(BatchListSummaryRow row)
        {
            var name = row.Stacks > 1 ? "?? (?? stacks)".Loc(row.Name, row.Stacks) : row.Name;
            var result = row.Kind switch
            {
                BatchListResultKind.Listed => "listed".Loc(),
                BatchListResultKind.ShopList => "added to the shop list".Loc(),
                BatchListResultKind.KeptBelow => "below your threshold, left where it is".Loc(),
                BatchListResultKind.Skipped => "skipped".Loc(),
                _ => "not done".Loc(),
            };
            if (row.FromMemory)
                result += " " + "(remembered, not checked)".Loc();
            return
            [
                name,
                row.Quantity.ToString("N0"),
                row.Price > 0 ? row.Price.ToString("N0") : "?",
                row.MinPrice > 0 ? row.MinPrice.ToString("N0") : "-",
                row.VendorUnitPrice > 0 ? row.VendorUnitPrice.ToString("N0") : "-",
                result,
                row.Note,
            ];
        }

        private void DrawSummarySection(IReadOnlyList<BatchListSummaryRow> rows, bool handled, string header, Vector4 colour)
        {
            var count = handled ? summaryHandledCount : summaryPendingCount;
            ImGui.TextColored(colour, header);
            if (count == 0)
            {
                ImGui.SameLine();
                Grey(handled ? "nothing".Loc() : "nothing - all done".Loc());
                return;
            }

            var flags = ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH;
            var size = Vector2.Zero;
            if (count > BatchListPanel.MaxVisibleRows)
            {
                flags |= ImGuiTableFlags.ScrollY;
                size = new Vector2(0, (BatchListPanel.MaxVisibleRows + 1) * ImGui.GetFrameHeightWithSpacing());
            }

            if (!ImGui.BeginTable(handled ? doneTableId : todoTableId, 6, flags, size))
                return;
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Item".Loc());
            ImGui.TableSetupColumn("Qty".Loc());
            ImGui.TableSetupColumn("Price".Loc());
            ImGui.TableSetupColumn("Minimum".Loc());
            ImGui.TableSetupColumn("NPC price".Loc());
            ImGui.TableSetupColumn("Result".Loc());
            ImGui.TableHeadersRow();
            for (var i = 0; i < rows.Count; i++)
            {
                if (rows[i].Handled != handled)
                    continue;
                var cells = summaryCells[i];
                ImGui.TableNextRow();
                for (var c = 0; c < 6; c++)
                {
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(cells[c]);
                    if (c == 5 && cells[6].Length > 0 && ImGui.IsItemHovered())
                        ImGui.SetTooltip(cells[6]);
                }
            }

            ImGui.EndTable();
        }

        private static void Grey(string text)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextUnformatted(text);
            ImGui.PopStyleColor();
        }
    }
}
