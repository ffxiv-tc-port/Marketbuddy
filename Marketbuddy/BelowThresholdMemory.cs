using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Marketbuddy
{
    // 本檔只有純資料與純函式：不讀遊戲記憶體、不碰 Dalamud，離線測試直接編這個檔（加 VendorSellPlan.cs、MarketTax.cs）。

    /// <summary>批次上架查價判成「低於門檻」的最後一次（每種道具含品質一列）。</summary>
    /// <param name="Price">那次查到、低於門檻的單價（&gt;0）。</param>
    /// <param name="VendorUnitPrice">NPC 收購單價；0＝商店不收。</param>
    internal readonly record struct BelowThresholdRecord(
        VendorSellKey Key,
        int Count,
        DateTime LastAtUtc,
        uint Price,
        uint VendorUnitPrice);

    /// <summary>「低於門檻」記憶本：有上限與修剪。不是執行緒安全的，持有者自己上鎖。</summary>
    internal sealed class BelowThresholdBook
    {
        public const string CsvHeader = "itemId,hq,count,lastAtUtc,price,vendorUnitPrice";

        private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";

        private readonly Dictionary<VendorSellKey, BelowThresholdRecord> records = new();

        public int MaxRecords { get; }
        public int MaxAgeDays { get; }

        public BelowThresholdBook(int maxRecords, int maxAgeDays)
        {
            MaxRecords = Math.Max(1, maxRecords);
            MaxAgeDays = Math.Max(1, maxAgeDays);
        }

        public int Count => records.Count;

        public IEnumerable<BelowThresholdRecord> Records => records.Values;

        public bool TryGet(VendorSellKey key, out BelowThresholdRecord record)
            => records.TryGetValue(key, out record);

        /// <summary>記一次低於門檻：次數 +1，時間與價格換成這一次的。價格 0 不記（不知道價就沒有可比的）。</summary>
        public void Note(VendorSellKey key, DateTime atUtc, uint price, uint vendorUnitPrice)
        {
            if (key.ItemId == 0 || price == 0)
                return;
            var count = records.TryGetValue(key, out var old) ? old.Count + 1 : 1;
            records[key] = new BelowThresholdRecord(key, count, atUtc, price, vendorUnitPrice);
        }

        /// <summary>這種道具又查到能上架的價：整列拿掉。回 true＝原本有。</summary>
        public bool Forget(VendorSellKey key) => records.Remove(key);

        public void Clear() => records.Clear();

        /// <summary>讀檔讀回來的一列併進來：次數相加，其餘取時間較新的那一邊。</summary>
        public void Merge(BelowThresholdRecord incoming)
        {
            if (incoming.Key.ItemId == 0 || incoming.Count <= 0 || incoming.Price == 0)
                return;
            if (!records.TryGetValue(incoming.Key, out var old))
            {
                records[incoming.Key] = incoming;
                return;
            }

            var newer = incoming.LastAtUtc > old.LastAtUtc ? incoming : old;
            records[incoming.Key] = newer with { Count = old.Count + incoming.Count };
        }

        /// <summary>丟掉超過 <see cref="MaxAgeDays"/> 天的；還超過上限就從最舊的開始丟。回傳丟掉幾列。</summary>
        public int Prune(DateTime nowUtc)
        {
            var cutoff = nowUtc.AddDays(-MaxAgeDays);
            var drop = new List<VendorSellKey>();
            foreach (var r in records.Values)
            {
                if (r.LastAtUtc < cutoff)
                    drop.Add(r.Key);
            }

            foreach (var key in drop)
                records.Remove(key);
            var removed = drop.Count;
            if (records.Count <= MaxRecords)
                return removed;

            var ordered = new List<BelowThresholdRecord>(records.Values);
            ordered.Sort((a, b) => a.LastAtUtc != b.LastAtUtc
                ? a.LastAtUtc.CompareTo(b.LastAtUtc)
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
            var ordered = new List<BelowThresholdRecord>(records.Values);
            ordered.Sort((a, b) => CompareKey(a.Key, b.Key));
            var sb = new StringBuilder();
            sb.Append(CsvHeader).Append("\r\n");
            foreach (var r in ordered)
            {
                sb.Append(r.Key.ItemId.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(r.Key.Hq ? '1' : '0').Append(',')
                    .Append(r.Count.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(r.LastAtUtc.ToString(TimestampFormat, CultureInfo.InvariantCulture)).Append(',')
                    .Append(r.Price.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(r.VendorUnitPrice.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            }

            return sb.ToString();
        }

        /// <summary>解析一列；表頭、空行、欄位不對的列回 false（整份檔不會因為一列壞掉而作廢）。</summary>
        public static bool TryParseCsvLine(string line, out BelowThresholdRecord record)
        {
            record = default;
            var f = line.Trim().Split(',');
            if (f.Length != 6 ||
                !uint.TryParse(f[0], NumberStyles.None, CultureInfo.InvariantCulture, out var itemId) || itemId == 0 ||
                f[1] is not ("0" or "1") ||
                !int.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count <= 0 ||
                !DateTime.TryParseExact(f[3], TimestampFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ||
                !uint.TryParse(f[4], NumberStyles.None, CultureInfo.InvariantCulture, out var price) || price == 0 ||
                !uint.TryParse(f[5], NumberStyles.None, CultureInfo.InvariantCulture, out var vendor))
                return false;
            record = new BelowThresholdRecord(new VendorSellKey(itemId, f[1] == "1"), count,
                DateTime.SpecifyKind(at, DateTimeKind.Utc), price, vendor);
            return true;
        }

        private static int CompareKey(VendorSellKey a, VendorSellKey b)
            => a.ItemId != b.ItemId ? a.ItemId.CompareTo(b.ItemId) : a.Hq.CompareTo(b.Hq);
    }

    internal static class BelowThresholdRule
    {
        public const int DefaultDays = 7;
        public const int MaxDays = 30;

        /// <summary>
        /// 這個價照現在的門檻還是低於門檻（扣稅後低於 NPC 收購價，或低於最低價）。判準與
        /// <see cref="BatchListRules.Classify"/> 相同；使用者調低門檻之後，舊的記憶自然不再成立。
        /// </summary>
        public static bool StillBelow(uint price, uint vendorUnitPrice, uint taxPercent, bool belowVendorOn, int minPrice)
        {
            if (price == 0)
                return false;
            var tax = Math.Min(taxPercent, 100u);
            if (belowVendorOn && vendorUnitPrice > 0 && MarketTax.NetAfterTax(price, tax) < vendorUnitPrice)
                return true;
            return minPrice > 0 && price < minPrice;
        }

        /// <summary>
        /// 這一件不查價、直接當成低於門檻：沒有強制重查、記憶在 <paramref name="days"/> 天內、照現在的門檻仍然低於門檻。
        /// 時間在未來超過一天（時鐘被調過）一律不跳過。
        /// </summary>
        public static bool ShouldSkip(BelowThresholdRecord record, DateTime nowUtc, int days, bool forceRequery,
            uint taxPercent, bool belowVendorOn, int minPrice)
        {
            if (forceRequery || days <= 0 || record.Key.ItemId == 0)
                return false;
            var age = nowUtc - record.LastAtUtc;
            if (age < TimeSpan.FromDays(-1) || age >= TimeSpan.FromDays(days))
                return false;
            return StillBelow(record.Price, record.VendorUnitPrice, taxPercent, belowVendorOn, minPrice);
        }
    }

    /// <summary>批次上架一疊的結果分類；前兩種是「已處理」，其餘是「還要處理」。</summary>
    internal enum BatchListResultKind
    {
        Listed,
        ShopList,
        KeptBelow,
        Skipped,
        NotProcessed,
    }

    /// <summary>批次上架一疊的結果（給跑完的彙整表）。</summary>
    /// <param name="Price">上架價或查到的價；0＝不知道。</param>
    /// <param name="MinPrice">當時的最低價設定；0＝沒設。</param>
    /// <param name="VendorUnitPrice">NPC 收購單價；0＝商店不收。</param>
    /// <param name="FromMemory">沒查價，用的是「低於門檻」記憶。</param>
    internal readonly record struct BatchListResultEntry(
        VendorSellKey Key,
        string Name,
        int Quantity,
        BatchListResultKind Kind,
        uint Price,
        int MinPrice,
        uint VendorUnitPrice,
        bool FromMemory,
        string Note);

    /// <summary>彙整表的一列：同一種道具、同一種結果的幾疊合成一列。</summary>
    internal sealed record BatchListSummaryRow(
        VendorSellKey Key,
        string Name,
        BatchListResultKind Kind,
        int Stacks,
        int Quantity,
        uint Price,
        int MinPrice,
        uint VendorUnitPrice,
        bool FromMemory,
        string Note)
    {
        public bool Handled => BatchListSummary.IsHandled(Kind);
    }

    internal static class BatchListSummary
    {
        public static bool IsHandled(BatchListResultKind kind)
            => kind is BatchListResultKind.Listed or BatchListResultKind.ShopList;

        /// <summary>
        /// 依（道具、結果）合併：件數、疊數相加；價格取最後一個已知的；說明取第一個非空的；有一疊來自記憶就標記憶。
        /// 已處理在前、還要處理在後，各自保留第一次出現的順序。
        /// </summary>
        public static List<BatchListSummaryRow> Summarize(IReadOnlyList<BatchListResultEntry> entries)
        {
            var order = new List<(VendorSellKey, BatchListResultKind)>();
            var rows = new Dictionary<(VendorSellKey, BatchListResultKind), BatchListSummaryRow>();
            foreach (var e in entries)
            {
                var id = (e.Key, e.Kind);
                if (!rows.TryGetValue(id, out var row))
                {
                    order.Add(id);
                    rows[id] = new BatchListSummaryRow(e.Key, e.Name, e.Kind, 1, e.Quantity, e.Price, e.MinPrice,
                        e.VendorUnitPrice, e.FromMemory, e.Note);
                    continue;
                }

                rows[id] = row with
                {
                    Stacks = row.Stacks + 1,
                    Quantity = row.Quantity + e.Quantity,
                    Price = e.Price > 0 ? e.Price : row.Price,
                    FromMemory = row.FromMemory || e.FromMemory,
                    Note = row.Note.Length > 0 ? row.Note : e.Note,
                };
            }

            var result = new List<BatchListSummaryRow>(order.Count);
            foreach (var id in order)
            {
                if (IsHandled(id.Item2))
                    result.Add(rows[id]);
            }

            foreach (var id in order)
            {
                if (!IsHandled(id.Item2))
                    result.Add(rows[id]);
            }

            return result;
        }
    }
}
