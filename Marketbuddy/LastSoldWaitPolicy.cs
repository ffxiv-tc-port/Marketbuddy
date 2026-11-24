using System;
using System.Threading;

namespace Marketbuddy
{
    /// <summary>
    /// 重掛／快速上架／批次上架查價時，一格要等 Universalis 的「最近成交價」等多久。
    /// 純邏輯、不碰 Dalamud（離線測試在 tests/LastSoldWaitTests）。
    /// 🔑 最近成交價只是加分項：等不到就照原本的市場比價定價，所以這裡寧可少等。
    /// </summary>
    internal static class LastSoldWaitPolicy
    {
        /// <summary>整位僱員一起重掛時的上限。整批一個請求就問完，等一次就夠所有格用。</summary>
        public const int BatchWaitMs = 25000;

        /// <summary>
        /// 只有一件的那幾種（快速上架後的定價、批次上架的查價）：每件都要各等一次，所以壓短。
        /// 慢的時候成功的查詢多落在 6～9 秒（近期成交那一條逾時 8 秒，之後最近成交價約 1 秒回來）；
        /// 道具在排進佇列那一刻就先問了，輪到它時通常已經問了一陣子。
        /// </summary>
        public const int SingleItemWaitMs = 8000;

        /// <summary>
        /// 這一格最多等幾毫秒；0＝不等，直接用原本的定價。
        /// </summary>
        /// <param name="singleItem">這一輪只有一件（快速上架定價、批次上架查價）。</param>
        /// <param name="struggling">Universalis 剛逾時或失敗過、還沒成功過一次（見 <see cref="LastSoldHealth"/>）。</param>
        public static int LimitMs(bool singleItem, bool struggling)
            => struggling ? 0 : singleItem ? SingleItemWaitMs : BatchWaitMs;
    }

    /// <summary>
    /// 「Universalis 最近是不是在吃力」的斷路器：逾時或失敗後 <see cref="WindowMs"/> 內、
    /// 而且之後還沒成功過，就算吃力。成功一次立刻恢復。
    /// 執行緒：任何執行緒都可以呼叫（只用 Interlocked）。回傳值只拿來決定要不要寫一行 log，
    /// 兩個執行緒同時翻轉時頂多多寫或少寫一行。
    /// </summary>
    internal sealed class LastSoldHealth
    {
        /// <summary>沿用「查失敗的道具 2 分鐘後才重問」的同一個長度。</summary>
        public const int WindowMs = 2 * 60 * 1000;

        private long troubleTicks;
        private long successTicks;

        public bool IsStruggling(DateTime nowUtc)
        {
            var trouble = Interlocked.Read(ref troubleTicks);
            return trouble > Interlocked.Read(ref successTicks) &&
                   nowUtc.Ticks - trouble < TimeSpan.TicksPerMillisecond * WindowMs;
        }

        /// <summary>記一次逾時或失敗。回 true＝由正常轉成吃力。</summary>
        public bool NoteTrouble(DateTime nowUtc)
        {
            var was = IsStruggling(nowUtc);
            Interlocked.Exchange(ref troubleTicks, nowUtc.Ticks);
            return !was;
        }

        /// <summary>記一次成功。回 true＝由吃力恢復。</summary>
        public bool NoteSuccess(DateTime nowUtc)
        {
            var was = IsStruggling(nowUtc);
            Interlocked.Exchange(ref successTicks, nowUtc.Ticks);
            return was;
        }
    }
}
