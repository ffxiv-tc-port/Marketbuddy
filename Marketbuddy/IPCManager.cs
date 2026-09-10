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
        /// <summary>
        /// 別的外掛透過 IPC 掛上來的停止鎖（鍵＝對方自己報的名字）。
        /// 🔴 <b>三種執行緒會碰這個集合</b>：<c>Marketbuddy.Lock</c>／<c>Marketbuddy.Unlock</c>
        /// 端點跑在<b>呼叫端外掛的執行緒</b>上、<see cref="IsLocked"/> 每幀在 framework
        /// 執行緒被好幾個模組讀、設定視窗在繪製執行緒列出內容並可能整批清掉。
        /// 裸 <c>HashSet</c> 的失敗形式不是「讀到舊值」，而是<b>集合本身壞掉</b>
        /// （或列舉到一半擲 <c>InvalidOperationException</c>）。
        /// ⇒ 一律走本類別的包裝方法，不要把集合本身或它的方法群組交出去。
        /// </summary>
        private static readonly HashSet<string> Locks = new();

        /// <summary>
        /// <see cref="Locks"/> 的鎖。🔴 鎖內只碰集合：不寫 log、不畫 ImGui、不做檔案 I/O、
        /// 不呼叫任何別的外掛。
        /// </summary>
        private static readonly object LocksGate = new();

        /// <summary>
        /// <c>Locks.Count</c> 的鏡像，只在持有 <see cref="LocksGate"/> 時寫入。
        /// <see cref="IsLocked"/> 每幀被讀很多次，走這個 volatile 讀就完全不必進鎖，
        /// 成本與原本的 <c>Locks.Count</c> 相同。
        /// </summary>
        private static volatile int lockCount;

        internal static bool IsLocked => lockCount > 0;

        /// <summary>設定視窗用：目前掛著幾把鎖。</summary>
        internal static int LockCount => lockCount;

        /// <summary>
        /// 設定視窗用：把鎖的名字拍成快照再交出去。
        /// 🔴 直接列舉集合會與 IPC 執行緒的插入並行 ⇒ 擲例外或讀到壞掉的內容。
        /// </summary>
        internal static string[] SnapshotLocks()
        {
            lock (LocksGate)
            {
                return Locks.ToArray();
            }
        }

        /// <summary>設定視窗「解除鎖定」按鈕用：把所有鎖清掉。</summary>
        internal static void ClearLocks()
        {
            lock (LocksGate)
            {
                Locks.Clear();
                lockCount = 0;
            }
        }

        /// <summary>
        /// <c>Marketbuddy.Lock</c> 端點的實作。回傳值與原本直接綁 <c>HashSet.Add</c> 逐字相同：
        /// 新掛上的回 <c>true</c>、同名鎖已經在了回 <c>false</c>（<c>null</c> 也照 HashSet 的原樣收）。
        /// </summary>
        private static bool AddLock(string name)
        {
            lock (LocksGate)
            {
                var added = Locks.Add(name);
                lockCount = Locks.Count;
                return added;
            }
        }

        /// <summary>
        /// <c>Marketbuddy.Unlock</c> 端點的實作。回傳值與原本直接綁 <c>HashSet.Remove</c> 逐字相同：
        /// 真的移掉了才回 <c>true</c>。
        /// </summary>
        private static bool RemoveLock(string name)
        {
            lock (LocksGate)
            {
                var removed = Locks.Remove(name);
                lockCount = Locks.Count;
                return removed;
            }
        }

        /// <summary>
        /// <c>Marketbuddy.IsLocked</c> 端點的實作。參數 <c>null</c> 問的是「有沒有任何鎖」，
        /// 否則問的是「這個名字有沒有掛鎖」——與原本的 lambda 逐字相同。
        /// </summary>
        private static bool QueryLocked(string name)
        {
            if (name == null)
                return IsLocked;

            lock (LocksGate)
            {
                return Locks.Contains(name);
            }
        }

        // ---------------------------------------------------------------
        // Marketbuddy.MarketCache.*(唯讀,給 PriceInsight 之類的查價外掛用)
        //
        // 🔴 新功能一律開**新名字**的端點。既有的 Lock／Unlock／IsLocked 名字與型別一個字
        //    都沒動——同名改型別是最兇的一種破壞,而且有些方向會靜默成功。
        // 🔴 這兩個端點跑在**呼叫端外掛的執行緒**上,所以它們只碰 MarketDataCache 的
        //    ConcurrentDictionary 鏡像(GetPublished),絕不碰那個裸 Dictionary 快取。
        // 🔴 查不到時回 null,而回傳的是**參考型別**:CallGate 的 InvokeFunc 對 null 走
        //    `(TRet)result`,參考型別安全;可空**值**型別才會擲一個看起來與 IPC 完全
        //    無關的 NullReferenceException。
        // ---------------------------------------------------------------

        /// <summary>
        /// 快取 IPC 的契約版本。加欄位時遞增。
        /// 🔑 消費端請用 <c>&gt;=</c> 比對,<b>不要用 <c>==</c></b>——嚴格相等會讓
        /// 我方合法地遞增版本號時,對方靜默失效。
        /// </summary>
        private const int MarketCacheApiVersion = 1;

        internal const string TagMarketCacheVersion = "Marketbuddy.MarketCache.Version";
        internal const string TagMarketCacheGet = "Marketbuddy.MarketCache.Get";

        /// <summary><c>Marketbuddy.MarketCache.Version</c> 端點的實作。</summary>
        private static int QueryMarketCacheVersion() => MarketCacheApiVersion;

        /// <summary>
        /// <c>Marketbuddy.MarketCache.Get</c> 端點的實作:這件道具在**本世界**最近一次
        /// 看到的真實掛單摘要。沒有資料回 <c>null</c>。
        /// </summary>
        /// <remarks>
        /// 🔴 純讀取,零副作用:不送任何市場查詢、不觸發任何遊戲操作。
        /// 這與 MarketDataCache「純被動接收」的設計約束一致——對外開放的是**已經看到的東西**,
        /// 不是「幫你去查一次」。
        /// </remarks>
        private static MarketDataCache.PublicSnapshot? QueryMarketCache(uint itemId)
            => MarketDataCache.GetPublished(itemId);

        internal static void Init()
        {
            // 🔴 這裡綁的必須是包裝方法。綁 Locks.Add／Locks.Remove 這種方法群組
            //    等於把裸集合的寫入直接暴露在呼叫端外掛的執行緒上。
            Svc.PluginInterface.GetIpcProvider<string, bool>("Marketbuddy.Lock").RegisterFunc(AddLock);
            Svc.PluginInterface.GetIpcProvider<string, bool>("Marketbuddy.Unlock").RegisterFunc(RemoveLock);
            Svc.PluginInterface.GetIpcProvider<string, bool>("Marketbuddy.IsLocked").RegisterFunc(QueryLocked);

            // 唯讀的市場快取查詢(見上面 MarketCache.* 那段的說明)。
            Svc.PluginInterface.GetIpcProvider<int>(TagMarketCacheVersion)
                .RegisterFunc(QueryMarketCacheVersion);
            Svc.PluginInterface.GetIpcProvider<uint, MarketDataCache.PublicSnapshot?>(TagMarketCacheGet)
                .RegisterFunc(QueryMarketCache);
        }

        internal static void Shutdown()
        {
            Svc.PluginInterface.GetIpcProvider<string, bool>("Marketbuddy.Lock").UnregisterFunc();
            Svc.PluginInterface.GetIpcProvider<string, bool>("Marketbuddy.Unlock").UnregisterFunc();
            Svc.PluginInterface.GetIpcProvider<string, bool>("Marketbuddy.IsLocked").UnregisterFunc();
            Svc.PluginInterface.GetIpcProvider<int>(TagMarketCacheVersion).UnregisterFunc();
            Svc.PluginInterface.GetIpcProvider<uint, MarketDataCache.PublicSnapshot?>(TagMarketCacheGet)
                .UnregisterFunc();
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
        /// <summary>AR 主視窗控制列(與僱員清單懸浮窗)的第三方繪製擴充點,每幀 SendMessage。</summary>
        internal const string TagOnMainControlsDraw = "AutoRetainer.OnMainControlsDraw";

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

        /// <summary>訂閱 AR 主控制列繪製擴充點(回呼在 AR 的 ImGui Draw 裡被每幀呼叫)。</summary>
        internal static void SubscribeMainControlsDraw(Action handler)
            => Svc.PluginInterface.GetIpcSubscriber<object>(TagOnMainControlsDraw).Subscribe(handler);

        internal static void UnsubscribeMainControlsDraw(Action handler)
        {
            try
            {
                Svc.PluginInterface.GetIpcSubscriber<object>(TagOnMainControlsDraw).Unsubscribe(handler);
            }
            catch (Exception e)
            {
                Svc.Log.Warning(e, "IPCManager: 取消訂閱 OnMainControlsDraw 失敗");
            }
        }

        // ---------------------------------------------------------------
        // Lifestream（世界轉移）
        //
        // 🔴 端點名與型別逐字取自 Lifestream/Lifestream/IPC/IPCProvider.cs（2026-09-07 實讀）：
        //    EzIPC 的預設前綴是對方的 InternalName，所以是 Lifestream.<方法名>。
        //    ChangeWorld(string) -> bool、IsBusy() -> bool、
        //    CanVisitSameDC(string) -> bool、CanVisitCrossDC(string) -> bool。
        // 🔴 <b>只能在 framework 執行緒上呼叫。</b>Lifestream 那側有一道
        //    IpcFrameworkGate：已經在主執行緒時就地執行（零額外成本），
        //    但從別的執行緒打過去會變成「排進主執行緒 + 同步等最多 5 秒」——
        //    在 ImGui 的繪製執行緒上呼叫就是卡住畫面 5 秒。
        // 📌 Lifestream 沒裝／IPC 還沒好一律靜默回 false／null，不擲例外給呼叫端。
        // ---------------------------------------------------------------

        /// <summary>Lifestream 現在忙不忙。沒裝時回 false。</summary>
        internal static bool IsLifestreamBusy()
        {
            try
            {
                return Svc.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc();
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 這個角色現在能不能去那個世界。
        /// 回 null 代表<b>問不到</b>（Lifestream 沒裝／IPC 還沒好）——與「不能去」要分得開，
        /// 因為畫面上要說的話完全不同。
        /// </summary>
        internal static bool? CanLifestreamVisit(string world)
        {
            if (string.IsNullOrWhiteSpace(world))
                return false;

            try
            {
                if (Svc.PluginInterface.GetIpcSubscriber<string, bool>("Lifestream.CanVisitSameDC")
                    .InvokeFunc(world))
                    return true;
                return Svc.PluginInterface.GetIpcSubscriber<string, bool>("Lifestream.CanVisitCrossDC")
                    .InvokeFunc(world);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 請 Lifestream 把角色送去 <paramref name="world"/>。
        /// </summary>
        /// <remarks>
        /// 🔴 <b>呼叫一次只換一次。</b>這裡沒有重試、沒有佇列、沒有「到了就繼續」的串接；
        /// 回 false 就是這一次沒成功，要不要再試由使用者再按一次按鈕決定。
        /// 🔴 絕不用聊天指令（空參數的 /li 等於跨世界傳送），一律走具名的 IPC 端點。
        /// </remarks>
        /// <returns>Lifestream 接受了這次請求才回 true；沒裝、忙碌中、去不了都回 false。</returns>
        internal static bool LifestreamChangeWorld(string world)
        {
            if (string.IsNullOrWhiteSpace(world))
                return false;

            try
            {
                return Svc.PluginInterface.GetIpcSubscriber<string, bool>("Lifestream.ChangeWorld")
                    .InvokeFunc(world);
            }
            catch (Exception e)
            {
                Svc.Log.Information(e, "[Marketbuddy] Lifestream.ChangeWorld 呼叫失敗（沒裝或 IPC 還沒好）。");
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
