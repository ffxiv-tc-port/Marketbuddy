using System;
using System.Collections.Generic;
using System.Text;

namespace Marketbuddy
{
    // 本檔只有純函式：不讀遊戲記憶體、不碰 Dalamud，離線探針直接編這一個檔。

    /// <summary>
    /// 畫面上顯示的道具名 ↔ 道具表名稱的比對。
    /// 台服道具表有些列 <c>Singular</c> 與 <c>Name</c> 寫法不同（用字、全形／半形空白），
    /// 上架視窗顯示的是其中一種，所以兩種都當候選。
    /// </summary>
    internal static class ItemNameMatch
    {
        /// <summary>
        /// 全形空白與不換行空白當成半形空白、去掉私用區圖示字元（優質圖示等）、去掉前後空白。
        /// 其餘逐字保留：這是相等比對的前置，不是模糊比對。
        /// </summary>
        public static string Normalize(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            var sb = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                if (c >= '\uE000' && c <= '\uF8FF')
                    continue;
                sb.Append(c is '\u3000' or '\u00A0' ? ' ' : c);
            }

            return sb.ToString().Trim();
        }

        /// <summary>
        /// 正規化後不重複、非空的候選名，<c>Singular</c> 排第一（上架視窗顯示的是它）；兩欄都空時回空陣列。
        /// </summary>
        public static string[] Candidates(string? name, string? singular)
        {
            var s = Normalize(singular);
            var n = Normalize(name);
            if (s.Length == 0)
                return n.Length == 0 ? [] : [n];
            if (n.Length == 0 || string.Equals(s, n, StringComparison.Ordinal))
                return [s];
            return [s, n];
        }

        /// <summary>
        /// 顯示名正規化後與候選逐字相等的位置：0＝第一候選（Singular），1＝其他候選，-1＝不相等。
        /// 刻意不用子字串比對：「奶油」會配上「奶油雞肉寬麵」的視窗，連按時把視窗配給錯的道具。
        /// </summary>
        public static int MatchRank(IReadOnlyList<string> candidates, string? shownName)
        {
            var shown = Normalize(shownName);
            if (shown.Length == 0)
                return -1;
            for (var i = 0; i < candidates.Count; i++)
            {
                if (string.Equals(candidates[i], shown, StringComparison.Ordinal))
                    return i == 0 ? 0 : 1;
            }

            return -1;
        }

        public static bool Matches(IReadOnlyList<string> candidates, string? shownName)
            => MatchRank(candidates, shownName) >= 0;
    }
}
