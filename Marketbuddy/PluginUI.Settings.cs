using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;
using Marketbuddy.Common;
using static Marketbuddy.Common.ImGuiEx;

namespace Marketbuddy
{
    internal partial class PluginUI
    {
        /// <summary>
        /// 設定視窗（分頁）。
        /// 🔴 IPC 鎖的告示刻意留在**分頁列之外的最上面**：那是「整個外掛已經停擺」的狀態，
        /// 藏進任何一個分頁都等於要使用者先猜對分頁才知道自己被鎖住了。
        /// </summary>
        public void DrawSettingsWindow()
        {
            if (!SettingsVisible) return;

            if (!ImGui.Begin("Marketbuddy config".Loc() + "###MarketbuddyConfig", ref _settingsVisible,
                    ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize))
            {
                ImGui.End();
                return;
            }

            DrawIpcLockNotice();

            // 🔑 視窗是 AlwaysAutoResize，而每個分頁的內容寬度天生不一樣——沒有一個共同的
            // 寬度下限，每切一次分頁整個視窗就跳一次大小。這只是視覺上的錨，不碰任何設定。
            ImGui.Dummy(new Vector2(SettingsContentWidth, 0f));

            if (ImGui.BeginTabBar("##mbsettingstabs"))
            {
                if (ImGui.BeginTabItem("Prices".Loc() + "##mbtabprices"))
                {
                    DrawPriceSettingsTab();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Relisting".Loc() + "##mbtabrelist"))
                {
                    DrawRelistSettingsTab();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Delisting".Loc() + "##mbtabdelist"))
                {
                    DrawDelistSettingsTab();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Panels & diagnostics".Loc() + "##mbtabpanels"))
                {
                    DrawPanelSettingsTab();
                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }

            ImGui.End();
        }

        /// <summary>
        /// 別的外掛透過 IPC 送來的停止鎖。
        /// 🔴 畫在分頁列**上面**：這是「整個外掛已經停擺」的狀態，不能藏進某一個分頁。
        /// </summary>
        private void DrawIpcLockNotice()
        {
            // 🔴 先拍快照再判空：直接讀 Count 之後又列舉集合，中間可能被 IPC 執行緒插入。
            var locks = IPCManager.SnapshotLocks();
            if (locks.Length == 0)
                return;

            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudRed);
            ImGui.TextWrapped(
                "Lock commands has been received from these plugins and Marketbuddy operation is fully halted:"
                    .Loc());
            ImGui.TextUnformatted($"{string.Join("\n", locks)}");
            if (ImGui.Button("Release locks".Loc()))
            {
                IPCManager.ClearLocks();
            }

            ImGui.PopStyleColor();
        }
    }
}