using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Marketbuddy.Common;

namespace Marketbuddy
{
    internal partial class PluginUI
    {
        // ---- 不自動上架清單：清單或語系變了才重組列文字（照名字排）。
        private readonly List<(uint ItemId, string Name)> excludeRows = new();
        private int excludeRowsRevision = -1;
        private int excludeRowsCount = -1;
        private CultureInfo? excludeRowsCulture;

        /// <summary>批次上架設定底下的「不自動上架的道具」：逐件移除或全部移除（形式比照快速上架跳過清單）。</summary>
        private void DrawBatchListExclusions()
        {
            DrawNestIndicator(1);
            var set = conf.BatchListExcludedItems;
            ImGui.TextUnformatted(DrawText.Loc("Never auto-listed: ?? item(s)", set.Count));
            HelpMarker(
                "Items you marked with \"Don't auto-list\" in the batch listing panel. Batch listing, \"Replenish all retainers\" and \"Tidy up this character\" skip them (NQ and HQ), even if they were ticked before. Quick listing and listing by hand are not affected. Removing an item here brings back the ticks you had for it."
                    .Loc());
            if (set.Count == 0)
            {
                GreyWrapped("None. Use \"Don't auto-list\" on a row of the batch listing panel.".Loc());
                return;
            }

            ImGui.SameLine();
            if (ImGui.SmallButton(DrawText.Concat("Remove all".Loc(), "##mbbatchexcludeall")))
            {
                BatchListExclusions.Clear();
                return;
            }

            if (excludeRowsRevision != BatchListExclusions.Revision || excludeRowsCount != set.Count ||
                !ReferenceEquals(excludeRowsCulture, CultureInfo.CurrentCulture))
                RebuildExcludeRows(set);

            var height = Math.Min(excludeRows.Count, 10) * ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().WindowPadding.Y * 2;
            ImGui.BeginChild("##mbbatchexcludelist", new Vector2(0, height), true);
            var removeAt = -1;
            for (var i = 0; i < excludeRows.Count; i++)
            {
                if (ImGui.SmallButton(DrawText.Concat(DrawText.Concat("Remove".Loc(), "##mbbatchexclude"), i)))
                    removeAt = i;
                ImGui.SameLine();
                ImGui.TextUnformatted(excludeRows[i].Name);
            }

            ImGui.EndChild();

            if (removeAt >= 0)
                BatchListExclusions.Set(excludeRows[removeAt].ItemId, false, excludeRows[removeAt].Name);
        }

        private void RebuildExcludeRows(HashSet<uint> set)
        {
            excludeRowsRevision = BatchListExclusions.Revision;
            excludeRowsCount = set.Count;
            excludeRowsCulture = CultureInfo.CurrentCulture;
            excludeRows.Clear();
            foreach (var id in set)
                excludeRows.Add((id, ItemName(id)));
            excludeRows.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));
        }
    }
}
