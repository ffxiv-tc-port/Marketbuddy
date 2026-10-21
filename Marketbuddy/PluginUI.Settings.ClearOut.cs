using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Lumina.Excel.Sheets;
using Marketbuddy.Common;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    internal partial class PluginUI
    {
        // ---- 常被低價下架：清單只在紀錄或門檻變了才重算；勾選以道具 id 記（AutoRetainer 的清單只認 id）。
        private List<JunkCandidate> junkRows = new();
        private readonly List<string> junkRowText = new();
        private long junkRowsRevision = -1;
        private long junkRowsThreshold = -1;
        private CultureInfo? junkRowsCulture;
        private readonly HashSet<uint> junkUnticked = new();
        private string junkCopyStatus = string.Empty;

        // ---- 背包分揀：列文字只在分揀結果換一份時重組。
        private readonly List<string> sortRowText = new();
        private readonly List<string> sortRowTooltip = new();
        private int sortRowTextVersion = -1;
        private CultureInfo? sortRowTextCulture;

        /// <summary>「清理背包」分頁：常被低價下架的道具匯出給 AutoRetainer，以及背包分揀。兩者都只在按按鈕時動。</summary>
        private void DrawClearOutTab()
        {
            ImGui.Spacing();
            DrawJunkExport();
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            DrawBagSort();
        }

        private void DrawJunkExport()
        {
            ImGui.TextUnformatted("Often delisted for a low price".Loc());
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Every time relisting delists something because its price fell below the NPC price or below your minimum, it is counted here (kept across restarts, up to 180 days). Listed: items delisted at least twice whose last known sale was still below your minimum."
                        .Loc());

            var threshold = (long)conf.BatchMinPrice;
            if (threshold <= 0)
            {
                GreyWrapped("Set a minimum price under Relisting (\"Delist items whose target price is below\") first - without it there is no telling what is still too cheap."
                    .Loc());
                return;
            }

            if (!LowPriceDelistLedger.Loaded)
            {
                GreyWrapped("Reading the record...".Loc());
                return;
            }

            var revision = LowPriceDelistLedger.Revision;
            if (revision != junkRowsRevision || threshold != junkRowsThreshold ||
                !ReferenceEquals(junkRowsCulture, CultureInfo.CurrentCulture))
            {
                junkRowsRevision = revision;
                junkRowsThreshold = threshold;
                junkRowsCulture = CultureInfo.CurrentCulture;
                junkRows = ClearOutRules.JunkCandidates(LowPriceDelistLedger.Snapshot(), threshold);
                junkRowText.Clear();
                foreach (var row in junkRows)
                    junkRowText.Add("??  delisted ?? times  last sale ?? gil (??)".Loc(ItemName(row.ItemId),
                        row.Count, row.LastSalePrice.ToString("N0"), FormatDay(row.LastSaleAtUtc)));
            }

            if (junkRows.Count == 0)
            {
                GreyWrapped(DrawText.Loc("Nothing yet: no item has been delisted twice with its last sale under ?? gil.",
                    DrawText.N0(threshold)));
                return;
            }

            if (ImGui.SmallButton("Tick all".Loc() + "##mbjunkall"))
                junkUnticked.Clear();
            ImGui.SameLine();
            if (ImGui.SmallButton("Untick all".Loc() + "##mbjunknone"))
            {
                foreach (var row in junkRows)
                    junkUnticked.Add(row.ItemId);
            }

            var height = Math.Min(junkRows.Count, 10) * ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().WindowPadding.Y * 2;
            ImGui.BeginChild("##mbjunklist", new Vector2(0, height), true);
            var ticked = 0;
            for (var i = 0; i < junkRows.Count; i++)
            {
                var row = junkRows[i];
                var on = !junkUnticked.Contains(row.ItemId);
                if (ImGui.Checkbox(DrawText.Concat("##mbjunk", (int)row.ItemId), ref on))
                {
                    if (on)
                        junkUnticked.Remove(row.ItemId);
                    else
                        junkUnticked.Add(row.ItemId);
                }

                if (on)
                    ticked++;
                ImGui.SameLine();
                ImGui.TextUnformatted(junkRowText[i]);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Item ID ??. Last delisted ??. Quality seen: ??.".Loc(row.ItemId,
                        FormatDay(row.LastDelistUtc), QualityText(row.HasHq, row.HasNq)));
            }

            ImGui.EndChild();

            var disabled = ticked == 0;
            if (disabled)
                ImGui.BeginDisabled();
            if (ImGui.Button(DrawText.Concat(DrawText.Loc("Copy ?? item(s) for AutoRetainer", ticked), "##mbjunkcopy")))
            {
                var ids = new List<uint>();
                foreach (var row in junkRows)
                {
                    if (!junkUnticked.Contains(row.ItemId))
                        ids.Add(row.ItemId);
                }

                ImGui.SetClipboardText(ClearOutRules.ToAutoRetainerClipboard(ids));
                junkCopyStatus = DrawText.Loc("Copied ?? item ID(s) to the clipboard.", ids.Count);
                Log.Information($"[Marketbuddy] 常被低價下架：複製 {ids.Count} 個道具 id 到剪貼簿（給 AutoRetainer）");
            }

            if (disabled)
                ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(
                    "Copies the ticked item IDs in the format AutoRetainer reads. In AutoRetainer, open \"Inventory Cleanup/Unconditional Sell List\", hold CTRL and click \"Merge with Clipboard\"; items already on the list are kept as they are.\nUse that list, not the Quick Venture one: the Quick Venture list only sells what comes back from quick ventures, while these come back from the market.\nAutoRetainer's list goes by item ID, so both HQ and NQ of an item get sold. It still leaves alone stacks bigger than its \"Maximum stack size to be sold\" and anything on its Protection List.\nNothing is written into AutoRetainer's settings by this plugin."
                        .Loc());

            if (junkCopyStatus.Length > 0)
                GreyWrapped(junkCopyStatus);
        }

        private void DrawBagSort()
        {
            var sorter = marketbuddy.BagSorter;
            ImGui.TextUnformatted("Bag sort".Loc());
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Sorts what is in your four bags into three piles: sell to the shop (delisted for a low price before, and its last sale is still below your minimum), put on the market, and needs a price check (no sale data, or the sale is too old). Only the first pile is ticked for you. Nothing is listed, delisted or looked up on the market board; prices come from Universalis (read only), and only when \"Also price from the most recent sale\" is on."
                        .Loc());
            if (sorter == null)
                return;
            sorter.MarkViewed();

            if (ImGui.Button("Sort my bags".Loc() + "##mbbagsort"))
                sorter.RequestSort();
            if (sorter.Active)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Clear".Loc() + "##mbbagsortclear"))
                    sorter.Clear();
            }

            if (!sorter.Active)
                return;

            if (!conf.RelistUseLastSoldPrice)
                GreyWrapped("\"Also price from the most recent sale\" (Relisting) is off, so no prices are looked up and everything lands in \"Needs a price check\"."
                    .Loc());
            if (conf.BatchMinPrice <= 0)
                GreyWrapped("No minimum price is set under Relisting, so nothing lands in \"Sell to the shop\".".Loc());
            GreyWrapped(sorter.ProtectedCount is { } protectedCount
                ? DrawText.Loc("InventoryTools craft lists: ?? item(s) protected and left alone.", protectedCount)
                : "InventoryTools craft lists could not be read (not installed?) - that protection is skipped.".Loc());

            var rows = sorter.Rows;
            if (sortRowTextVersion != sorter.Version || !ReferenceEquals(sortRowTextCulture, CultureInfo.CurrentCulture))
            {
                sortRowTextVersion = sorter.Version;
                sortRowTextCulture = CultureInfo.CurrentCulture;
                sortRowText.Clear();
                sortRowTooltip.Clear();
                foreach (var row in rows)
                {
                    sortRowText.Add(SortRowLine(row));
                    sortRowTooltip.Add(SortReasonText(row));
                }
            }

            DrawSortPile(sorter, rows, BagSortPile.Vendor, "Sell to the shop", true);
            DrawSortPile(sorter, rows, BagSortPile.Market, "Put on the market", false);
            DrawSortPile(sorter, rows, BagSortPile.NeedsPricing, "Needs a price check", false);
            DrawSortPile(sorter, rows, BagSortPile.Excluded, "Left alone", false);

            var stacks = 0;
            var gil = 0L;
            foreach (var row in rows)
            {
                if (!sorter.IsTicked(row))
                    continue;
                stacks += row.Stacks;
                gil += (long)row.Quantity * row.VendorUnitPrice;
            }

            var seller = marketbuddy.VendorSeller;
            var reason = seller == null || !seller.IsAvailable
                ? "the right-click menu hook is not available".Loc()
                : seller.IsRunning ? "selling to the shop is running right now".Loc()
                : stacks == 0 ? "nothing is ticked to sell".Loc() : string.Empty;
            var disabled = reason.Length > 0;
            if (disabled)
                ImGui.BeginDisabled();
            if (ImGui.Button(DrawText.Concat(
                    "Hand the ticked over to \"Sell to the shop\" (?? stack(s), about ?? gil)".Loc(stacks, DrawText.N0(gil)),
                    "##mbbagsorthandover")))
                sorter.RequestHandOver();
            if (disabled)
                ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(disabled
                    ? reason
                    : "Only puts the ticked stacks on the \"Sell to the shop\" list next to a retainer's sell list; nothing is sold yet. Open a retainer's sell list and press \"Sell to the shop\" there (twice, to confirm). Each stack is checked again right before it goes and skipped if it changed."
                        .Loc());

            if (sorter.LastHandOverStacks >= 0)
                GreyWrapped(DrawText.Loc(
                    "Handed over ?? stack(s). Open a retainer's sell list and press \"Sell to the shop\" there.",
                    sorter.LastHandOverStacks));
        }

        private void DrawSortPile(BagSorter sorter, IReadOnlyList<BagSorter.Row> rows, BagSortPile pile, string title,
            bool openByDefault)
        {
            var count = 0;
            foreach (var row in rows)
            {
                if (row.Verdict.Pile == pile)
                    count++;
            }

            var flags = openByDefault ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
            var label = DrawText.Loc(title, count, static (t, c) => "?? (??)".Loc(t.Loc(), c));
            if (!ImGui.TreeNodeEx(DrawText.Concat(label, DrawText.Concat("###mbbagpile", (int)pile)), flags))
                return;

            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.Verdict.Pile != pile)
                    continue;
                if (row.Verdict.CanTick)
                {
                    var on = sorter.IsTicked(row);
                    if (ImGui.Checkbox(DrawText.Concat(row.Key.Hq ? "##mbbagrowh" : "##mbbagrown", (int)row.Key.ItemId), ref on))
                        sorter.SetTicked(row, on);
                    ImGui.SameLine();
                }

                ImGui.TextUnformatted(i < sortRowText.Count ? sortRowText[i] : string.Empty);
                if (ImGui.IsItemHovered() && i < sortRowTooltip.Count)
                    ImGui.SetTooltip(sortRowTooltip[i]);
            }

            ImGui.TreePop();
        }

        private static string SortRowLine(BagSorter.Row row)
        {
            var sale = row.SalePrice >= 0
                ? "last sale ?? gil (??)".Loc(row.SalePrice.ToString("N0"), FormatDay(row.SaleAtUtc))
                : "last sale ?".Loc();
            var line = "?? x??  ??".Loc(row.Name, row.Quantity, sale);
            if (row.DelistCount > 0)
                line += "  " + "delisted ?? times".Loc(row.DelistCount);
            return line;
        }

        private static string SortReasonText(BagSorter.Row row)
        {
            var why = row.Verdict.Reason switch
            {
                BagSortReason.Protected => "On an InventoryTools craft list.".Loc(),
                BagSortReason.NotMarketable => "Cannot be sold on the market.".Loc(),
                BagSortReason.PriceLookupOff => "No price looked up: the last-sale lookup is off.".Loc(),
                BagSortReason.PriceLoading => "Still asking Universalis...".Loc(),
                BagSortReason.PriceFailed => "Universalis could not be reached; try sorting again later.".Loc(),
                BagSortReason.NoSaleData => "Universalis has no sale of this on your data centre.".Loc(),
                BagSortReason.SaleStale => "The last sale is older than your freshness limit under Relisting.".Loc(),
                BagSortReason.BelowThresholdAndDelisted => "Delisted for a low price before, and the last sale is still below your minimum.".Loc(),
                BagSortReason.BelowThresholdNeverDelisted => "The last sale is below your minimum, but it has never been delisted for a low price, so it is not ticked.".Loc(),
                BagSortReason.AtOrAboveThreshold => "The last sale is at or above your minimum.".Loc(),
                BagSortReason.NoThreshold => "No minimum price is set.".Loc(),
                _ => string.Empty,
            };
            if (row.Verdict.Pile != BagSortPile.Excluded && !row.Verdict.CanTick)
                why += "\n" + "Cannot be sold this way: NPC shops do not buy it, or the game asks yes/no when selling it (materia, fully spiritbonded, unique)."
                    .Loc();
            var npc = row.VendorUnitPrice > 0
                ? "NPC price: ?? gil each".Loc(row.VendorUnitPrice.ToString("N0"))
                : "NPC shops do not buy this".Loc();
            return why + "\n" + "?? stack(s). ??".Loc(row.Stacks, npc);
        }

        private static string FormatDay(DateTime utc)
            => utc == DateTime.MinValue ? "?" : utc.ToLocalTime().ToString("MM-dd", CultureInfo.InvariantCulture);

        private static string QualityText(bool hq, bool nq)
            => hq && nq ? "HQ, NQ" : hq ? "HQ" : "NQ";

        private readonly Dictionary<uint, string> itemNameCache = new();

        private string ItemName(uint itemId)
        {
            if (itemNameCache.TryGetValue(itemId, out var name))
                return name;
            var sheet = DataManager.GetExcelSheet<Item>();
            name = sheet != null && sheet.TryGetRow(itemId, out var row) ? row.Name.ExtractText() : $"#{itemId}";
            itemNameCache[itemId] = name;
            return name;
        }

        private static void GreyWrapped(string text)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextWrapped(text);
            ImGui.PopStyleColor();
        }
    }
}
