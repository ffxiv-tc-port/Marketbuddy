using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Lumina.Excel.Sheets;
using Marketbuddy.Common;
using static Marketbuddy.Common.Dalamud;
using static Marketbuddy.Common.ImGuiEx;

namespace Marketbuddy
{
    internal sealed partial class PriceSurveyWindow
    {
        // =====================================================================
        //  比價
        // =====================================================================

        private void DrawCompareTab()
        {
            ImGui.Spacing();

            using (Disabled(loadTask != null))
            {
                if (ImGui.Button("Reload the log".Loc()))
                    RequestLoad();
            }

            ImGui.SameLine();
            if (loadTask != null)
                Grey("Loading...".Loc());
            else if (loadedAt == DateTime.MinValue)
                Grey("Not loaded yet".Loc());
            else
                Grey(DrawText.Loc("?? row(s), ?? item(s), ?? world(s)",
                    (totalRowsLoaded, entries.Count, worlds.Count), static (t, a) => t.Loc(a.Item1, a.Item2, a.Item3)));

            // 🔑 藏了東西就要說出來：少一個欄位跟「那個世界沒資料」在畫面上分不出來。
            if (hiddenExcludedWorlds.Count > 0)
            {
                hiddenExcludedText ??= "(?? excluded: ??)".Loc(
                    hiddenExcludedWorlds.Count, string.Join(", ", hiddenExcludedWorlds));
                SameLineIfFits(ImGui.CalcTextSize(hiddenExcludedText).X);
                GreyWrapped(hiddenExcludedText);
                Tooltip("Those worlds are on your excluded list, so their columns are left out of this table and out of the cheapest/undercut numbers. Their rows are still in the log file - untick the world to bring them back."
                    .Loc());
            }

            SameLineIfFits(200);
            ImGui.SetNextItemWidth(200);
            if (ImGui.BeginCombo("##mbsurveysort", SortModeKeys[sortMode].Loc()))
            {
                for (var i = 0; i < SortModeKeys.Length; i++)
                {
                    if (!ImGui.Selectable(SortModeKeys[i].Loc(), sortMode == i))
                        continue;
                    sortMode = i;
                    SortEntries();
                }

                ImGui.EndCombo();
            }

            ImGui.Spacing();
            GreyWrapped("A grey ? = this world has not been scanned (it is not 0 gil); a grey - = confirmed nobody is selling; green = that lowest price is your own."
                .Loc());
            ImGui.Spacing();

            if (entries.Count == 0)
            {
                ImGui.TextWrapped(
                    loadedAt == DateTime.MinValue
                        ? "Nothing has been loaded yet.".Loc()
                        : "The log file has no data yet - scan a world on the Scan tab first.".Loc());
                return;
            }

            DrawCompareTable();
        }

        private void DrawCompareTable()
        {
            var worldCount = Math.Min(worlds.Count, MaxWorldColumns);
            var columns = 3 + worldCount;

            const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
                                          ImGuiTableFlags.ScrollY | ImGuiTableFlags.ScrollX |
                                          ImGuiTableFlags.SizingFixedFit;

            if (!ImGui.BeginTable("##mbsurveytable", columns, flags, new Vector2(0, -1)))
                return;

            ImGui.TableSetupScrollFreeze(1, 1);
            ImGui.TableSetupColumn("Item".Loc(), ImGuiTableColumnFlags.WidthFixed, 220);
            ImGui.TableSetupColumn("Your price".Loc(), ImGuiTableColumnFlags.WidthFixed, 90);
            for (var i = 0; i < worldCount; i++)
                ImGui.TableSetupColumn(worlds[i].Name, ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Undercut".Loc(), ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableHeadersRow();

            foreach (var entry in entries)
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(entry.Label);

                ImGui.TableNextColumn();
                if (entry.OurPrice < 0)
                    Grey("?");
                else
                    ImGui.TextUnformatted(DrawText.N0(entry.OurPrice));

                for (var i = 0; i < worldCount; i++)
                {
                    ImGui.TableNextColumn();
                    DrawWorldCell(entry, worlds[i]);
                }

                ImGui.TableNextColumn();
                DrawUndercutCell(entry);
            }

            ImGui.EndTable();
        }

