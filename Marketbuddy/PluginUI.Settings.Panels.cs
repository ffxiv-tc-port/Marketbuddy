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
        /// <summary>「面板」：即時掛單面板、市場板買入累計面板，以及側邊面板的位置。</summary>
        private void DrawPanelSettingsTab()
        {
            if (ImGui.Checkbox("Show a live sell list next to the game's one".Loc(), ref conf.LiveSellListOverlay))
                conf.Save();
            HelpMarker(
                "Batch repricing writes prices straight into the retainer's market container without opening any game window - which is exactly why it is fast, but it also means the game's sell list never redraws and keeps showing the prices it had when you opened it (a just-listed item stays at 999,999,999 on screen even though the server already has the right price). This panel is drawn by the plugin and re-read every frame, so it is always current. It only reads: no game windows are touched and nothing is clicked for you."
                    .Loc());

            DrawNestIndicator(1);
            if (ImGui.Checkbox("Also show market prices and how long each item has been listed".Loc(),
                    ref conf.LiveSellListMarketColumns))
                conf.Save();
            HelpMarker(
                "Four extra columns, purely informational: the cheapest listing on your own world, the cheapest anywhere on your data centre, what the item last actually sold for (with the date), and how long it has been sitting on the board. The first three come from the Universalis web API in the same request the relist pricing uses, so nothing is asked of the game's market board; where Universalis has nothing the cell shows a grey question mark - never a zero. The game never says when something was listed, so the age is counted from the moment Marketbuddy watched it go up; anything that was already listed before then is marked with a ~ and counted from when the plugin loaded. Nothing here triggers anything - an item sitting there for a month is never relisted or delisted for you."
                    .Loc(),
                "The age clock is fed by the same look at the sell list that the sales log uses, so turning off \"Record what disappears from your retainers' listings\" stops it too."
                    .Loc());

            DrawNestIndicator(1);
            if (ImGui.Checkbox("Also show how often each item has actually sold".Loc(),
                    ref conf.LiveSellListSalesHistoryColumn))
                conf.Save();
            HelpMarker(
                "One extra column: how many times that item has sold, and how many times it came off the board without selling - counted from your own sales records, so no web request and nothing asked of the game. It answers \"is it even worth listing this again\" while you are standing at the sell list. Where there is no basis for an answer - the recording is off, or Marketbuddy has never had that retainer's list open - the cell shows a grey question mark instead of a zero, because \"we never watched\" and \"it never sold\" lead to opposite decisions. Purely informational: nothing is ever delisted or repriced because of these numbers."
                    .Loc());

            ImGui.Spacing();
            if (ImGui.Checkbox("Show a running total next to the market board's results".Loc(),
                    ref conf.MarketBuyTallyOverlay))
                conf.Save();
            HelpMarker(
                "Buying one thing off the market board usually means clicking a lot of rows, and the game never tells you where you are up to. This panel keeps the running count and the running gil for the trip, and adds a small table showing what the cheapest few listings add up to, so \"is this enough\" and \"what will this cost\" are answered on screen instead of in your head. Type in how many you want and the row that gets you there is highlighted. It only reads: nothing is ever bought, clicked or queried for you."
                    .Loc());

            // 子選項一律畫出來（上一層關著時停用），不然沒開的人永遠不知道有這些選項。
            var buyTallyOn = conf.MarketBuyTallyOverlay;
            if (!buyTallyOn) ImGui.BeginDisabled();
            DrawNestIndicator(1);
            ImGui.SetNextItemWidth(70);
            if (ImGui.InputInt(DrawText.Concat("rows in the running-total table".Loc(), "##mbbuytallyrows"),
                    ref conf.MarketBuyPreviewRows, 0))
            {
                conf.MarketBuyPreviewRows = Math.Clamp(conf.MarketBuyPreviewRows, 3, 20);
                conf.Save();
            }

            DrawNestIndicator(1);
            ImGui.SetNextItemWidth(200);
            ImGui.DragFloat2("Buy panel position (relative to the results window's top right)".Loc(),
                ref conf.MarketBuyPanelOffset, 1f, -4000f, 4000f, "%.0f");
            if (ImGui.IsItemDeactivatedAfterEdit())
                conf.Save();
            if (!buyTallyOn) ImGui.EndDisabled();

            // 重掛面板與即時掛單面板是**同一欄**（重掛在上、即時掛單接在下面），
            // 所以位置只留一個滑桿，拖它就是整欄一起動。範圍可負：往左／往上也要調得動。
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            var sidePanelsOn = conf.LiveSellListOverlay || conf.AdjustMaxStackSizeInSellList || conf.BatchRepriceEnabled;
            if (!sidePanelsOn) ImGui.BeginDisabled();
            ImGui.SetNextItemWidth(200);
            ImGui.DragFloat2("Side panel position (relative to the sell list's top right)".Loc(),
                ref conf.LiveSellListOffset, 1f, -4000f, 4000f, "%.0f");
            if (ImGui.IsItemDeactivatedAfterEdit())
                conf.Save();
            if (!sidePanelsOn) ImGui.EndDisabled();
            HelpMarker(
                "Moves both side panels at once: the relisting controls sit at the top right of the game's sell list and the live listing table is stacked directly underneath them."
                    .Loc(),
                sidePanelsOn
                    ? null
                    : "Only used when a panel next to the sell list is turned on: the live sell list, the stack-size adjustment or the one-click relist button."
                        .Loc());

            var retainerPanelOn = conf.BatchRepriceEnabled;
            if (!retainerPanelOn) ImGui.BeginDisabled();
            ImGui.SetNextItemWidth(200);
            ImGui.DragFloat2("All-retainers panel position (relative to the retainer list's top right)".Loc(),
                ref conf.RetainerPanelOffset, 1f, -4000f, 4000f, "%.0f");
            if (ImGui.IsItemDeactivatedAfterEdit())
                conf.Save();
            if (!retainerPanelOn) ImGui.EndDisabled();
            if (!retainerPanelOn)
                HelpMarker("Only used with \"Show a one-click relist button in the retainer sell list\" turned on (Relist pricing).".Loc());
        }

        /// <summary>「巡檢與診斷」：跨世界價格巡檢的入口與診斷記錄。</summary>
        private void DrawSurveyAndDiagnosticsPage()
        {
            DrawPriceSurveyEntry();

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            if (ImGui.Checkbox("Write the detailed market diagnostics to the log".Loc(),
                    ref conf.VerboseMarketDiagnostics))
                conf.Save();
            HelpMarker(
                "Off by default, and while it is off these lines are not written at all. Turn it on when someone asks you for a market-board log; leaving it on makes the log a lot noisier. Refusals, timeouts and market errors are reported regardless of this setting."
                    .Loc());
        }

        /// <summary>
        /// 跨世界價格巡檢的入口。
        /// 🔴 這裡只有「顯示這個功能」與「打開它的視窗」兩件事——真的要跑一定要使用者
        /// 自己在那個視窗裡按下「掃描這個世界」，沒有任何自動觸發。
        /// </summary>
        private void DrawPriceSurveyEntry()
        {
            if (ImGui.Checkbox("Cross-world price survey (reads only, never changes a price)".Loc(),
                    ref conf.PriceSurveyEnabled))
                conf.Save();
            HelpMarker(
                "Take your own listings around the worlds and look up what each of them is going for, one item at a time, then compare. Nothing happens on its own: you press a button once per world, and travelling to the next world is a separate button - unless you arm a round yourself in the survey window, which is off by default. It only ever sends the same market query the game's own search sends - no price is changed, nothing is listed or delisted. Results are appended to price_survey.csv in this plugin's config folder."
                    .Loc());

            // 純通知：只有「整份掃完」會叫它，按停與讓路都不會。
            DrawNestIndicator(1);
            if (ImGui.Checkbox("Ask TataruPraise to say a line when a survey finishes".Loc(),
                    ref conf.TataruPraiseOnSurveyDone))
                conf.Save();
            HelpMarker(
                "Needs the TataruPraise plugin; without it this does nothing at all. It only sends a notification - no extra market query is sent and nothing about the survey changes. It speaks once, and only when this world's list was surveyed all the way to the end: stopping it yourself, standing down for a relist or a delist, giving up after the first item got no answer, or failing to build the list at all, all stay quiet."
                    .Loc());

            DrawNestIndicator(1);
            if (ImGui.Button("Open the price survey window".Loc()))
                SurveyVisible = true;
        }
    }
}
