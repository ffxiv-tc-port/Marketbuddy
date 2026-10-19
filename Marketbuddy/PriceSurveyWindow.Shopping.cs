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
        //  採購
        // =====================================================================

        /// <summary>
        /// 「打開這個開關會多花多少」——<b>按下去之前</b>就要看得見。
        /// 🔑 這裡刻意報<b>上限</b>（清單全部都要問一次）而不是期望值：與我方掛售清單重疊的
        /// 那幾件其實不必多查，但那要等實際建佇列才知道。把估計報高不會害人多等，
        /// 報低會。
        /// </summary>
        private void DrawShoppingCost()
        {
            // 🔑 開關<b>關著</b>的時候也要看得見件數與預估時間——「打開會多花多久」正是
            //    使用者在按下那個核取方塊之前要知道的事。沒有清單檔又沒開，才完全不佔版面。
            if (!conf.PriceSurveyShoppingList && !ShoppingList.FileExists)
                return;

            ImGui.Spacing();

            if (!ShoppingList.Loaded)
            {
                Grey("Reading the shopping list...".Loc());
                return;
            }

            if (!ShoppingList.FileExists)
            {
                Grey("No shopping list file yet (??).".Loc(ShoppingList.FileName));
                Tooltip(SafeShoppingListPath());
                SameLineIfFits(ButtonWidth("Create a template list file".Loc()));
                if (ImGui.Button("Create a template list file".Loc()))
                {
                    // 🔴 只在檔案不存在時建立，永遠不覆寫使用者寫好的清單。
                    if (!ShoppingList.TryCreateTemplate())
                        ShoppingList.RequestReload();
                }

                return;
            }

            var count = ShoppingList.Items.Count;
            ImGui.TextWrapped(DrawText.Loc(
                "Shopping list: ?? item(s) - at most about ?? more minute(s) on every world you scan.",
                (count, MarketRequestGate.IntervalMs),
                static (t, a) => t.Loc(a.Item1, (a.Item1 * a.Item2 / 60000.0).ToString("F1"))));
            if (TooltipHovered())
                Tooltip(
                    "Worked out from the current ?? ms gap between queries. Items you are already selling need no extra query, so the real cost is usually lower than this."
                        .Loc(MarketRequestGate.IntervalMs));

            SameLineIfFits(ButtonWidth("Reload the shopping list".Loc()));
            using (Disabled(ShoppingList.IsLoading))
            {
                if (ImGui.Button("Reload the shopping list".Loc()))
                    ShoppingList.RequestReload();
            }

            Tooltip(SafeShoppingListPath());

            // ⚠️ 認不出來與不能買賣的那幾行必須看得見：靜靜地少查幾件與「查過了沒人賣」
            //    在畫面上長得一模一樣。
            var unresolved = ShoppingList.Unresolved;
            if (unresolved.Count > 0)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
                ImGui.TextWrapped("?? line(s) were not recognised: ??".Loc(
                    unresolved.Count, string.Join(", ", unresolved)));
                ImGui.PopStyleColor();
                Tooltip("Write the item name exactly as the game spells it, or write the item id instead.".Loc());
            }

            var unmarketable = ShoppingList.Unmarketable;
            if (unmarketable.Count > 0)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
                ImGui.TextWrapped("?? line(s) cannot be traded on the market board: ??".Loc(
                    unmarketable.Count, string.Join(", ", unmarketable)));
                ImGui.PopStyleColor();
            }

            if (ShoppingList.Truncated > 0)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
                ImGui.TextWrapped("?? line(s) past the ?? item limit were ignored.".Loc(
                    ShoppingList.Truncated, ShoppingList.MaxEntries));
                ImGui.PopStyleColor();
            }
        }

        private void DrawShoppingTab()
        {
            ImGui.Spacing();

            using (Disabled(shoppingLoadTask != null))
            {
                if (ImGui.Button(DrawText.Concat("Reload the log".Loc(), "##mbshoppingreload")))
                    RequestShoppingLoad();
            }

            ImGui.SameLine();
            if (shoppingLoadTask != null)
                Grey("Loading...".Loc());
            else if (shoppingLoadedAt == DateTime.MinValue)
                Grey("Not loaded yet".Loc());
            else
                Grey(DrawText.Loc("?? row(s), ?? item(s), ?? world(s)",
                    (shoppingTotalRowsLoaded, shoppingEntries.Count, shoppingWorlds.Count),
                    static (t, a) => t.Loc(a.Item1, a.Item2, a.Item3)));

            if (shoppingHiddenExcludedWorlds.Count > 0)
            {
                shoppingHiddenExcludedText ??= "(?? excluded: ??)".Loc(
                    shoppingHiddenExcludedWorlds.Count, string.Join(", ", shoppingHiddenExcludedWorlds));
                SameLineIfFits(ImGui.CalcTextSize(shoppingHiddenExcludedText).X);
                GreyWrapped(shoppingHiddenExcludedText);
                Tooltip("Those worlds are on your excluded list, so their columns are left out of this table and out of the cheapest/spread numbers. Their rows are still in the log file - untick the world to bring them back."
                    .Loc());
            }

            SameLineIfFits(240);
            ImGui.SetNextItemWidth(240);
            if (ImGui.BeginCombo("##mbshoppingsort", ShoppingSortModeKeys[shoppingSortMode].Loc()))
            {
                for (var i = 0; i < ShoppingSortModeKeys.Length; i++)
                {
                    if (!ImGui.Selectable(ShoppingSortModeKeys[i].Loc(), shoppingSortMode == i))
                        continue;
                    shoppingSortMode = i;
                    SortShoppingEntries();
                }

                ImGui.EndCombo();
            }

            ImGui.Spacing();
            if (!conf.PriceSurveyShoppingList)
            {
                GreyWrapped("Looking up the shopping list is switched off, so nothing new is being recorded. Turn it on over on the Scan tab."
                    .Loc());
                ImGui.Spacing();
            }

            GreyWrapped("Each cell is the cheapest unit price that world had, and under it how many were on sale there. A grey ? = never looked up (it is not 0 gil); a grey - = confirmed nobody is selling."
                .Loc());
            ImGui.Spacing();

            if (shoppingEntries.Count == 0)
            {
                ImGui.TextWrapped(
                    shoppingLoadedAt == DateTime.MinValue
                        ? "Nothing has been loaded yet.".Loc()
                        : "No shopping prices recorded yet - put items in ?? and scan a world with the shopping option on."
                            .Loc(ShoppingList.FileName));
                return;
            }

            DrawShoppingTable();
        }

        private void DrawShoppingTable()
        {
            RefreshShoppingWanted();

            var worldCount = Math.Min(shoppingWorlds.Count, MaxWorldColumns);
            var columns = 3 + worldCount;

            const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
                                          ImGuiTableFlags.ScrollY | ImGuiTableFlags.ScrollX |
                                          ImGuiTableFlags.SizingFixedFit;

            if (!ImGui.BeginTable("##mbshoppingtable", columns, flags, new Vector2(0, -1)))
                return;

            ImGui.TableSetupScrollFreeze(1, 1);
            ImGui.TableSetupColumn("Item".Loc(), ImGuiTableColumnFlags.WidthFixed, 220);
            ImGui.TableSetupColumn("Wanted".Loc(), ImGuiTableColumnFlags.WidthFixed, 60);
            for (var i = 0; i < worldCount; i++)
                ImGui.TableSetupColumn(shoppingWorlds[i].Name, ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Go here".Loc(), ImGuiTableColumnFlags.WidthFixed, 150);
            ImGui.TableHeadersRow();

            foreach (var entry in shoppingEntries)
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(entry.Label);

                ImGui.TableNextColumn();
                if (shoppingWantedById.TryGetValue(entry.ItemId, out var wanted) && wanted > 0)
                    ImGui.TextUnformatted(DrawText.N0(wanted));
                else
                    Grey("—");

                for (var i = 0; i < worldCount; i++)
                {
                    ImGui.TableNextColumn();
                    DrawShoppingCell(entry, shoppingWorlds[i]);
                }

                ImGui.TableNextColumn();
                DrawShoppingBestCell(entry);
            }

            ImGui.EndTable();
        }

        private void DrawShoppingCell(ShoppingEntry entry, (uint WorldId, string Name, DateTime LatestAt) world)
        {
            if (!entry.ByWorld.TryGetValue(world.WorldId, out var row))
            {
                // 🔑 「這個世界沒查過」必須看得見，而且不可以長得像一個價格。
                Grey("?");
                Tooltip("This world has never been looked up for this item.".Loc());
                return;
            }

            if (!row.Answered)
            {
                Grey("?");
                if (TooltipHovered())
                    Tooltip("?? had no result last time (??).".Loc(world.Name, row.Verdict));
                return;
            }

            var price = row.CheapestPrice;
            if (price < 0)
            {
                Grey("—");
                if (TooltipHovered())
                    Tooltip("?? confirmed nobody is selling (??).".Loc(world.Name, FormatAge(row.AtUtc)));
                return;
            }

            var cheapest = entry.BestPrice >= 0 && price == entry.BestPrice;
            if (cheapest)
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.HealerGreen);
            else
                ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.Text]);
            ImGui.TextUnformatted(row.CheapestIsHq ? DrawText.Concat(DrawText.N0(price), HqSuffix) : DrawText.N0(price));
            ImGui.PopStyleColor();

            // 🔑 「這裡買得到幾個」與「那是什麼時候的事」都放在列上：出門前要看的就是這兩個。
            //    不知道時畫 ?，絕不畫成 0——0 的意思是「確認一個都沒有」。
            var quantity = row.CheapestQuantity;
            Grey(quantity < 0 ? "×?" : DrawText.N0("×", quantity));
            if (!TooltipHovered())
                return;

            Tooltip("??  ??  ?? listing(s)  NQ ??  HQ ??  source ??".Loc(
                world.Name,
                FormatAge(row.AtUtc),
                row.ListingCount < 0 ? "?" : row.ListingCount.ToString(),
                row.LowestNq < 0 ? "-" : row.LowestNq.ToString("N0") + " ×" + FormatQuantity(row.NqQuantity),
                row.LowestHq < 0 ? "-" : row.LowestHq.ToString("N0") + " ×" + FormatQuantity(row.HqQuantity),
                row.Source));
        }

        private static string FormatQuantity(long quantity)
            => quantity < 0 ? "?" : quantity.ToString("N0");

        private void DrawShoppingBestCell(ShoppingEntry entry)
        {
            if (entry.BestPrice < 0)
            {
                if (entry.HasAnyData)
                {
                    Grey("—");
                    Tooltip("Every world that was looked up had nobody selling this.".Loc());
                }
                else
                {
                    Grey("?");
                    Tooltip("No world has been looked up for this item yet.".Loc());
                }

                return;
            }

            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.HealerGreen);
            ImGui.TextUnformatted(DrawText.Loc("?? at ?? gil", (entry.BestWorldName, entry.BestPrice),
                static (t, a) => t.Loc(a.Item1, a.Item2.ToString("N0"))));
            ImGui.PopStyleColor();
            if (TooltipHovered())
                Tooltip(entry.WorstPrice > entry.BestPrice
                    ? "?? is the cheapest looked-up world; the dearest was ?? at ?? (?? more per unit)."
                        .Loc(entry.BestWorldName, entry.WorstWorldName, entry.WorstPrice.ToString("N0"),
                            (entry.WorstPrice - entry.BestPrice).ToString("N0"))
                    : "Only one world has a price for this item so far.".Loc());
        }

        private void RequestShoppingLoad()
        {
            if (shoppingLoadTask != null)
                return;
            shoppingLoadRequested = true;
            // 🔴 讀檔一律丟到執行緒池。
            shoppingLoadTask = Task.Run(ShoppingSurveyLog.LoadAll);
        }

        private void PumpShoppingLoad()
        {
            var task = shoppingLoadTask;
            if (task == null || !task.IsCompleted)
                return;

            shoppingLoadTask = null;
            List<ShoppingSurveyRow> rows;
            try
            {
                rows = task.GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                Log.Information(e, "[Marketbuddy] 採購：整理採購行情時發生例外。");
                return;
            }

            RebuildShopping(rows);
            shoppingLoadedAt = DateTime.UtcNow;
        }

        private void RebuildShopping(List<ShoppingSurveyRow> rows)
        {
            shoppingEntries.Clear();
            shoppingWorlds.Clear();
            shoppingHiddenExcludedWorlds.Clear();
            shoppingHiddenExcludedText = null;
            shoppingTotalRowsLoaded = rows.Count;

            var latestPerWorld = new Dictionary<uint, (string Name, DateTime At)>();
            var byItem = new Dictionary<uint, ShoppingEntry>();

            foreach (var row in rows)
            {
                // 🔴 被排除的世界整列不算。這一張表的用途就是「出發前決定要去哪個世界買」，
                //    列一個去不了的世界而且說它最便宜，比不列它糟得多。
                if (conf.IsWorldExcluded(row.WorldId))
                {
                    shoppingHiddenExcludedWorlds.Add(
                        row.WorldName.Length > 0 ? row.WorldName : "#" + row.WorldId);
                    continue;
                }

                if (!latestPerWorld.TryGetValue(row.WorldId, out var w) || row.AtUtc > w.At)
                    latestPerWorld[row.WorldId] = (row.WorldName, row.AtUtc);

                if (!byItem.TryGetValue(row.ItemId, out var entry))
                {
                    byItem[row.ItemId] = entry = new ShoppingEntry
                    {
                        ItemId = row.ItemId,
                        Label = ItemName(row.ItemId),
                    };
                    shoppingEntries.Add(entry);
                }

                // 同一個世界只留最新的那一列。
                if (entry.ByWorld.TryGetValue(row.WorldId, out var existing) && existing.AtUtc >= row.AtUtc)
                    continue;
                entry.ByWorld[row.WorldId] = row;
            }

            foreach (var (worldId, (name, at)) in latestPerWorld)
                shoppingWorlds.Add((worldId, name, at));

            shoppingWorlds.Sort((a, b) => a.WorldId.CompareTo(b.WorldId));

            foreach (var entry in shoppingEntries)
                entry.Summarise();

            SortShoppingEntries();
        }

        private void SortShoppingEntries()
        {
            switch (shoppingSortMode)
            {
                case 1:
                    shoppingEntries.Sort((a, b) =>
                    {
                        // 沒有價格的排最後，而不是排在「0 gil」的位置。
                        var left = a.BestPrice < 0 ? long.MaxValue : a.BestPrice;
                        var right = b.BestPrice < 0 ? long.MaxValue : b.BestPrice;
                        var c = left.CompareTo(right);
                        return c != 0 ? c : a.ItemId.CompareTo(b.ItemId);
                    });
                    return;
                case 2:
                    shoppingEntries.Sort((a, b) => a.ItemId.CompareTo(b.ItemId));
                    return;
                default:
                    shoppingEntries.Sort((a, b) =>
                    {
                        var c = b.Spread.CompareTo(a.Spread);
                        return c != 0 ? c : a.ItemId.CompareTo(b.ItemId);
                    });
                    return;
            }
        }

        /// <summary>採購清單檔換了新版本就把「我想買幾個」重建一次。</summary>
        private void RefreshShoppingWanted()
        {
            var generation = ShoppingList.Generation;
            if (generation == shoppingWantedGeneration)
                return;
            shoppingWantedGeneration = generation;

            shoppingWantedById.Clear();
            foreach (var item in ShoppingList.Items)
                shoppingWantedById[item.ItemId] = item.Wanted;
        }

        private static string SafeShoppingListPath()
        {
            try
            {
                return ShoppingList.FilePath;
            }
            catch
            {
                return ShoppingList.FileName;
            }
        }

        /// <summary>採購表格的一列：一件想買的道具，以及它在各世界的最新一筆記錄。</summary>
        private sealed class ShoppingEntry
        {
            public uint ItemId;
            public string Label = string.Empty;
            public readonly Dictionary<uint, ShoppingSurveyRow> ByWorld = new();

            /// <summary>所有查過的世界裡最便宜的單價（-1＝沒有任何世界有人在賣，或都還沒查）。</summary>
            public long BestPrice = -1;

            public string BestWorldName = string.Empty;

            /// <summary>所有查過的世界裡最貴的單價（-1＝同上）。</summary>
            public long WorstPrice = -1;

            public string WorstWorldName = string.Empty;

            /// <summary>最貴與最便宜的差（0＝只有一個世界有價格，或資料不足）。</summary>
            public long Spread;

            /// <summary>至少有一個世界問到了答案（用來分辨「沒人在賣」與「還不知道」）。</summary>
            public bool HasAnyData;

            public void Summarise()
            {
                BestPrice = -1;
                WorstPrice = -1;
                BestWorldName = string.Empty;
                WorstWorldName = string.Empty;
                Spread = 0;
                HasAnyData = false;

                foreach (var row in ByWorld.Values)
                {
                    if (!row.Answered)
                        continue;
                    HasAnyData = true;

                    var price = row.CheapestPrice;
                    if (price < 0)
                        continue;

                    if (BestPrice < 0 || price < BestPrice)
                    {
                        BestPrice = price;
                        BestWorldName = row.WorldName;
                    }

                    if (WorstPrice < 0 || price > WorstPrice)
                    {
                        WorstPrice = price;
                        WorstWorldName = row.WorldName;
                    }
                }

                if (BestPrice >= 0 && WorstPrice > BestPrice)
                    Spread = WorstPrice - BestPrice;
            }
        }
    }
}
