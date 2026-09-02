using System;
using System.Collections.Generic;
using System.Threading;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy.Common
{
    /// <summary>按法的種類，決定併 key 的粒度、逃生口長度與記錄等級。</summary>
    internal enum PressKind
    {
        /// <summary>
        /// 「回答一次即終結」的按法：按下去視窗就會關（選僱員、選單項、Confirm、關窗）。
        /// 同一扇窗（同位址）的所有終結型按法**併成同一個 key** —— 已經回答過就不准再碰。
        /// </summary>
        Terminal,

        /// <summary>
        /// 不會讓視窗消失的一次性操作（切到歷史頁籤、開比價視窗）。
        /// 依參數各自 key，但只要那扇窗已經有終結型紀錄就一律擋下。
        /// </summary>
        Auxiliary,

        /// <summary>
        /// 多次互動窗：按一次翻一頁、視窗不會因為被按而消失（Talk）。
        /// 逃生口短（<see cref="RoutineRePressEscapeFrames"/> 幀）而且走逃生口是常態，記 Debug。
        /// </summary>
        Routine,
    }

    /// <summary>
    /// 「不要對正在關閉中的視窗再按一次」的守衛。
    ///
    /// 🔴🔴 這在防什麼：SelectYesno 這類「按下即關」的原生視窗，在按下之後的幾幀裡
    /// <c>GetAddonByName</c> 仍然回得到實例，<c>IsVisible</c> 與
    /// <c>UldManager.LoadedState == Loaded</c> 也都還是真 —— 也就是說
    /// <see cref="AddonHelpers.GetReadyAddon"/> 的三關全部通過 —— 這時再送一次
    /// callback / ReceiveEvent 就是原生 AccessViolation。AVE 在 .NET Core 屬於
    /// corrupted-state exception，<c>try/catch</c> 完全攔不到，唯一的防護是**不要送第二次**。
    ///
    /// 🔴 節流不是防護：牆鐘節流記的是「上一次動作在哪一幀」，不是「這扇窗按過了」。
    /// 本外掛原有的 250ms / 500ms / 1000ms 節流換算成幀取決於實機 FPS，30fps 時
    /// 250ms 只有約 7.5 幀，正好落在危險窗口內。守衛記的是**位址**，與 FPS 無關。
    ///
    /// 🔴 位址只做等值比較，永遠不解參考。位址會被新視窗重用，所以一定要搭配
    /// 「看到生命週期結束」的解除點（PreFinalize / PostSetup / 每幀輪詢）與逾時兜底。
    ///
    /// 🔴 這個守衛**只做防護**：它只會讓按下的次數變少，永遠不會多按一次，也不會
    /// 改變任何觸發條件。被擋下時一律回 <c>false</c>，對呼叫端的意義是「這一輪沒按到」，
    /// 走的是它本來就有的「addon 還沒就緒」那條路徑。
    /// </summary>
    internal static unsafe class AddonPressGuard
    {
        /// <summary>
        /// 單答終結窗的逃生口（幀）。關閉中的危險窗口實測在 10 幀以內，60 幀有六倍餘裕；
        /// 同時遠小於呼叫端的看門狗（導航步驟 20 秒、關僱員選單 15 秒），
        /// 所以永遠不會把整條佇列拖到逾時被砍掉。
        /// </summary>
        private const int TerminalEscapeFrames = 60;

        /// <summary>
        /// 多次互動窗（Talk）的逃生口（幀）。Talk 按一次翻一頁、視窗不會消失，
        /// 所以走逃生口是常態而不是異常；15 幀不落在 10 幀的危險窗口內，
        /// 每頁最多多等 0.25 秒（60fps）幾乎無感。
        /// </summary>
        private const int RoutineRePressEscapeFrames = 15;

        /// <summary>
        /// 會被登記與解除的視窗名。只是先把 AddonLifecycle 監聽器掛好而已 ——
        /// 名單外的視窗照樣受保護，只是解除完全靠每幀輪詢。
        /// </summary>
        private static readonly string[] WatchedAddons =
        {
            "RetainerList",
            "RetainerSellList",
            "SelectString",
            "Talk",
            "RetainerSell",
            "ItemSearchResult",
            "ContextMenu",
        };

        private sealed class Press
        {
            public Press(string addon, nint address, PressKind kind, long frame)
            {
                Addon = addon;
                Address = address;
                Kind = kind;
                Frame = frame;
            }

            public string Addon { get; }
            public nint Address { get; }
            public PressKind Kind { get; }
            public long Frame { get; }
        }

        private readonly record struct PressKey(string Addon, nint Address, PressKind Kind, long Param);

        private static readonly Dictionary<PressKey, Press> Presses = new();

        /// <summary>移除用的暫存清單。只在主執行緒上用，而且兩個使用點不會互相巢狀。</summary>
        private static readonly List<PressKey> Doomed = new();

        /// <summary>
        /// 🔴 守衛自己的時鐘。**絕對不能用 <c>UiBuilder.FrameCount</c>**：
        /// 那個計數器的 <c>++</c> 排在 <c>OnDraw()</c> 的三個「隱藏 UI 就 return」之後
        /// （過場動畫、使用者按隱藏 UI 熱鍵、GPose，三個開關預設全開），
        /// 過場期間它完全不前進 —— 而按下點走的是原生事件，照常每幀被呼叫，
        /// 結果就是「按得下去、逃生口永遠不到期」。
        /// <c>Framework.Update</c> 在遊戲的 update hook 內，不受 UI 隱藏影響。
        /// </summary>
        private static long frameCount;

        /// <summary>
        /// 🔴 防重複訂閱用 <c>Interlocked</c> 而不是 bool：重複訂閱不是沒效果，
        /// 是計數器一個 tick 前進 2 ＝所有逃生口對半砍，會把補按推進危險窗口裡。
        /// </summary>
        private static int initialized;

        /// <summary>
        /// 訂閱時鐘與生命週期監聽器。
        /// 🔴 要在外掛建構子裡**盡可能早**呼叫：同一個外掛內部的 <c>Framework.Update</c>
        /// 是單一多播委派包在同一個 try/catch 裡，排在前面的處理常式擲例外，
        /// 後面所有處理常式那個 tick 完全不會被呼叫 —— 時鐘停掉會讓逃生口不到期。
        /// </summary>
        internal static void Initialize()
        {
            if (Interlocked.CompareExchange(ref initialized, 1, 0) != 0)
                return;

            Framework.Update += OnFrameworkUpdate;
            foreach (var name in WatchedAddons)
            {
                AddonLifecycle.RegisterListener(AddonEvent.PostSetup, name, OnAddonSetup);
                AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, name, OnAddonFinalize);
            }
        }

        internal static void Shutdown()
        {
            if (Interlocked.CompareExchange(ref initialized, 0, 1) != 1)
                return;

            Framework.Update -= OnFrameworkUpdate;
            foreach (var name in WatchedAddons)
            {
                AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, name, OnAddonSetup);
                AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, name, OnAddonFinalize);
            }

            Presses.Clear();
            Doomed.Clear();
        }

        /// <summary>
        /// 只問「現在按下去會不會被擋」，不留下任何紀錄。
        /// 給那種「按之前得先建立呼叫端狀態」的路徑用（QuickLister 必須在送出 callback
        /// 之前就把待處理項排好，因為那個 callback 可能同步開出 RetainerSell）。
        /// 同一幀同一執行緒內接著呼叫 <see cref="TryPress"/> 一定得到相同答案。
        /// </summary>
        internal static bool CanPress(string addonName, nint address, PressKind kind, long param = 0)
            => !IsBlocked(addonName, address, kind, param);

        /// <summary>
        /// 登記一次按下。回 <c>true</c> 才可以真的送出事件；回 <c>false</c> 的意思一律是
        /// **「這一輪不按，下一輪再說」**，不是錯誤，也不該升級成重試迴圈。
        /// </summary>
        internal static bool TryPress(string addonName, nint address, PressKind kind, long param = 0)
        {
            if (address == 0 || string.IsNullOrEmpty(addonName))
                return true; // 沒有可以比對的身分就沒有守衛可言：不擋，維持原行為。

            if (IsBlocked(addonName, address, kind, param))
            {
                if (kind == PressKind.Routine)
                    Log.Debug($"AddonPressGuard: {addonName} 這一輪不按（同一扇窗剛按過，等它翻頁或等逃生口）");
                else
                    Log.Information(
                        $"AddonPressGuard: 略過對 {addonName} 的重複按下（同一扇窗已經按過，可能正在關閉中）");
                return false;
            }

            Presses[MakeKey(addonName, address, kind, param)] =
                new Press(addonName, address, kind, frameCount);
            return true;
        }

        private static PressKey MakeKey(string addonName, nint address, PressKind kind, long param)
            // 終結型按法併 key：Yes/No、選項/取消、Confirm/Close 都是「回答一次就結束」，
            // 用不同參數各自記帳等於沒擋到。
            => new(addonName, address, kind, kind == PressKind.Terminal ? 0 : param);

        private static bool IsBlocked(string addonName, nint address, PressKind kind, long param)
        {
            if (address == 0 || string.IsNullOrEmpty(addonName))
                return false;

            // 這扇窗已經被「回答」過了 ⇒ 任何按法都不准再碰它。
            if (Presses.ContainsKey(MakeKey(addonName, address, PressKind.Terminal, 0)))
                return true;

            if (kind == PressKind.Terminal)
                return false;

            return Presses.ContainsKey(MakeKey(addonName, address, kind, param));
        }

        private static void OnFrameworkUpdate(IFramework framework)
        {
            // 🔴 時鐘的遞增必須排在所有 early return 之前，否則「沒有紀錄時時鐘就停住」，
            //    下一次登記的逃生口會用到一個過期的幀號。
            frameCount++;

            if (Presses.Count == 0)
                return;

            ReleaseFinishedOrExpired();
        }

        /// <summary>
        /// 每幀輪詢的解除：①逃生口到期 ②該位址已經不在 addon 清單裡（生命週期結束）
        /// ③位址還在但已經看不見（有些視窗在僱員之間只是隱藏再重顯、同位址重用）。
        /// </summary>
        private static void ReleaseFinishedOrExpired()
        {
            Doomed.Clear();
            foreach (var entry in Presses)
            {
                var press = entry.Value;
                var escape = press.Kind == PressKind.Routine
                    ? RoutineRePressEscapeFrames
                    : TerminalEscapeFrames;

                if (frameCount - press.Frame >= escape)
                {
                    // 逃生口：永久封鎖會讓呼叫端卡到看門狗，而看門狗到期是把**整條佇列**砍掉。
                    if (press.Kind == PressKind.Routine)
                        Log.Debug($"AddonPressGuard: {press.Addon} 的重按封鎖到期（{escape} 幀），可以再按一次");
                    else
                        Log.Information(
                            $"AddonPressGuard: {press.Addon} 的重按封鎖在 {escape} 幀後逾時解除（那扇窗一直沒有走完生命週期）");
                    Doomed.Add(entry.Key);
                    continue;
                }

                if (!IsStillAlive(press.Addon, press.Address))
                    Doomed.Add(entry.Key);
            }

            foreach (var key in Doomed)
                Presses.Remove(key);
            Doomed.Clear();
        }

        /// <summary>
        /// 掃全部索引找這個位址。<c>GetAddonByName(name, 1)</c> 只看得到第 1 格，
        /// 多窗情境會漏掉；掃到第一個空的就停。
        /// 🔴 這裡對存下來的位址**只做等值比較**；解參考的是遊戲這一幀自己交回來的指標。
        /// </summary>
        private static bool IsStillAlive(string addonName, nint address)
        {
            for (var i = 1; i < 100; i++)
            {
                var addon = Commons.GetUnitBase(addonName, i);
                if (addon == null)
                    return false;
                if ((nint)addon != address)
                    continue;
                return addon->IsVisible;
            }

            return false;
        }

        private static void OnAddonSetup(AddonEvent type, AddonArgs args)
        {
            if (Presses.Count == 0)
                return;

            // 🔴 解除一定要按**位址**，不能按名稱：同名的第二扇窗被建立時，
            //    按名稱清會把第一扇（正在關閉中）的紀錄一起清掉，
            //    下一幀對它再送一次就是 AVE。
            // 🔴 同幀豁免：本外掛有「在 PostSetup 處理常式裡直接按下去」的路徑
            //    （QuickLister 的快速上架、比價視窗、歷史頁籤），而 AddonLifecycle
            //    監聽器彼此之間的呼叫順序**不可依賴**（服務端註冊走 RunOnTick，
            //    派送時直接 foreach 一個順序無關的集合）。沒有這道豁免，
            //    晚一步跑到的解除會把同一幀剛登記的紀錄清掉。
            //    ⚠️ 「上一扇窗留下的舊紀錄」不靠這裡清 —— 那是 PreFinalize 與
            //    每幀輪詢的工作，它們發生在更早的幀，所以不受監聽器順序影響。
            RemoveByAddress((nint)args.Addon.Address, exemptCurrentFrame: true);
        }

        private static void OnAddonFinalize(AddonEvent type, AddonArgs args)
        {
            if (Presses.Count == 0)
                return;

            RemoveByAddress((nint)args.Addon.Address, exemptCurrentFrame: false);
        }

        private static void RemoveByAddress(nint address, bool exemptCurrentFrame)
        {
            if (address == 0)
                return;

            Doomed.Clear();
            foreach (var entry in Presses)
            {
                if (entry.Value.Address != address)
                    continue;
                if (exemptCurrentFrame && entry.Value.Frame == frameCount)
                    continue;
                Doomed.Add(entry.Key);
            }

            foreach (var key in Doomed)
                Presses.Remove(key);
            Doomed.Clear();
        }
    }
}
