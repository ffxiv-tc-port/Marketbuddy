using System;
using System.Collections.Generic;

namespace Marketbuddy
{
    /// <summary>「這件被低價下架過，這次不要再試掛」的判定（純函式，離線測試直接編這個檔）。</summary>
    internal static class QuickListSkipRule
    {
        /// <param name="lastDelistUtc">最後一次低價下架；<see cref="DateTime.MinValue"/>＝沒有紀錄。</param>
        /// <param name="dismissedUtc">使用者從清單移除的時間；null＝沒移除過。移除之後<b>再被低價下架</b>才會回到清單。</param>
        /// <param name="windowDays">只看這麼多天內的下架；0＝不限。</param>
        internal static bool ShouldSkip(DateTime lastDelistUtc, DateTime? dismissedUtc, DateTime nowUtc, int windowDays)
        {
            if (lastDelistUtc == DateTime.MinValue)
                return false;
            if (windowDays > 0 && lastDelistUtc < nowUtc.AddDays(-windowDays))
                return false;
            return dismissedUtc is not { } dismissed || lastDelistUtc > dismissed;
        }

        /// <summary>丟掉超過 <paramref name="keepDays"/> 天的移除紀錄（取低價下架紀錄的保存天數：那時它蓋住的紀錄也已經過期）。</summary>
        internal static int PruneDismissals(List<QuickListSkipDismissal> dismissals, DateTime nowUtc, int keepDays)
            => dismissals.RemoveAll(d => d == null || TicksToUtc(d.DismissedUtcTicks) < nowUtc.AddDays(-keepDays));

        /// <summary>設定檔被手改成範圍外的值時當成很久以前（＝那筆移除不再蓋住任何紀錄），不擲例外。</summary>
        internal static DateTime TicksToUtc(long ticks)
            => ticks <= 0 || ticks > DateTime.MaxValue.Ticks ? DateTime.MinValue : new DateTime(ticks, DateTimeKind.Utc);
    }

    /// <summary>使用者從「快速上架跳過清單」移除的一件。🔴 欄位名＝存檔的 JSON 鍵。</summary>
    [Serializable]
    public class QuickListSkipDismissal
    {
        public uint ItemId;
        public bool Hq;
        public long DismissedUtcTicks;
    }
}
