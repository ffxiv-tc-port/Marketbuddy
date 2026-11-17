using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Dalamud.Game;
using Dalamud.Game.Gui;
using Dalamud.Game.NativeWrapper;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Logging;
using Dalamud.Memory;
using Dalamud.Plugin;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Component.GUI;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy.Common
{
    // based on https://github.com/Caraxi/SimpleTweaksPlugin/blob/main/Helper/Common.cs
    // and https://github.com/daemitus/ClickLib/blob/master/ClickLib/ClickBase.cs
    internal static class Commons
    {
        // Decodes through SeString instead of a raw UTF-8 read, so text that
        // carries payloads (e.g. an item link in the retainer sell window's item
        // name) yields the displayed text rather than the raw payload bytes.
        // Payload-free input decodes to the same string, so the numeric call sites are unaffected.
        public static unsafe string Utf8StringToString(Utf8String str) {
            if (str.IsEmpty || str.BufUsed <= 1 || str.StringPtr.Value == null)
                return string.Empty;
            return MemoryHelper.ReadSeString(&str).TextValue;
        }

        public static unsafe AtkUnitBase* GetUnitBase(string name, int index = 1)
        {
            return (AtkUnitBase*)Dalamud.GameGui.GetAddonByName(name, index).Address;
        }

        internal static unsafe void SendClick(IntPtr arg1, EventType arg2, uint arg3, void* target)
        {
            SendClick(arg1, arg2, arg3, target, IntPtr.Zero);
        }

        internal static unsafe void SendClick(IntPtr arg1, EventType arg2, uint arg3, void* target, IntPtr arg5)
        {
            var listener = (AtkEventListener*)arg1;

            var arg4 = Marshal.AllocHGlobal(0x40);
            for (var i = 0; i < 0x40; i++)
                Marshal.WriteByte(arg4, i, 0);

            var evt = (AtkEvent*)arg4;
            evt->Target = (AtkEventTarget*)target;
            evt->Listener = listener;

            if (arg5 == IntPtr.Zero)
            {
                arg5 = Marshal.AllocHGlobal(0x40);
                for (var i = 0; i < 0x40; i++)
                    Marshal.WriteByte(arg5, i, 0);
            }

            listener->ReceiveEvent((AtkEventType)arg2, (int)arg3, (AtkEvent*)arg4, (AtkEventData*)arg5);

            Marshal.FreeHGlobal(arg4);
            Marshal.FreeHGlobal(arg5);
        }
    }
}