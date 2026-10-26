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
        /// 「比價視窗」：市場比價視窗那一套互動——查詢卡住時自動重試、自動開窗、
        /// 修飾鍵、點一個價格要做什麼，以及那些動作共用的降價幅度。
        /// </summary>
        private void DrawPriceSettingsTab()
        {
            if (ImGui.Checkbox("Auto-retry a market search that looks throttled/stuck".Loc(), ref conf.AutoRequeryOnThrottle))
                conf.Save();
            HelpMarker(
                "Applies to any market price list window - the market board search and the retainer sell \"compare prices\" popup alike. No packets, hooks or memory edits: if the server keeps rejecting the query, this simply gives up after a few tries and the window is left as-is."
                    .Loc());

            ImGui.Spacing();
            if (ImGui.Checkbox("Open current prices list when adjusting a price".Loc(), ref conf.AutoOpenComparePrices))
                conf.Save();

            DrawNestIndicator(1);
            if (ImGui.Checkbox(
                    DrawText.Loc("Holding SHIFT ??", conf.AutoOpenComparePrices
                        ? "prevents the above".Loc()
                        : "does the above".Loc()),
                    ref conf.HoldShiftToStop))
                conf.Save();


            ImGui.Spacing();
            if (ImGui.Checkbox("Holding CTRL pastes a price from the clipboard and confirms it".Loc(),
                    ref conf.HoldCtrlToPaste))
                conf.Save();

            ImGui.Spacing();
            if (ImGui.Checkbox("Open price history together with current prices list".Loc(), ref conf.AutoOpenHistory))
                conf.Save();


            DrawNestIndicator(1);
            if (ImGui.Checkbox(
                    DrawText.Loc("Holding ALT ??", conf.AutoOpenHistory ? "prevents the above".Loc() : "does the above".Loc()),
                    ref conf.HoldAltHistoryHandling))
                conf.Save();

            ImGui.Spacing();
            ImGui.SetNextItemWidth(45);
            if (conf.UndercutUsePercent)
            {
                if (ImGui.InputInt("##percundercut", ref conf.UndercutPercent, 0))
                    UndercutPriceChanged();
            }
            else
            {
                if (ImGui.InputInt("##gilundercut", ref conf.UndercutPrice, 0))
                    UndercutPriceChanged();
            }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(55);
            DrawUndercutTypeSelector();
            ImGui.SameLine();
            ImGui.TextUnformatted("undercut over the selected price".Loc());

            DrawPriceRoundingSettings();

            DrawNestIndicator(1);
            if (ImGui.Checkbox(
                    DrawText.Loc("Clicking a price copies that price with a ?? undercut to the clipboard", GetUndercutText()),
                    ref conf.SaveToClipboard))
                conf.Save();

            DrawNestIndicator(1);
            if (ImGui.Checkbox(
                    DrawText.Loc("Clicking a price sets your price as that price with a ?? undercut", GetUndercutText()),
                    ref conf.AutoInputNewPrice))
            {
                if (!conf.AutoInputNewPrice)
                    conf.AutoConfirmNewPrice = false;
                conf.Save();
            }

            DrawNestIndicator(2);
            if (!conf.AutoInputNewPrice) PushStyleDisabled();
            if (ImGui.Checkbox(
                    "Closes the price list and confirms the new price after selecting it from the list".Loc(),
                    ref conf.AutoConfirmNewPrice))
            {
                if (!conf.AutoInputNewPrice)
                    conf.AutoConfirmNewPrice = false;
                conf.Save();
            }

            if (!conf.AutoInputNewPrice) PopStyleDisabled();
        }

        /// <summary>
        /// 降價的尾數捨去：比價列表點價、重掛引擎、「即時掛單」面板與待處理清單都吃這一組
        /// （規則本體見 <see cref="RelistPricing.RoundDownToMultiple"/>）。
        /// </summary>
        private void DrawPriceRoundingSettings()
        {
            DrawNestIndicator(1);
            var roundRowX = ImGui.GetCursorPosX();
            if (ImGui.Checkbox(DrawText.Concat("Round the undercut price down to a multiple of".Loc(), "##mbroundenable"),
                    ref conf.EnablePriceRounding))
                conf.Save();

            // 推／彈只看這個區域變數：上面的勾選框同一幀改得到設定欄位。
            var roundingOn = conf.EnablePriceRounding;
            var roundLabel = DrawText.Concat("gil (1 = no rounding)".Loc(), "##mbroundmultiple");
            SameLineOrBelow(roundRowX, LabeledItemWidth(90, roundLabel));
            if (!roundingOn) PushStyleDisabled();
            ImGui.SetNextItemWidth(90);
            if (ImGui.InputInt(roundLabel, ref conf.PriceRoundingMultiple, 0))
            {
                conf.PriceRoundingMultiple = Math.Clamp(conf.PriceRoundingMultiple, 1, Configuration.MAX_PRICE);
                conf.Save();
            }
            if (!roundingOn) PopStyleDisabled();
            HelpMarker(
                "Every price worked out from the cheapest listing - clicking a price in the current prices list, \"Relist this retainer\", \"Relist all retainers\", and the figures in the live listings panel and the pending list - is lowered to a multiple of this number: with 100, 12,345 becomes 12,300. It only ever goes down. If that would take it below 1 gil or below your \"delist items whose target price is below\" minimum, that price is left unrounded. With an undercut of 0, a lowest price already on a multiple is matched, not undercut. Prices taken from the most recent sale are not affected."
                    .Loc());
        }
    }
}