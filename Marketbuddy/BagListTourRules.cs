using System.Collections.Generic;

namespace Marketbuddy
{
    // 本檔只有純資料與純函式：不讀遊戲記憶體、不碰 Dalamud，離線測試直接編這個檔（加 MarketSlots.cs、BelowThresholdMemory.cs、VendorSellPlan.cs）。

    /// <summary>背包上架巡迴走到一名僱員面前時要怎麼做。</summary>
    internal enum BagTourStep
    {
        Visit,

        /// <summary>背包裡已經沒有勾選的道具可上架：這名與之後的僱員都不必去。</summary>
        BagEmpty,

        /// <summary>依本插件看過的出售清單，這名僱員確知已滿。</summary>
        SkipFull,
    }

    internal static class BagListTourRules
    {
        /// <summary>背包先判（清空就整趟收工），再判這名僱員；不知道格數的照樣去。</summary>
        internal static BagTourStep Decide(int remainingStacks, int? listed, int capacity)
        {
            if (remainingStacks <= 0)
                return BagTourStep.BagEmpty;
            return MarketSlots.KnownFull(listed, capacity) ? BagTourStep.SkipFull : BagTourStep.Visit;
        }

        /// <summary>
        /// 在前一名僱員那裡已經有結論、不必在下一名再試的結果：低於門檻（加進賣給商店清單或留著）與跳過。
        /// 上架成功與「還沒輪到」（市場滿了停下）的留著，下一名接著上。
        /// </summary>
        internal static bool DropAfter(BatchListResultKind kind)
            => kind is BatchListResultKind.ShopList or BatchListResultKind.KeptBelow or BatchListResultKind.Skipped;
    }

    /// <summary>
    /// 一趟背包上架巡迴的彙整：每名僱員的批次結果合進來。
    /// 「還沒輪到」只保留最後一批的（前面僱員滿了沒上的，後面的僱員會再上），其餘依（道具、結果）相加。
    /// </summary>
    internal sealed class BagTourLedger
    {
        private readonly List<(VendorSellKey Key, BatchListResultKind Kind)> order = new();
        private readonly Dictionary<(VendorSellKey, BatchListResultKind), BatchListSummaryRow> merged = new();
        private readonly HashSet<VendorSellKey> dropped = new();
        private List<BatchListSummaryRow> pending = new();

        /// <summary>之後的僱員不再上架的道具。</summary>
        internal IReadOnlySet<VendorSellKey> Dropped => dropped;

        internal void AddRun(IReadOnlyList<BatchListSummaryRow> run)
        {
            pending = new List<BatchListSummaryRow>();
            foreach (var row in run)
            {
                if (row.Kind == BatchListResultKind.NotProcessed)
                {
                    pending.Add(row);
                    continue;
                }

                if (BagListTourRules.DropAfter(row.Kind))
                    dropped.Add(row.Key);
                var id = (row.Key, row.Kind);
                if (merged.TryGetValue(id, out var have))
                {
                    merged[id] = have with
                    {
                        Stacks = have.Stacks + row.Stacks,
                        Quantity = have.Quantity + row.Quantity,
                        Price = row.Price > 0 ? row.Price : have.Price,
                        FromMemory = have.FromMemory || row.FromMemory,
                        Note = have.Note.Length > 0 ? have.Note : row.Note,
                    };
                    continue;
                }

                order.Add(id);
                merged[id] = row;
            }
        }

        /// <summary>彙整表：已處理在前、還要處理在後（同 <see cref="BatchListSummary.Summarize"/> 的排法）。</summary>
        internal List<BatchListSummaryRow> Rows()
        {
            var rows = new List<BatchListSummaryRow>(order.Count + pending.Count);
            foreach (var id in order)
            {
                if (BatchListSummary.IsHandled(id.Kind))
                    rows.Add(merged[id]);
            }

            foreach (var id in order)
            {
                if (!BatchListSummary.IsHandled(id.Kind))
                    rows.Add(merged[id]);
            }

            rows.AddRange(pending);
            return rows;
        }
    }
}
