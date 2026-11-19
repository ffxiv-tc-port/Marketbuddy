using System;
using System.Collections.Generic;

namespace Marketbuddy
{
    /// <summary>
    /// 自家僱員掛過的價（同道具、同品質、同單價）的不可變索引，給「最近成交價是不是自家掛的價」用。
    /// 🔴 純資料：不碰遊戲、不做 I/O、不讀設定；建好就不再改，可以跨執行緒直接讀。
    /// </summary>
    /// <remarks>
    /// 兩種來源：每位僱員最後一次看到的掛單（<b>不看多久以前</b>：基準線只在內容變了才換，
    /// 時間戳是「最後一次變動」，不是「最後一次看到」），以及僱員銷售紀錄裡近
    /// <see cref="WindowDays"/> 天從架上消失的掛單（賣掉、被下架、原因不明都算，都是自家掛過的價）。
    /// </remarks>
    internal sealed class OwnPriceIndex
    {
        internal const int WindowDays = 7;

        internal static readonly OwnPriceIndex Empty = new([]);

        private readonly HashSet<(uint ItemId, bool Hq, long UnitPrice)> prices;

        private OwnPriceIndex(HashSet<(uint, bool, long)> prices) => this.prices = prices;

        /// <summary>記得幾個不同的 (道具, 品質, 單價)。</summary>
        internal int Count => prices.Count;

        /// <param name="shelf">每位僱員基準線裡的掛單。</param>
        /// <param name="gone">僱員銷售紀錄的列：發現消失的時間與當時的單價。</param>
        internal static OwnPriceIndex Build(
            IEnumerable<(uint ItemId, bool Hq, long UnitPrice)> shelf,
            IEnumerable<(uint ItemId, bool Hq, long UnitPrice, DateTime AtUtc)> gone,
            DateTime nowUtc)
        {
            var set = new HashSet<(uint, bool, long)>();
            foreach (var (itemId, hq, price) in shelf)
            {
                if (itemId != 0 && price > 0)
                    set.Add((itemId, hq, price));
            }

            var cutoff = nowUtc.AddDays(-WindowDays);
            foreach (var (itemId, hq, price, atUtc) in gone)
            {
                if (itemId != 0 && price > 0 && atUtc >= cutoff)
                    set.Add((itemId, hq, price));
            }

            return new OwnPriceIndex(set);
        }

        /// <summary>這個價是自家僱員掛過的嗎。比的是原始單價，不捨去。</summary>
        internal bool Contains(uint itemId, bool hq, long unitPrice)
            => unitPrice > 0 && prices.Contains((itemId, hq, unitPrice));
    }
}
