using System;
using System.Collections.Generic;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    /// <summary>
    /// 快速上架／批次上架要跳過的道具：<see cref="LowPriceDelistLedger"/> 裡近期被低價下架過（低於 NPC 收購價或低於最低價）、
    /// 而且使用者之後沒有從清單移除的那幾件。
    /// 🔴 只在 framework 執行緒呼叫（讀改設定裡的移除清單）。紀錄檔還沒讀完時一律不跳過（＝舊行為）。
    /// <para>移除不動低價下架紀錄本身（清理背包的匯出與背包分揀還要用），只記一個時間點；之後再被低價下架就回到清單。</para>
    /// </summary>
    internal static class QuickListSkipList
    {
        /// <summary>移除紀錄保存的天數，比照低價下架紀錄的保存天數。</summary>
        private const int DismissalKeepDays = 180;

        internal static bool ShouldSkip(Configuration conf, uint itemId, bool hq, out LowPriceDelistRecord record)
        {
            record = default;
            if (!conf.QuickListSkipLowPriceDelisted || !LowPriceDelistLedger.Loaded || itemId == 0)
                return false;
            if (!LowPriceDelistLedger.TryGet(new VendorSellKey(itemId, hq), out record))
                return false;
            return QuickListSkipRule.ShouldSkip(record.LastDelistUtc, DismissedAt(conf, record.Key), DateTime.UtcNow,
                conf.QuickListSkipDays);
        }

        /// <summary>
        /// 這件剛以不低於門檻的價定好價：正在跳過的話記一筆移除（只在真的會改變結果時才寫設定檔）。
        /// </summary>
        internal static void NoteListedOk(Configuration conf, uint itemId, bool hq)
        {
            if (!ShouldSkip(conf, itemId, hq, out var record))
                return;
            Dismiss(conf, record.Key);
            Log.Information(
                $"[Marketbuddy] 快速上架跳過清單：{itemId}（HQ={hq}）這次定價不低於門檻，" +
                $"從清單拿掉（低價下架 {record.Count} 次、最後 {RelistPricing.FormatAt(record.LastDelistUtc)}）");
        }

        /// <summary>目前生效的清單（依最後下架時間新到舊）。</summary>
        internal static List<LowPriceDelistRecord> Active(Configuration conf)
        {
            var now = DateTime.UtcNow;
            var result = new List<LowPriceDelistRecord>();
            foreach (var record in LowPriceDelistLedger.Snapshot())
            {
                if (QuickListSkipRule.ShouldSkip(record.LastDelistUtc, DismissedAt(conf, record.Key), now, conf.QuickListSkipDays))
                    result.Add(record);
            }

            result.Sort((a, b) => b.LastDelistUtc.CompareTo(a.LastDelistUtc));
            return result;
        }

        internal static void Dismiss(Configuration conf, VendorSellKey key)
        {
            var now = DateTime.UtcNow;
            conf.QuickListSkipDismissed.RemoveAll(d => d != null && d.ItemId == key.ItemId && d.Hq == key.Hq);
            conf.QuickListSkipDismissed.Add(new QuickListSkipDismissal
            {
                ItemId = key.ItemId, Hq = key.Hq, DismissedUtcTicks = now.Ticks,
            });
            QuickListSkipRule.PruneDismissals(conf.QuickListSkipDismissed, now, DismissalKeepDays);
            conf.Save();
        }

        internal static void DismissAll(Configuration conf, IEnumerable<LowPriceDelistRecord> records)
        {
            var now = DateTime.UtcNow;
            foreach (var record in records)
            {
                conf.QuickListSkipDismissed.RemoveAll(d => d != null && d.ItemId == record.Key.ItemId && d.Hq == record.Key.Hq);
                conf.QuickListSkipDismissed.Add(new QuickListSkipDismissal
                {
                    ItemId = record.Key.ItemId, Hq = record.Key.Hq, DismissedUtcTicks = now.Ticks,
                });
            }

            QuickListSkipRule.PruneDismissals(conf.QuickListSkipDismissed, now, DismissalKeepDays);
            conf.Save();
        }

        private static DateTime? DismissedAt(Configuration conf, VendorSellKey key)
        {
            foreach (var d in conf.QuickListSkipDismissed)
            {
                if (d != null && d.ItemId == key.ItemId && d.Hq == key.Hq)
                    return QuickListSkipRule.TicksToUtc(d.DismissedUtcTicks);
            }

            return null;
        }
    }
}
