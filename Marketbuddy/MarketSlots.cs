using System;
using System.Collections.Generic;
using System.Text;

namespace Marketbuddy
{
    // 本檔只有純資料與純函式：不讀遊戲記憶體、不碰 Dalamud，離線測試直接編這個檔（加 MarketFullPeers.cs、Localization.cs）。

    /// <summary>
    /// 一名僱員市場格數的已知來源（都不發請求）。<paramref name="Listed"/>＝本插件看過的出售清單記下的掛單格數，null＝不知道；
    /// 記錄之後的賣出看不到，所以只會多算、不會少算。<paramref name="Counter"/>＝遊戲僱員清單上的計數，已知落後於上架，
    /// 只放進滑鼠提示，不拿來判斷有沒有空位。
    /// </summary>
    internal readonly record struct RetainerSlots(ulong RetainerId, string Name, int? Listed, DateTime? RecordedAtUtc, int? Counter);

    internal static class MarketSlots
    {
        /// <summary>空幾格；不知道（沒有紀錄或上限讀不到）回 null，不回 0。</summary>
        internal static int? Free(int? listed, int capacity)
            => listed is { } n && capacity > 0 ? Math.Max(0, capacity - n) : null;

        /// <summary>確知已滿才是 true；不知道一律 false（不因為不知道而停用按鈕或跳過僱員）。</summary>
        internal static bool KnownFull(int? listed, int capacity) => Free(listed, capacity) == 0;

        /// <summary>要拜訪的僱員在 <paramref name="retainers"/> 裡的索引（照原順序）：確知已滿的不去，有空位與不知道的都去。</summary>
        internal static List<int> PlanVisits(IReadOnlyList<RetainerSlots> retainers, int capacity)
        {
            var visits = new List<int>(retainers.Count);
            for (var i = 0; i < retainers.Count; i++)
            {
                if (!KnownFull(retainers[i].Listed, capacity))
                    visits.Add(i);
            }

            return visits;
        }

        /// <summary>列上那一行：「其他僱員空位：A 3、B ?、C 0」。沒有其他僱員回空字串；<paramref name="peers"/> 為 null＝僱員資料還沒載入。</summary>
        internal static string PeersLine(IReadOnlyList<RetainerSlots>? peers, int capacity)
        {
            if (peers == null)
                return "Other retainers' free slots: ??".Loc("?");
            if (peers.Count == 0)
                return string.Empty;

            var parts = new StringBuilder();
            foreach (var peer in peers)
            {
                if (parts.Length > 0)
                    parts.Append(" / ");
                parts.Append(NameOf(peer)).Append(' ');
                parts.Append(Free(peer.Listed, capacity) is { } free ? free.ToString() : "?");
            }

            return "Other retainers' free slots: ??".Loc(parts.ToString());
        }

        /// <summary>滑鼠提示：每名一行，帶紀錄時刻；不知道的那名附上遊戲計數並說明為什麼不採用。</summary>
        internal static string PeersTooltip(IReadOnlyList<RetainerSlots>? peers, int capacity, DateTime nowUtc)
        {
            if (peers == null)
                return "Retainer data is not loaded yet.".Loc();
            if (peers.Count == 0)
                return "This character has no other retainers.".Loc();

            var lines = new StringBuilder();
            foreach (var peer in peers)
            {
                if (lines.Length > 0)
                    lines.Append('\n');
                var name = NameOf(peer);
                if (Free(peer.Listed, capacity) is { } free && peer.RecordedAtUtc is { } at)
                    lines.Append("??: ?? free (recorded ??)".Loc(name, free, MarketFullPeers.Age(at, nowUtc)));
                else if (peer.Counter is { } counted)
                    lines.Append("??: not known (the retainer list says ?? listed, which can lag behind)".Loc(name, counted));
                else
                    lines.Append("??: unknown".Loc(name));
            }

            return lines.ToString();
        }

        private static string NameOf(RetainerSlots r) => r.Name.Length > 0 ? r.Name : "?";
    }
}
