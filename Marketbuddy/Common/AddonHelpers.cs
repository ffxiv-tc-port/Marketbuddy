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

        /// <summary>RetainerList row selection: Callback(2, index, 0, 0), same shape as ECommons AddonMaster.RetainerList.</summary>
        public static void FireRetainerListSelect(AtkUnitBase* retainerList, uint index)
        {
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
        }

        /// <summary>Context menu entry selection: Callback(0, index, 0u, 0, 0), same shape as AutoRetainer's QuickSellItems.</summary>
        public static void FireContextMenuSelect(AtkUnitBase* contextMenu, int index)
        {
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
            contextMenu->FireCallback(5, values, true);
        }

        /// <summary>Fires a single-int callback (list selections use the entry index, -1 closes list addons).</summary>
        public static void FireIntCallback(AtkUnitBase* addon, int value)
        {
            var values = stackalloc AtkValue[1];
            values[0].Type = ValueType.Int;
            values[0].Int = value;
            addon->FireCallback(1, values, true);
        }

        /// <summary>
        /// Selects the SelectString entry whose text starts with <paramref name="text"/>.
        /// Returns false when no such entry exists (or the addon is not ready).
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
                FireIntCallback(selectString, i);
                return true;
            }

            return false;
        }

        /// <summary>Advances a Talk dialogue (same event sequence as ECommons AddonMaster.Talk.Click, proven on TC via AutoRetainer).</summary>
        public static void ClickTalk(AtkUnitBase* talk)
        {
            // 🔴 AtkStage.Instance() 是 [StaticAddress(..., isPointer: true)]：產生器讀「指標的位址」
            //    再解參考一層，遊戲尚未建立該單例時回 null（非 isPointer 的才保證不回 null）。
            //    `&stage->AtkEventTarget` 在 null 上算出來的是 0（AtkEventTarget 在 +0x0），
            //    把 0 當事件目標交給原生 ReceiveEvent，後果是攔不到的 AVE。
            //    取不到就不點：呼叫端（MultiRetainerTour）本來就會在下一輪重試。
            var stage = AtkStage.Instance();
            if (stage == null) return;

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
        }
    }
}
