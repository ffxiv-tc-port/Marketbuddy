using System;

namespace Marketbuddy
{
    // 本檔只有純函式：不讀遊戲記憶體、不碰 Dalamud，離線測試直接編這一個檔。

    /// <summary>批次上架對一件道具的查價結論。</summary>
    internal enum PriceProbeOutcome
    {
        /// <summary>照這個價上架。</summary>
        List,

        /// <summary>扣稅後低於 NPC 收購價：不上架，改列進賣給商店的清單。</summary>
        BelowVendor,

        /// <summary>低於使用者設的最低價：不上架，改列進賣給商店的清單。</summary>
        BelowMin,

        /// <summary>板上沒人在賣、也沒有可用的成交價：跳過，不掛上限價。</summary>
        NoData,

        /// <summary>參考價被異常低價保護擋下：跳過。</summary>
        Hold,

        /// <summary>查價本身失敗（送不出、被拒、逾時）：跳過。</summary>
        Failed,

        /// <summary>引擎被中止（ESC、IPC 鎖、AutoRetainer 忙…）：整批停。</summary>
        Aborted,
    }

    internal static class BatchListRules
    {
        /// <summary>遊戲拒絕「到市場出售」（選了之後上架視窗沒開）連續這麼多件就整批停。</summary>
        internal const int MaxConsecutiveRefusals = 2;

        /// <summary>
        /// 把定價決策換成批次上架的結論，語意比照快速上架那一格：自家掛單是板上最低時與它齊平（比目標價低才用）。
        /// </summary>
        /// <param name="decisionPrice">定價決策的目標價；&lt;=0＝沒有。</param>
        /// <param name="held">唯一的參考價被異常低價保護擋下。</param>
        /// <param name="ownLowest">自家掛單的最低單價；&lt;=0＝沒有。只在 <paramref name="ownIsLowest"/> 時用。</param>
        /// <param name="vendorUnitPrice">NPC 收購單價；0＝商店不收（不比）。</param>
        /// <param name="minPrice">使用者的最低價；&lt;=0＝停用。</param>
        internal static PriceProbeOutcome Classify(long decisionPrice, bool held, bool ownIsLowest, long ownLowest,
            uint vendorUnitPrice, uint taxPercent, bool delistBelowVendor, int minPrice, int maxPrice, out uint price)
        {
            price = 0;
            if (held)
                return PriceProbeOutcome.Hold;

            long target;
            if (decisionPrice > 0)
                target = ownIsLowest && ownLowest > 0 && ownLowest < decisionPrice ? ownLowest : decisionPrice;
            else if (ownIsLowest && ownLowest > 0)
                target = ownLowest;
            else
                return PriceProbeOutcome.NoData;

            target = Math.Clamp(target, 1L, Math.Max(1L, maxPrice));
            price = (uint)target;

            var tax = Math.Min(taxPercent, 100u);
            if (delistBelowVendor && vendorUnitPrice > 0 && target * (100L - tax) / 100L < vendorUnitPrice)
                return PriceProbeOutcome.BelowVendor;
            if (minPrice > 0 && target < minPrice)
                return PriceProbeOutcome.BelowMin;
            return PriceProbeOutcome.List;
        }

        /// <summary>批次面板列得出來的道具：能上市場、不是收藏品、不是連結型格子。</summary>
        internal static bool IsListable(bool untradable, uint itemSearchCategory, bool collectable, bool symbolic)
            => !untradable && itemSearchCategory != 0 && !collectable && !symbolic;

        /// <summary>
        /// 連續「選了到市場出售、上架視窗卻沒開」的計數：開了就歸零。回 true＝已達上限、要整批停。
        /// </summary>
        internal static bool NoteWindowOutcome(ref int streak, bool windowOpened)
        {
            streak = windowOpened ? 0 : streak + 1;
            return streak >= MaxConsecutiveRefusals;
        }
    }
}
