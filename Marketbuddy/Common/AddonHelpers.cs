using System;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace Marketbuddy.Common
{
    /// <summary>
    /// Small native-UI primitives for the multi-retainer tour, modelled on the
    /// TC-production-proven AutoRetainer/ECommons recipes: addon callbacks via
    /// the game's own AtkUnitBase.FireCallback (bundled ClientStructs member,
    /// no signature scan), SelectString entry matching by Lumina Addon-sheet
    /// text (never hardcoded strings), and the standard Talk-advance click.
    /// </summary>
    internal static unsafe class AddonHelpers
    {
        public static bool IsAddonReady(AtkUnitBase* addon)
            => addon != null && addon->IsVisible && addon->UldManager.LoadedState == AtkLoadState.Loaded;

        public static AtkUnitBase* GetReadyAddon(string name)
        {
            var addon = Commons.GetUnitBase(name);
            return IsAddonReady(addon) ? addon : null;
        }

        /// <summary>
        /// 讀出這扇視窗自己報的名字（用來當守衛的 key，並讓守衛能用 GetAddonByName 輪詢它）。
        /// ⚠️ 刻意不用 CS 產生的 <c>NameString</c>：它走的是
        /// <c>CreateReadOnlySpanFromNullTerminated</c>，**沒有長度上限**，
        /// 名稱緩衝區裡沒有 NUL 時會一路往後掃出界。這裡把範圍夾在那 32 個 byte 之內。
        /// </summary>
        public static string ReadAddonName(AtkUnitBase* addon)
        {
            if (addon == null)
                return string.Empty;

            var name = addon->Name;
            var end = name.IndexOf((byte)0);
            if (end < 0)
                end = name.Length;
            return end == 0 ? string.Empty : System.Text.Encoding.UTF8.GetString(name[..end]);
        }

        /// <summary>
        /// RetainerList row selection: Callback(2, index, 0, 0), same shape as ECommons AddonMaster.RetainerList.
        /// 回 false ＝守衛擋下（這扇窗剛按過，可能正在關閉中），呼叫端這一輪不要當成已完成。
        /// </summary>
        public static bool FireRetainerListSelect(AtkUnitBase* retainerList, uint index)
        {
            if (!AddonPressGuard.TryPress("RetainerList", (nint)retainerList, PressKind.Terminal))
                return false;

            var values = stackalloc AtkValue[4];
            values[0].Type = ValueType.Int;
            values[0].Int = 2;
            values[1].Type = ValueType.UInt;
            values[1].UInt = index;
            values[2].Type = 0;
            values[2].Int = 0;
            values[3].Type = 0;
            values[3].Int = 0;
            retainerList->FireCallback(4, values, true);
            return true;
        }

        /// <summary>
        /// Context menu entry selection: Callback(0, index, 0u, 0, 0), same shape as AutoRetainer's QuickSellItems.
        /// <paramref name="addonName"/> 是這扇選單自己報的名字（見 <see cref="ReadAddonName"/>）：
        /// 守衛用它比對與輪詢，硬寫一個名字會在名字不符時靜默失去保護。
        ///
        /// 🔴 <paramref name="closedByCallback"/> ＝**原生端有沒有在回到 C# 之前就把這扇窗關掉**。
        /// 台服的 <c>AtkUnitBase::FireCallback</c> 在 close 參數為 true、且處理常式回非零時，
        /// 會在**同一個呼叫堆疊裡**先後跑完 vf6 <c>Hide</c> 與 vf4 <c>Close</c>；
        /// 而它的回傳暫存器**只在那個關窗區塊裡**被設為 1。
        /// ⇒ 回傳值的語意是「我有沒有替你把窗關掉」，**不是**「callback 成不成功」。
        /// 把它當成功與否用是靜默誤判（<c>close: false</c> 的呼叫恆回 false）。
        /// 🔴 拿到 true 之後**不准在同一個堆疊裡再碰這扇窗**：<c>AtkUnitBase::Close</c> 沒有
        /// 任何 already-closed 的 early-out，第二發就是攔不到的 AccessViolationException。
        /// </summary>
        public static bool FireContextMenuSelect(
            AtkUnitBase* contextMenu, int index, string addonName, out bool closedByCallback)
        {
            closedByCallback = false;
            if (!AddonPressGuard.TryPress(addonName, (nint)contextMenu, PressKind.Terminal))
                return false;

            var values = stackalloc AtkValue[5];
            values[0].Type = ValueType.Int;
            values[0].Int = 0;
            values[1].Type = ValueType.Int;
            values[1].Int = index;
            values[2].Type = ValueType.UInt;
            values[2].UInt = 0;
            values[3].Type = ValueType.Int;
            values[3].Int = 0;
            values[4].Type = ValueType.Int;
            values[4].Int = 0;
            closedByCallback = contextMenu->FireCallback(5, values, true);
            return true;
        }

        /// <summary>
        /// Fires a single-int callback (list selections use the entry index, -1 closes list addons).
        /// 兩種用法都會讓視窗關掉，所以在守衛裡是同一個 key：已經回答過的那扇窗不准再按。
        /// </summary>
        public static bool FireIntCallback(AtkUnitBase* addon, string addonName, int value)
        {
            if (!AddonPressGuard.TryPress(addonName, (nint)addon, PressKind.Terminal))
                return false;

            var values = stackalloc AtkValue[1];
            values[0].Type = ValueType.Int;
            values[0].Int = value;
            addon->FireCallback(1, values, true);
            return true;
        }

        /// <summary>
        /// Selects the SelectString entry whose text starts with <paramref name="text"/>.
        /// Returns false when no such entry exists (or the addon is not ready).
        ///
        /// 🔴 回傳值的意思是「**有沒有找到那一項**」，不是「有沒有真的送出」：
        /// 守衛擋下這一發時仍然回 true。呼叫端（TickLeaveRetainer）用回傳值決定
        /// 「找不到項目就改成關掉選單」，守衛擋下不代表項目不存在，回 false 會讓它誤按關閉。
        /// </summary>
        public static bool TrySelectStringEntry(AtkUnitBase* selectString, string text)
        {
            if (selectString == null || string.IsNullOrEmpty(text))
                return false;

            var popup = &((AddonSelectString*)selectString)->PopupMenu.PopupMenu;
            if (popup->EntryNames == null)
                return false;

            for (var i = 0; i < popup->EntryCount; i++)
            {
                var namePtr = popup->EntryNames[i];
                if (namePtr.Value == null)
                    continue;
                var entryText = MemoryHelper.ReadSeStringNullTerminated((nint)namePtr.Value).TextValue;
                if (!entryText.StartsWith(text, StringComparison.Ordinal))
                    continue;
                FireIntCallback(selectString, "SelectString", i);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Advances a Talk dialogue (same event sequence as ECommons AddonMaster.Talk.Click, proven on TC via AutoRetainer).
        /// 三發 ReceiveEvent 是**一次**按下（一個滑鼠點擊的 Down/Click/Up），守衛只記一次。
        /// 回 false ＝這一輪沒有送出（取不到 AtkStage，或守衛擋下）。
        /// </summary>
        public static bool ClickTalk(AtkUnitBase* talk)
        {
            // 🔴 AtkStage.Instance() 是 [StaticAddress(..., isPointer: true)]：產生器讀「指標的位址」
            //    再解參考一層，遊戲尚未建立該單例時回 null（非 isPointer 的才保證不回 null）。
            //    `&stage->AtkEventTarget` 在 null 上算出來的是 0（AtkEventTarget 在 +0x0），
            //    把 0 當事件目標交給原生 ReceiveEvent，後果是攔不到的 AVE。
            //    取不到就不點：呼叫端（MultiRetainerTour）本來就會在下一輪重試。
            var stage = AtkStage.Instance();
            if (stage == null) return false;

            // Talk 是「按一次翻一頁、視窗不會因為被按而消失」的多次互動窗：
            // 守衛照樣記位址，但逃生口很短，走逃生口是常態。
            if (!AddonPressGuard.TryPress("Talk", (nint)talk, PressKind.Routine))
                return false;

            var evt = stackalloc AtkEvent[1];
            evt[0] = default;
            evt[0].Listener = (AtkEventListener*)talk;
            evt[0].Target = &stage->AtkEventTarget;
            evt[0].State.StateFlags = (AtkEventStateFlags)132;

            var data = stackalloc AtkEventData[1];
            data[0] = default;

            talk->ReceiveEvent(AtkEventType.MouseDown, 0, evt, data);
            talk->ReceiveEvent(AtkEventType.MouseClick, 0, evt, data);
            talk->ReceiveEvent(AtkEventType.MouseUp, 0, evt, data);
            return true;
        }
    }
}
