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
        //  銷售
        // =====================================================================

        private void DrawSalesTab()
        {
            PumpSalesLoad();
            // 巡迴時記錄會一列一列連續寫進來：自動重讀至少隔幾秒（同出售清單那邊的彙總），最後一次變動仍會讀到。
            if (salesTask == null && salesRevisionLoaded != RetainerSalesLog.Revision &&
                (salesRevisionLoaded < 0 || DateTime.UtcNow - salesRequestedAt >= SalesAutoReloadThrottle))
                RequestSalesLoad();

            ImGui.Spacing();

            if (ImGui.Checkbox("Record what disappears from your retainers' listings".Loc(),
                    ref conf.RetainerSalesLogEnabled))
                conf.Save();
            Tooltip(
                "On (default). While a retainer's sell list is open this reads that retainer's market container and gil, and writes down anything that vanished since the last time it was seen. Read-only: no packets, no hooks, nothing is ever listed, repriced or delisted because of it."
                    .Loc());

            ImGui.SameLine();
            using (Disabled(salesTask != null))
            {
                if (ImGui.Button("Reload".Loc()))
                    RequestSalesLoad();
            }

            // 🔑 一件一列的彙總才回答得了「這件到底掛不掛得掉」；時間序回答的是「剛剛發生了什麼」。
            ImGui.Checkbox("One row per item instead of a timeline".Loc(), ref salesByItem);
            Tooltip(
                "Adds up every entry for the same item so you can see whether it has ever actually sold, instead of listing the events in the order they happened."
                    .Loc());

            if (!salesByItem)
            {
                ImGui.Checkbox("Also show what Marketbuddy delisted itself".Loc(), ref showOwnDelists);
                Tooltip("Those are not sales - they are in the file so the numbers add up.".Loc());
            }

            ImGui.Spacing();
            DrawSalesSummary();

            ImGui.Spacing();
            Grey(DrawText.Loc("Sales file: ??", RetainerSalesLog.FileName));
            Tooltip(SafeSalesPath());
            Grey(DrawText.Loc("Baseline file: ?? (?? retainer(s) known)",
                (RetainerMarketState.FileName, RetainerMarketState.Count), static (t, a) => t.Loc(a.FileName, a.Count)));
            Tooltip(
                "A retainer only starts producing entries after its sell list has been open once - that first visit is what establishes the baseline to compare against."
                    .Loc());

            // 🔑 「有沒有真的在取樣」必須在列上看得見：取不到樣的話這個功能是完全靜默的，
            //    而「一件都沒賣掉」與「根本沒看過你的僱員」長得一模一樣。
            var watcher = gui.SalesWatcher;
            if (watcher != null)
            {
                if (watcher.LastSnapshotAt == DateTime.MinValue)
                    Grey("No snapshot taken in this session yet.".Loc());
                else
                    Grey(DrawText.Loc("Last snapshot ?? (?? this session)",
                        (FormatAge(watcher.LastSnapshotAt), watcher.SnapshotsTaken),
                        static (t, a) => t.Loc(a.Item1, a.SnapshotsTaken)));

                if (watcher.LastSkipReason.Length > 0)
                    GreyWrapped(DrawText.Loc("Last sample skipped: ??", watcher.LastSkipReason));
            }

            ImGui.Spacing();
            ImGui.Separator();

            if (salesTask != null && sales.Count == 0)
            {
                ImGui.Spacing();
                Grey("Reading the sales file...".Loc());
                return;
            }

            if (salesByItem)
                DrawSalesByItemTable();
            else
                DrawSalesTable();
        }

        /// <summary>
        /// 摘要。🔴 只加總<b>高信心</b>的列，而且低信心的件數一定要放在旁邊看得見——
        /// 少了那半句，一個偏低的數字看起來就像一個準確的數字。
        /// </summary>
        private void DrawSalesSummary()
        {
            var since = DateTime.UtcNow.AddDays(-SalesSummaryDays);
            var soldItems = 0;
            var soldEvents = 0;
            long received = 0;
            var unknownItems = 0;
            var receivedUnknown = false;

            foreach (var row in sales)
            {
                if (row.AtUtc < since)
                    continue;
                switch (row.Confidence)
                {
                    case RetainerSaleConfidence.Sold:
                        soldItems += row.Quantity;
                        soldEvents++;
                        if (row.Received >= 0)
                            received += row.Received;
                        else
                            receivedUnknown = true;
                        break;
                    case RetainerSaleConfidence.Unknown:
                        unknownItems += row.Quantity;
                        break;
                }
            }

            if (soldEvents == 0 && unknownItems == 0)
            {
                Grey(DrawText.Loc("Nothing recorded in the last ?? days.", SalesSummaryDays));
                return;
            }

            ImGui.TextWrapped(DrawText.Loc("Last ?? days: ?? item(s) sold with high confidence, ?? gil received",
                (soldItems, received), static (t, a) => t.Loc(SalesSummaryDays, a.soldItems, a.received.ToString("N0"))));
            if (receivedUnknown)
                GreyWrapped("Some of those sales have no amount, so the total is a lower bound.".Loc());
            if (unknownItems > 0)
                GreyWrapped(DrawText.Loc("?? more item(s) only known to have disappeared - not counted.", unknownItems));
        }

        private void DrawSalesTable()
        {
            var shown = 0;
            var hiddenDelists = 0;
            var visible = salesVisible;
            visible.Clear();
            foreach (var row in sales)
            {
                if (row.Confidence == RetainerSaleConfidence.Delisted && !showOwnDelists)
                {
                    hiddenDelists++;
                    continue;
                }

                if (visible.Count >= MaxSalesRows)
                {
                    shown++;
                    continue;
                }

                visible.Add(row);
                shown++;
            }

            if (hiddenDelists > 0)
                GreyWrapped(DrawText.Loc("?? entry(ies) hidden because Marketbuddy delisted them itself.", hiddenDelists));

            if (visible.Count == 0)
            {
                ImGui.Spacing();
                ImGui.TextWrapped(
                    "Nothing recorded yet. Open a retainer's sell list once to establish the baseline; anything that has gone from it the next time you look shows up here."
                        .Loc());
                return;
            }

            if (shown > visible.Count)
                GreyWrapped(DrawText.Loc("Showing the newest ?? of ?? entries; the file has them all.",
                    (visible.Count, shown), static (t, a) => t.Loc(a.Count, a.shown)));

            if (!ImGui.BeginTable("##mbsalestable", 7, ScrollTableFlags,
                    ScrollTableSize(SalesTableSlot, visible.Count, SalesRowHeight(), ImGui.GetContentRegionAvail().Y)))
                return;

            NoteScrollTable(SalesTableSlot);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("When".Loc(), ImGuiTableColumnFlags.WidthFixed, 120);
            ImGui.TableSetupColumn("Retainer".Loc(), ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableSetupColumn("Item".Loc(), ImGuiTableColumnFlags.WidthFixed, 220);
            ImGui.TableSetupColumn("Qty".Loc(), ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableSetupColumn("Unit price".Loc(), ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Received".Loc(), ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Confidence".Loc(), ImGuiTableColumnFlags.WidthFixed, 170);
            ImGui.TableHeadersRow();

            // Begin 把這個區域變數的位址記進 ImGui（全部插件共用）；離開迴圈的每一條路都要 End，否則留下指到失效堆疊的指標。
            var clipper = default(ImGuiListClipper);
            clipper.Begin(visible.Count);
            try
            {
                while (clipper.Step())
                {
                    for (var index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
                    {
                        var row = visible[index];
                        ImGui.TableNextRow();
                        ImGui.PushID(index);

                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(DrawText.LocalMinute(row.AtUtc, "MM-dd HH:mm"));
                        if (TooltipHovered())
                            Tooltip(row.PrevAtUtc == DateTime.MinValue
                                ? "?? (the previous look is not recorded)".Loc(FormatAge(row.AtUtc))
                                : "Between ?? and ?? (local time)".Loc(
                                    row.PrevAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                                    row.AtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")));

                        ImGui.TableNextColumn();
                        if (row.RetainerName.Length > 0)
                        {
                            ImGui.TextUnformatted(row.RetainerName);
                        }
                        else
                        {
                            Grey("?");
                            Tooltip("That retainer's name was not readable when this was recorded.".Loc());
                        }

                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(ItemLabel(row.ItemId, row.Hq));

                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(DrawText.N0(row.Quantity));

                        ImGui.TableNextColumn();
                        if (row.UnitPrice < 0)
                        {
                            // 🔑 「不知道」要在列上看得見。畫成 0 會被讀成「有人用 0 gil 買走」。
                            Grey("?");
                            Tooltip(
                                "You had that item listed at more than one price and the missing pieces span both, so which price they went at cannot be worked out."
                                    .Loc());
                        }
                        else
                        {
                            ImGui.TextUnformatted(DrawText.N0(row.UnitPrice));
                        }

                        ImGui.TableNextColumn();
                        if (row.Received < 0)
                        {
                            Grey("?");
                            Tooltip("No amount could be tied to this entry.".Loc());
                        }
                        else
                        {
                            ImGui.TextUnformatted(DrawText.N0(row.Received));
                        }

                        ImGui.TableNextColumn();
                        DrawConfidenceCell(row);

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

        private static void DrawConfidenceCell(RetainerSaleRow row)
        {
            switch (row.Confidence)
            {
                case RetainerSaleConfidence.Sold:
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.HealerGreen);
                    ImGui.TextUnformatted("Sold".Loc());
                    ImGui.PopStyleColor();
                    if (TooltipHovered())
                        Tooltip(GilMatchExplanation(row));
                    return;
                case RetainerSaleConfidence.Delisted:
                    Grey("Delisted by Marketbuddy".Loc());
                    Tooltip("Marketbuddy took this off the board itself, so it is not a sale.".Loc());
                    return;
                default:
                    Grey("Gone - reason unknown".Loc());
                    Tooltip(
                        "It is no longer listed, but the retainer's gil did not move by the matching amount, so this may just as well have been delisted by hand. Deliberately not counted as income."
                            .Loc());
                    return;
            }
        }

        /// <summary>
        /// 這一列為什麼算高信心。<c>gross</c> 出現在這裡是有意義的資訊：
        /// 它代表僱員收到的是<b>未扣稅</b>的金額，也就是市場稅不是由賣方負擔。
        /// </summary>
        private static string GilMatchExplanation(RetainerSaleRow row)
        {
            var delta = row.HasGilDelta ? row.GilDelta.ToString("N0") : "?";
            return row.Basis == "gross"
                ? "The retainer's gil went up by ?? which matches the listing price before tax."
                    .Loc(delta)
                : "The retainer's gil went up by ?? which matches the listing price minus ??% market tax."
                    .Loc(delta, row.TaxPercent);
        }

        private void RequestSalesLoad()
        {
            if (salesTask != null)
                return;
            salesRevisionLoaded = RetainerSalesLog.Revision;
            salesRequestedAt = DateTime.UtcNow;
            // 🔴 讀檔一律丟到執行緒池。
            salesTask = Task.Run(RetainerSalesLog.LoadAll);
        }

        private void PumpSalesLoad()
        {
            var task = salesTask;
            if (task == null || !task.IsCompleted)
                return;

            salesTask = null;
            List<RetainerSaleRow> rows;
            try
            {
                rows = task.GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                Log.Information(e, "[Marketbuddy] 僱員銷售：讀取記錄檔時發生例外。");
                return;
            }

            // 追加是非同步的，剛記下的列可能還沒落地 —— 補上這個工作階段寫過的列。
            rows.AddRange(RetainerSalesLog.SessionRowsMissingFrom(rows));

            rows.Sort((a, b) => b.AtUtc.CompareTo(a.AtUtc));

            sales.Clear();
            sales.AddRange(rows);
            salesStamp++;
        }

        /// <summary>
        /// 一件一列的彙總表：這件道具賣掉過幾次、沒賣掉就下架過幾次。
        /// 🔴 「賣出」只算高信心（<see cref="RetainerSaleConfidence.Sold"/>）。信心不足的那些
        /// 有自己的一欄，永遠不併進賣出——把它們加進去等於用一個自信的數字蓋掉「我們其實不知道」。
        /// 🔴 表格上方一定要寫出<b>紀錄從什麼時候開始</b>：一張看起來很空的表，可能是
        /// 「東西都賣不掉」，也可能是「我們才看了兩天」——那兩件事會導出相反的決定。
        /// 📌 純顯示：這張表沒有任何會改價或下架的按鈕。
        /// </summary>
        private void DrawSalesByItemTable()
        {
            EnsureSalesAggregate();
            var snapshot = salesAggregate;

            // 觀察起點。首選是「第一次看到某位僱員的清單」那個時間（掛售年齡紀錄），
            // 那份還沒讀回來時退而用「紀錄裡最早的一筆事件」——兩者都是下界，都誠實。
            // 🔴 兩個都拿不到時<b>不可以</b>省略這一行：一張空表若不寫紀錄從何時開始，
            //    「都賣不掉」與「才剛開始看」看起來一模一樣。
            var since = RetainerListingAge.EarliestSeen();
            if (since == null && snapshot.FirstEventUtc != DateTime.MinValue)
                since = snapshot.FirstEventUtc;

            if (since is { } start)
            {
                GreyWrapped(DrawText.Loc("Records kept since ?? - only high-confidence sales count as sold.",
                    FormatCoverage(start)));
                Tooltip(
                    "Nothing that happened before Marketbuddy first had one of your retainers' sell lists open is in these numbers."
                        .Loc());
            }
            else
            {
                GreyWrapped("No retainer has been looked at yet, so there is no basis for any of these numbers.".Loc());
            }

            if (snapshot.ByItemAndQuality.Count == 0)
            {
                ImGui.Spacing();
                ImGui.TextWrapped(
                    "Nothing recorded yet. Open a retainer's sell list once to establish the baseline; anything that has gone from it the next time you look shows up here."
                        .Loc());
                return;
            }

            ImGui.Spacing();
            ImGui.SetNextItemWidth(300);
            if (ImGui.BeginCombo("##mbsalesitemsort", SalesItemSortKeys[salesItemSortMode].Loc()))
            {
                for (var i = 0; i < SalesItemSortKeys.Length; i++)
                {
                    if (ImGui.Selectable(SalesItemSortKeys[i].Loc(), salesItemSortMode == i))
                        salesItemSortMode = i;
                }

                ImGui.EndCombo();
            }

            // 彙總（整份換掉、不就地改）與排序方式都沒變時，沿用上一次排好的那一份。
            var list = salesItemRows;
            if (!ReferenceEquals(salesItemRowsFor, snapshot) || salesItemRowsSort != salesItemSortMode)
            {
                list.Clear();
                foreach (var (key, value) in snapshot.ByItemAndQuality)
                    list.Add((key, value));

                list.Sort((a, b) => salesItemSortMode switch
                {
                    // 預設（0）：沒賣出過的排前面，同組再比「沒賣掉就下架」的次數；1＝賣出多的排前面；2＝最近有異動的排前面。
                    1 => b.History.SoldEvents != a.History.SoldEvents
                        ? b.History.SoldEvents.CompareTo(a.History.SoldEvents)
                        : b.History.SoldQuantity.CompareTo(a.History.SoldQuantity),
                    2 => b.History.LastEventUtc.CompareTo(a.History.LastEventUtc),
                    _ => (a.History.SoldEvents == 0) != (b.History.SoldEvents == 0)
                        ? (a.History.SoldEvents == 0 ? -1 : 1)
                        : b.History.OffBoardEvents.CompareTo(a.History.OffBoardEvents),
                });

                salesItemRowsFor = snapshot;
                salesItemRowsSort = salesItemSortMode;
            }

            if (list.Count > MaxSalesRows)
                GreyWrapped(DrawText.Loc("Showing ?? of ?? items; sorting picks which ones, the file has them all.",
                    list.Count, static (t, count) => t.Loc(MaxSalesRows, count)));

            var count = Math.Min(list.Count, MaxSalesRows);
            if (!ImGui.BeginTable("##mbsalesitemtable", 6, ScrollTableFlags,
                    ScrollTableSize(SalesItemTableSlot, count, SalesRowHeight(), ImGui.GetContentRegionAvail().Y)))
                return;

            NoteScrollTable(SalesItemTableSlot);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Item".Loc(), ImGuiTableColumnFlags.WidthFixed, 240);
            ImGui.TableSetupColumn("Sold".Loc(), ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("Received".Loc(), ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("Delisted".Loc(), ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("Gone - unknown".Loc(), ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("Last sold".Loc(), ImGuiTableColumnFlags.WidthFixed, 120);
            ImGui.TableHeadersRow();

            // Begin 把這個區域變數的位址記進 ImGui（全部插件共用）；離開迴圈的每一條路都要 End，否則留下指到失效堆疊的指標。
            var clipper = default(ImGuiListClipper);
            clipper.Begin(count);
            try
            {
                while (clipper.Step())
                {
                    for (var index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
                    {
                        var (key, history) = list[index];
                        ImGui.TableNextRow();
                        ImGui.PushID(index);

                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(ItemLabel(key.ItemId, key.Hq));

                        ImGui.TableNextColumn();
                        if (history.SoldEvents > 0)
                        {
                            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.HealerGreen);
                            ImGui.TextUnformatted(DrawText.N0(history.SoldEvents));
                            ImGui.PopStyleColor();
                            if (TooltipHovered())
                                Tooltip("?? item(s) in total.".Loc(history.SoldQuantity));
                        }
                        else
                        {
                            Grey("0");
                            Tooltip("Never seen to sell in the records kept so far.".Loc());
                        }

                        ImGui.TableNextColumn();
                        if (history.SoldEvents == 0)
                        {
                            // 🔑 沒賣出過就沒有「實收」這回事——畫 0 會被讀成「賣掉了但一毛沒拿到」。
                            Grey("—");
                        }
                        else
                        {
                            ImGui.TextUnformatted(DrawText.N0(history.SoldGil));
                            if (history.SoldGilIncomplete)
                                Tooltip("Some of those sales have no amount, so the total is a lower bound.".Loc());
                        }

                        ImGui.TableNextColumn();
                        Grey(DrawText.N0(history.DelistedEvents));
                        Tooltip("Marketbuddy took this off the board itself, so it is not a sale.".Loc());

                        ImGui.TableNextColumn();
                        Grey(DrawText.N0(history.UnknownEvents));
                        Tooltip(
                            "It is no longer listed, but the retainer's gil did not move by the matching amount, so this may just as well have been delisted by hand. Deliberately not counted as income."
                                .Loc());

                        ImGui.TableNextColumn();
                        if (history.LastSoldUtc == DateTime.MinValue)
                            Grey("—");
                        else
                            ImGui.TextUnformatted(DrawText.LocalMinute(history.LastSoldUtc, "MM-dd HH:mm"));

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

        /// <summary>只有 <see cref="sales"/> 換過內容才重算彙總。純計算、零 I/O。</summary>
        private void EnsureSalesAggregate()
        {
            if (salesAggregateStamp == salesStamp)
                return;
            salesAggregateStamp = salesStamp;
            salesAggregate = SalesHistorySnapshot.Build(sales);
        }

        /// <summary>觀察期：從哪一天起算、到現在多久。</summary>
        private static string FormatCoverage(DateTime sinceUtc)
        {
            var span = DateTime.UtcNow - sinceUtc;
            if (span < TimeSpan.Zero)
                span = TimeSpan.Zero;

            var length = span.TotalDays >= 1
                ? DrawText.Loc("?? day(s)", (int)span.TotalDays)
                : span.TotalHours >= 1
                    ? DrawText.Loc("?? hour(s)", (int)span.TotalHours)
                    : "less than an hour".Loc();

            return DrawText.Loc("?? (??)", DrawText.LocalMinute(sinceUtc, "yyyy-MM-dd"), length);
        }

        private static string SafeSalesPath()
        {
            try
            {
                return RetainerSalesLog.FilePath;
            }
            catch
            {
                return RetainerSalesLog.FileName;
            }
        }
    }
}
