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
    // It is good to have this be disposable in general, in case you ever need it
    // to do any cleanup
    internal partial class PluginUI : IDisposable
    {
        private Configuration conf => Configuration.GetOrLoad();

        private Marketbuddy marketbuddy;

        private bool _settingsVisible;

        /// <summary>
        /// 多角色重掛「武裝」失敗的原因。
        /// 🔑 顯示在按鈕旁邊而不是丟到聊天視窗：使用者是在這個視窗裡按的按鈕，
        /// 答案要出現在他正在看的地方。
        /// </summary>
        private string multiCharArmError = string.Empty;

        /// <summary>跨世界價格巡檢的獨立視窗。</summary>
        private readonly PriceSurveyWindow surveyWindow;

        /// <summary>重掛面板裡的「賣給商店」一段；元件沒建起來時是 null。</summary>
        private readonly VendorSellPanel? vendorSellPanel;

        /// <summary>重掛面板裡的「批次上架」一段；元件沒建起來時是 null。</summary>
        private readonly BatchListPanel? batchListPanel;

        // passing in the image here just for simplicity
        public PluginUI(Marketbuddy plugin)
        {
            marketbuddy = plugin;
            surveyWindow = new PriceSurveyWindow(plugin.PriceSurvey, plugin.MarketGuiEventHandler);
            if (plugin.VendorSeller != null)
                vendorSellPanel = new VendorSellPanel(plugin.VendorSeller);
            if (plugin.BatchLister != null)
                batchListPanel = new BatchListPanel(plugin.BatchLister);
            SettingsVisible = false;
        }

        public bool SettingsVisible
        {
            get => _settingsVisible;
            set => _settingsVisible = value;
        }

        /// <summary>
        /// 跨世界價格巡檢視窗開著沒有。刻意**不存檔**：那是一個「現在要做這件事」的視窗，
        /// 不是一個設定；下次進遊戲不該自己跳出來。
        /// </summary>
        public bool SurveyVisible
        {
            get => _surveyVisible;
            set => _surveyVisible = value;
        }

        private bool _surveyVisible;

        public void Dispose()
        {
        }

        /// <summary>
        /// 兩塊側邊面板之間的垂直間距。單純是視覺留白——**不是**排版邏輯的一部分：
        /// 即時掛單面板的位置是從重掛面板**實際量到的下緣**算出來的，不是猜的高度，
        /// 所以重掛面板長高長矮（堆疊列開關、佇列列出現、跑起來變成進度列）都不會重疊。
        /// </summary>
        private const float SidePanelGap = 4f;

        /// <summary>下架巡迴的「已武裝」到期時間；<see cref="DateTime.MinValue"/> = 沒武裝。</summary>
        private DateTime delistArmedUntil = DateTime.MinValue;

        /// <summary>武裝之後多久自動解除。夠久可以看清楚字、夠短不會一直掛著。</summary>
        private static readonly TimeSpan DelistArmWindow = TimeSpan.FromSeconds(6);

        /// <summary>「把停在上限價的那幾件下架」的「已武裝」到期時間；<see cref="DateTime.MinValue"/> = 沒武裝。</summary>
        private DateTime capDelistArmedUntil = DateTime.MinValue;

        /// <summary>本僱員全下架的「已武裝」到期時間；<see cref="DateTime.MinValue"/> = 沒武裝。</summary>
        private DateTime sellListDelistArmedUntil = DateTime.MinValue;

        public void Draw()
        {
            DrawSettingsWindow();
            DrawSurveyWindow();
            // 順序即排版：重掛面板先畫，畫完把它的下緣交給即時掛單面板當作起點。
            // 兩者在同一個 ImGui frame 內先後執行，所以這個值永遠是「這一幀的」，不會慢一拍。
            var repriceBottom = DrawRepriceWindow();
            DrawRetainerListOverlay();
            marketbuddy.LiveSellList.Draw(repriceBottom);
            // 買方那一側的面板貼在市場板搜尋結果視窗旁，與上面那一欄（僱員出售品視窗旁）
            // 位置互不相干，所以不必參與那個堆疊。
            marketbuddy.MarketBuyTally.Draw();
        }

        /// <summary>
        /// 跨世界價格巡檢視窗。關掉視窗＝<b>暫停</b>巡檢：一個看不見的東西不應該還在背景送查詢。
        /// 🔴 關視窗同時<b>解除自動續跑的武裝</b>：暫停對「正在換世界、但沒有在掃描」的狀態是 no-op，少了那一步整條鏈會在看不見的地方繼續跑。
        /// </summary>
        private void DrawSurveyWindow()
        {
            var wasVisible = _surveyVisible;
            surveyWindow.Draw(ref _surveyVisible);
            if (wasVisible && !_surveyVisible)
                surveyWindow.OnClosed();
        }

        private void DrawUndercutTypeSelector()
        {
            if (ImGui.BeginCombo("##undercuttype", conf.UndercutUsePercent ? "%" : "gil"))
            {
                if (ImGui.Selectable("Fixed gil undercut".Loc())) conf.UndercutUsePercent = false;
                if (ImGui.Selectable("Percentage undercut".Loc())) conf.UndercutUsePercent = true;
                ImGui.EndCombo();
            }
        }

        /// <summary>
        /// 使用者沒有設定降價（0 gil 或 0%）。此時「（最低價 -0gil）」不帶任何資訊，
        /// 只會把按鈕撐寬，所以標題把那段括號整個省掉。
        /// </summary>
        private bool UndercutIsZero =>
            conf.UndercutUsePercent ? conf.UndercutPercent == 0 : conf.UndercutPrice == 0;

        /// <summary>
        /// 按鈕的滑鼠提示：把從標題省掉的定價規則交代清楚，而且完全不佔版面。
        /// </summary>
        /// <remarks>
        /// 🔴 <b>講錯的規則比不講更糟。</b>定價規則是「板上最低價」與「最近成交價」
        /// <b>取低者</b>（規則本體見 <see cref="RelistPricing"/>），所以兩句都要說，
        /// 而且要說清楚「只有一個知道時用那一個」。
        /// </remarks>
        private string PricingRuleText()
        {
            var board = UndercutIsZero
                ? "the cheapest listing on your own world that is not yours".Loc()
                : "the cheapest listing on your own world that is not yours, minus ??".Loc(
                    GetUndercutText());
            var roundTo = conf.EffectiveRoundingMultiple();
            if (roundTo > 1)
                board = "??, then rounded down to a multiple of ?? gil".Loc(board, roundTo);

            if (!conf.RelistUseLastSoldPrice)
                return "Prices at ??".Loc(board);

            var sale = conf.RelistLastSoldMaxAgeDays > 0
                ? "the most recent sale on your data centre within the last ?? day(s), rounded down to 100 gil"
                    .Loc(conf.RelistLastSoldMaxAgeDays)
                : "the most recent sale on your data centre, rounded down to 100 gil".Loc();

            return "Prices at whichever of these is lower:".Loc() + "\n  - " + board + "\n  - " + sale
                   + "\n"
                   + "If only one of the two is known, that one is used; if neither is, the item is left alone."
                       .Loc();
        }

        private string GetUndercutText(bool escape = false)
        {
            if (conf.UndercutUsePercent)
            {
                return DrawText.Concat(conf.UndercutPercent, escape ? "%%" : "%");
            }
            else
            {
                return DrawText.Concat(conf.UndercutPrice, "gil");
            }
        }

        /// <summary>等同 <c>template.Loc(index, total, name)</c>（「第幾件／共幾件：哪一件」那一行）。</summary>
        private static string ProgressText(string template, int index, int total, string name)
            => DrawText.Loc(template, (index, total, name), static (t, a) => t.Loc(a.index, a.total, a.name));

        private void MaximumStackSizeChanged()
        {
            conf.MaximumStackSize = conf.MaximumStackSize <= 9999
                ? conf.MaximumStackSize >= 1 ? conf.MaximumStackSize : 1
                : 9999;
            conf.Save();
        }

        private void UndercutPriceChanged()
        {
            if (conf.UndercutPrice < 0)
                conf.UndercutPrice = 0;
            if (conf.UndercutPercent > 99) conf.UndercutPercent = 99;
            if (conf.UndercutPercent < 0) conf.UndercutPercent = 0;
            conf.Save();
        }

        private static void DrawNestIndicator(int depth)
        {
            // https://github.com/DelvUI/DelvUI/blob/62b28ce1901f374ec167c26ce9fcf3afaf2adb13/DelvUI/Config/Tree/FieldNode.cs#L58

            // This draws the L shaped symbols and padding to the left of config items collapsible under a checkbox.
            // Shift cursor to the right to pad for children with depth more than 1.
            // 26 is an arbitrary value I found to be around half the width of a checkbox
            ImGui.SetCursorPos(ImGui.GetCursorPos() + new Vector2(26, 0) * Math.Max((depth - 1), 0));

            var color = ImGui.GetStyle().Colors[(int)ImGuiCol.Text];

            ImGui.TextColored(new Vector4(color.X, color.Y, color.Z, 0.9f), "\u2002\u2514");
            //ImGui.TextColored(new Vector4(229f / 255f, 57f / 255f, 57f / 255f, 1f), "\u2002\u2514");
            ImGui.SameLine();
        }

        /// <summary>
        /// 一列「文字＋同一行的輸入框」：輸入框（含右邊的標籤共 <paramref name="nextWidth"/> 寬）接在同一行放得下就接，
        /// 放不下就移到下一行、對齊這一列的起點 <paramref name="rowStartX"/>（視窗內座標）。
        /// </summary>
        private static void SameLineOrBelow(float rowStartX, float nextWidth)
        {
            if (!SameLineIfFits(nextWidth))
                ImGui.SetCursorPosX(rowStartX);
        }

        private static void PushStyleDisabled()
        {
            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.2f);
        }

        private static void PopStyleDisabled()
        {
            ImGui.PopStyleVar();
        }
    }
}