        private void DrawWorldCell(CompareEntry entry, (uint WorldId, string Name, DateTime LatestAt) world)
        {
            if (!entry.ByWorld.TryGetValue(world.WorldId, out var row))
            {
                // 🔑 「這個世界沒掃過」必須看得見，而且不可以長得像一個價格。
                Grey("?");
                Tooltip("This world has not been scanned for this item.".Loc());
                return;
            }

            var price = row.LowestForQuality;
            if (row.Verdict is "refused" or "timeout")
            {
                Grey("?");
                if (TooltipHovered())
                    Tooltip("?? had no result last time (??).".Loc(world.Name, row.Verdict));
                return;
            }

            if (price < 0)
            {
                Grey("—");
                if (TooltipHovered())
                    Tooltip("?? confirmed nobody is selling (??).".Loc(world.Name, FormatAge(row.AtUtc)));
                return;
            }

            var mine = row.LowestIsOurs == 1;
            if (mine)
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.HealerGreen);
            else if (entry.OurPrice >= 0 && price < entry.OurPrice)
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
            else
                ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.Text]);
            ImGui.TextUnformatted(DrawText.N0(price));
            ImGui.PopStyleColor();
            if (!TooltipHovered())
                return;

            var owner = row.LowestIsOurs switch
            {
                1 => "the lowest price is your own".Loc(),
                0 => "the lowest price is somebody else's".Loc(),
                _ => "who holds the lowest price is unknown".Loc(),
            };
            Tooltip("??  ??  ?? listing(s)  ??  source ??".Loc(
                world.Name, owner,
                row.ListingCount < 0 ? "?" : row.ListingCount.ToString(),
                FormatAge(row.AtUtc), row.Source));
        }

        private void DrawUndercutCell(CompareEntry entry)
        {
            if (entry.WorstUndercut <= 0)
            {
                if (entry.HasAnyData)
                {
                    Grey("—");
                    Tooltip("No scanned world is cheaper than you.".Loc());
                }
                else
                {
                    Grey("?");
                    Tooltip("No world has any data yet.".Loc());
                }

                return;
            }

            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
            ImGui.TextUnformatted(DrawText.N0("-", entry.WorstUndercut));
            ImGui.PopStyleColor();
            if (TooltipHovered())
                Tooltip("Cheapest is ?? (?? gil).".Loc(entry.BestWorldName, entry.BestPrice.ToString("N0")));
        }

        // =====================================================================
        //  記錄檔載入與整理
        // =====================================================================

        private void RequestLoad()
        {
            if (loadTask != null)
                return;
            loadRequested = true;
            // 🔴 讀檔一律丟到執行緒池：這個檔案會長到幾萬列。
            loadTask = Task.Run(PriceSurveyLog.LoadAll);
        }

        private void PumpLoad()
        {
            var task = loadTask;
            if (task == null || !task.IsCompleted)
                return;

            loadTask = null;
            List<PriceSurveyRow> rows;
            try
            {
                rows = task.GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                Log.Information(e, "[Marketbuddy] 巡檢：整理比價資料時發生例外。");
                return;
            }

            Rebuild(rows);
            loadedAt = DateTime.UtcNow;
        }

        private void Rebuild(List<PriceSurveyRow> rows)
        {
            entries.Clear();
            worlds.Clear();
            worldLatest.Clear();
            hiddenExcludedWorlds.Clear();
            hiddenExcludedText = null;
            totalRowsLoaded = rows.Count;

            var latestPerWorld = new Dictionary<uint, (string Name, DateTime At)>();
            var byKey = new Dictionary<(uint ItemId, bool Hq), CompareEntry>();

            foreach (var row in rows)
            {
                // 🔴 被排除的世界整列不算：欄位、最低價、壓價幅度、排序全部跟著一致，
                //    不會出現「欄位藏起來了但最便宜的還是它」那種自相矛盾的畫面。
                //    ⚠️ 記錄檔<b>沒有</b>被改動，勾回來就整份回來了。
                if (conf.IsWorldExcluded(row.WorldId))
                {
                    hiddenExcludedWorlds.Add(row.WorldName.Length > 0 ? row.WorldName : "#" + row.WorldId);
                    continue;
                }

                if (!latestPerWorld.TryGetValue(row.WorldId, out var w) || row.AtUtc > w.At)
                    latestPerWorld[row.WorldId] = (row.WorldName, row.AtUtc);

                var key = (row.ItemId, row.Hq);
                if (!byKey.TryGetValue(key, out var entry))
                {
                    byKey[key] = entry = new CompareEntry
                    {
                        ItemId = row.ItemId,
                        Hq = row.Hq,
                        Label = ItemName(row.ItemId) + (row.Hq ? " " + (char)SeIconChar.HighQuality : string.Empty),
                    };
                    entries.Add(entry);
                }

                // 同一個世界只留最新的那一列。
                if (entry.ByWorld.TryGetValue(row.WorldId, out var existing) && existing.AtUtc >= row.AtUtc)
                    continue;
                entry.ByWorld[row.WorldId] = row;
            }

            foreach (var (worldId, (name, at)) in latestPerWorld)
            {
                worlds.Add((worldId, name, at));
                worldLatest[worldId] = at;
            }

            worlds.Sort((a, b) => a.WorldId.CompareTo(b.WorldId));

            foreach (var entry in entries)
                entry.Summarise();

            SortEntries();
        }

        private void SortEntries()
        {
            switch (sortMode)
            {
                case 1:
                    entries.Sort((a, b) =>
                    {
                        var c = a.ItemId.CompareTo(b.ItemId);
                        return c != 0 ? c : a.Hq.CompareTo(b.Hq);
                    });
                    return;
                case 2:
                    entries.Sort((a, b) => b.OurPrice.CompareTo(a.OurPrice));
                    return;
                default:
                    entries.Sort((a, b) =>
                    {
                        var c = b.WorstUndercut.CompareTo(a.WorstUndercut);
                        return c != 0 ? c : a.ItemId.CompareTo(b.ItemId);
                    });
                    return;
            }
        }

        /// <summary>比價表格的一列：一件道具的一個品質，以及它在各世界的最新一筆記錄。</summary>
        private sealed class CompareEntry
        {
            public uint ItemId;
            public bool Hq;
            public string Label = string.Empty;
            public readonly Dictionary<uint, PriceSurveyRow> ByWorld = new();

            /// <summary>我方在這個品質上的最低掛售價（-1＝不知道）。</summary>
            public long OurPrice = -1;

            /// <summary>所有已掃世界裡最便宜的價格（-1＝都沒有資料）。</summary>
            public long BestPrice = -1;

            public string BestWorldName = string.Empty;

            /// <summary>被壓得最兇的那個世界壓了多少（0＝沒有人比我便宜、或資料不足）。</summary>
            public long WorstUndercut;

            /// <summary>至少有一個世界問到了答案（用來分辨「沒被壓價」與「還不知道」）。</summary>
            public bool HasAnyData;

            public void Summarise()
            {
                OurPrice = -1;
                BestPrice = -1;
                BestWorldName = string.Empty;
                WorstUndercut = 0;
                HasAnyData = false;

                foreach (var row in ByWorld.Values)
                {
                    if (row.OurPrice >= 0 && (OurPrice < 0 || row.OurPrice < OurPrice))
                        OurPrice = row.OurPrice;

                    if (row.Verdict is not ("ok" or "empty"))
                        continue;
                    HasAnyData = true;

                    var price = row.LowestForQuality;
                    if (price < 0)
                        continue;
                    if (BestPrice < 0 || price < BestPrice)
                    {
                        BestPrice = price;
                        BestWorldName = row.WorldName;
                    }

                    var undercut = row.UndercutBy;
                    if (undercut > WorstUndercut)
                        WorstUndercut = undercut;
                }
            }
        }
    }
}
