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
        //  待處理
        // =====================================================================

        private void DrawPendingTab()
        {
            var pending = gui.Pending;
            if (pending == null)
            {
                ImGui.Spacing();
                Grey("The pending list is not available.".Loc());
                return;
            }

            // 讓 framework 執行緒知道「現在有人在看」，它才會去拍僱員與引擎狀態的快照。
            pending.NoteUiVisible();

            ImGui.Spacing();

            using (Disabled(pending.IsRecomputing))
            {
                if (ImGui.Button("Recalculate".Loc()))
                    pending.RequestRecompute("user button");
            }

            Tooltip(
                "Reads the survey log and your listings and works out which items are undercut or should come off the board. Pure arithmetic: no market query is sent, and no price is ever changed by this button."
                    .Loc());

            ImGui.SameLine();
            if (pending.IsRecomputing)
                Grey("Recomputing...".Loc());
            else if (pending.LastComputedAt == DateTime.MinValue)
                Grey("Not calculated in this session yet".Loc());
            else
                Grey(DrawText.Loc("Last calculated ??", FormatAge(pending.LastComputedAt)));

            if (ImGui.Checkbox("Recalculate automatically when a survey finishes".Loc(),
                    ref conf.PendingRecomputeAfterSurvey))
                conf.Save();
            Tooltip(
                "On (default): the moment a world has been surveyed all the way to the end, this list is worked out again from the fresh data. It only calculates - it never changes a price, never delists anything and never sends an extra market query. Repricing always stays one button per row, pressed by you."
                    .Loc());

            ImGui.Checkbox("Also show the ones you skipped".Loc(), ref showSkipped);
            ImGui.SameLine();
            using (Disabled(pending.IsRecomputing))
            {
                if (ImGui.Button("Clear skip marks".Loc()))
                    PendingActions.ClearSkipped();
            }

            Tooltip("Puts every entry you skipped back on the list.".Loc());

            if (pending.StatusText.Length > 0)
            {
                ImGui.Spacing();
                ImGui.TextWrapped(pending.StatusText);
            }

            ImGui.Spacing();
            // 🔑 「被壓價」整個桶都是相對於家世界定義的，所以那是哪一個世界必須看得見。
            if (pending.HomeWorldId != 0)
                Grey(DrawText.Loc("Home world: ??", pending.HomeWorldName));
            else
                Grey("Home world: ? (not logged in)".Loc());
            Grey(DrawText.Loc("List file: ??", PendingActions.FileName));
            Tooltip(SafePendingPath());
            GreyWrapped("A grey ? means \"not known\" - never 0 gil. A suggested price from another world is a reference only: that is a different market."
                .Loc());
            // 🔑 藏了東西就要說出來：拿掉一個世界的建議價，
            //    與「那件東西真的沒人在賣」在畫面上分不出來。
            if (pending.ExcludedWorldsInLog.Count > 0)
            {
                GreyWrapped(pendingExcludedText.Get(pending.ExcludedWorldsInLog,
                    static worlds => "?? excluded world(s) not used for suggestions: ??".Loc(
                        worlds.Count, string.Join(", ", worlds))));
                Tooltip("Those worlds are on your excluded list, so their prices are never used as a suggested price here. Nothing was deleted - the survey log still has every row, and unticking a world brings it back on the next recalculation. Your own world is never excluded from its own pricing."
                    .Loc());
            }

            ImGui.Spacing();
            ImGui.Separator();

            var rows = pending.Snapshot;
            var shown = 0;
            foreach (var kind in BucketOrder)
                shown += DrawBucket(pending, rows, kind);

            if (shown != 0)
                return;

            ImGui.Spacing();
            ImGui.TextWrapped(PendingActions.Loaded
                ? "Nothing is waiting for you right now.".Loc()
                : "Reading the list file...".Loc());
        }

        private int DrawBucket(PendingActionsBuilder pending, IReadOnlyList<PendingActionRow> rows,
            PendingActionKind kind)
        {
            var bucket = bucketScratch;
            bucket.Clear();
            var hiddenSkipped = 0;
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.Kind != kind)
                    continue;
                if (row.Skipped && !showSkipped)
                {
                    hiddenSkipped++;
                    continue;
                }

                bucket.Add(row);
            }

            bucket.Sort(CompareBucketRows);

            var header = DrawText.Loc(BucketTitle(kind), (bucket.Count, (int)kind),
                static (t, a) => t.Loc() + $" ({a.Item1})" + "###mbpendingbucket" + a.Item2);
            ImGui.Spacing();
            if (!ImGui.CollapsingHeader(header, ImGuiTreeNodeFlags.DefaultOpen))
                return bucket.Count + hiddenSkipped;

            GreyWrapped(BucketHelp(kind).Loc());

            if (hiddenSkipped > 0)
                Grey(DrawText.Loc("?? entry(ies) hidden because you skipped them.", hiddenSkipped));

            if (bucket.Count == 0)
            {
                Grey("Nothing in this group.".Loc());
                return hiddenSkipped;
            }

            DrawBucketTable(pending, bucket, kind);
            return bucket.Count + hiddenSkipped;
        }

        /// <summary>
        /// 排序：能算出「可以省多少」的排前面（差額大的優先），其餘照道具編號。
        /// 差額不知道時排在最後——那些是「還缺資料」而不是「沒事」。
        /// </summary>
        private static int CompareBucketRows(PendingActionRow a, PendingActionRow b)
        {
            var da = Gap(a);
            var db = Gap(b);
            if (da != db)
                return db.CompareTo(da);
            var c = a.ItemId.CompareTo(b.ItemId);
            return c != 0 ? c : a.Slot.CompareTo(b.Slot);
        }

        /// <summary>目前價與建議價的差額；任一邊不知道時 -1（＝排最後，不是 0）。</summary>
        private static long Gap(PendingActionRow row)
            => row.CurrentPrice < 0 || row.SuggestedPrice < 0 ? -1 : row.CurrentPrice - row.SuggestedPrice;

        private static string BucketTitle(PendingActionKind kind) => kind switch
        {
            PendingActionKind.PriceCap => "Parked at the price cap",
            PendingActionKind.PriceAnomaly => "Left alone - the cheap price looks mistyped",
            PendingActionKind.BelowMinimum => "Should come off the board",
            _ => "Undercut on your home world",
        };

        private static string BucketHelp(PendingActionKind kind) => kind switch
        {
            PendingActionKind.PriceCap =>
                "Quick-listed while nobody was selling that item, so no price could be worked out and it was left at the cap on purpose. Nobody can buy these until you price them.",
            PendingActionKind.PriceAnomaly =>
                "The cheapest price found was so far below what everything else costs that it looks like somebody dropped a zero, so your price was left exactly as it was - nothing here was changed. Check it yourself: if the cheap listing is real, reprice by hand. An entry recorded when a relist actually held your price stays until that slot is repriced or delisted, or you skip it. An entry worked out from survey data is redone every time you recalculate, so it disappears once that listing is gone. The permanent record is the ANOMALY-HOLD line in /xllog.",
            PendingActionKind.BelowMinimum =>
                "Going by the current market these would end up under the minimum price you set, so relisting them would delist them instead. Decide whether to keep holding them.",
            _ =>
                "Somebody else on your home world is selling the same thing cheaper than you, going by the last survey.",
        };

        private void DrawBucketTable(PendingActionsBuilder pending, List<PendingActionRow> bucket,
            PendingActionKind kind)
        {
            // 每一列都有按鈕，所以列高是按鈕的高度。
            var rowHeight = ImGui.GetFrameHeight() + (ImGui.GetStyle().CellPadding.Y * 2);
            var size = ScrollTableSize((int)kind, bucket.Count, rowHeight,
                ImGui.GetWindowHeight() * BucketTableMaxHeightRatio);

            if (!ImGui.BeginTable(DrawText.Concat("##mbpendingtable", (int)kind), 6, ScrollTableFlags, size))
                return;

            NoteScrollTable((int)kind);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Item".Loc(), ImGuiTableColumnFlags.WidthFixed, 220);
            ImGui.TableSetupColumn("Retainer / slot".Loc(), ImGuiTableColumnFlags.WidthFixed, 150);
            ImGui.TableSetupColumn("Now".Loc(), ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Suggested".Loc(), ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Source".Loc(), ImGuiTableColumnFlags.WidthFixed, 180);
            ImGui.TableSetupColumn("Actions".Loc(), ImGuiTableColumnFlags.WidthFixed, 170);
            ImGui.TableHeadersRow();

            // Begin 把這個區域變數的位址記進 ImGui（全部插件共用）；離開迴圈的每一條路都要 End，否則留下指到失效堆疊的指標。
            var clipper = default(ImGuiListClipper);
            clipper.Begin(bucket.Count);
            try
            {
                while (clipper.Step())
                {
                    for (var index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
                    {
                        var row = bucket[index];
                        ImGui.TableNextRow();
                        ImGui.PushID(index);

                        ImGui.TableNextColumn();
                        var label = ItemLabel(row.ItemId, row.Hq);
                        if (row.Skipped)
                            Grey(label);
                        else
                            ImGui.TextUnformatted(label);
                        if (row.Skipped)
                            Tooltip("You skipped this one.".Loc());

                        ImGui.TableNextColumn();
                        DrawPlacementCell(row);

                        ImGui.TableNextColumn();
                        if (row.CurrentPrice < 0)
                        {
                            Grey("?");
                            Tooltip("The current price of this listing is not known.".Loc());
                        }
                        else
                        {
                            ImGui.TextUnformatted(DrawText.N0(row.CurrentPrice));
                        }

                        ImGui.TableNextColumn();
                        DrawSuggestedCell(row);

                        ImGui.TableNextColumn();
                        DrawSourceCell(row);

                        ImGui.TableNextColumn();
                        DrawRowActions(pending, row);

                        ImGui.PopID();
                    }
                }
            }
            finally
            {
                clipper.End();
            }

            ImGui.EndTable();
        }

        private void DrawPlacementCell(PendingActionRow row)
        {
            if (row.RetainerId == 0 || row.Slot < 0)
            {
                // 🔑 「不知道它掛在哪」本身要看得見：藏起來的話使用者會以為清單只是漏了這件。
                Grey("?");
                Tooltip(
                    "Which retainer and slot this listing sits in is not known, so the reprice button cannot be used on it. Install AllaganTools (or open the retainer's sell list once) and recalculate."
                        .Loc());
                return;
            }

            var name = row.RetainerName.Length > 0 ? row.RetainerName : "?";
            ImGui.TextUnformatted(DrawText.Loc("??  #??", (name, row.Slot + 1), static (t, a) => t.Loc(a.name, a.Item2)));
            if (row.RetainerName.Length == 0)
                Tooltip("That retainer belongs to another character, so its name cannot be read from here.".Loc());
        }

        private static void DrawSuggestedCell(PendingActionRow row)
        {
            // 🔴 「疑似打錯的低價」這一桶的 SuggestedPrice 不是「建議你掛的價」，
            //    而是「被擋下來的那個可疑價」。畫成綠色的建議價會把意思完全弄反。
            if (row.Kind == PendingActionKind.PriceAnomaly)
            {
                if (row.SuggestedPrice < 0)
                {
                    Grey("?");
                    Tooltip("The suspicious price is not known.".Loc());
                    return;
                }

                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
                ImGui.TextUnformatted(DrawText.N0(row.SuggestedPrice));
                ImGui.PopStyleColor();
                Tooltip(
                    "The price that was refused - what the cheapest listing (or the last sale) was asking. Your own price was left exactly as it was; nothing was changed."
                        .Loc());
                return;
            }

            if (row.SuggestedPrice < 0)
            {
                // 🔴 絕不畫成 0：那是一個合法但荒謬的價格。
                Grey("?");
                Tooltip(row.SuggestionSource == "survey-excluded"
                    ? "The only price known for this item is on a world you excluded, so nothing is suggested. The source column says which world."
                        .Loc()
                    : "No usable reference price was found for this item.".Loc());
                return;
            }

            var reference = !row.SuggestionIsLocal;
            ImGui.PushStyleColor(ImGuiCol.Text,
                reference ? ImGuiColors.DalamudGrey : ImGuiColors.HealerGreen);
            ImGui.TextUnformatted(DrawText.N0(row.SuggestedPrice));
            ImGui.PopStyleColor();
            Tooltip(reference
                ? "Worked out from another world's listings, so treat it as a hint only - that is a different market."
                    .Loc()
                : "Your undercut settings applied to the cheapest listing that is not yours. Pressing the reprice button asks the server again and lets the relist engine decide the real price."
                    .Loc());
        }

        private static void DrawSourceCell(PendingActionRow row)
        {
            if (row.SuggestionSource.Length == 0)
            {
                Grey("?");
                Tooltip("No reference price: neither the live cache nor the survey log knows this item.".Loc());
                return;
            }

            var world = row.SuggestionWorld.Length > 0 ? row.SuggestionWorld : "?";
            var age = row.SuggestionAtUtc == DateTime.MinValue ? "?" : FormatAge(row.SuggestionAtUtc);

            switch (row.SuggestionSource)
            {
                case "live":
                    ImGui.TextUnformatted(DrawText.Loc("live  ??  ??", world, age));
                    Tooltip("From the market data this plugin already had in memory for your own world.".Loc());
                    return;
                case "survey":
                    ImGui.TextUnformatted(DrawText.Loc("survey  ??  ??", world, age));
                    Tooltip("From the cross-world survey log, the row for your own world.".Loc());
                    return;
                case PriceSourceTag.Sale:
                    // 🔑 這一格的年齡就是「那筆成交是多久以前的事」——定價規則只採
                    //    新鮮度窗內的成交，所以它是判讀「為什麼這一件走成交價」的關鍵欄位。
                    ImGui.TextUnformatted(DrawText.Loc("last sale  ??  ??", world, age));
                    Tooltip(
                        "What this item actually sold for on your data centre, rounded down to 100 gil. The relist engine uses whichever is lower - this, or the cheapest listing on your own world."
                            .Loc());
                    return;
                case "anomaly-peer":
                    // 🔑 「拿什麼當正常價」必須在列上看得見：那是整個判定的全部依據。
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
                    ImGui.TextUnformatted(DrawText.Loc("looks mistyped  normal ??  ??", world, age));
                    ImGui.PopStyleColor();
                    if (TooltipHovered())
                        Tooltip(
                            "The next seller along is asking ?? gil, so the cheapest listing looks like somebody dropped a zero. This test compares one seller against another, so a market-wide crash never lands here - only a single odd listing does."
                                .Loc(world));
                    return;
                case "anomaly-own":
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
                    ImGui.TextUnformatted(DrawText.Loc("looks mistyped  vs your ??  ??", world, age));
                    ImGui.PopStyleColor();
                    if (TooltipHovered())
                        Tooltip(
                            "Nobody else was selling, so there was no second seller to compare against and your own ?? gil had to be the yardstick. This is the weaker of the two tests: it cannot tell \"somebody mistyped\" apart from \"you are asking too much\"."
                                .Loc(world));
                    return;
                case "survey-excluded":
                    // 🔑 「知道但故意不用」要看得見，而且要看得見是哪一個世界——
                    //    否則它跟「真的沒資料」在畫面上是同一個灰色問號。
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
                    ImGui.TextUnformatted(DrawText.Loc("excluded  ??  ??", world, age));
                    ImGui.PopStyleColor();
                    if (TooltipHovered())
                        Tooltip(
                            "The only price known for this item is on ??, which is on your excluded list, so it was not used. Untick that world and recalculate to use it again."
                                .Loc(world));
                    return;
                default:
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
                    ImGui.TextUnformatted(DrawText.Loc("other world  ??  ??", world, age));
                    ImGui.PopStyleColor();
                    Tooltip(
                        "Your own world has no data for this item, so this is the cheapest listing found on another world. Buyers cannot reach it from here - use it as a hint, not as a price."
                            .Loc());
                    return;
            }
        }

        /// <summary>
        /// 一列上的兩顆按鈕。
        /// 🔴 「重新定價」<b>不會自己算一個價格寫進去</b>：它把那一格原封不動交給既有的
        /// <c>BatchReprice.StartQuickReprice</c>，由那條路徑重新向伺服器問一次行情、
        /// 套用使用者自己的降價設定、必要時依門檻下架。這裡沒有第二套改價實作。
        /// </summary>
        private static void DrawRowActions(PendingActionsBuilder pending, PendingActionRow row)
        {
            var addressable = row.RetainerId != 0 && row.Slot >= 0;
            var thisRetainer = addressable && row.RetainerId == pending.ActiveRetainerId;
            var canReprice = thisRetainer && pending.RepriceReady;

            using (Disabled(!canReprice))
            {
                if (ImGui.Button("Reprice".Loc()))
                    pending.RequestReprice(row.ItemId, row.Hq, row.Slot);
            }

            if (!canReprice)
            {
                if (TooltipHovered())
                {
                    string why;
                    if (!addressable)
                        why = "This entry has no known retainer and slot, so it cannot be handed to the relist engine."
                            .Loc();
                    else if (!thisRetainer)
                        why = "Open this retainer's sell list first - the button only works on the retainer in front of you."
                            .Loc();
                    else
                        why = pending.RepriceBlockedReason.Length > 0
                            ? "Cannot reprice right now: ??".Loc(pending.RepriceBlockedReason)
                            : "Cannot reprice right now.".Loc();
                    Tooltip(why);
                }
            }
            else
            {
                Tooltip(
                    "Hands this one slot to the same single-item pricing the quick lister uses: it asks the server for the current listings and applies your own undercut settings. The suggested price in the table is only an estimate of where it will land."
                        .Loc());
            }

            ImGui.SameLine();
            using (Disabled(row.Skipped))
            {
                if (ImGui.Button("Skip".Loc()))
                    PendingActions.MarkSkipped(row.Key);
            }

            Tooltip(row.Skipped
                ? "Already skipped. Use \"Clear skip marks\" above to bring it back."
                    .Loc()
                : "Takes this entry off the list. Recalculating will not bring it back until you clear the skip marks."
                    .Loc());
        }

        private static string SafePendingPath()
        {
            try
            {
                return PendingActions.FilePath;
            }
            catch
            {
                return PendingActions.FileName;
            }
        }
    }
}
