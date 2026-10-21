using System;

namespace Marketbuddy
{
    /// <summary>一筆已按下確認、市場格還沒出現的上架（或在延後確認期間視窗被關掉、沒送出的那一筆）。</summary>
    internal readonly record struct InFlightListing(ulong RetainerId, DateTime ConfirmedAt, bool Sent);

    internal enum ConfirmGateVerdict
    {
        ConfirmNow,
        Wait,
        /// <summary>等到上限還沒等到：照舊送出。</summary>
        CapReached,
    }

    /// <summary>
    /// 快速上架「確認」要不要先等：同一名僱員還有更早送出、市場格還沒出現的上架就等，最多 <see cref="WaitCapMs"/>。
    /// 純函式，呼叫端當幀把等待清單抄進來；不認得僱員（0）一律不等。
    /// </summary>
    internal static class QuickListConfirmGate
    {
        internal const int WaitCapMs = 3000;

        // 本 pin 的 AtkEventType：MouseMove 5、MouseOver 6、MouseOut 7、FocusStart 18、FocusStop 19、ButtonRelease 24。
        private const byte MouseMove = 5, MouseOver = 6, MouseOut = 7, FocusStart = 18, FocusStop = 19, ButtonRelease = 24;

        /// <summary>
        /// 延後確認期間上架視窗收到這個事件時要不要放棄自動確認。只有懸停／焦點類不放棄；
        /// 其餘（含未知值）一律放棄：使用者可能已自己按了確認，再按一次就是對已送出的窗重送。
        /// ButtonRelease 單純滑鼠移出按鈕也會送；真的點擊一定先有 ButtonPress（23），所以排除它不會漏掉點擊。
        /// </summary>
        internal static bool ShouldAbandonOnEvent(byte atkEventType)
            => atkEventType is not (MouseMove or MouseOver or MouseOut or FocusStart or FocusStop or ButtonRelease);

        /// <summary>延後確認送出前：背包原格當幀還是這件道具才按（空格或換了道具＝已被搬走，再按是對空的來源上架）。</summary>
        internal static bool SourceStillHoldsItem(uint currentItemId, uint expectedItemId)
            => currentItemId != 0 && currentItemId == expectedItemId;

        /// <summary>鍵盤上會被遊戲當成「確定」的鍵（VirtualKey）：Return 13、Space 32、NumPad0 96。</summary>
        internal static readonly int[] ConfirmKeys = [13, 32, 96];

        /// <summary>
        /// 鍵盤／手把確定可能不經過上架視窗的 ReceiveEvent：延後中只要手把任一鈕或確定鍵按著（level）就放棄。
        /// <paramref name="gamepadHeldMask"/> 是所有按著的手把鈕；<paramref name="heldKeys"/> 是按著的鍵碼。
        /// </summary>
        internal static bool ShouldAbandonOnInput(ushort gamepadHeldMask, ReadOnlySpan<int> heldKeys)
        {
            if (gamepadHeldMask != 0)
                return true;
            foreach (var key in heldKeys)
            {
                if (Array.IndexOf(ConfirmKeys, key) >= 0)
                    return true;
            }

            return false;
        }

        /// <param name="waitingSince">這一筆開始等的時刻；之後才送出的不擋它。</param>
        /// <param name="blocking">擋住它的筆數（只算同一名僱員、真的送出過、在它之前送出的）。</param>
        internal static ConfirmGateVerdict Decide(ReadOnlySpan<InFlightListing> inFlight, ulong retainerId,
            DateTime waitingSince, DateTime now, out int blocking)
        {
            blocking = 0;
            if (retainerId == 0)
                return ConfirmGateVerdict.ConfirmNow;
            foreach (var listing in inFlight)
            {
                if (listing.Sent && listing.RetainerId == retainerId && listing.ConfirmedAt <= waitingSince)
                    blocking++;
            }

            if (blocking == 0)
                return ConfirmGateVerdict.ConfirmNow;
            return (now - waitingSince).TotalMilliseconds >= WaitCapMs
                ? ConfirmGateVerdict.CapReached
                : ConfirmGateVerdict.Wait;
        }
    }

    /// <summary>
    /// 快速上架自己的上架視窗：按了確認等它關的那扇，以及延後確認放棄後交給使用者、還開著的那扇。
    /// 兩者都算「快速上架開的窗」，引擎不可把它當成使用者手動改價。位址只做等值比較，不解參考；框架執行緒專用。
    /// </summary>
    internal sealed class SellWindowOwnership
    {
        private nint confirmed;
        private nint handedOver;

        internal bool AnyOpen => confirmed != 0 || handedOver != 0;

        internal bool HandedOverOpen => handedOver != 0;

        internal void Confirmed(nint addon) => confirmed = addon;

        internal void HandedOver(nint addon) => handedOver = addon;

        /// <summary>新的上架視窗開了：交出去的那扇已經不在（同位址重開也是另一件道具）。</summary>
        internal void OnSetup() => handedOver = 0;

        /// <summary>按了確認那扇：任何一扇上架視窗關掉都清（遊戲同時只有一扇）。交出去那扇只在它自己關掉時清。</summary>
        internal void OnFinalize(nint addon)
        {
            confirmed = 0;
            if (addon == handedOver)
                handedOver = 0;
        }

        /// <summary>每幀：目前那扇上架視窗已經不是交出去的那扇（漏收關窗事件）就清。</summary>
        internal void OnFrame(nint current)
        {
            if (handedOver != 0 && current != handedOver)
                handedOver = 0;
        }

        /// <param name="deferred">延後確認中的那扇；沒有傳 0。</param>
        internal bool Owns(nint addon, nint deferred)
            => addon != 0 && (addon == confirmed || addon == handedOver || addon == deferred);
    }
}
