using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Lumina.Excel.Sheets;
using Marketbuddy.Common;
using static Marketbuddy.Common.Dalamud;
using static Marketbuddy.Common.ImGuiEx;

namespace Marketbuddy
{
    /// <summary>
    /// 跨世界價格巡檢的操作介面：一個獨立視窗，五個分頁（掃描／比價／採購／待處理／銷售）。
    /// 🔑 UI 規則：「不知道」必須在列上看得見。沒掃過的世界畫灰色的 <c>?</c>、
    /// 確認過沒人在賣畫灰色的 <c>—</c>，<b>絕不畫成 0</b>——0 在價格欄是一個合法但荒謬的值，
    /// 看起來像「那個世界真的有人用 0 gil 在賣」。長說明放 tooltip，列上只留判讀得出來的東西。
    /// </summary>
    internal sealed partial class PriceSurveyWindow
    {
        /// <summary>比價表格最多列幾個世界欄；再多就要橫向捲動了。</summary>
        private const int MaxWorldColumns = 12;

        private readonly PriceSurvey survey;

        /// <summary>拿得到重掛引擎與待處理清單驅動器的那個中介物件。</summary>
        private readonly MarketGuiEventHandler gui;

        private Configuration conf => Configuration.GetOrLoad();

        /// <summary>記錄檔的讀取工作（一定在執行緒池上，檔案會長到幾萬列）。</summary>
        private Task<List<PriceSurveyRow>>? loadTask;

        private bool loadRequested;
        private DateTime loadedAt = DateTime.MinValue;

        /// <summary>上一次看到的 <see cref="PriceSurvey.RunSerial"/>；變了就重讀記錄檔。</summary>
        private int lastSeenRunSerial = -1;

        /// <summary>比價分頁的資料（已經整理成一件一列）。</summary>
        private readonly List<CompareEntry> entries = [];

        /// <summary>記錄檔裡出現過的世界，以及那個世界最後一次被掃到的時間。</summary>
        private readonly List<(uint WorldId, string Name, DateTime LatestAt)> worlds = [];

        private int totalRowsLoaded;

        /// <summary>換世界下拉選單目前選到第幾個。</summary>
        private int travelChoice;

        /// <summary>每個世界最後一次被掃到的時間（下拉選單上標「還沒掃過」用）。</summary>
        private readonly Dictionary<uint, DateTime> worldLatest = new();

        /// <summary>
        /// 比價表裡因為被排除而沒列出來的世界名。
        /// 🔑 <b>一定要畫在畫面上</b>：欄位靜靜地少一個跟「那個世界沒有資料」長得一模一樣。
        /// </summary>
        private readonly SortedSet<string> hiddenExcludedWorlds = new(StringComparer.Ordinal);

        /// <summary>採購表裡因為被排除而沒列出來的世界名。</summary>
        private readonly SortedSet<string> shoppingHiddenExcludedWorlds = new(StringComparer.Ordinal);

        /// <summary>上一次重整表格時看到的排除清單修訂號；變了就重讀。</summary>
        private int lastSeenExclusionRevision = -1;

        // ---- 每幀都會畫到的文字：輸入沒變就不重組（只在繪製執行緒上碰）----

        /// <summary>優質品接在道具名後面的那一段。</summary>
        private static readonly string HqSuffix = " " + (char)SeIconChar.HighQuality;

        private readonly StringBuilder scratch = new();

        /// <summary>排除清單的複本；每幀重填，不重新配置。</summary>
        private readonly List<uint> excludedScratch = [];

        private string? excludedNames;

        /// <summary>兩張表「藏了哪些世界」那一句；null＝要重組（只有重整資料會改那兩個集合）。</summary>
        private string? hiddenExcludedText;

        private string? shoppingHiddenExcludedText;

        private TextSlot<(int, int)> progressText;
        private TextSlot<double> remainingText;
        private TextSlot<(int, int, int, int, int, int)> statsText;
        private TextSlot<IReadOnlyList<string>> pendingExcludedText;

        /// <summary>比價分頁的排序方式。</summary>
        private int sortMode;

        private static readonly string[] SortModeKeys =
        [
            "Undercut amount (largest first)",
            "Item id",
            "Your listing price (highest first)",
        ];

        // ---- 採購分頁 ----

        /// <summary>採購行情記錄檔的讀取工作（一定在執行緒池上）。</summary>
        private Task<List<ShoppingSurveyRow>>? shoppingLoadTask;

        private bool shoppingLoadRequested;
        private DateTime shoppingLoadedAt = DateTime.MinValue;
        private int shoppingTotalRowsLoaded;

        /// <summary>採購分頁的資料（一件一列）。</summary>
        private readonly List<ShoppingEntry> shoppingEntries = [];

