using System;
using System.Collections.Generic;

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
            if (delistBelowVendor && vendorUnitPrice > 0 && MarketTax.NetAfterTax(target, tax) < vendorUnitPrice)
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

        // ---- 不自動上架的道具：以道具 id 判（NQ／HQ 一起）。null＝沒有排除清單。

        internal static bool IsExcluded(IReadOnlySet<uint>? excluded, uint itemId)
            => excluded != null && excluded.Count > 0 && excluded.Contains(itemId);

        /// <summary>依勾選上架的入口在開始前都過這一道：就地移除被排除的項目，回移除了幾項。</summary>
        internal static int RemoveExcluded<T>(List<T> picks, IReadOnlySet<uint>? excluded, Func<T, uint> itemIdOf)
        {
            if (excluded == null || excluded.Count == 0)
                return 0;
            var kept = 0;
            for (var i = 0; i < picks.Count; i++)
            {
                if (excluded.Contains(itemIdOf(picks[i])))
                    continue;
                picks[kept++] = picks[i];
            }

            var removed = picks.Count - kept;
            picks.RemoveRange(kept, removed);
            return removed;
        }

        /// <summary>加入或移出排除清單；回 true＝清單有變。</summary>
        internal static bool SetExcluded(HashSet<uint> excluded, uint itemId, bool exclude)
            => exclude ? excluded.Add(itemId) : excluded.Remove(itemId);

        /// <summary>
        /// 這名僱員有沒有「沒被排除」的補上架勾選（全僱員補上架／整理一輪據此決定要不要去他那裡補）。
        /// 用索引走訪：每幀可能被僱員清單面板呼叫，不配置列舉器。
        /// </summary>
        internal static bool HasEligiblePick<T>(IReadOnlyList<T> picks, ulong retainerId, IReadOnlySet<uint>? excluded,
            Func<T, ulong> retainerOf, Func<T, uint> itemIdOf)
        {
            if (retainerId == 0)
                return false;
            for (var i = 0; i < picks.Count; i++)
            {
                var p = picks[i];
                if (retainerOf(p) == retainerId && !IsExcluded(excluded, itemIdOf(p)))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 滑鼠提示的逐行清單：最多 <paramref name="maxLines"/> 行，超出的最後一行換成 <paramref name="moreLine"/>（帶剩下的件數）。
        /// </summary>
        internal static List<string> CapLines(IReadOnlyList<string> lines, int maxLines, Func<int, string> moreLine)
        {
            if (lines.Count <= maxLines)
                return new List<string>(lines);
            var result = new List<string>(maxLines + 1);
            for (var i = 0; i < maxLines; i++)
                result.Add(lines[i]);
            result.Add(moreLine(lines.Count - maxLines));
            return result;
        }
    }
}
