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

        // AutoRetainer character postprocess
        //
        // 🔴 契約名逐字抄自 AutoRetainer 的 AutoRetainerAPI/ApiConsts.cs 與
        //    AutoRetainer/Modules/IPC.cs（2026-08-31 實讀本 fork 原始碼）。
        //    CallGate 是純字串比對：名字打錯不會有任何錯誤訊息，只會永遠收不到事件。
        // 📌 AR 端註冊的是 void 動作 ⇒ 消費端用 ICallGateSubscriber<…, object> + InvokeAction。
        internal const string TagOnCharacterAdditionalTask = "AutoRetainer.OnCharacterAdditionalTask";
        internal const string TagOnCharacterReadyForPostprocess = "AutoRetainer.OnCharacterReadyForPostprocess";
        internal const string TagRequestCharacterPostprocess = "AutoRetainer.RequestCharacterPostprocess";
        internal const string TagFinishCharacterPostprocessRequest = "AutoRetainer.FinishCharacterPostprocessRequest";
        internal const string TagGetRegisteredCids = "AutoRetainer.GetRegisteredCIDs";
        internal const string TagGetOfflineCharacterData = "AutoRetainer.GetOfflineCharacterData";

        /// <summary>
        /// AutoRetainer <c>OfflineCharacterData</c> 的鏡像型別。
        /// </summary>
        /// <remarks>
        /// 🔴 <b>欄位名必須與 AR 端逐字相同。</b>型別對不上時 Dalamud 的 CallGate 會把物件
        /// 做一次 JSON 來回轉（<c>CallGateChannel.ConvertObject</c>），名字打錯不會報錯，
        /// 只會靜默拿到預設值（false／0／null）。
        /// <para>
        /// 來源：<c>AutoRetainer/AutoRetainerAPI/AutoRetainerAPI/Configuration/OfflineCharacterData.cs</c>
        /// （2026-08-31 實讀）。只抄這裡真的會用到的幾個欄位，其餘欄位反序列化時被忽略。
        /// </para>
        /// </remarks>
        // CS0649「從未指派」：這些欄位是 Newtonsoft 反序列化時填的（CallGate 的
        // ConvertObject 走 JSON 來回轉），編譯器看不到那條路徑。**不要**改成屬性或加初始值
        // 去消警告——欄位名與形狀必須跟 AR 端一致。
#pragma warning disable CS0649
        internal sealed class AutoRetainerCharacterData
        {
            public ulong CID;
            public string Name = string.Empty;
            public string World = string.Empty;

            /// <summary>這個角色有沒有被使用者勾進 AutoRetainer 的多開排程。</summary>
            public bool Enabled;

            /// <summary>勾了「不要處理這個角色的僱員」——那種角色沒有重掛可做。</summary>
            public bool ExcludeRetainer;
        }
#pragma warning restore CS0649

        /// <summary>
        /// AutoRetainer 認得的角色 CID 清單。AutoRetainer 沒裝／IPC 還沒好時回 null
        /// （<b>不是</b>空清單——「沒裝」跟「一個角色都沒登記」要分得開）。
        /// </summary>
        internal static List<ulong>? GetAutoRetainerRegisteredCids()
        {
            try
            {
                return Svc.PluginInterface.GetIpcSubscriber<List<ulong>>(TagGetRegisteredCids).InvokeFunc();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>某個角色在 AutoRetainer 裡的離線資料。拿不到回 null。</summary>
        internal static AutoRetainerCharacterData? GetAutoRetainerCharacterData(ulong cid)
        {
            try
            {
                return Svc.PluginInterface
                    .GetIpcSubscriber<ulong, AutoRetainerCharacterData>(TagGetOfflineCharacterData)
                    .InvokeFunc(cid);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>AutoRetainer 的多開模式開著沒有。AutoRetainer 沒裝時回 null。</summary>
        internal static bool? IsAutoRetainerMultiModeEnabled()
        {
            try
            {
                return Svc.PluginInterface.GetIpcSubscriber<bool>("AutoRetainer.GetMultiModeEnabled").InvokeFunc();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 向 AutoRetainer 登記「這個角色換角前讓我做一段收尾」。
        /// 🔴 <b>會擲例外</b>（同一次廣播裡重複登記時 AR 自己 throw），呼叫端必須接。
        /// </summary>
        internal static void RequestAutoRetainerCharacterPostprocess(string pluginName)
        {
            Svc.PluginInterface.GetIpcSubscriber<string, object>(TagRequestCharacterPostprocess)
                .InvokeAction(pluginName);
        }

        /// <summary>
        /// 🔴🔴 告訴 AutoRetainer「我做完了，可以換角了」。
        /// AR 端的等待是 <c>timeLimitMS: int.MaxValue</c>，少呼叫這一次它就<b>永遠</b>停在那裡。
        /// </summary>
        internal static void FinishAutoRetainerCharacterPostprocess()
        {
            Svc.PluginInterface.GetIpcSubscriber<object>(TagFinishCharacterPostprocessRequest).InvokeAction();
        }

        /// <summary>訂閱「要換角了，想收尾的外掛現在登記」。</summary>
        internal static void SubscribeCharacterAdditionalTask(Action handler)
            => Svc.PluginInterface.GetIpcSubscriber<object>(TagOnCharacterAdditionalTask).Subscribe(handler);

        internal static void UnsubscribeCharacterAdditionalTask(Action handler)
        {
            try
            {
                Svc.PluginInterface.GetIpcSubscriber<object>(TagOnCharacterAdditionalTask).Unsubscribe(handler);
            }
            catch (Exception e)
            {
                Svc.Log.Warning(e, "IPCManager: 取消訂閱 OnCharacterAdditionalTask 失敗");
            }
        }

        /// <summary>訂閱「輪到某個外掛了」。⚠️ 參數是外掛名，不是自己就必須忽略。</summary>
        internal static void SubscribeCharacterReadyForPostprocess(Action<string> handler)
            => Svc.PluginInterface.GetIpcSubscriber<string, object>(TagOnCharacterReadyForPostprocess)
                .Subscribe(handler);

        internal static void UnsubscribeCharacterReadyForPostprocess(Action<string> handler)
        {
            try
            {
                Svc.PluginInterface.GetIpcSubscriber<string, object>(TagOnCharacterReadyForPostprocess)
                    .Unsubscribe(handler);
            }
            catch (Exception e)
            {
                Svc.Log.Warning(e, "IPCManager: 取消訂閱 OnCharacterReadyForPostprocess 失敗");
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