        /// <summary>採購記錄檔裡出現過的世界。</summary>
        private readonly List<(uint WorldId, string Name, DateTime LatestAt)> shoppingWorlds = [];

        private int shoppingSortMode;

        private static readonly string[] ShoppingSortModeKeys =
        [
            "Biggest gap between worlds (largest first)",
            "Cheapest price (lowest first)",
            "Item id",
        ];

        /// <summary>道具 → 使用者寫在清單檔裡的「我想買幾個」。跟著清單檔的世代號重建。</summary>
        private readonly Dictionary<uint, int> shoppingWantedById = new();

        private int shoppingWantedGeneration = -1;

        // ---- 待處理分頁 ----

        /// <summary>已經按過「跳過」的那些要不要一起顯示出來。刻意不存檔：它是一個當下的檢視選項。</summary>
        private bool showSkipped;

        /// <summary>四個桶的顯示順序。先處理最急的（掛在上限價＝現在沒人買得起）。</summary>
        private static readonly PendingActionKind[] BucketOrder =
        [
            PendingActionKind.PriceCap,
            PendingActionKind.PriceAnomaly,
            PendingActionKind.BelowMinimum,
            PendingActionKind.Undercut,
        ];

        /// <summary>一個桶的列；四個桶先後共用同一份（填好、排好、畫完才輪到下一個），不每幀配置。</summary>
        private readonly List<PendingActionRow> bucketScratch = [];

        // ---- 銷售分頁 ----

        /// <summary>銷售記錄檔的讀取工作（一定在執行緒池上）。</summary>
        private Task<List<RetainerSaleRow>>? salesTask;

        /// <summary>畫面上顯示的列（檔案列＋這個工作階段剛寫的列，已去重並由新到舊排序）。</summary>
        private readonly List<RetainerSaleRow> sales = [];

        /// <summary>已經載入的是哪一代記錄（<see cref="RetainerSalesLog.Revision"/>）；-1＝還沒載過。</summary>
        private long salesRevisionLoaded = -1;

        private DateTime salesRequestedAt = DateTime.MinValue;

        private static readonly TimeSpan SalesAutoReloadThrottle = TimeSpan.FromSeconds(3);

        /// <summary>我方自己下架的那些列要不要一起顯示。預設不顯示：那不是賣出，只是把帳補平。</summary>
        private bool showOwnDelists;

        /// <summary>
        /// 「銷售」分頁畫的是時間序事件表（false），還是<b>一件一列</b>的彙總（true）。
        /// 🔑 時間序回答不了「這件到底賣不賣得掉」——那要把同一件道具的所有事件加起來才看得見。
        /// </summary>
        private bool salesByItem;

        /// <summary>彙總表的排序方式。</summary>
        private int salesItemSortMode;

        private static readonly string[] SalesItemSortKeys =
        [
            "Came off the board most without ever selling",
            "Sold the most",
            "Most recent activity",
        ];

        /// <summary><see cref="sales"/> 換過幾次內容；彙總拿它判斷要不要重算，免得每幀重算。</summary>
        private int salesStamp;

        private SalesHistorySnapshot salesAggregate = SalesHistorySnapshot.Empty;

        private int salesAggregateStamp = -1;

        /// <summary>畫面上最多列幾列；再多就只是拖慢繪製，檔案裡的東西一列都沒少。</summary>
        private const int MaxSalesRows = 300;

        /// <summary>摘要統計的視窗長度（天）。</summary>
        private const int SalesSummaryDays = 7;

        /// <summary>時間序表這一幀要畫的列；每幀重填，不重新配置。</summary>
        private readonly List<RetainerSaleRow> salesVisible = [];

        /// <summary>一件一列那張表排好的列，以及它是照哪一份彙總、哪一種排序排的。</summary>
        private readonly List<((uint ItemId, bool Hq) Key, ItemSaleHistory History)> salesItemRows = [];

        private SalesHistorySnapshot? salesItemRowsFor;
        private int salesItemRowsSort = -1;

        public PriceSurveyWindow(PriceSurvey survey, MarketGuiEventHandler gui)
        {
            this.survey = survey;
            this.gui = gui;
        }

