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

            DrawLiveSellListDisplayOptions(conf);

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

            // 重掛面板與即時掛單面板是**同一欄**（重掛面板貼基準點、即時掛單接在它外側），
            // 所以位置只有一組，調它就是整欄一起動。
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            var sidePanelsOn = conf.LiveSellListOverlay || conf.AdjustMaxStackSizeInSellList || conf.BatchRepriceEnabled;
            if (!sidePanelsOn) ImGui.BeginDisabled();
            var sellListOpen = marketbuddy.MarketGuiEventHandler.AddonRetainerSellList_Frame(out var sellTopLeft, out var sellSize);
            DrawSideColumnPositionOptions(conf, sellListOpen, sellTopLeft, sellSize);
            if (!sidePanelsOn) ImGui.EndDisabled();
            if (!sidePanelsOn)
                HelpMarker(
                    "Only used when a panel next to the sell list is turned on: the live sell list, the stack-size adjustment or the one-click relist button."
                        .Loc());

            ImGui.Spacing();
            var retainerPanelOn = conf.BatchRepriceEnabled;
            if (!retainerPanelOn) ImGui.BeginDisabled();
            var retainerListOpen = marketbuddy.MarketGuiEventHandler.AddonRetainerList_Frame(out var retTopLeft, out var retSize);
            if (DrawAnchorOptions("##mbretainerpanel", "All-retainers panel anchor".Loc(),
                    ref conf.RetainerPanelAnchor, ref conf.RetainerPanelOffset, retainerListOpen, retTopLeft, retSize))
                conf.Save();
            if (!retainerPanelOn) ImGui.EndDisabled();
            if (!retainerPanelOn)
                HelpMarker("Only used with \"Show a one-click relist button in the retainer sell list\" turned on (Relist pricing).".Loc());
        }

        /// <summary>
        /// 即時掛單面板的顯示項目。設定分頁與面板標題列的「選項」彈出框共用這一個方法，兩邊永遠一致。
        /// </summary>
        internal static void DrawLiveSellListDisplayOptions(Configuration conf)
        {
            DrawNestIndicator(1);
            ImGui.TextUnformatted("Shown on the live sell list:".Loc());
            HelpMarker(
                "The item name is always shown - it is what tells the rows apart. Hiding a column only hides it; nothing that feeds the other columns or the relist pricing changes."
                    .Loc());

            DrawNestIndicator(2);
            var changed = ImGui.Checkbox(DrawText.Concat("Retainer name".Loc(), "##mblslname"),
                ref conf.LiveSellListShowRetainerName);
            ImGui.SameLine();
            changed |= ImGui.Checkbox(DrawText.Concat("Qty".Loc(), "##mblsqty"), ref conf.LiveSellListShowQuantity);
            ImGui.SameLine();
            changed |= ImGui.Checkbox(DrawText.Concat("Unit price".Loc(), "##mblsunit"), ref conf.LiveSellListShowUnitPrice);
            ImGui.SameLine();
            changed |= ImGui.Checkbox(DrawText.Concat("Total".Loc(), "##mblstotal"), ref conf.LiveSellListShowTotal);

            DrawNestIndicator(2);
            changed |= ImGui.Checkbox("Also show market prices and how long each item has been listed".Loc(),
                ref conf.LiveSellListMarketColumns);
            HelpMarker(
                "Four extra columns, purely informational: the cheapest listing on your own world, the cheapest anywhere on your data centre, what the item last actually sold for (with the date), and how long it has been sitting on the board. The first three come from the Universalis web API in the same request the relist pricing uses, so nothing is asked of the game's market board; where Universalis has nothing the cell shows a grey question mark - never a zero. The game never says when something was listed, so the age is counted from the moment Marketbuddy watched it go up; anything that was already listed before then is marked with a ~ and counted from when the plugin loaded. Nothing here triggers anything - an item sitting there for a month is never relisted or delisted for you."
                    .Loc(),
                "The age clock is fed by the same look at the sell list that the sales log uses, so turning off \"Record what disappears from your retainers' listings\" stops it too."
                    .Loc());

            // 四欄各自的開關掛在總開關底下；總開關關著時停用（照樣畫出來，不然沒開的人不知道有）。
            var marketOn = conf.LiveSellListMarketColumns;
            if (!marketOn) ImGui.BeginDisabled();
            DrawNestIndicator(3);
            changed |= ImGui.Checkbox(DrawText.Concat("Lowest here".Loc(), "##mblslowworld"),
                ref conf.LiveSellListShowLowestWorld);
            ImGui.SameLine();
            changed |= ImGui.Checkbox(DrawText.Concat("Lowest on DC".Loc(), "##mblslowdc"),
                ref conf.LiveSellListShowLowestDc);
            ImGui.SameLine();
            changed |= ImGui.Checkbox(DrawText.Concat("Last sale".Loc(), "##mblslastsale"),
                ref conf.LiveSellListShowLastSale);
            ImGui.SameLine();
            changed |= ImGui.Checkbox(DrawText.Concat("Listed for".Loc(), "##mblsage"),
                ref conf.LiveSellListShowListedFor);
            if (!marketOn) ImGui.EndDisabled();

            DrawNestIndicator(2);
            changed |= ImGui.Checkbox("Also show how often each item has actually sold".Loc(),
                ref conf.LiveSellListSalesHistoryColumn);
            HelpMarker(
                "One extra column: how many times that item has sold, and how many times it came off the board without selling - counted from your own sales records, so no web request and nothing asked of the game. It answers \"is it even worth listing this again\" while you are standing at the sell list. Where there is no basis for an answer - the recording is off, or Marketbuddy has never had that retainer's list open - the cell shows a grey question mark instead of a zero, because \"we never watched\" and \"it never sold\" lead to opposite decisions. Purely informational: nothing is ever delisted or repriced because of these numbers."
                    .Loc());

            var relistOn = conf.RelistUseLastSoldPrice;
            if (!relistOn) ImGui.BeginDisabled();
            DrawNestIndicator(2);
            changed |= ImGui.Checkbox(DrawText.Concat("Relist at".Loc(), "##mblsrelist"),
                ref conf.LiveSellListShowRelistColumn);
            if (!relistOn) ImGui.EndDisabled();
            HelpMarker(
                "What the relist button would list each item at. Only has something to show while \"Also price from the most recent sale, and use whichever is lower\" is turned on (Relist pricing); hiding it changes nothing about the pricing itself."
                    .Loc());

            if (changed)
                conf.Save();
        }

        private static readonly string[] AnchorLabelKeys =
        [
            "Right side, top edges aligned",
            "Right side, bottom edges aligned",
            "Left side, top edges aligned",
            "Left side, bottom edges aligned",
            "Fixed spot on the screen",
        ];

        /// <summary>出售品視窗旁那一欄（重掛面板＋即時掛單）的基準點與偏移；設定分頁與面板彈出框共用。</summary>
        internal static void DrawSideColumnPositionOptions(Configuration conf, bool nativeOpen, Vector2 nativeTopLeft,
            Vector2 nativeSize)
        {
            if (DrawAnchorOptions("##mbsidecolumn", "Side panel anchor".Loc(), ref conf.LiveSellListAnchor,
                    ref conf.LiveSellListOffset, nativeOpen, nativeTopLeft, nativeSize,
                    "Moves both side panels at once: the relisting controls sit right at the anchor point (at the bottom when the anchor is a bottom edge) and the live listing table is stacked directly against them."
                        .Loc()))
                conf.Save();
        }

        /// <summary>基準點下拉選單＋偏移；回傳 true＝值變了、呼叫端要存檔。</summary>
        /// <param name="nativeOpen">原生視窗這一幀開著（<paramref name="nativeTopLeft"/>／<paramref name="nativeSize"/> 才有意義）。</param>
        private static bool DrawAnchorOptions(string id, string label, ref PanelAnchor anchor, ref Vector2 offset,
            bool nativeOpen, Vector2 nativeTopLeft, Vector2 nativeSize, string? more = null)
        {
            var changed = false;
            var current = PanelPlacementMath.Normalize(anchor);
            ImGui.TextUnformatted(label);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(220);
            if (ImGui.BeginCombo(DrawText.Concat(id, "combo"), AnchorLabelKeys[(int)current].Loc()))
            {
                for (var i = 0; i < AnchorLabelKeys.Length; i++)
                {
                    if (!ImGui.Selectable(AnchorLabelKeys[i].Loc(), i == (int)current) || i == (int)current)
                        continue;
                    var next = (PanelAnchor)i;
                    offset = PanelPlacementMath.OffsetAfterSwitch(current, next, offset, nativeOpen, nativeTopLeft,
                        nativeSize);
                    anchor = next;
                    changed = true;
                }

                ImGui.EndCombo();
            }

            HelpMarker(
                "The panel is placed against this point of the game window and follows it every frame, whatever its position, size or scale. On the left side the panel's right edge stays against the game window however wide the panel gets, so it never covers it. Picking a different point resets the offset to a small gap; \"Fixed spot on the screen\" starts from where the panel is anchored now if that window is open."
                    .Loc(), more);

            ImGui.SetNextItemWidth(200);
            ImGui.DragFloat2(DrawText.Concat(
                    current == PanelAnchor.Screen ? "Position on the screen".Loc() : "Offset from the anchor".Loc(),
                    DrawText.Concat(id, "offset")),
                ref offset, 1f, -4000f, 4000f, "%.0f");
            if (ImGui.IsItemDeactivatedAfterEdit())
                changed = true;
            ImGui.SameLine();
            if (ImGui.SmallButton(DrawText.Concat("Reset".Loc(), DrawText.Concat(id, "reset"))))
            {
                offset = PanelPlacementMath.DefaultOffset(current);
                changed = true;
            }

            return changed;
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
