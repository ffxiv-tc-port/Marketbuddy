using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Marketbuddy
{
    // 本檔契約：不引用 Dalamud（離線測試直接編它）。
    // 兩台之間共享的單位是「市場觀測」（某世界某道具一次查價的事實）。我方價（OurPrice）與
    // 「最低價是不是我的」（LowestIsOurs）是單邊視角，不上線；收到的一方用自己的清單與僱員在本地算。

    /// <summary>某個世界某件道具一次查價的結果。價格 -1＝沒有掛單或不知道；賣家僱員 0＝不知道。</summary>
    /// <param name="Sales">同一次查詢回來的成交紀錄摘要；舊檔、舊對端、快取命中都是「不知道」（<see cref="SaleSummary.Known"/>＝false）。</param>
    internal readonly record struct MarketObservation(
        DateTime AtUtc,
        uint WorldId,
        string WorldName,
        uint ItemId,
        long LowestNq,
        ulong LowestNqRetainer,
        long LowestHq,
        ulong LowestHqRetainer,
        int ListingCount,
        string Source,
        int GateIntervalMs,
        string Verdict,
        SaleSummary Sales = default)
    {
        /// <summary>這一次真的問到答案（有人賣或確認沒人賣）。</summary>
        public bool Answered => Verdict is "ok" or "empty";

        /// <summary>問到答案、沒人在賣，而且這一頁成交紀錄確認是空的（成交不知道時是 false）。</summary>
        public bool NothingAtAll => Verdict == "empty" && Sales.Known && Sales.PageCount == 0;
    }

    /// <summary>
    /// 市場查詢附帶的成交紀錄（遊戲一頁最多 20 筆，由新到舊）的摘要。
    /// <para>🔑 <see cref="Known"/>＝false 時其餘欄位一律無意義（＝不知道，不是 0）。價格 -1／時間 MinValue＝這一頁沒有那個品質的成交。</para>
    /// </summary>
    internal readonly record struct SaleSummary(
        bool Known,
        long LastNq,
        DateTime LastNqUtc,
        long LastHq,
        DateTime LastHqUtc,
        int RecentCount,
        int PageCount)
    {
        internal const int RecentDays = 7;
        internal const int MaxPageCount = 100;

        internal static SaleSummary FromPage(IEnumerable<(long UnitPrice, bool Hq, DateTime AtUtc)> sales, DateTime nowUtc)
        {
            long lastNq = -1, lastHq = -1;
            DateTime nqAt = DateTime.MinValue, hqAt = DateTime.MinValue;
            int recent = 0, page = 0;
            var cutoff = nowUtc.AddDays(-RecentDays);
            foreach (var (price, hq, at) in sales)
            {
                if (page >= MaxPageCount)
                    break;
                page++;
                if (at >= cutoff)
                    recent++;
                if (hq && at > hqAt)
                {
                    hqAt = at;
                    lastHq = price;
                }
                else if (!hq && at > nqAt)
                {
                    nqAt = at;
                    lastNq = price;
                }
            }

            return new SaleSummary(true, lastNq, nqAt, lastHq, hqAt, recent, page);
        }
    }

    /// <summary>主機推給另一台的清單裡的一件。</summary>
    internal readonly record struct SharedListItem(uint ItemId, bool Hq);

    /// <summary>主機推過去的清單（已驗證）。</summary>
    internal sealed class SharedList
    {
        public required string ListId;
        public required DateTime ExportedUtc;
        public required string SourceKey;
        public required List<SharedListItem> Items;

        /// <summary>主機的排除世界；代掃時與這台自己的排除清單取聯集。</summary>
        public required HashSet<uint> ExcludedWorlds;

        /// <summary>主機那份世界進度（給「去資料最舊的世界」看兩邊合起來的新舊）。ContentId 一律 0。</summary>
        public required List<SurveyWorldRow> HostWorlds;
    }

    internal static class SurveyShare
    {
        /// <summary>＝<c>Configuration.MAX_PRICE</c>（本檔不引用設定類別）。</summary>
        internal const long MaxPrice = 999_999_999;

        /// <summary>
        /// 一份清單最多幾件。🔑 留給「全伺服器掃描」：台服可上市場的道具約 1.6 萬件，
        /// 清單分批傳（<see cref="ListChunkItems"/>），總量不卡在單則封包上限。
        /// </summary>
        internal const int MaxListItems = 60_000;

        internal const int ListChunkItems = 5_000;
        internal const int MaxObservationBatch = 2_000;
        internal const int MaxWorldsPerMessage = 64;
        internal const int MaxIdLength = 48;
        internal const int MaxWorldNameLength = 64;
        internal const int MaxListingCount = 10_000;
        internal const int MaxGateIntervalMs = 600_000;
        internal const int MaxPlannedTotal = 1_000_000;
        internal const int MaxReasonLength = 300;

        private static readonly DateTime EarliestPlausibleUtc = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>同一件道具在同一個世界、同一秒的觀測＝同一筆。</summary>
        internal static DateTime TruncateToSecond(DateTime utc)
            => new(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);

        /// <summary>
        /// 一筆觀測換成「我方」記錄列：用<b>本地</b>的清單價與<b>本地</b>的僱員判斷最低價是不是自己的。
        /// 規則逐字照巡檢原本的寫法：最低價不知道、或不知道我方有哪些僱員時寫 -1，不寫 0。
        /// </summary>
        internal static PriceSurveyRow ToRow(MarketObservation o, bool hq, long ourPrice, IReadOnlySet<ulong> ownRetainers)
        {
            var lowest = hq ? o.LowestHq : o.LowestNq;
            var lowestRetainer = hq ? o.LowestHqRetainer : o.LowestNqRetainer;
            var lowestIsOurs = lowest < 0 || ownRetainers.Count == 0
                ? -1
                : ownRetainers.Contains(lowestRetainer) ? 1 : 0;
            return new PriceSurveyRow(o.AtUtc, o.WorldId, o.WorldName, o.ItemId, hq, ourPrice,
                o.LowestNq, o.LowestHq, lowestIsOurs, o.ListingCount, o.Source, o.GateIntervalMs, o.Verdict);
        }

        /// <summary>
        /// 一批觀測換成本地記錄列：只有本地清單上有的 (道具, 品質) 才產生列——
        /// 沒在賣的東西寫進 price_survey.csv 只會多出一堆「我方價不知道」的幽靈列。
        /// </summary>
        internal static List<PriceSurveyRow> ToRows(IEnumerable<MarketObservation> observations,
            IReadOnlyDictionary<uint, List<(bool Hq, long OurPrice)>> localList, IReadOnlySet<ulong> ownRetainers)
        {
            var rows = new List<PriceSurveyRow>();
            foreach (var o in observations)
            {
                if (!localList.TryGetValue(o.ItemId, out var mine))
                    continue;
                foreach (var (hq, ourPrice) in mine)
                    rows.Add(ToRow(o, hq, ourPrice, ownRetainers));
            }

            return rows;
        }

        // =====================================================================
        //  不受信任輸入的驗證
        // =====================================================================

        /// <returns>null＝通過；否則是拒收原因代碼。</returns>
        internal static string? Validate(in MarketObservation o, Func<uint, bool>? isKnownItem,
            Func<uint, bool>? isKnownWorld, DateTime nowUtc)
        {
            if (o.AtUtc < EarliestPlausibleUtc || o.AtUtc > nowUtc.AddDays(1))
                return "bad-time";
            if (o.WorldId == 0 || (isKnownWorld != null && !isKnownWorld(o.WorldId)))
                return "unknown-world";
            if (!IsPlainName(o.WorldName))
                return "bad-name";
            if (o.ItemId == 0 || (isKnownItem != null && !isKnownItem(o.ItemId)))
                return "unknown-item";
            if (!IsPrice(o.LowestNq) || !IsPrice(o.LowestHq))
                return "bad-price";
            if (o.ListingCount < -1 || o.ListingCount > MaxListingCount)
                return "bad-count";
            if (o.Source is not ("live" or "cache"))
                return "bad-source";
            if (o.GateIntervalMs < 0 || o.GateIntervalMs > MaxGateIntervalMs)
                return "bad-gate";
            if (o.Verdict is not ("ok" or "empty" or "refused" or "timeout"))
                return "bad-verdict";
            return Validate(o.Sales, nowUtc);
        }

        /// <returns>null＝通過；否則是拒收原因代碼。不知道的摘要一律通過（內容不會被讀）。</returns>
        internal static string? Validate(in SaleSummary s, DateTime nowUtc)
        {
            if (!s.Known)
                return null;
            if (!IsPrice(s.LastNq) || !IsPrice(s.LastHq) || s.LastNq == 0 || s.LastHq == 0)
                return "bad-sales";
            if (!IsSaleTime(s.LastNqUtc, s.LastNq, nowUtc) || !IsSaleTime(s.LastHqUtc, s.LastHq, nowUtc))
                return "bad-sales";
            if (s.PageCount < 0 || s.PageCount > SaleSummary.MaxPageCount || s.RecentCount < 0 || s.RecentCount > s.PageCount)
                return "bad-sales";
            return null;
        }

        private static bool IsSaleTime(DateTime at, long price, DateTime nowUtc)
            => price < 0
                ? at == DateTime.MinValue
                : at >= new DateTime(2013, 1, 1, 0, 0, 0, DateTimeKind.Utc) && at <= nowUtc.AddDays(1);

        /// <returns>null＝通過；否則是拒收原因代碼。</returns>
        internal static string? Validate(in SurveyWorldRow w, Func<uint, bool>? isKnownWorld, DateTime nowUtc)
        {
            if (w.AtUtc < EarliestPlausibleUtc || w.AtUtc > nowUtc.AddDays(1))
                return "bad-time";
            if (w.WorldId == 0 || (isKnownWorld != null && !isKnownWorld(w.WorldId)))
                return "unknown-world";
            if (!IsPlainName(w.WorldName))
                return "bad-name";
            if (w.PlannedTotal < 0 || w.PlannedTotal > MaxPlannedTotal || w.Surveyed < 0 || w.Surveyed > w.PlannedTotal)
                return "bad-progress";
            return null;
        }

        internal static bool IsPrice(long price) => price >= -1 && price <= MaxPrice;

        internal static bool IsPlainName(string? s)
        {
            if (s == null || s.Length > MaxWorldNameLength)
                return false;
            foreach (var c in s)
            {
                if (char.IsControl(c))
                    return false;
            }

            return true;
        }

        /// <summary>收到的原因文字只拿來顯示：夾長度、去掉控制字元。</summary>
        internal static string CleanText(string? s)
        {
            if (string.IsNullOrEmpty(s))
                return string.Empty;
            var chars = new List<char>(Math.Min(s.Length, MaxReasonLength));
            foreach (var c in s)
            {
                if (chars.Count >= MaxReasonLength)
                    break;
                chars.Add(char.IsControl(c) ? ' ' : c);
            }

            return new string(chars.ToArray());
        }

        // =====================================================================
        //  識別碼與位址
        // =====================================================================

        internal static string NewListId(DateTime utc, int random)
            => utc.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-" +
               (random & 0xFFFF).ToString("x4", CultureInfo.InvariantCulture);

        /// <summary>代號只准英數、連字號、底線（會進 log 與狀態列）。</summary>
        internal static bool IsSafeId(string? id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > MaxIdLength)
                return false;
            foreach (var c in id)
            {
                if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '-' or '_'))
                    return false;
            }

            return true;
        }

        internal static string NormaliseSourceKey(string? key)
            => key is "csv" or "allagantools" or "sell-list" ? key : "imported";

        /// <summary>
        /// 只收私有位址：RFC1918（10/8、172.16/12、192.168/16）與本機迴路（127/8、::1）。
        /// IPv4 對應的 IPv6 位址先還原再判。
        /// </summary>
        internal static bool IsPrivateAddress(IPAddress? address)
        {
            if (address == null)
                return false;
            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();
            if (IPAddress.IsLoopback(address))
                return true;
            if (address.AddressFamily != AddressFamily.InterNetwork)
                return false;
            var b = address.GetAddressBytes();
            return b[0] == 10
                   || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                   || (b[0] == 192 && b[1] == 168);
        }

        /// <summary>解析「IPv4[:埠]」。不接受主機名稱：插件不做名稱解析。</summary>
        internal static bool TryParseEndpoint(string? text, int defaultPort, out IPEndPoint endpoint)
        {
            endpoint = null!;
            var s = text?.Trim() ?? string.Empty;
            if (s.Length == 0)
                return false;
            var port = defaultPort;
            var colon = s.LastIndexOf(':');
            if (colon > 0)
            {
                if (!int.TryParse(s[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port)
                    || port is < 1 or > 65535)
                    return false;
                s = s[..colon];
            }

            if (!IPAddress.TryParse(s, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
                return false;
            endpoint = new IPEndPoint(address, port);
            return true;
        }

        // =====================================================================
        //  遠端開始的閘門（純判斷；遊戲狀態由呼叫端讀好帶進來）
        // =====================================================================

        /// <summary>
        /// 主機要求「開始一輪」時，這台能不能接。回 null＝可以；否則是要傳回主機畫面的原因。
        /// 順序就是優先序：使用者沒同意永遠排第一。
        /// </summary>
        internal static string? RemoteStartBlock(bool consent, bool listMatches, string? busyReason,
            string? alreadyRunningReason, string? canArmReason, string? canStartReason)
        {
            if (!consent)
                return "consent";
            if (!listMatches)
                return "list";
            if (!string.IsNullOrEmpty(busyReason))
                return busyReason;
            if (!string.IsNullOrEmpty(alreadyRunningReason))
                return alreadyRunningReason;
            if (!string.IsNullOrEmpty(canArmReason))
                return canArmReason;
            if (!string.IsNullOrEmpty(canStartReason))
                return canStartReason;
            return null;
        }
    }
}