        public void Draw(ref bool visible)
        {
            if (!visible)
                return;

            ImGui.SetNextWindowSizeConstraints(new Vector2(560, 320), new Vector2(float.MaxValue, float.MaxValue));
            if (!ImGui.Begin(DrawText.Concat("Cross-world price survey".Loc(), "###MarketbuddyPriceSurvey"), ref visible,
                    ImGuiWindowFlags.NoCollapse))
            {
                ImGui.End();
                return;
            }

            PumpLoad();
            PumpShoppingLoad();
            if (!loadRequested)
                RequestLoad();

            // 🔑 一輪收場（跑完、暫停、停止）就把記錄檔重讀一次。
            //    少了這一步，「哪些世界掃過」與比價表都會停在視窗第一次打開時的樣子：
            //    掃完一個世界再拉開換世界選單，它照樣寫著「還沒掃過」。
            var serial = survey.RunSerial;
            if (serial != lastSeenRunSerial)
            {
                lastSeenRunSerial = serial;
                RequestLoad();
                RequestShoppingLoad();
            }

            // 🔑 排除清單改了也要重讀：兩張表的欄位是在整理資料那一步就決定的，
            //    不重讀的話勾掉一個世界之後它的欄位會留在畫面上直到下一次掃描。
            var exclusionRevision = conf.WorldExclusionRevision;
            if (exclusionRevision != lastSeenExclusionRevision)
            {
                lastSeenExclusionRevision = exclusionRevision;
                RequestLoad();
                RequestShoppingLoad();
            }

            if (ImGui.BeginTabBar("##mbsurveytabs"))
            {
                if (ImGui.BeginTabItem(DrawText.Concat("Scan".Loc(), "##mbsurveyscan")))
                {
                    DrawScanTab();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem(DrawText.Concat("Compare".Loc(), "##mbsurveycompare")))
                {
                    if (!loadRequested)
                        RequestLoad();
                    DrawCompareTab();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem(DrawText.Concat("Shopping".Loc(), "##mbsurveyshopping")))
                {
                    if (!shoppingLoadRequested)
                        RequestShoppingLoad();
                    DrawShoppingTab();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem(DrawText.Concat("To do".Loc(), "##mbsurveypending")))
                {
                    DrawPendingTab();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem(DrawText.Concat("Sales".Loc(), "##mbsurveysales")))
                {
                    DrawSalesTab();
                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }

            ImGui.End();
        }

        /// <summary>
        /// 視窗被關掉時呼叫：巡檢跟著停手——看不見的東西不應該還在背景送查詢。
        /// 🔑 是<b>暫停</b>而不是結束：進度留著，重開視窗按「繼續掃描」
        /// 就從原地接下去。關窗仍然一件都不會再查，而且<b>不會</b>自己恢復。
        /// </summary>
        public void OnClosed()
        {
            survey.RequestPause("The survey window was closed".Loc());

            // 🔴 掃描中的那條路徑已經由暫停連帶解除武裝了，但「武裝著卻正在換世界」
            //    的時候沒有東西在跑、暫停是 no-op ⇒ 少了這一行，關掉視窗之後那條鏈
            //    會在看不見的地方繼續換世界、繼續開始掃描。
            survey.RequestDisarmTour("The survey window was closed".Loc());
        }

        // =====================================================================
        //  小工具
        // =====================================================================

        private readonly Dictionary<uint, string> nameCache = new();

        private string ItemName(uint itemId)
        {
            if (itemId == 0)
                return "—";
            if (nameCache.TryGetValue(itemId, out var cached))
                return cached;

            var name = $"#{itemId}";
            try
            {
                var sheet = DataManager.GetExcelSheet<Item>();
                if (sheet != null && sheet.TryGetRow(itemId, out var row))
                {
                    var text = row.Name.ExtractText();
                    if (text.Length > 0)
                        name = text;
                }
            }
            catch
            {
                // 查不到名字只影響顯示。
            }

            nameCache[itemId] = name;
            return name;
        }

        /// <summary>道具名；優質品後面接優質圖示。</summary>
        private string ItemLabel(uint itemId, bool hq)
            => hq ? DrawText.Concat(ItemName(itemId), HqSuffix) : ItemName(itemId);

        private static string SafeLogPath()
        {
            try
            {
                return PriceSurveyLog.FilePath;
            }
            catch
            {
                return PriceSurveyLog.FileName;
            }
        }

        private static string FormatDuration(double seconds)
        {
            if (seconds < 0)
                return "?";
            var span = TimeSpan.FromSeconds(seconds);
            return span.TotalHours >= 1
                ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
                : $"{span.Minutes}:{span.Seconds:00}";
        }

        private static string FormatAge(DateTime atUtc)
        {
            var age = DateTime.UtcNow - atUtc;
            if (age.TotalMinutes < 1)
                return "just now".Loc();
            if (age.TotalHours < 1)
                return DrawText.Loc("?? minute(s) ago", (int)age.TotalMinutes);
            if (age.TotalDays < 1)
                return DrawText.Loc("?? hour(s) ago", (int)age.TotalHours);
            return DrawText.Loc("?? day(s) ago", (int)age.TotalDays);
        }

        // ---- 表內自己捲的表（待處理四桶、銷售兩張）----

        private const ImGuiTableFlags ScrollTableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
                                                         ImGuiTableFlags.ScrollX | ImGuiTableFlags.ScrollY |
                                                         ImGuiTableFlags.SizingFixedFit;

        /// <summary>視窗再矮，表格也至少露出這麼多列（分頁自己會整頁捲）。</summary>
        private const int MinScrollTableRows = 6;

        /// <summary>待處理的一桶最高佔視窗高度的幾成。刻意不給滿：留一截表外的地方，滾輪才捲得到整頁、看得到下一桶。</summary>
        private const float BucketTableMaxHeightRatio = 0.6f;

        /// <summary>
        /// <see cref="scrollTableHasBarX"/> 的索引：0～3 是待處理的桶（<see cref="PendingActionKind"/> 的值）。
        /// 超出陣列的索引不會擲例外：那張表只是不補水平捲軸的高度。
        /// </summary>
        private const int SalesTableSlot = 4;

        private const int SalesItemTableSlot = 5;

        /// <summary>每張表上一次畫的時候有沒有水平捲軸；有的話外框要多留捲軸的高度，否則列少的表會多出一條垂直捲軸。</summary>
        private readonly bool[] scrollTableHasBarX = new bool[6];

        /// <summary>
        /// 外框大小：列少就剛好包住全部的列；列多就停在 <paramref name="maxHeight"/>（不低於 <see cref="MinScrollTableRows"/> 列），其餘在表內捲。
        /// 🔴 高度一定要給正值：帶捲動的表不給高度時 ImGui 把視窗剩下的高度全給它，排在後面的表只剩 1 像素。
        /// </summary>
        private Vector2 ScrollTableSize(int slot, int rows, float rowHeight, float maxHeight)
        {
            var style = ImGui.GetStyle();
            var chrome = ImGui.GetTextLineHeight() + (style.CellPadding.Y * 2)
                         + ((uint)slot < (uint)scrollTableHasBarX.Length && scrollTableHasBarX[slot] ? style.ScrollbarSize : 0f);
            var floor = (Math.Min(rows, MinScrollTableRows) * rowHeight) + chrome;
            return new Vector2(0, Math.Max(floor, Math.Min((rows * rowHeight) + chrome, maxHeight)));
        }

        /// <summary>緊接在 BeginTable 回 true 之後呼叫（這時的目前視窗是表格自己的捲動區）。</summary>
        private void NoteScrollTable(int slot)
        {
            if ((uint)slot < (uint)scrollTableHasBarX.Length)
                scrollTableHasBarX[slot] = ImGui.GetScrollMaxX() > 0;
        }

        /// <summary>銷售兩張表的列高：每一格都是一行文字。</summary>
        private static float SalesRowHeight() => ImGui.GetTextLineHeight() + (ImGui.GetStyle().CellPadding.Y * 2);

        private static void Grey(string text)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextUnformatted(text);
            ImGui.PopStyleColor();
        }

