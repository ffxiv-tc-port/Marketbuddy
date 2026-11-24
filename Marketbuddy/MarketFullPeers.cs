using System;
using System.Collections.Generic;
using System.Text;

namespace Marketbuddy
{
    /// <summary>
    /// 同一個角色的另一名僱員，市場掛單數的兩種已知來源（都不發任何請求）。
    /// <paramref name="SnapshotCount"/>／<paramref name="SnapshotAtUtc"/>：本插件上次看到她出售清單時記下的掛單格數與記錄時刻
    /// （記錄時刻＝內容最後一次變動，之後的賣出看不到）；<paramref name="Counter"/>：遊戲僱員清單上的掛單計數（之後的上架看不到）。
    /// </summary>
    internal readonly record struct PeerRetainer(string Name, int? SnapshotCount, DateTime? SnapshotAtUtc, int? Counter);

    /// <summary>快速上架撞到「市場已滿」時，附在同一則訊息後面的其他僱員空位（純顯示）。</summary>
    internal static class MarketFullPeers
    {
        /// <summary>
        /// 有本插件的紀錄就用紀錄（掛單都要開出售清單，紀錄不會漏掉上架，只會少算之後的賣出），
        /// 沒有才用遊戲的僱員清單計數，兩者都沒有寫「未知」。<paramref name="peers"/> 為 null＝僱員資料還沒載入。
        /// 沒有其他僱員回空字串。
        /// </summary>
        internal static string Describe(IReadOnlyList<PeerRetainer>? peers, int capacity, DateTime nowUtc)
        {
            if (peers == null)
                return " Other retainers' free slots: ??".Loc("unknown".Loc());
            if (peers.Count == 0)
                return string.Empty;

            var parts = new StringBuilder();
            foreach (var peer in peers)
            {
                if (parts.Length > 0)
                    parts.Append(" / ");
                var name = peer.Name.Length > 0 ? peer.Name : "?";
                if (peer.SnapshotCount is { } listed && peer.SnapshotAtUtc is { } at)
                    parts.Append("??: ?? free (recorded ??)".Loc(name, Free(capacity, listed), Age(at, nowUtc)));
                else if (peer.Counter is { } counted)
                    parts.Append("??: about ?? free (retainer list count)".Loc(name, Free(capacity, counted)));
                else
                    parts.Append("??: unknown".Loc(name));
            }

            return " Other retainers' free slots: ??".Loc(parts.ToString());
        }

        private static int Free(int capacity, int listed) => Math.Max(0, capacity - listed);

        internal static string Age(DateTime atUtc, DateTime nowUtc)
        {
            var age = nowUtc - atUtc;
            if (age.TotalMinutes < 1)
                return "just now".Loc();
            if (age.TotalHours < 1)
                return "?? minute(s) ago".Loc((int)age.TotalMinutes);
            if (age.TotalDays < 1)
                return "?? hour(s) ago".Loc((int)age.TotalHours);
            return "?? day(s) ago".Loc((int)age.TotalDays);
        }
    }
}
