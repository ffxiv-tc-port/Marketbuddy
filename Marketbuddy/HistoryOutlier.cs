using System;

namespace Marketbuddy
{
    /// <summary>一個參考價和近期成交中位數比起來是什麼狀況。</summary>
    /// <remarks>⚠️ <see cref="NoData"/> 刻意是 0：沒有零值的列舉會讓 <c>default</c> 落在無效值上。</remarks>
    internal enum HistoryVerdict
    {
        /// <summary>沒有可用的中位數（資料太少、低於下限、設定無效）⇒ 呼叫端照舊判定。</summary>
        NoData = 0,

        /// <summary>在中位數的倍數範圍內，或雖然偏離但有獨立的現況觀測佐證。</summary>
        Normal = 1,

        /// <summary>比中位數低太多倍，而且沒有佐證。</summary>
        Low = 2,

        /// <summary>比中位數高太多倍，而且沒有佐證。</summary>
        High = 3,
    }

    /// <summary>
    /// 「和近期成交共識價差太多倍」的判定（轉錢交易、打錯字的掛單與成交）；共識價怎麼算見 <see cref="PriceConsensus"/>。
    /// 🔴 純函式：不讀設定、不碰遊戲、不做 I/O，離線測試直接編這個檔。
    /// <para>🔑 偏離本身不夠：整個市場一起漲跌時中位數會落後好幾天。所以偏離的那一筆要
    /// <b>沒有任何獨立的現況觀測站在它那邊</b>才算離群——偏低時有別的觀測也一樣低（&lt; 參考價×倍數），
    /// 偏高時有別的觀測也一樣高（×倍數 &gt; 參考價），就當成行情真的變了。</para>
    /// </summary>
    internal static class HistoryOutlier
    {
        internal const int MinRatio = 2;
        internal const int MaxRatio = 1000;

        /// <param name="reference">要判的參考價；&lt;=0＝沒有。</param>
        /// <param name="median">近期成交共識價；&lt;=0＝沒有。</param>
        /// <param name="ratio">差幾倍（含）以上算離群。</param>
        /// <param name="minMedian">中位數低於它就不判（便宜貨本來就常整批跳動）。</param>
        /// <param name="lowWitnesses">偏低時可以佐證的獨立觀測；&lt;=0 的值不算。</param>
        /// <param name="highWitnesses">偏高時可以佐證的獨立觀測；&lt;=0 的值不算。</param>
        /// <remarks>全程整數運算；價格上限 999,999,999 × 1000 仍在 <c>long</c> 範圍內。</remarks>
        internal static HistoryVerdict Judge(long reference, long median, int ratio, long minMedian,
            ReadOnlySpan<long> lowWitnesses, ReadOnlySpan<long> highWitnesses)
        {
            if (reference <= 0 || median <= 0 || median < minMedian || ratio < MinRatio || ratio > MaxRatio)
                return HistoryVerdict.NoData;

            var k = (long)ratio;
            if (reference * k <= median)
            {
                foreach (var y in lowWitnesses)
                {
                    if (y > 0 && y < reference * k)
                        return HistoryVerdict.Normal;
                }

                return HistoryVerdict.Low;
            }

            if (reference >= median * k)
            {
                foreach (var y in highWitnesses)
                {
                    if (y > 0 && y * k > reference)
                        return HistoryVerdict.Normal;
                }

                return HistoryVerdict.High;
            }

            return HistoryVerdict.Normal;
        }

        /// <summary>不看佐證時這個參考價偏離中位數的方向（<see cref="HistoryVerdict.Low"/>／<see cref="HistoryVerdict.High"/>／其他＝沒偏離）。</summary>
        /// <remarks>只給診斷 log 分辨「有佐證所以照常採用」；判定一律走 <see cref="Judge"/>。</remarks>
        internal static HistoryVerdict Deviation(long reference, long median, int ratio, long minMedian)
            => Judge(reference, median, ratio, minMedian, ReadOnlySpan<long>.Empty, ReadOnlySpan<long>.Empty);
    }
}