        /// <summary>
        /// 會換行的灰字，給占好幾行的說明段落用。
        /// 🔴 <see cref="Grey"/> 走 <c>TextUnformatted</c>，<b>不換行</b>：整段文字會把
        /// 視窗的內容寬度撐開（視窗最小寬 560，使用者調過的寬度不會自己變大，
        /// 所以實際表現是被裁掉）。長段落一律走這一支。
        /// </summary>
        private static void GreyWrapped(string text)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextWrapped(text);
            ImGui.PopStyleColor();
        }

        /// <summary>
        /// 上一個項目有沒有被指到（判準與 <see cref="Tooltip"/> 相同）。
        /// 提示文字要當場組的（帶參數、要查表、要算時間）先問這個，沒指到就不組。
        /// </summary>
        private static bool TooltipHovered() => ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);

        /// <summary>長說明放 tooltip，不占列上版面。</summary>
        private static void Tooltip(string text)
        {
            // 接在停用控制項後面的說明多半正是「為什麼不能按」，不帶這個旗標就永遠畫不出來。
            if (!ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                return;
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30f);
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }

        /// <summary>
        /// 同 <see cref="Tooltip"/>，給停用區段<b>裡面</b>的項目用：區段把 Style.Alpha 乘上 DisabledAlpha，
        /// 不推回 1 說明框會跟著變淡。ImRaii.PushStyle 的多載牽涉 ImPlot 組件（本專案沒參考），所以直接 new ImRaii.Style()。
        /// </summary>
        private static void TooltipInDisabled(string text)
        {
            if (!ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                return;
            using (new ImRaii.Style().Push(ImGuiStyleVar.Alpha, 1f))
                Tooltip(text);
        }

        private static DisabledScope Disabled(bool disabled) => new(disabled);

        private readonly struct DisabledScope : IDisposable
        {
            private readonly bool active;

            public DisabledScope(bool disabled)
            {
                active = disabled;
                if (active)
                    ImGui.BeginDisabled();
            }

            public void Dispose()
            {
                if (active)
                    ImGui.EndDisabled();
            }
        }
    }
}
