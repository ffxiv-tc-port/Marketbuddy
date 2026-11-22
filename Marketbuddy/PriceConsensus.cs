using System;

namespace Marketbuddy
{
    /// <summary>一次共識價計算的結果。</summary>
    /// <param name="Price">共識價；<b>-1＝算不出來</b>（筆數不夠），絕不是 0。</param>
    /// <param name="Used">排除特殊交易之後真的拿來算的筆數。</param>
    /// <param name="Considered">窗內取進來的筆數（排除之前）。</param>
    /// <param name="Dropped">被當成特殊交易丟掉的成交單價（只給 log）。</param>
    internal readonly record struct ConsensusResult(long Price, int Used, int Considered, long[] Dropped)
    {
        internal static readonly ConsensusResult None = new(-1, 0, 0, []);

        public bool IsKnown => Price > 0;

        /// <summary>給 log 的丟棄清單；沒有就是 <c>-</c>。</summary>
        public string DroppedText => Dropped is not { Length: > 0 } ? "-" : string.Join("/", Dropped);
    }

    /// <summary>
    /// 「最新多筆成交的共識價」與「別的世界的一般掛單」兩種參照價的計算。
    /// 🔴 純函式：不讀設定、不碰遊戲、不做 I/O，離線測試直接編這個檔。
    /// <para>🔑 共識價＝先取中位數，丟掉和它差到倍數（含）以上的特殊交易（轉錢、打錯字），
    /// 剩下的再取一次中位數；剩不到 <see cref="MinEntries"/> 筆就算不出來。</para>
    /// </summary>
    internal static class PriceConsensus
    {
        /// <summary>排除特殊交易之後至少要剩這麼多筆，共識價才算數。</summary>
        internal const int MinEntries = 3;

        /// <summary>每個品質最多存、最多取這麼多筆。</summary>
        internal const int MaxEntries = 50;

        internal const int MinWindowDays = 1;
        internal const int MaxWindowDays = 90;

        /// <summary>倍數的合理範圍與 <see cref="PriceAnomalyGuard"/> 相同；超出範圍＝不排除任何一筆。</summary>
        internal static bool ValidRatio(int ratio) => ratio >= 2 && ratio <= 1000;

        /// <param name="newestFirst">已經篩過窗與世界的成交單價，<b>由新到舊</b>。</param>
        /// <param name="entries">取最新幾筆（夾在 <see cref="MinEntries"/>..<see cref="MaxEntries"/>）。</param>
        /// <param name="ratio">差幾倍（含）以上算特殊交易。</param>
        internal static ConsensusResult Compute(ReadOnlySpan<long> newestFirst, int entries, int ratio)
        {
            var want = Math.Clamp(entries, MinEntries, MaxEntries);
            Span<long> taken = stackalloc long[MaxEntries];
            var n = 0;
            foreach (var price in newestFirst)
            {
                if (n >= want)
                    break;
                if (price > 0)
                    taken[n++] = price;
            }

            if (n < MinEntries)
                return ConsensusResult.None with { Considered = n };

            var sorted = taken[..n];
            sorted.Sort();
            var first = Median(sorted);

            Span<long> kept = stackalloc long[MaxEntries];
            var keptCount = 0;
            var droppedCount = 0;
            Span<long> dropped = stackalloc long[MaxEntries];
            var k = (long)ratio;
            foreach (var price in sorted)
            {
                if (ValidRatio(ratio) && (price * k <= first || price >= first * k))
                    dropped[droppedCount++] = price;
                else
                    kept[keptCount++] = price;
            }

            var droppedArray = droppedCount == 0 ? [] : dropped[..droppedCount].ToArray();
            if (keptCount < MinEntries)
                return new ConsensusResult(-1, keptCount, n, droppedArray);

            return new ConsensusResult(Median(kept[..keptCount]), keptCount, n, droppedArray);
        }

        /// <summary>已排序序列的中位數（偶數筆取中間兩筆的平均、無條件捨去）；空序列 -1。</summary>
        internal static long Median(ReadOnlySpan<long> sorted)
        {
            if (sorted.Length == 0)
                return -1;
            var mid = sorted.Length / 2;
            return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
        }

        /// <summary>
        /// 一群別的世界的掛單裡「最便宜的一般掛單」：丟掉比參照價便宜到倍數（含）以上的特殊低價，取剩下最便宜的。
        /// 參照價＝下中位數（偶數筆取中間偏低那一筆）。
        /// </summary>
        /// <remarks>
        /// 偏高的特殊掛單不必丟：取的是最便宜那一筆，只要有任何一筆一般掛單它就不會被選中。
        /// 用下中位數而不是平均：兩筆一高一低時平均會被天價拉走，把唯一的正常價判成特殊低價。
        /// </remarks>
        /// <param name="pool">會被就地排序。</param>
        /// <param name="reference">參照價；沒有掛單時 -1。</param>
        /// <returns>最便宜的一般掛單；沒有時 -1。</returns>
        internal static long CheapestOrdinary(Span<long> pool, int ratio, out long reference)
        {
            reference = -1;
            if (pool.Length == 0)
                return -1;
            pool.Sort();
            reference = pool[(pool.Length - 1) / 2];
            if (!ValidRatio(ratio))
                return pool[0];

            var k = (long)ratio;
            foreach (var price in pool)
            {
                if (price > 0 && price * k > reference)
                    return price;
            }

            return -1;
        }

        /// <summary>
        /// 同業基準（下一位賣家）是不是特殊掛單；是的話回 -1（＝當成沒有下一位賣家），否則原樣回傳。
        /// </summary>
        /// <remarks>
        /// 兩個條件都要成立才丟：下一位賣家比參照價貴到倍數（含）以上，<b>而且</b>最低價本身沒有比參照價便宜到倍數以上。
        /// 後者不成立時兩個價都偏離參照價，分不出誰才是特殊的，照舊讓同業判定去擋。
        /// </remarks>
        /// <param name="lowest">被判的那個參考價（板上最低價或成交價）。</param>
        /// <param name="peer">下一位賣家的最低價；&lt;=0＝沒有。</param>
        /// <param name="reference">第三方參照價（共識價，或別的世界一般掛單的參照價）；&lt;=0＝沒有。</param>
        internal static long SanitizePeer(long lowest, long peer, long reference, int ratio)
        {
            if (lowest <= 0 || peer <= 0 || reference <= 0 || !ValidRatio(ratio))
                return peer;
            var k = (long)ratio;
            return peer >= reference * k && lowest * k > reference ? -1 : peer;
        }
    }
}
