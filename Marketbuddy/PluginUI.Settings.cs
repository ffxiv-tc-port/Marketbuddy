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
        /// <summary>設定視窗左側的分類（英文原文＝loc 鍵）；順序＝畫面順序，索引對應 <see cref="DrawSettingsPage"/>。</summary>
        private static readonly string[] SettingsPages =
        [
            "Price list window",
            "Listing",
            "Relist pricing",
            "Price safeguards",
            "All retainers & multi-character",
            "Delisting",
            "Clear-out",
            "Panels",
            "Survey & diagnostics",
        ];

        /// <summary>目前選的分類。刻意不存檔：只是「看到哪一頁」，不是設定。</summary>
        private int settingsPage;

        /// <summary>
        /// 設定視窗：左側分類清單＋右側可捲動的內容。
        /// 🔴 IPC 鎖的告示刻意留在**分類之外的最上面**：那是「整個外掛已經停擺」的狀態，
        /// 藏進任何一個分類都等於要使用者先猜對分類才知道自己被鎖住了。
        /// </summary>
        public void DrawSettingsWindow()
        {
            if (!SettingsVisible) return;

            // 視窗 id 換過：舊 id 存的是自動撐高的尺寸，可能比螢幕還高。
            var scale = ImGuiHelpers.GlobalScale;
            var minSize = new Vector2(560f, 320f) * scale;
            ImGui.SetNextWindowSize(new Vector2(900f, 600f) * scale, ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSizeConstraints(minSize, Vector2.Max(minSize, ImGui.GetMainViewport().WorkSize));
            if (!ImGui.Begin("Marketbuddy config".Loc() + "###MarketbuddyConfigV2", ref _settingsVisible,
                    ImGuiWindowFlags.NoCollapse))
            {
                ImGui.End();
                return;
            }

            DrawIpcLockNotice();

            var navWidth = 0f;
            foreach (var page in SettingsPages)
                navWidth = Math.Max(navWidth, ImGui.CalcTextSize(page.Loc()).X);
            navWidth += ImGui.GetStyle().WindowPadding.X * 2 + ImGui.GetStyle().FramePadding.X * 2;

            ImGui.BeginChild("##mbsettingsnav", new Vector2(navWidth, 0f), true);
            for (var i = 0; i < SettingsPages.Length; i++)
            {
                ImGui.PushID(i);
                if (ImGui.Selectable(SettingsPages[i].Loc(), settingsPage == i))
                    settingsPage = i;
                ImGui.PopID();
            }

            ImGui.EndChild();

            ImGui.SameLine();
            ImGui.BeginChild("##mbsettingspage", Vector2.Zero, false, ImGuiWindowFlags.HorizontalScrollbar);
            DrawSettingsPage(Math.Clamp(settingsPage, 0, SettingsPages.Length - 1));
            ImGui.EndChild();

            ImGui.End();
        }

        private void DrawSettingsPage(int page)
        {
            ImGui.TextUnformatted(SettingsPages[page].Loc());
            ImGui.Separator();
            ImGui.Spacing();
            switch (page)
            {
                case 0: DrawPriceSettingsTab(); break;
                case 1: DrawListingSettingsPage(); break;
                case 2: DrawRelistPricingPage(); break;
                case 3: DrawPriceSafeguardsPage(); break;
                case 4: DrawTourSettingsPage(); break;
                case 5: DrawDelistSettingsTab(); break;
                case 6: DrawClearOutTab(); break;
                case 7: DrawPanelSettingsTab(); break;
                case 8: DrawSurveyAndDiagnosticsPage(); break;
            }
        }

        /// <summary>列尾的「(?)」：長說明收進滑鼠提示，<paramref name="more"/> 另起一段。放不下就換到下一行。</summary>
        private static void HelpMarker(string text, string? more = null)
        {
            SameLineIfFits(ImGui.CalcTextSize("(?)").X);
            ImGui.TextDisabled("(?)");
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                WrappedTooltip(text, more);
        }

        /// <summary>會自動換行的滑鼠提示；停用（或淡化）區段裡打開也要全亮。</summary>
        private static void WrappedTooltip(string text, string? more = null)
        {
            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 1f);
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 32f);
            ImGui.TextUnformatted(text);
            if (more != null)
            {
                ImGui.Spacing();
                ImGui.TextUnformatted(more);
            }

            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
            ImGui.PopStyleVar();
        }

        /// <summary>
        /// 別的外掛透過 IPC 送來的停止鎖。
        /// 🔴 畫在分類**上面**：這是「整個外掛已經停擺」的狀態，不能藏進某一個分類。
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
