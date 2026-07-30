using System;
using Dalamud.Plugin.Services;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    /// <summary>
    /// Single point of contact with AutoRetainer.
    ///
    /// - Caches "is AutoRetainer busy" at 1 Hz (callers ask every frame, an IPC
    ///   invoke per frame would be wasteful).
    /// - Suppresses AutoRetainer for the duration of a user-triggered
    ///   Marketbuddy operation (reference counted, restores the previous state
    ///   and never un-suppresses a suppression somebody else owns).
    /// - Raises the "AutoRetainer finished, you can run it now" notice, but
    ///   only when the user was actually blocked before.
    ///
    /// Suppression is strictly scoped to operations the user started by hand;
    /// nothing here runs unattended and AutoRetainer's post-process API is
    /// deliberately not used.
    /// </summary>
    internal static class AutoRetainerBridge
    {
        private const int PollIntervalMs = 1000;

        private static DateTime lastPoll = DateTime.MinValue;
        private static bool busyCached;
        private static bool blockedPending;
        private static int suppressionCount;
        private static bool suppressionOwned;

        /// <summary>Cached busy state (refreshed at 1 Hz from AutoRetainer.IsBusy).</summary>
        internal static bool IsBusy => busyCached;

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
