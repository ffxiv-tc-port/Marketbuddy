using System;
using Dalamud.Plugin.Services;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    /// <summary>
    /// Single point of contact with AutoRetainer.
    /// - Caches "is AutoRetainer busy" at 1 Hz (callers ask every frame, an IPC
    ///   invoke per frame would be wasteful).
    /// - Suppresses AutoRetainer for the duration of a user-triggered
    ///   Marketbuddy operation (reference counted, restores the previous state
    ///   and never un-suppresses a suppression somebody else owns).
    /// - Raises the "AutoRetainer finished, you can run it now" notice, but
    ///   only when the user was actually blocked before.
    /// Suppression is strictly scoped to operations the user started by hand.
    /// <see cref="MultiCharacterTour"/> 會在 AR 換角前接手跑一輪全僱員重掛。
    /// 那條路徑仍然要使用者手動武裝、一輪跑完就自己解除,不是常駐的事件驅動自動化。
    /// </summary>
    internal static class AutoRetainerBridge
    {
        private const int PollIntervalMs = 1000;

        private static DateTime lastPoll = DateTime.MinValue;
        private static bool busyCached;
        private static bool blockedPending;
        private static int suppressionCount;
        private static bool suppressionOwned;

        /// <summary>
        /// 🔴 <b>AutoRetainer 正把控制權交給我們</b>的期間為 true
        /// （<see cref="MultiCharacterTour"/> 的 character postprocess 接手區間）。
        /// </summary>
        /// <remarks>
        /// 這段期間 AR 不是在跟我們搶,而是<b>停在無限等待上等我們回覆</b>,
        /// 所以本外掛內部所有「AutoRetainer 忙碌中就不要動」的閘門都必須讓開,
        /// 否則巡迴與批次引擎會拒絕起跑 ⇒ 我們永遠做不完 ⇒ AR 永遠等下去。
        /// ⚠️ 這個旗標只由 <see cref="MultiCharacterTour"/> 設定,而且它每一條離開路徑
        /// （成功／跳過／中止／逾時／使用者停止／卸載）都會把它清掉。
        /// </remarks>
        internal static bool ExternalDriveActive { get; set; }

        /// <summary>
        /// Cached busy state (refreshed at 1 Hz from AutoRetainer.IsBusy).
        /// AR 正在等我們做角色收尾時一律回 false,見 <see cref="ExternalDriveActive"/>。
        /// </summary>
        internal static bool IsBusy => busyCached && !ExternalDriveActive;

        /// <summary>True while our own suppression of AutoRetainer is active.</summary>
        internal static bool IsSuppressing => suppressionCount > 0;

        internal static void Init()
        {
            busyCached = IPCManager.IsAutoRetainerBusy();
            lastPoll = DateTime.UtcNow;
            Framework.Update += OnFrameworkUpdate;
        }

        internal static void Shutdown()
        {
            Framework.Update -= OnFrameworkUpdate;
            // Unload must never leave AutoRetainer suppressed by us.
            ForceReleaseSuppression();
            blockedPending = false;
            // 靜態欄位活得比外掛實例久(重載時同一個 AssemblyLoadContext 可能還在),
            // 讓開的閘門不能留到下一次載入。
            ExternalDriveActive = false;
        }

        /// <summary>
        /// Called when a user-triggered operation was refused because
        /// AutoRetainer is busy: arms the one-shot "now available" notice.
        /// </summary>
        internal static void ArmAvailabilityNotice()
        {
            blockedPending = true;
        }

        /// <summary>
        /// Suppresses AutoRetainer while a user-triggered operation runs.
        /// Reference counted; the first acquirer records whether AutoRetainer
        /// was already suppressed by somebody else so that state is preserved.
        /// </summary>
        internal static void AcquireSuppression(string operation)
        {
            suppressionCount++;
            if (suppressionCount > 1)
                return;

            // 🔴 AR 正在等我們做角色收尾時**不要**碰它的全域抑制旗標。
            //    互斥已經由 AR 自己的 TaskManager 提供(它那兩條會搶僱員清單的路徑都寫著
            //    !TaskManager.IsBusy,而它此刻正忙著等我們),多按一個全域旗標只會多出
            //    一條「忘記放開就永久癱瘓 AR」的失敗路徑。
            if (ExternalDriveActive)
            {
                suppressionOwned = false;
                return;
            }

            try
            {
                if (IPCManager.IsAutoRetainerSuppressed())
                {
                    // Somebody else owns the suppression: leave it alone.
                    suppressionOwned = false;
                    Log.Debug($"AutoRetainerBridge: {operation} - AutoRetainer already suppressed by someone else, not touching it");
                    return;
                }

                suppressionOwned = IPCManager.SetAutoRetainerSuppressed(true);
                if (suppressionOwned)
                    Log.Debug($"AutoRetainerBridge: suppressed AutoRetainer for {operation}");
            }
            catch (Exception e)
            {
                suppressionOwned = false;
                Log.Warning(e, "AutoRetainerBridge: could not suppress AutoRetainer");
            }
        }

        /// <summary>Releases one suppression reference; restores AutoRetainer when the last one goes.</summary>
        internal static void ReleaseSuppression()
        {
            if (suppressionCount == 0)
                return;

            suppressionCount--;
            if (suppressionCount > 0)
                return;

            ForceReleaseSuppression();
        }

        private static void ForceReleaseSuppression()
        {
            suppressionCount = 0;
            if (!suppressionOwned)
                return;

            suppressionOwned = false;
            try
            {
                IPCManager.SetAutoRetainerSuppressed(false);
                Log.Debug("AutoRetainerBridge: released AutoRetainer suppression");
            }
            catch (Exception e)
            {
                Log.Warning(e, "AutoRetainerBridge: could not release AutoRetainer suppression");
            }
        }

        private static void OnFrameworkUpdate(IFramework framework)
        {
            if ((DateTime.UtcNow - lastPoll).TotalMilliseconds < PollIntervalMs)
                return;
            lastPoll = DateTime.UtcNow;

            var wasBusy = busyCached;
            busyCached = IPCManager.IsAutoRetainerBusy();

            // Announce availability once, and only to a user who was actually
            // turned away earlier. While our own suppression is active the
            // transition is our own doing, so it is not worth announcing.
            if (wasBusy && !busyCached && blockedPending && !IsSuppressing)
            {
                blockedPending = false;
                ChatGui.Print("[Marketbuddy] AutoRetainer has finished - you can run the relist now.".Loc());
            }
        }
    }
}
