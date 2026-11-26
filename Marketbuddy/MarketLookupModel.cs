using System;
using System.Collections.Generic;
using System.Globalization;

namespace Marketbuddy
{
    // 本檔契約：不引用 Dalamud（離線測試直接編它）；不做 I/O、不寫 log。
    // 「不知道」一律是 Unknown（畫面上的 ?），「問到了、沒有」是 None（畫面上的 —），絕不寫成 0。

    /// <summary>可上市場道具的一件：名稱與比對用的折疊字串。</summary>
    internal readonly record struct LookupCatalogEntry(uint ItemId, string Name, string Folded);

    /// <summary>行情查詢視窗的搜尋：道具名稱包含比對，或道具 id。</summary>
    internal static class MarketLookupSearch
    {
        /// <summary>比對用：<see cref="ItemNameMatch.Normalize"/> 後轉小寫（中文不受影響）。</summary>
        internal static string Fold(string? text) => ItemNameMatch.Normalize(text).ToLowerInvariant();

        /// <summary>
        /// 命中的索引，依序：id 相同、名稱相同、名稱開頭相同、名稱包含；同一級照 id。最多 <paramref name="max"/> 筆，空查詢回空。
        /// </summary>
        internal static List<int> Filter(IReadOnlyList<LookupCatalogEntry> catalog, string? query, int max, out int totalMatches)
        {
            totalMatches = 0;
            var result = new List<int>();
            var q = Fold(query);
            if (q.Length == 0 || max <= 0)
                return result;

            var hasId = uint.TryParse(q, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id != 0;
            var ranked = new List<(int Rank, uint ItemId, int Index)>();
            for (var i = 0; i < catalog.Count; i++)
            {
                var e = catalog[i];
                int rank;
                if (hasId && e.ItemId == id)
                    rank = 0;
                else if (e.Folded.Length == 0)
                    continue;
                else if (string.Equals(e.Folded, q, StringComparison.Ordinal))
                    rank = 1;
                else if (e.Folded.StartsWith(q, StringComparison.Ordinal))
                    rank = 2;
                else if (e.Folded.Contains(q, StringComparison.Ordinal))
                    rank = 3;
                else
                    continue;
                ranked.Add((rank, e.ItemId, i));
            }

            totalMatches = ranked.Count;
            ranked.Sort(static (a, b) => a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank) : a.ItemId.CompareTo(b.ItemId));
            for (var i = 0; i < ranked.Count && i < max; i++)
                result.Add(ranked[i].Index);
            return result;
        }
    }

    internal enum LookupOwner
    {
        /// <summary>分不出來：沒有資料、賣家不知道，或不知道自家有哪些僱員。</summary>
        Unknown = 0,
        Other = 1,
        Own = 2,
        /// <summary>這個品質沒有人在賣。</summary>
        NoListing = 3,
    }

    /// <summary>行情查詢的一列（一個世界）。<paramref name="HasData"/>＝false 時其餘欄位無意義（整列都是 ?）。</summary>
    internal readonly record struct LookupRow(uint WorldId, string WorldName, bool HasData, MarketObservation Obs, bool Stale);

    internal static class MarketLookupModel
    {
        internal const string Unknown = "?";
        internal const string None = "—";

        /// <summary>資料比這個舊就畫淡色、也不拿來挑最便宜的世界（除非每個世界都這麼舊）。</summary>
        internal const int StaleHours = 24;

        /// <summary>
        /// 每個世界一列：資料中心的世界都列（沒資料的整列 ?），再加上不在名單上但有觀測的世界。
        /// 有資料的照世界名排在前面，沒資料的排後面。
        /// </summary>
        internal static List<LookupRow> BuildRows(IReadOnlyList<(uint WorldId, string Name)> dataCentreWorlds,
            IReadOnlyList<MarketObservation> latest, DateTime nowUtc, int staleHours)
        {
            var byWorld = new Dictionary<uint, MarketObservation>();
            foreach (var o in latest)
            {
                if (o.WorldId == 0)
                    continue;
                if (!byWorld.TryGetValue(o.WorldId, out var known) || o.AtUtc > known.AtUtc)
                    byWorld[o.WorldId] = o;
            }

            var rows = new List<LookupRow>(Math.Max(dataCentreWorlds.Count, byWorld.Count));
            var listed = new HashSet<uint>();
            foreach (var (worldId, name) in dataCentreWorlds)
            {
                if (worldId == 0 || !listed.Add(worldId))
                    continue;
                rows.Add(byWorld.TryGetValue(worldId, out var o)
                    ? new LookupRow(worldId, name.Length > 0 ? name : o.WorldName, true, o, IsStale(o.AtUtc, nowUtc, staleHours))
                    : new LookupRow(worldId, name, false, default, false));
            }

            foreach (var (worldId, o) in byWorld)
            {
                if (listed.Add(worldId))
                    rows.Add(new LookupRow(worldId, o.WorldName, true, o, IsStale(o.AtUtc, nowUtc, staleHours)));
            }

            rows.Sort(static (a, b) => a.HasData != b.HasData
                ? (a.HasData ? -1 : 1)
                : string.CompareOrdinal(a.WorldName, b.WorldName));
            return rows;
        }

