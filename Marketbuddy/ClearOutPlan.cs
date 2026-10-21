using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Marketbuddy
{
    // 本檔只有純資料與純函式：不讀遊戲記憶體、不碰 Dalamud，離線測試直接編這個檔（加 VendorSellPlan.cs）。
    // 共用給「常被低價下架」匯出、背包分揀，以及之後的批次上架。

    /// <summary>某一種道具（含品質）被重掛的兩道自動下架拿下來的累計紀錄。</summary>
    /// <param name="LastSalePrice">最後一次下架當時查到的成交單價；-1＝那次沒有成交價。</param>
    /// <param name="LastSaleAtUtc">那筆成交的時間；<see cref="DateTime.MinValue"/>＝不知道。</param>
    /// <param name="LastTargetPrice">最後一次下架時算出、低於門檻的那個價；0＝不知道。</param>
    internal readonly record struct LowPriceDelistRecord(
        VendorSellKey Key,
        int Count,
        DateTime LastDelistUtc,
        long LastSalePrice,
        DateTime LastSaleAtUtc,
        uint LastTargetPrice);

    /// <summary>
    /// 「低價下架」紀錄本：每種道具一列，有上限與修剪。不是執行緒安全的，持有者自己上鎖。
    /// </summary>
    internal sealed class LowPriceDelistBook
    {
        public const string CsvHeader = "itemId,hq,count,lastDelistUtc,lastSalePrice,lastSaleAtUtc,lastTargetPrice";

        private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";

        private readonly Dictionary<VendorSellKey, LowPriceDelistRecord> records = new();

        public int MaxRecords { get; }
        public int MaxAgeDays { get; }

        public LowPriceDelistBook(int maxRecords, int maxAgeDays)
        {
            MaxRecords = Math.Max(1, maxRecords);
            MaxAgeDays = Math.Max(1, maxAgeDays);
        }

        public int Count => records.Count;

        public IEnumerable<LowPriceDelistRecord> Records => records.Values;

        public bool TryGet(VendorSellKey key, out LowPriceDelistRecord record)
            => records.TryGetValue(key, out record);

        /// <summary>記一次下架。次數 +1；成交價、時間、目標價換成這一次的（這一次沒有成交價就留著上一次的）。</summary>
        public void Note(VendorSellKey key, DateTime atUtc, long salePrice, DateTime saleAtUtc, uint targetPrice)
        {
            if (key.ItemId == 0)
                return;
            var hasSale = salePrice >= 0;
            if (records.TryGetValue(key, out var old))
            {
                records[key] = new LowPriceDelistRecord(key, old.Count + 1,
                    atUtc > old.LastDelistUtc ? atUtc : old.LastDelistUtc,
                    hasSale ? salePrice : old.LastSalePrice,
                    hasSale ? saleAtUtc : old.LastSaleAtUtc,
                    targetPrice > 0 ? targetPrice : old.LastTargetPrice);
            }
            else
            {
                records[key] = new LowPriceDelistRecord(key, 1, atUtc,
                    hasSale ? salePrice : -1, hasSale ? saleAtUtc : DateTime.MinValue, targetPrice);
            }
        }

        /// <summary>讀檔讀回來的一列併進來：次數相加，其餘取最後一次下架較新的那一邊。</summary>
        public void Merge(LowPriceDelistRecord incoming)
        {
            if (incoming.Key.ItemId == 0 || incoming.Count <= 0)
                return;
            if (!records.TryGetValue(incoming.Key, out var old))
            {
                records[incoming.Key] = incoming;
                return;
            }

            var incomingNewer = incoming.LastDelistUtc > old.LastDelistUtc;
            var newer = incomingNewer ? incoming : old;
            var older = incomingNewer ? old : incoming;
            records[incoming.Key] = newer with
            {
                Count = old.Count + incoming.Count,
                LastSalePrice = newer.LastSalePrice >= 0 ? newer.LastSalePrice : older.LastSalePrice,
                LastSaleAtUtc = newer.LastSalePrice >= 0 ? newer.LastSaleAtUtc : older.LastSaleAtUtc,
            };
        }

        /// <summary>丟掉最後一次下架超過 <see cref="MaxAgeDays"/> 天的；還超過上限就從最舊的開始丟。回傳丟掉幾列。</summary>
        public int Prune(DateTime nowUtc)
        {
            var cutoff = nowUtc.AddDays(-MaxAgeDays);
            var drop = new List<VendorSellKey>();
            foreach (var r in records.Values)
            {
                if (r.LastDelistUtc < cutoff)
                    drop.Add(r.Key);
            }

            foreach (var key in drop)
                records.Remove(key);
            var removed = drop.Count;
            if (records.Count <= MaxRecords)
                return removed;

            var ordered = new List<LowPriceDelistRecord>(records.Values);
            ordered.Sort((a, b) => a.LastDelistUtc != b.LastDelistUtc
                ? a.LastDelistUtc.CompareTo(b.LastDelistUtc)
                : CompareKey(a.Key, b.Key));
            for (var i = 0; i < ordered.Count - MaxRecords; i++)
            {
                records.Remove(ordered[i].Key);
                removed++;
            }

            return removed;
        }

        public string ToCsv()
        {
            var ordered = new List<LowPriceDelistRecord>(records.Values);
            ordered.Sort((a, b) => CompareKey(a.Key, b.Key));
            var sb = new StringBuilder();
            sb.Append(CsvHeader).Append("\r\n");
            foreach (var r in ordered)
            {
                sb.Append(r.Key.ItemId.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(r.Key.Hq ? '1' : '0').Append(',')
                    .Append(r.Count.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(FormatTime(r.LastDelistUtc)).Append(',')
                    .Append(r.LastSalePrice.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(FormatTime(r.LastSaleAtUtc)).Append(',')
                    .Append(r.LastTargetPrice.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            }

            return sb.ToString();
        }

        /// <summary>解析一列；表頭、空行、欄位不對的列回 false（整份檔不會因為一列壞掉而作廢）。</summary>
        public static bool TryParseCsvLine(string line, out LowPriceDelistRecord record)
        {
            record = default;
            var f = line.Trim().Split(',');
            if (f.Length != 7 ||
                !uint.TryParse(f[0], NumberStyles.None, CultureInfo.InvariantCulture, out var itemId) || itemId == 0 ||
                f[1] is not ("0" or "1") ||
                !int.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count <= 0 ||
                !TryParseTime(f[3], out var delistAt) || delistAt == DateTime.MinValue ||
                !long.TryParse(f[4], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var sale) ||
                !TryParseTime(f[5], out var saleAt) ||
                !uint.TryParse(f[6], NumberStyles.None, CultureInfo.InvariantCulture, out var target))
                return false;
            record = new LowPriceDelistRecord(new VendorSellKey(itemId, f[1] == "1"), count, delistAt,
                sale < 0 ? -1 : sale, saleAt, target);
            return true;
        }

        private static int CompareKey(VendorSellKey a, VendorSellKey b)
            => a.ItemId != b.ItemId ? a.ItemId.CompareTo(b.ItemId) : a.Hq.CompareTo(b.Hq);

        private static string FormatTime(DateTime utc)
            => utc == DateTime.MinValue ? string.Empty : utc.ToString(TimestampFormat, CultureInfo.InvariantCulture);

        private static bool TryParseTime(string text, out DateTime utc)
        {
            utc = DateTime.MinValue;
            if (text.Length == 0)
                return true;
            if (!DateTime.TryParseExact(text, TimestampFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                return false;
            utc = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            return true;
        }
    }

    /// <summary>「常被低價下架」清單的一列：同一個道具 id 的優質與普通合在一起（AutoRetainer 的清單只認道具 id）。</summary>
    internal readonly record struct JunkCandidate(
        uint ItemId,
        int Count,
        long LastSalePrice,
        DateTime LastSaleAtUtc,
        DateTime LastDelistUtc,
        bool HasHq,
        bool HasNq);

    internal static class ClearOutRules
    {
        /// <summary>匯出清單的門檻：同一個道具 id 合計低價下架這麼多次以上。</summary>
        public const int JunkMinDelists = 2;

        /// <summary>
        /// 低價下架合計 ≥ <see cref="JunkMinDelists"/> 次、而且最後一次下架時的成交價已知又低於門檻的道具，
        /// 依次數多到少排。門檻 ≤ 0（使用者沒設最低價）一律回空：沒有門檻就判不出「仍低於門檻」。
        /// </summary>
        public static List<JunkCandidate> JunkCandidates(IEnumerable<LowPriceDelistRecord> records, long threshold)
        {
            var byId = new Dictionary<uint, JunkCandidate>();
            if (threshold <= 0)
                return new List<JunkCandidate>();
            foreach (var r in records)
            {
                if (!byId.TryGetValue(r.Key.ItemId, out var c))
                {
                    byId[r.Key.ItemId] = new JunkCandidate(r.Key.ItemId, r.Count, r.LastSalePrice, r.LastSaleAtUtc,
                        r.LastDelistUtc, r.Key.Hq, !r.Key.Hq);
                    continue;
                }

                var newer = r.LastDelistUtc > c.LastDelistUtc;
                byId[r.Key.ItemId] = c with
                {
                    Count = c.Count + r.Count,
                    LastSalePrice = newer ? r.LastSalePrice : c.LastSalePrice,
                    LastSaleAtUtc = newer ? r.LastSaleAtUtc : c.LastSaleAtUtc,
                    LastDelistUtc = newer ? r.LastDelistUtc : c.LastDelistUtc,
                    HasHq = c.HasHq || r.Key.Hq,
                    HasNq = c.HasNq || !r.Key.Hq,
                };
            }

            var result = new List<JunkCandidate>();
            foreach (var c in byId.Values)
            {
                if (c.Count >= JunkMinDelists && c.LastSalePrice >= 0 && c.LastSalePrice < threshold)
                    result.Add(c);
            }

            result.Sort((a, b) => a.Count != b.Count ? b.Count.CompareTo(a.Count) : a.ItemId.CompareTo(b.ItemId));
            return result;
        }

        /// <summary>AutoRetainer「與剪貼簿合併」吃的格式：JSON 的 <c>List&lt;uint&gt;</c>，去重、由小到大。</summary>
        public static string ToAutoRetainerClipboard(IEnumerable<uint> itemIds)
        {
            var set = new SortedSet<uint>();
            foreach (var id in itemIds)
            {
                if (id != 0)
                    set.Add(id);
            }

            var sb = new StringBuilder("[");
            var first = true;
            foreach (var id in set)
            {
                if (!first)
                    sb.Append(',');
                sb.Append(id.ToString(CultureInfo.InvariantCulture));
                first = false;
            }

            return sb.Append(']').ToString();
        }
    }

    internal enum BagSortPile
    {
        Excluded = 0,
        Vendor,
        Market,
        NeedsPricing,
    }

    internal enum BagSortReason
    {
        None = 0,
        Protected,
        NotMarketable,
        PriceLookupOff,
        PriceLoading,
        PriceFailed,
        NoSaleData,
        SaleStale,
        BelowThresholdAndDelisted,
        BelowThresholdNeverDelisted,
        AtOrAboveThreshold,
        NoThreshold,
    }

    internal enum BagSortSaleState
    {
        /// <summary>沒有開「用最近成交價」，本插件不會去查。</summary>
        Off = 0,
        Loading,
        Failed,
        NoData,
        Ready,
    }

    /// <summary>背包裡某一種道具（同 id、同品質的所有格子合計）。</summary>
    /// <param name="CannotVendor">商店不收、會跳確認框（魔晶石、精煉度滿、獨佔），這種永遠不能勾。</param>
    /// <param name="Marketable">能上市場（不是不可交易、有市場分類）。</param>
    /// <param name="Protected">在保護清單上（例：InventoryTools 製作清單要用的）。</param>
    internal readonly record struct BagSortItem(
        VendorSellKey Key,
        int Quantity,
        bool CannotVendor,
        bool Marketable,
        bool Protected);

    /// <param name="Stale">成交時間超出使用者設的新鮮度窗（呼叫端用 <c>RelistPricing.IsSaleStale</c> 算）。</param>
    internal readonly record struct BagSortSale(BagSortSaleState State, long UnitPrice, bool Stale);

    internal readonly record struct BagSortVerdict(BagSortPile Pile, BagSortReason Reason, bool PreTicked, bool CanTick);

    internal static class BagSortClassifier
    {
        /// <summary>
        /// 保護清單優先；然後成交價要能用（有、新鮮），否則「要查價」；
        /// 只有「低價下架紀錄有、而且這次成交價仍低於門檻」進「賣商店」並預先勾選，其餘「上市場」不勾。
        /// </summary>
        /// <param name="delistedBefore">低價下架紀錄裡有這一種（同 id、同品質）。</param>
        /// <param name="threshold">使用者的最低價門檻；≤ 0＝沒設，沒有東西會進「賣商店」。</param>
        public static BagSortVerdict Classify(BagSortItem item, BagSortSale sale, bool delistedBefore, long threshold)
        {
            if (item.Protected)
                return new BagSortVerdict(BagSortPile.Excluded, BagSortReason.Protected, false, false);
            if (!item.Marketable)
                return new BagSortVerdict(BagSortPile.Excluded, BagSortReason.NotMarketable, false, false);

            var canTick = !item.CannotVendor;
            var pricing = sale.State switch
            {
                BagSortSaleState.Off => BagSortReason.PriceLookupOff,
                BagSortSaleState.Loading => BagSortReason.PriceLoading,
                BagSortSaleState.Failed => BagSortReason.PriceFailed,
                BagSortSaleState.NoData => BagSortReason.NoSaleData,
                _ => sale.UnitPrice < 0 ? BagSortReason.NoSaleData : sale.Stale ? BagSortReason.SaleStale : BagSortReason.None,
            };
            if (pricing != BagSortReason.None)
                return new BagSortVerdict(BagSortPile.NeedsPricing, pricing, false, canTick);

            if (threshold <= 0)
                return new BagSortVerdict(BagSortPile.Market, BagSortReason.NoThreshold, false, canTick);
            if (sale.UnitPrice >= threshold)
                return new BagSortVerdict(BagSortPile.Market, BagSortReason.AtOrAboveThreshold, false, canTick);
            if (!delistedBefore)
                return new BagSortVerdict(BagSortPile.Market, BagSortReason.BelowThresholdNeverDelisted, false, canTick);
            return new BagSortVerdict(BagSortPile.Vendor, BagSortReason.BelowThresholdAndDelisted, canTick, canTick);
        }
    }
}
