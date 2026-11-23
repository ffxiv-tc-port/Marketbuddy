using System;

namespace Marketbuddy
{
    /// <summary>
    /// 即時掛單面板「看盤」欄位的純計算：扣稅後實拿、板上比我便宜的件數、預估幾天賣完、資料新不新。
    /// 不碰任何 Dalamud 服務（離線測試直接編譯本檔）。
    /// 🔴 「不知道」一律回 -1／NaN，<b>不回 0</b>：0 件、0 天、0 gil 都是合法但意思相反的答案。
    /// </summary>
    internal static class MarketWatchMath
    {
        /// <summary>Universalis 的資料超過這麼久沒人上傳，整列淡色顯示。</summary>
        internal static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);

        /// <summary>整堆賣完扣稅後實拿（<see cref="MarketTax.NetAfterTax"/>）；單價或數量不知道回 -1。</summary>
        internal static long NetTotal(long unitPrice, long quantity, uint taxPercent)
        {
            if (unitPrice <= 0 || quantity <= 0)
                return -1;
            return MarketTax.NetAfterTax(unitPrice * quantity, Math.Min(taxPercent, 25u));
        }

        /// <summary>
        /// 同品質、單價<b>嚴格低於</b> <paramref name="myUnitPrice"/> 的掛單加起來幾件；我的單價不知道回 -1。
        /// </summary>
        /// <param name="capPrice">
        /// 回傳的掛單被筆數上限截掉時，回傳那一批裡最貴的單價（Universalis 由便宜到貴回傳）；沒被截掉傳 <see cref="long.MaxValue"/>。
        /// </param>
        /// <param name="lowerBound">true＝截掉的那些也可能比我便宜，數字是「至少」。</param>
        internal static long CheaperQuantity(ReadOnlySpan<long> prices, ReadOnlySpan<int> quantities, long myUnitPrice,
            long capPrice, out bool lowerBound)
        {
            lowerBound = false;
            if (myUnitPrice <= 0)
                return -1;

            long total = 0;
            var n = Math.Min(prices.Length, quantities.Length);
            for (var i = 0; i < n; i++)
            {
                if (prices[i] > 0 && prices[i] < myUnitPrice && quantities[i] > 0)
                    total += quantities[i];
            }

            lowerBound = capPrice < myUnitPrice;
            return total;
        }

        /// <summary>
        /// 預估幾天賣完＝（比我便宜的件數＋我這一堆）÷ 每日成交件數。
        /// 任一輸入不知道回 NaN；每日成交是 0（窗內沒有成交）回正無限大。
        /// </summary>
        internal static double DaysToSell(long cheaperQuantity, long myQuantity, double unitsPerDay)
        {
            if (cheaperQuantity < 0 || myQuantity <= 0 || double.IsNaN(unitsPerDay) || unitsPerDay < 0)
                return double.NaN;
            if (unitsPerDay == 0)
                return double.PositiveInfinity;
            return (cheaperQuantity + myQuantity) / unitsPerDay;
        }

        /// <summary>上傳時間不知道（<see cref="DateTime.MinValue"/>）也算太舊。</summary>
        internal static bool IsStale(DateTime uploadedUtc, DateTime nowUtc, TimeSpan maxAge)
            => uploadedUtc == DateTime.MinValue || nowUtc - uploadedUtc > maxAge;
    }
}