        internal static bool IsStale(DateTime atUtc, DateTime nowUtc, int staleHours)
            => atUtc == DateTime.MinValue || atUtc < nowUtc.AddHours(-staleHours);

        /// <summary>這個品質的最低價；沒資料 -1（Unknown），問到了但沒人賣也是 -1（看 <see cref="PriceText"/> 分辨）。</summary>
        internal static long Lowest(in LookupRow row, bool hq)
            => !row.HasData ? -1 : hq ? row.Obs.LowestHq : row.Obs.LowestNq;

        internal static string PriceText(in LookupRow row, bool hq)
        {
            if (!row.HasData)
                return Unknown;
            var price = Lowest(row, hq);
            return price > 0 ? price.ToString("N0", CultureInfo.InvariantCulture) : None;
        }

        /// <summary>最低那筆是不是自家僱員掛的。</summary>
        internal static LookupOwner OwnerOf(in LookupRow row, bool hq, IReadOnlySet<ulong>? ownRetainers)
        {
            if (!row.HasData)
                return LookupOwner.Unknown;
            if (Lowest(row, hq) <= 0)
                return LookupOwner.NoListing;
            var seller = hq ? row.Obs.LowestHqRetainer : row.Obs.LowestNqRetainer;
            if (seller == 0 || ownRetainers == null || ownRetainers.Count == 0)
                return LookupOwner.Unknown;
            return ownRetainers.Contains(seller) ? LookupOwner.Own : LookupOwner.Other;
        }

        /// <summary>
        /// 這個品質最便宜的那一列；先只看不舊的，都舊才看舊的。同價取資料新的。沒有回 -1。
        /// </summary>
        internal static int Cheapest(IReadOnlyList<LookupRow> rows, bool hq)
        {
            var best = PickCheapest(rows, hq, allowStale: false);
            return best >= 0 ? best : PickCheapest(rows, hq, allowStale: true);
        }

        private static int PickCheapest(IReadOnlyList<LookupRow> rows, bool hq, bool allowStale)
        {
            var best = -1;
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (!row.HasData || (row.Stale && !allowStale))
                    continue;
                var price = Lowest(row, hq);
                if (price <= 0)
                    continue;
                if (best < 0)
                {
                    best = i;
                    continue;
                }

                var bestPrice = Lowest(rows[best], hq);
                if (price < bestPrice || (price == bestPrice && row.Obs.AtUtc > rows[best].Obs.AtUtc))
                    best = i;
            }

            return best;
        }

        internal static string CountText(in LookupRow row)
            => !row.HasData || row.Obs.ListingCount < 0
                ? Unknown
                : row.Obs.ListingCount.ToString(CultureInfo.InvariantCulture);

        /// <summary>這一頁成交紀錄裡最新的一筆（兩種品質取較新的）；成交不知道或這一頁沒有成交時回 false。</summary>
        internal static bool TryLastSale(in LookupRow row, out long price, out DateTime atUtc, out bool hq)
        {
            price = -1;
            atUtc = DateTime.MinValue;
            hq = false;
            if (!row.HasData || !row.Obs.Sales.Known)
                return false;
            var s = row.Obs.Sales;
            var nqOk = s.LastNq > 0 && s.LastNqUtc != DateTime.MinValue;
            var hqOk = s.LastHq > 0 && s.LastHqUtc != DateTime.MinValue;
            if (!nqOk && !hqOk)
                return false;
            hq = hqOk && (!nqOk || s.LastHqUtc > s.LastNqUtc);
            price = hq ? s.LastHq : s.LastNq;
            atUtc = hq ? s.LastHqUtc : s.LastNqUtc;
            return true;
        }

        /// <summary>最近成交價：成交不知道 ?；知道但這一頁沒有成交 —。</summary>
        internal static string LastSaleText(in LookupRow row)
        {
            if (!row.HasData || !row.Obs.Sales.Known)
                return Unknown;
            return TryLastSale(row, out var price, out _, out _)
                ? price.ToString("N0", CultureInfo.InvariantCulture)
                : None;
        }

        /// <summary>
        /// 近 7 天成交筆數。遊戲一次只給一頁（最多 20 筆）：整頁都在 7 天內時真實筆數可能更多，寫成「N+」。
        /// </summary>
        internal static string RecentSalesText(in LookupRow row)
        {
            if (!row.HasData || !row.Obs.Sales.Known)
                return Unknown;
            var s = row.Obs.Sales;
            var n = s.RecentCount.ToString(CultureInfo.InvariantCulture);
            return RecentSalesCapped(s) ? n + "+" : n;
        }

        internal const int GamePageSize = 20;

        internal static bool RecentSalesCapped(in SaleSummary s)
            => s.Known && s.PageCount >= GamePageSize && s.RecentCount >= s.PageCount;
    }
}
