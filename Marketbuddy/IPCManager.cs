using Marketbuddy.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Svc = Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    internal static class IPCManager
    {
        internal static HashSet<string> Locks = new();
        internal static bool IsLocked => Locks.Count > 0;
        
        internal static void Init()
        {
            Svc.PluginInterface.GetIpcProvider<string, bool>("Marketbuddy.Lock").RegisterFunc(Locks.Add);
            Svc.PluginInterface.GetIpcProvider<string, bool>("Marketbuddy.Unlock").RegisterFunc(Locks.Remove);
            Svc.PluginInterface.GetIpcProvider<string, bool>("Marketbuddy.IsLocked").RegisterFunc((str) => str == null?IsLocked:Locks.Contains(str));
        }

        internal static void Shutdown()
        {
            Svc.PluginInterface.GetIpcProvider<string, bool>("Marketbuddy.Lock").UnregisterFunc();
            Svc.PluginInterface.GetIpcProvider<string, bool>("Marketbuddy.Unlock").UnregisterFunc();
            Svc.PluginInterface.GetIpcProvider<string, bool>("Marketbuddy.IsLocked").UnregisterFunc();
        }

        /// <summary>
        /// True when AutoRetainer is actively driving retainer automation.
        /// Prefers our fork's precise "AutoRetainer.IsBusy" endpoint
        /// (PluginEnabled || MultiMode.Active || TaskManager.IsBusy); when that
        /// endpoint does not exist (older AutoRetainer builds) it falls back to
        /// the coarse MultiMode-enabled flag. AutoRetainer absent or IPC not
        /// ready: silently treated as not busy.
        /// </summary>
        internal static bool IsAutoRetainerBusy()
        {
            try
            {
                return Svc.PluginInterface.GetIpcSubscriber<bool>("AutoRetainer.IsBusy").InvokeFunc();
            }
            catch
            {
                // Endpoint missing (older AutoRetainer): coarse fallback below.
            }

            try
            {
                return Svc.PluginInterface.GetIpcSubscriber<bool>("AutoRetainer.GetMultiModeEnabled").InvokeFunc();
            }
            catch
            {
                // AutoRetainer absent or IPC not ready: no restriction.
                return false;
            }
        }

        /// <summary>
        /// True when AutoRetainer is currently suppressed (by us or anybody
        /// else). AutoRetainer absent: false.
        /// </summary>
        internal static bool IsAutoRetainerSuppressed()
        {
            try
            {
                return Svc.PluginInterface.GetIpcSubscriber<bool>("AutoRetainer.GetSuppressed").InvokeFunc();
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Sets AutoRetainer's suppression flag. Returns true when the call
        /// actually went through (false = AutoRetainer absent / IPC not ready),
        /// so callers know whether they now own the suppression.
        /// </summary>
        internal static bool SetAutoRetainerSuppressed(bool suppressed)
        {
            try
            {
                Svc.PluginInterface.GetIpcSubscriber<bool, object>("AutoRetainer.SetSuppressed").InvokeAction(suppressed);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
