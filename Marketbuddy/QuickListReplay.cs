namespace Marketbuddy
{
    /// <summary>一格背包在某一刻的樣子：容器、格號、道具 id（不含 HQ 旗標）、數量、是不是 HQ。</summary>
    internal readonly record struct ReplaySlot(int Container, int Slot, uint ItemId, int Quantity, bool Hq);

    internal enum ReplayVerdict
    {
        /// <summary>還在等上一個選單的守衛紀錄解除。</summary>
        Wait,

        /// <summary>這一幀可以在同一份選單上選出售。</summary>
        Fire,

        /// <summary>放棄補送；理由在 out 參數。</summary>
        Abandon,
    }

    /// <summary>
    /// 快速上架被選單守衛擋下後的補送判斷（純函式，不碰遊戲狀態）。
    /// 契約：補送只對「使用者那一次右鍵開出、到現在還開著的同一份選單」選一次出售；
    /// 期限內、沒有別的狀態擋著、沒有使用者輸入、選單仍是為同一格建的、那一格仍是同一件同數量同品質，
    /// 守衛也放行了才回 <see cref="ReplayVerdict.Fire"/>。任何一項不成立就放棄，不重試、不改補別件。
    /// </summary>
    internal static class QuickListReplay
    {
        /// <summary>從被擋下那一刻起最多等這麼久。</summary>
        internal const int WindowMs = 1000;

        /// <param name="blockedBy">非 null＝別的狀態擋著（出售清單關了、別的自動化在跑等），直接放棄。</param>
        /// <param name="menuOpen">同一份選單（同一個位址）這一幀還看得見。</param>
        /// <param name="menuContainer">選單現在的目標容器（遊戲 agent 記的）。</param>
        /// <param name="current">那一格現在的樣子；null＝讀不到或空格。</param>
        internal static ReplayVerdict Decide(in ReplaySlot recorded, double elapsedMs, string? blockedBy, bool userInput,
            bool menuOpen, int menuContainer, int menuSlot, ReplaySlot? current, bool guardAllows, out string reason)
        {
            reason = string.Empty;
            if (elapsedMs > WindowMs)
            {
                reason = $"等了 {WindowMs} ms 上一個選單的守衛紀錄還沒解除";
                return ReplayVerdict.Abandon;
            }

            if (blockedBy != null)
            {
                reason = blockedBy;
                return ReplayVerdict.Abandon;
            }

            if (userInput)
            {
                reason = "等待期間有滑鼠左鍵／Esc／確定鍵／手把輸入";
                return ReplayVerdict.Abandon;
            }

            if (!menuOpen)
            {
                reason = "右鍵選單已經關了或換了一扇";
                return ReplayVerdict.Abandon;
            }

            if (menuContainer != recorded.Container || menuSlot != recorded.Slot)
            {
                reason = $"右鍵選單現在是別格的（{menuContainer}#{menuSlot}）";
                return ReplayVerdict.Abandon;
            }

            if (current is not { } now || now != recorded)
            {
                reason = current is { } c
                    ? $"那一格已經不是同一件同數量（現在=#{c.ItemId}×{c.Quantity}{(c.Hq ? " HQ" : string.Empty)}）"
                    : "那一格已經空了或讀不到";
                return ReplayVerdict.Abandon;
            }

            return guardAllows ? ReplayVerdict.Fire : ReplayVerdict.Wait;
        }
    }
}
