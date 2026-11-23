using System;

namespace Marketbuddy
{
    /// <summary>市場稅的唯一算法：所有「扣稅後實拿」都走這裡，不要各寫一份。不碰 Dalamud（離線測試直接編譯本檔）。</summary>
    internal static class MarketTax
    {
        /// <summary>
        /// 賣出總價扣稅後的實拿：稅額捨去、實拿＝總價－稅額（遊戲的算法：錢包實測 2,399 gil 稅 5% 進帳 2,280）。
        /// 稅率夾在 0～100；總價 ≤0 原樣回傳。
        /// </summary>
        internal static long NetAfterTax(long gross, long taxPercent)
        {
            if (gross <= 0)
                return gross;
            return gross - (gross * Math.Clamp(taxPercent, 0, 100) / 100);
        }
    }
}
