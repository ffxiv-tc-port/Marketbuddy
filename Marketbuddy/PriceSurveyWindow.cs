using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Interface.Colors;
using Lumina.Excel.Sheets;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    /// <summary>
    /// 跨世界價格巡檢的操作介面：一個獨立視窗，兩個分頁。
    /// 🔑 UI 規則：「不知道」必須在列上看得見。沒掃過的世界畫灰色的 <c>?</c>、
    /// 確認過沒人在賣畫灰色的 <c>—</c>，<b>絕不畫成 0</b>——0 在價格欄是一個合法但荒謬的值，
    /// 看起來像「那個世界真的有人用 0 gil 在賣」。長說明放 tooltip，列上只留判讀得出來的東西。
    /// </summary>
    internal sealed class PriceSurveyWindow
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

        /// <summary>比價分頁的排序方式。</summary>
        private int sortMode;

        private static readonly string[] SortModeKeys =
        [
            "Undercut amount (largest first)",
            "Item id",
            "Your listing price (highest first)",
        ];

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
            if (!ImGui.Begin("Cross-world price survey".Loc() + "###MarketbuddyPriceSurvey", ref visible,
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
                if (ImGui.BeginTabItem("Scan".Loc() + "##mbsurveyscan"))
                {
                    DrawScanTab();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Compare".Loc() + "##mbsurveycompare"))
                {
                    if (!loadRequested)
                        RequestLoad();
                    DrawCompareTab();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Shopping".Loc() + "##mbsurveyshopping"))
                {
                    if (!shoppingLoadRequested)
                        RequestShoppingLoad();
                    DrawShoppingTab();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("To do".Loc() + "##mbsurveypending"))
                {
                    DrawPendingTab();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Sales".Loc() + "##mbsurveysales"))
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
        /// 🔑 2026-09-08 起改成<b>暫停</b>而不是結束：進度留著，重開視窗按「繼續掃描」
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
        //  掃描
        // =====================================================================

        private void DrawScanTab()
        {
            ImGui.Spacing();

            // 🔴 世界名讀的是巡檢在 framework 執行緒拍好的快照，不是 PlayerState：
            //    繪製執行緒不解遊戲的原生指標。
            var worldName = survey.CurrentWorldName;
            if (worldName.Length == 0)
                Grey("Current world: ? (not logged in)".Loc());
            else
                ImGui.TextUnformatted("Current world: ??".Loc(worldName));

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            var running = survey.IsRunning;
            var paused = survey.IsPaused;

            // 🔴 暫停中也不准改：這幾項全部參與「要掃哪些件」的計算，中途改了
            //    續跑接上去的那半段會用不同的規則，兩半合起來就不是同一份資料了。
            using (Disabled(running || paused))
            {
                if (ImGui.Checkbox("Take the list from InventoryTools' log file (covers every character's retainers)".Loc(),
                        ref conf.PriceSurveyAllCharacters))
                    conf.Save();
                Tooltip(
                    "Off = try in order: the retainer sell list if it is open (that retainer only), otherwise AllaganTools (every retainer of the current character), and the log file as a last resort.\nOn (default) = read InventoryTools' inventories.csv directly, covering every character's retainers; the downside is that its freshness depends on when InventoryTools last wrote to disk."
                        .Loc());

                if (ImGui.Checkbox("Only survey items the last round showed as undercut".Loc(), ref conf.PriceSurveyOnlyUndercut))
                    conf.Save();
                Tooltip(
                    "Needs data in the log file already. With no data at all this filters out every item, and the window says so instead of quietly doing nothing."
                        .Loc());

                if (ImGui.Checkbox("Also look up my shopping list on every world".Loc(),
                        ref conf.PriceSurveyShoppingList))
                    conf.Save();
                Tooltip(
                    "Off by default. The shopping list is a plain text file you write yourself (shopping_list.txt in this plugin's config folder), one item per line. With this on, every world you scan also records what your shopping list costs there, how many are on sale and when it was looked up - so you know which world to travel to before you leave.\nIt never buys anything. Items you are already selling cost no extra query - the same lookup fills in both lists."
                        .Loc());
            }

            DrawShoppingCost();

            using (Disabled(running || paused))
            {
                ImGui.SetNextItemWidth(200);
                if (ImGui.SliderInt("Skip items already surveyed within (hours)".Loc(), ref conf.PriceSurveySkipHours, 0, 168))
                    conf.Save();
                Tooltip(
                    "Items this world already answered within that many hours are skipped, so an interrupted survey can carry on. 0 = never skip, always ask again. Refusals and timeouts do not count as surveyed and are always asked again next round."
                        .Loc());

                ImGui.SetNextItemWidth(200);
                if (ImGui.SliderInt("Market cache that may be reused (seconds)".Loc(), ref conf.PriceSurveyCacheSeconds, 0, 3600))
                    conf.Save();
                Tooltip(
                    "0 (default) = ask the server again for every item. A survey is meant to record what this world looks like right now; mixing in values from minutes ago leads to wrong conclusions later. Only raise it when re-running the same round."
                        .Loc());
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            if (running)
                DrawRunningControls();
            else if (paused)
                DrawPausedControls();
            else
                DrawIdleControls();

            DrawTravelSection(running || paused);

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            Grey("The gap between items is currently ?? ms (shared with relisting's own throttle gate, which tunes itself).".Loc(
                MarketRequestGate.IntervalMs));
            Grey("Log file: ??".Loc(PriceSurveyLog.FileName));
            Tooltip(SafeLogPath());

            var pending = PriceSurveyLog.PendingCount;
            if (pending > 0)
                Grey("?? row(s) still being written to the log file.".Loc(pending));

            if (conf.PriceSurveyShoppingList)
            {
                Grey("Shopping log file: ??".Loc(ShoppingSurveyLog.FileName));
                var shoppingPending = ShoppingSurveyLog.PendingCount;
                if (shoppingPending > 0)
                    Grey("?? row(s) still being written to the shopping log file.".Loc(shoppingPending));
            }

            ImGui.Spacing();
            GreyWrapped("Whatever is looked up here is uploaded anonymously to Universalis by Dalamud itself (if you have that turned on in Dalamud). The survey itself contacts nothing on its own: the only setting that makes this plugin send its own HTTP request is \"Also price from the most recent sale, and use whichever is lower\", which reads prices from Universalis. The market price columns on the live sell list panel reuse that very same lookup and send no request of their own, so they only have data while that setting is on. Nothing else in this plugin talks to any website."
                .Loc());
        }

        /// <summary>
        /// 「去下一個世界」：排除清單、手動換世界、自動續跑三塊。
        /// 🔴 「前往」那顆按鈕<b>只換世界</b>：按下去請 Lifestream 送你過去，抵達之後什麼
        /// 都不會發生，要掃描得自己回到上面再按一次「掃描這個世界」。
        /// </summary>
        private void DrawTravelSection(bool running)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.TextUnformatted("Travel to another world".Loc());
            Grey("Pressing Travel never starts a scan by itself - press the scan button again once you get there. Only a round you arm yourself (below) carries on across worlds."
                .Loc());

            // 🔴 排除清單一律畫：Lifestream 沒裝的時候更需要看得見它，
            //    否則使用者連「為什麼少了一個世界」都查不到。
            DrawWorldExclusions();
            DrawTravelPicker(running);
            DrawAutoTour();
        }

        /// <summary>
        /// 「哪些世界不要碰」。<b>排除清單只有一份真值</b>（設定裡那一份），
        /// 換世界選單、自動續跑、能不能在這裡開始掃描、待處理清單的建議價、
        /// 以及「最近成交價重掛」全部讀它。
        /// </summary>
        private void DrawWorldExclusions()
        {
            // 🔴 先抄一份：下面的核取方塊會改設定裡那個 List，邊改邊列舉會擲例外。
            var excluded = new List<uint>(conf.PriceSurveyExcludedWorlds);
            if (excluded.Count == 0)
            {
                Grey("No world is excluded.".Loc());
            }
            else
            {
                var names = new List<string>(excluded.Count);
                foreach (var worldId in excluded)
                    names.Add(WorldNameOf(worldId));
                ImGui.TextUnformatted("Excluded: ??".Loc(string.Join(", ", names)));
            }

            // 🔴 「掛售所在的世界被勾進去了」必須在列上看得見，不能只靠 tooltip：
            //    定價不理會那個勾，但換世界、掃描與兩張表仍然照勾的算——看不見的話
            //    使用者會以為自己勾錯了，或以為定價也跟著排除了。
            var sellingWorldId = Configuration.SellingWorldId();
            if (sellingWorldId != 0 && conf.IsWorldExcluded(sellingWorldId))
                Grey("?? is where your own items are listed, so pricing still reads it.".Loc(
                    WorldNameOf(sellingWorldId)));

            Tooltip(
                "An excluded world is left out of the travel list, is never picked by automatic world hopping, and cannot be scanned even while you stand on it. Rows already in the log files are kept - the compare and shopping tables just stop showing those columns, and say so.\nRamuh (4034) ships excluded because that world is shut down on this service, so travelling there always fails. That is a normal setting rather than a hard-coded rule: if it ever comes back, untick it here."
                    .Loc() + "\n" +
                "Excluded worlds are also left out of the pending list's suggested prices and of the most-recent-sale relist price, so a world that shut down cannot set your prices any more. Items whose only sales are on those worlds are left on their usual pricing instead of being given a made-up price."
                    .Loc() + "\n" +
                "The one exception is the world your own items are listed on: pricing always reads that world, even while it is ticked here. Ticking it still keeps that world out of the travel list, out of automatic world hopping, out of scanning, and out of the compare and shopping tables."
                    .Loc());

            if (!ImGui.CollapsingHeader("Choose which worlds to leave out".Loc() + "###mbsurveyexclude"))
                return;

            var worldsHere = survey.DataCentreWorlds;
            if (worldsHere.Count == 0)
                Grey("The world list is built once you are logged in.".Loc());

            var listed = new HashSet<uint>();
            foreach (var (worldId, name) in worldsHere)
            {
                listed.Add(worldId);
                var isExcluded = conf.IsWorldExcluded(worldId);
                if (ImGui.Checkbox(name + "##mbsurveyexclude" + worldId, ref isExcluded))
                    conf.SetWorldExcluded(worldId, isExcluded);

                if (worldId == survey.CurrentWorldId)
                {
                    ImGui.SameLine();
                    Grey("(you are here)".Loc());
                }

                DrawSellingWorldNote(worldId, sellingWorldId, isExcluded);
            }

            // 🔑 排除清單上但不在這個資料中心的世界也要列得出來，否則使用者換了資料中心
            //    之後就再也勾不掉它——那會變成一個看得見卻改不動的設定。
            foreach (var worldId in excluded)
            {
                if (listed.Contains(worldId))
                    continue;
                var isExcluded = true;
                if (ImGui.Checkbox(WorldNameOf(worldId) + "##mbsurveyexclude" + worldId, ref isExcluded))
                    conf.SetWorldExcluded(worldId, isExcluded);
                ImGui.SameLine();
                Grey("(not on this data centre)".Loc());
                DrawSellingWorldNote(worldId, sellingWorldId, isExcluded);
            }
        }

        /// <summary>
        /// 勾起來的是「自己掛售的那個世界」時，在那一列上寫明定價不受影響。
        /// 🔑 靜默忽略使用者的勾選是最糟的一種處置：他看得到勾勾、看不到它對定價沒作用。
        /// </summary>
        private static void DrawSellingWorldNote(uint worldId, uint sellingWorldId, bool isExcluded)
        {
            if (worldId == 0 || worldId != sellingWorldId || !isExcluded)
                return;

            ImGui.SameLine();
            Grey("(pricing still reads this world)".Loc());
            Tooltip(
                "This is where your own items are listed, so the pending list's suggested price and the most-recent-sale relist price always read this world. The tick still keeps it out of the travel list, out of automatic world hopping, out of scanning, and out of the compare and shopping tables."
                    .Loc());
        }

        /// <summary>
        /// 世界 id → 看得懂的名字。查不到就畫成 <c>#id</c>——
        /// 🔑 那是誠實的「我只知道 id」，不是假裝知道名字。
        /// </summary>
        private string WorldNameOf(uint worldId)
        {
            foreach (var (id, name) in survey.DataCentreWorlds)
            {
                if (id == worldId)
                    return name;
            }

            foreach (var world in worlds)
            {
                if (world.WorldId == worldId && world.Name.Length > 0)
                    return world.Name;
            }

            return "#" + worldId;
        }

        private void DrawTravelPicker(bool running)
        {
            if (survey.LifestreamMissing)
            {
                Grey("Lifestream is not installed, so this plugin cannot move you between worlds. Travel by hand, then press the scan button again."
                    .Loc());
                return;
            }

            var targets = survey.TravelTargets;
            if (targets.Count == 0)
            {
                Grey("Lifestream does not report any world you can travel to from here.".Loc());
                return;
            }

            if (travelChoice >= targets.Count)
                travelChoice = 0;

            ImGui.SetNextItemWidth(260);
            if (ImGui.BeginCombo("##mbsurveyworld", WorldLabel(targets[travelChoice])))
            {
                for (var i = 0; i < targets.Count; i++)
                {
                    if (ImGui.Selectable(WorldLabel(targets[i]), travelChoice == i))
                        travelChoice = i;
                }

                ImGui.EndCombo();
            }

            ImGui.SameLine();

            // 🔑 武裝中也鎖住：那一輪自己在決定要去哪裡，手動插一腳只會讓它等到逾時
            //    然後解除武裝——按鈕看起來有反應、結果卻是把整輪弄掉。
            var armed = survey.IsTourArmed;
            using (Disabled(running || armed))
            {
                if (ImGui.Button("Travel there".Loc()))
                    survey.RequestChangeWorld(targets[travelChoice].Name);
            }

            if (armed)
                Grey("An armed round is choosing the worlds. Disarm it below if you want to travel by hand."
                    .Loc());

            if (survey.IsPaused)
                Grey("A round is paused on ??. Continue it or stop it before travelling - a paused round cannot be picked up on a different world."
                    .Loc(survey.WorldName));

            if (survey.TravelStatus.Length > 0)
                ImGui.TextWrapped(survey.TravelStatus);
        }

        /// <summary>
        /// 「掃完就自動去資料最舊的世界接著掃」。
        /// </summary>
        /// <remarks>
        /// 🔴 兩道人為閘門：設定<b>預設關</b>（打開也只是讓按鈕可按），以及一定要使用者
        /// 親手按「武裝一輪」。武裝狀態不存檔，而且只有「整份掃完」才會往下一個世界走。
        /// </remarks>
        private void DrawAutoTour()
        {
            var armed = survey.IsTourArmed;

            ImGui.Spacing();
            ImGui.TextUnformatted("Carry on with the world that has the oldest data".Loc());

            using (Disabled(armed))
            {
                if (ImGui.Checkbox("Allow automatic world hopping".Loc(), ref conf.PriceSurveyAutoTour))
                    conf.Save();
            }

            Tooltip(
                "Off by default, and turning it on only makes the Arm button clickable - nothing starts on its own.\nOnce armed, finishing a world's list makes this plugin ask Lifestream to travel to the world whose survey data is the oldest (never surveyed counts as oldest) and start scanning there. Excluded worlds are never picked, each world is visited at most once per round, and the round stops at the limit below.\nArming is never remembered: reloading the plugin or restarting the game always leaves it disarmed. Anything other than finishing a world - a pause, a stop, closing this window, a timeout, AutoRetainer or a relist taking over, Lifestream refusing - disarms it on the spot and says why."
                    .Loc());

            if (conf.PriceSurveyAutoTour)
            {
                using (Disabled(armed))
                {
                    ImGui.SetNextItemWidth(200);
                    if (ImGui.SliderInt("At most this many worlds per round".Loc(),
                            ref conf.PriceSurveyAutoTourMaxWorlds, 1, Configuration.MAX_TOUR_WORLDS))
                        conf.Save();
                }

                Tooltip("A hard cap on one armed round, on top of \"each world at most once\". There are only eight worlds here, so eight means \"every world I can reach\"."
                    .Loc());
            }

            if (!armed)
            {
                var canArm = survey.CanArmTour(out var why);
                using (Disabled(!canArm))
                {
                    if (ImGui.Button("Arm one round".Loc(), new Vector2(180, 0)))
                        survey.RequestArmTour();
                }

                if (!canArm && conf.PriceSurveyAutoTour)
                {
                    ImGui.SameLine();
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
                    ImGui.TextUnformatted(why);
                    ImGui.PopStyleColor();
                }
            }
            else
            {
                if (ImGui.Button("Disarm".Loc(), new Vector2(180, 0)))
                    survey.RequestDisarmTour("Disarmed by the user".Loc());

                ImGui.SameLine();
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudYellow);
                ImGui.TextUnformatted("Armed: ?? / ?? world change(s) used".Loc(
                    survey.TourWorldsChanged, survey.TourMaxWorlds));
                ImGui.PopStyleColor();

                if (survey.TourTravelTargetName.Length > 0)
                    Grey("Target world: ??".Loc(survey.TourTravelTargetName));
            }

            if (survey.TourStatus.Length > 0)
                ImGui.TextWrapped(survey.TourStatus);
        }

        /// <summary>
        /// 下拉選單上的一列：世界名，加上「這個世界掃到哪了」。
        /// 🔑 權威來源是 <see cref="PriceSurveyWorldLog"/>（它記得那一輪的完整清單有幾件），
        /// 所以「掃完了」與「掃到一半」分得出來。沒有那一列時退回行情記錄檔裡的最後時間戳
        /// ——那只知道「有掃過一些」，所以話要說得比較弱，<b>不可以講成掃完了</b>。
        /// </summary>
        private string WorldLabel((uint WorldId, string Name) target)
        {
            if (PriceSurveyWorldLog.TryGet(target.WorldId, out var row))
            {
                var freshness = PriceSurveyWorldLog.Classify(
                    row, conf.PriceSurveySkipHours, survey.CurrentContentId, DateTime.UtcNow);
                switch (freshness)
                {
                    case SurveyWorldFreshness.Complete:
                        return "?? (all ?? done, ??)".Loc(target.Name, row.PlannedTotal, FormatAge(row.AtUtc));
                    case SurveyWorldFreshness.Partial:
                        return "?? (?? / ?? done, ??)".Loc(
                            target.Name, row.Surveyed, row.PlannedTotal, FormatAge(row.AtUtc));
                    case SurveyWorldFreshness.Stale:
                        return "?? (?? - past the keep-for window, will be surveyed again)".Loc(
                            target.Name, FormatAge(row.AtUtc));
                    case SurveyWorldFreshness.OtherCharacter:
                        return "?? (?? - another character's list)".Loc(target.Name, FormatAge(row.AtUtc));
                    case SurveyWorldFreshness.Untracked:
                        return "?? (last visited ??)".Loc(target.Name, FormatAge(row.AtUtc));
                }
            }

            return worldLatest.TryGetValue(target.WorldId, out var at)
                ? "?? (has some data, ??)".Loc(target.Name, FormatAge(at))
                : "?? (not scanned yet)".Loc(target.Name);
        }

        private void DrawIdleControls()
        {
            var canStart = survey.CanStart(out var why);
            using (Disabled(!canStart))
            {
                if (ImGui.Button("Scan this world".Loc(), new Vector2(180, 0)))
                    survey.RequestStart();
            }

            if (!canStart)
            {
                ImGui.SameLine();
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
                ImGui.TextUnformatted(why);
                ImGui.PopStyleColor();
            }

            if (!conf.PriceSurveyEnabled)
            {
                ImGui.Spacing();
                if (ImGui.Button("Enable this feature".Loc()))
                {
                    conf.PriceSurveyEnabled = true;
                    conf.Save();
                }

                ImGui.SameLine();
                Grey("(off by default; enabling it only makes the button clickable, nothing happens on its own)".Loc());
            }

            DrawThisWorldProgress();
            DrawWorldMemoryControls();

            if (survey.StatusText.Length == 0)
                return;

            ImGui.Spacing();
            if (survey.LastRunLookedUnsupported)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudRed);
                ImGui.TextWrapped(survey.StatusText);
                ImGui.PopStyleColor();
            }
            else
            {
                ImGui.TextWrapped(survey.StatusText);
            }
        }

        /// <summary>
        /// 閒置時，把「這個世界上次掃到哪」直接寫在按鈕底下。
        /// 🔑 這一行存在的理由：續掃本來就會跳過保留時間內問過的道具，可是進度列
        /// 每一輪都從 0 重新算，看起來就像整份重來。把「上次到哪、按下去會從哪接」
        /// 寫在列上，使用者才看得出來它其實沒有重來。
        /// </summary>
        private void DrawThisWorldProgress()
        {
            var worldId = survey.CurrentWorldId;
            if (worldId == 0)
                return;

            ImGui.Spacing();

            if (!PriceSurveyWorldLog.TryGet(worldId, out var row))
            {
                Grey(PriceSurveyWorldLog.Loaded
                    ? "This world has not been surveyed yet.".Loc()
                    : "Reading the world progress file...".Loc());
                return;
            }

            var freshness = PriceSurveyWorldLog.Classify(
                row, conf.PriceSurveySkipHours, survey.CurrentContentId, DateTime.UtcNow);

            switch (freshness)
            {
                case SurveyWorldFreshness.Complete:
                    Grey("This world: all ?? item(s) surveyed ??.".Loc(row.PlannedTotal, FormatAge(row.AtUtc)));
                    Tooltip(
                        "Scanning again now would find every item still inside the keep-for window, so it would have nothing left to ask."
                            .Loc());
                    return;
                case SurveyWorldFreshness.Partial:
                    ImGui.TextUnformatted("This world: ?? / ?? item(s) surveyed ?? - scanning again carries on from item ??."
                        .Loc(row.Surveyed, row.PlannedTotal, FormatAge(row.AtUtc), row.Surveyed + 1));
                    Tooltip(
                        "Items already answered inside the keep-for window are skipped, so the ones below were never lost. Refusals and timeouts are always asked again."
                            .Loc());
                    return;
                case SurveyWorldFreshness.Stale:
                    Grey("This world: last surveyed ??, which is past the keep-for window - the next scan starts over.".Loc(
                        FormatAge(row.AtUtc)));
                    Tooltip("Raise \"Skip items already surveyed within (hours)\" if you want a longer memory.".Loc());
                    return;
                case SurveyWorldFreshness.OtherCharacter:
                    Grey("This world: the ?? record was made by another character with a list that only covered that character."
                        .Loc(FormatAge(row.AtUtc)));
                    return;
                case SurveyWorldFreshness.Untracked:
                    Grey("This world: last visited ??. \"Skip items already surveyed within\" is 0, so nothing is ever skipped."
                        .Loc(FormatAge(row.AtUtc)));
                    return;
            }
        }

        /// <summary>「忘記掃過哪些世界」——「掃過」的第四種失效條件，也是唯一一種由使用者觸發的。</summary>
        private void DrawWorldMemoryControls()
        {
            var known = PriceSurveyWorldLog.Count;
            if (known == 0)
                return;

            ImGui.Spacing();
            Grey("?? world(s) remembered".Loc(known));
            ImGui.SameLine();
            using (Disabled(PriceSurveyWorldLog.IsLoading))
            {
                if (ImGui.Button("Forget which worlds were scanned".Loc()))
                    PriceSurveyWorldLog.Clear();
            }

            Tooltip(
                "Clears ?? only. Every price this survey has ever recorded stays in ??, and nothing in the game is touched."
                    .Loc(PriceSurveyWorldLog.FileName, PriceSurveyLog.FileName));
        }

        /// <summary>
        /// 暫停中的操作列。
        /// 🔴 這裡的「繼續掃描」是<b>離開暫停唯一的前進方向</b>，而且一定要使用者按。
        /// </summary>
        private void DrawPausedControls()
        {
            var canResume = survey.CanResume(out var why);
            using (Disabled(!canResume))
            {
                if (ImGui.Button("Continue the survey".Loc(), new Vector2(180, 0)))
                    survey.RequestResume();
            }

            ImGui.SameLine();
            if (ImGui.Button("Stop and discard".Loc(), new Vector2(180, 0)))
                survey.RequestStop("The paused round was discarded".Loc());

            Tooltip(
                "Discarding only throws away this round's remaining queue. Everything already surveyed stays in the log file, so starting again would skip it anyway."
                    .Loc());

            if (!canResume)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
                ImGui.TextWrapped(why);
                ImGui.PopStyleColor();
            }

            ImGui.Spacing();

            var total = Math.Max(1, survey.Total);
            ImGui.ProgressBar(survey.Processed / (float)total, new Vector2(-1, 0),
                $"{survey.Processed} / {survey.Total}");

            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudYellow);
            ImGui.TextWrapped("Paused: ??".Loc(survey.PauseReason));
            ImGui.PopStyleColor();

            var pausedFor = survey.PausedForSeconds;
            if (pausedFor >= 0)
                Grey("Paused for ??".Loc(FormatDuration(pausedFor)));

            Grey("World ??  Source ??".Loc(survey.WorldName, survey.SourceLabel));
            Grey("Nothing is being asked while paused, and it will not resume on its own - press Continue.".Loc());
            Grey("?? row(s) recorded  ?? found  ?? nobody selling  ?? refused  ?? timed out  ?? from cache".Loc(
                survey.RecordedRows, survey.OkCount, survey.EmptyCount, survey.RefusedCount,
                survey.TimeoutCount, survey.CacheCount));
            if (survey.SkippedAlreadyDone > 0)
                Grey("?? item(s) skipped (surveyed within the keep-for window)".Loc(survey.SkippedAlreadyDone));
        }

        private void DrawRunningControls()
        {
            var preparing = survey.State == PriceSurvey.SurveyState.Preparing;

            // 🔑 準備階段還沒有佇列可以留著，所以那時候只給「停止」——
            //    給一顆按下去等於停止的「暫停」比沒有那顆更糟。
            using (Disabled(preparing))
            {
                if (ImGui.Button("Pause".Loc(), new Vector2(180, 0)))
                    survey.RequestPause("Paused by the user".Loc());
            }

            Tooltip(
                "Keeps this round's queue and progress. Nothing is asked while paused, and it never continues on its own - you press Continue."
                    .Loc());

            ImGui.SameLine();
            if (ImGui.Button("Stop the price survey".Loc(), new Vector2(180, 0)))
                survey.RequestStop("Stopped by the user".Loc());

            ImGui.Spacing();

            if (preparing)
            {
                ImGui.TextUnformatted(survey.StatusText);
                return;
            }

            var total = Math.Max(1, survey.Total);
            ImGui.ProgressBar(survey.Processed / (float)total, new Vector2(-1, 0),
                $"{survey.Processed} / {survey.Total}");

            var remaining = survey.EstimatedRemainingSeconds;
            if (remaining >= 0)
                ImGui.TextUnformatted("About ?? left".Loc(FormatDuration(remaining)));

            ImGui.TextUnformatted("Current: ??".Loc(ItemName(survey.CurrentItemId)));
            Grey("World ??  Source ??".Loc(survey.WorldName, survey.SourceLabel));
            Grey("?? row(s) recorded  ?? found  ?? nobody selling  ?? refused  ?? timed out  ?? from cache".Loc(
                survey.RecordedRows, survey.OkCount, survey.EmptyCount, survey.RefusedCount,
                survey.TimeoutCount, survey.CacheCount));
            if (survey.SkippedAlreadyDone > 0)
                Grey("?? item(s) skipped (surveyed within the keep-for window)".Loc(survey.SkippedAlreadyDone));

            // 🔑 「這一輪多花的時間花在哪」要看得見：多查了幾件、記了幾列。
            if (survey.ShoppingExtraQueued > 0 || survey.ShoppingRecordedRows > 0)
                Grey("Shopping list: ?? extra item(s) queued, ?? row(s) recorded, ?? skipped".Loc(
                    survey.ShoppingExtraQueued, survey.ShoppingRecordedRows, survey.ShoppingSkippedAlreadyDone));
        }

        // =====================================================================
        //  比價
        // =====================================================================

        private void DrawCompareTab()
        {
            ImGui.Spacing();

            using (Disabled(loadTask != null))
            {
                if (ImGui.Button("Reload the log".Loc()))
                    RequestLoad();
            }

            ImGui.SameLine();
            if (loadTask != null)
                Grey("Loading...".Loc());
            else if (loadedAt == DateTime.MinValue)
                Grey("Not loaded yet".Loc());
            else
                Grey("?? row(s), ?? item(s), ?? world(s)".Loc(totalRowsLoaded, entries.Count, worlds.Count));

            // 🔑 藏了東西就要說出來：少一個欄位跟「那個世界沒資料」在畫面上分不出來。
            if (hiddenExcludedWorlds.Count > 0)
            {
                ImGui.SameLine();
                Grey("(?? excluded: ??)".Loc(
                    hiddenExcludedWorlds.Count, string.Join(", ", hiddenExcludedWorlds)));
                Tooltip("Those worlds are on your excluded list, so their columns are left out of this table and out of the cheapest/undercut numbers. Their rows are still in the log file - untick the world to bring them back."
                    .Loc());
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(200);
            if (ImGui.BeginCombo("##mbsurveysort", SortModeKeys[sortMode].Loc()))
            {
                for (var i = 0; i < SortModeKeys.Length; i++)
                {
                    if (!ImGui.Selectable(SortModeKeys[i].Loc(), sortMode == i))
                        continue;
                    sortMode = i;
                    SortEntries();
                }

                ImGui.EndCombo();
            }

            ImGui.Spacing();
            Grey("A grey ? = this world has not been scanned (it is not 0 gil); a grey - = confirmed nobody is selling; green = that lowest price is your own."
                .Loc());
            ImGui.Spacing();

            if (entries.Count == 0)
            {
                ImGui.TextWrapped(
                    loadedAt == DateTime.MinValue
                        ? "Nothing has been loaded yet.".Loc()
                        : "The log file has no data yet - scan a world on the Scan tab first.".Loc());
                return;
            }

            DrawCompareTable();
        }

        private void DrawCompareTable()
        {
            var worldCount = Math.Min(worlds.Count, MaxWorldColumns);
            var columns = 3 + worldCount;

            const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
                                          ImGuiTableFlags.ScrollY | ImGuiTableFlags.ScrollX |
                                          ImGuiTableFlags.SizingFixedFit;

            if (!ImGui.BeginTable("##mbsurveytable", columns, flags, new Vector2(0, -1)))
                return;

            ImGui.TableSetupScrollFreeze(1, 1);
            ImGui.TableSetupColumn("Item".Loc(), ImGuiTableColumnFlags.WidthFixed, 220);
            ImGui.TableSetupColumn("Your price".Loc(), ImGuiTableColumnFlags.WidthFixed, 90);
            for (var i = 0; i < worldCount; i++)
                ImGui.TableSetupColumn(worlds[i].Name, ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Undercut".Loc(), ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableHeadersRow();

            foreach (var entry in entries)
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(entry.Label);

                ImGui.TableNextColumn();
                if (entry.OurPrice < 0)
                    Grey("?");
                else
                    ImGui.TextUnformatted(entry.OurPrice.ToString("N0"));

                for (var i = 0; i < worldCount; i++)
                {
                    ImGui.TableNextColumn();
                    DrawWorldCell(entry, worlds[i]);
                }

                ImGui.TableNextColumn();
                DrawUndercutCell(entry);
            }

            ImGui.EndTable();
        }

        private void DrawWorldCell(CompareEntry entry, (uint WorldId, string Name, DateTime LatestAt) world)
        {
            if (!entry.ByWorld.TryGetValue(world.WorldId, out var row))
            {
                // 🔑 「這個世界沒掃過」必須看得見，而且不可以長得像一個價格。
                Grey("?");
                Tooltip("This world has not been scanned for this item.".Loc());
                return;
            }

            var price = row.LowestForQuality;
            if (row.Verdict is "refused" or "timeout")
            {
                Grey("?");
                Tooltip("?? had no result last time (??).".Loc(world.Name, row.Verdict));
                return;
            }

            if (price < 0)
            {
                Grey("—");
                Tooltip("?? confirmed nobody is selling (??).".Loc(world.Name, FormatAge(row.AtUtc)));
                return;
            }

            var mine = row.LowestIsOurs == 1;
            if (mine)
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.HealerGreen);
            else if (entry.OurPrice >= 0 && price < entry.OurPrice)
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
            else
                ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.Text]);
            ImGui.TextUnformatted(price.ToString("N0"));
            ImGui.PopStyleColor();

            var owner = row.LowestIsOurs switch
            {
                1 => "the lowest price is your own".Loc(),
                0 => "the lowest price is somebody else's".Loc(),
                _ => "who holds the lowest price is unknown".Loc(),
            };
            Tooltip("??  ??  ?? listing(s)  ??  source ??".Loc(
                world.Name, owner,
                row.ListingCount < 0 ? "?" : row.ListingCount.ToString(),
                FormatAge(row.AtUtc), row.Source));
        }

        private void DrawUndercutCell(CompareEntry entry)
        {
            if (entry.WorstUndercut <= 0)
            {
                if (entry.HasAnyData)
                {
                    Grey("—");
                    Tooltip("No scanned world is cheaper than you.".Loc());
                }
                else
                {
                    Grey("?");
                    Tooltip("No world has any data yet.".Loc());
                }

                return;
            }

            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
            ImGui.TextUnformatted("-" + entry.WorstUndercut.ToString("N0"));
            ImGui.PopStyleColor();
            Tooltip("Cheapest is ?? (?? gil).".Loc(entry.BestWorldName, entry.BestPrice.ToString("N0")));
        }

        // =====================================================================
        //  採購
        // =====================================================================

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

        /// <summary>
        /// 「打開這個開關會多花多少」——<b>按下去之前</b>就要看得見。
        /// 🔑 這裡刻意報<b>上限</b>（清單全部都要問一次）而不是期望值：與我方掛售清單重疊的
        /// 那幾件其實不必多查，但那要等實際建佇列才知道。把估計報高不會害人多等，
        /// 報低會。
        /// </summary>
        private void DrawShoppingCost()
        {
            // 🔑 開關<b>關著</b>的時候也要看得見件數與預估時間——「打開會多花多久」正是
            //    使用者在按下那個核取方塊之前要知道的事。沒有清單檔又沒開，才完全不佔版面。
            if (!conf.PriceSurveyShoppingList && !ShoppingList.FileExists)
                return;

            ImGui.Spacing();

            if (!ShoppingList.Loaded)
            {
                Grey("Reading the shopping list...".Loc());
                return;
            }

            if (!ShoppingList.FileExists)
            {
                Grey("No shopping list file yet (??).".Loc(ShoppingList.FileName));
                Tooltip(SafeShoppingListPath());
                ImGui.SameLine();
                if (ImGui.Button("Create a template list file".Loc()))
                {
                    // 🔴 只在檔案不存在時建立，永遠不覆寫使用者寫好的清單。
                    if (!ShoppingList.TryCreateTemplate())
                        ShoppingList.RequestReload();
                }

                return;
            }

            var count = ShoppingList.Items.Count;
            var minutes = count * MarketRequestGate.IntervalMs / 60000.0;
            ImGui.TextUnformatted(
                "Shopping list: ?? item(s) - at most about ?? more minute(s) on every world you scan.".Loc(
                    count, minutes.ToString("F1")));
            Tooltip(
                "Worked out from the current ?? ms gap between queries. Items you are already selling need no extra query, so the real cost is usually lower than this."
                    .Loc(MarketRequestGate.IntervalMs));

            ImGui.SameLine();
            using (Disabled(ShoppingList.IsLoading))
            {
                if (ImGui.Button("Reload the shopping list".Loc()))
                    ShoppingList.RequestReload();
            }

            Tooltip(SafeShoppingListPath());

            // ⚠️ 認不出來與不能買賣的那幾行必須看得見：靜靜地少查幾件與「查過了沒人賣」
            //    在畫面上長得一模一樣。
            var unresolved = ShoppingList.Unresolved;
            if (unresolved.Count > 0)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
                ImGui.TextWrapped("?? line(s) were not recognised: ??".Loc(
                    unresolved.Count, string.Join(", ", unresolved)));
                ImGui.PopStyleColor();
                Tooltip("Write the item name exactly as the game spells it, or write the item id instead.".Loc());
            }

            var unmarketable = ShoppingList.Unmarketable;
            if (unmarketable.Count > 0)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
                ImGui.TextWrapped("?? line(s) cannot be traded on the market board: ??".Loc(
                    unmarketable.Count, string.Join(", ", unmarketable)));
                ImGui.PopStyleColor();
            }

            if (ShoppingList.Truncated > 0)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
                ImGui.TextWrapped("?? line(s) past the ?? item limit were ignored.".Loc(
                    ShoppingList.Truncated, ShoppingList.MaxEntries));
                ImGui.PopStyleColor();
            }
        }

        private void DrawShoppingTab()
        {
            ImGui.Spacing();

            using (Disabled(shoppingLoadTask != null))
            {
                if (ImGui.Button("Reload the log".Loc() + "##mbshoppingreload"))
                    RequestShoppingLoad();
            }

            ImGui.SameLine();
            if (shoppingLoadTask != null)
                Grey("Loading...".Loc());
            else if (shoppingLoadedAt == DateTime.MinValue)
                Grey("Not loaded yet".Loc());
            else
                Grey("?? row(s), ?? item(s), ?? world(s)".Loc(
                    shoppingTotalRowsLoaded, shoppingEntries.Count, shoppingWorlds.Count));

            if (shoppingHiddenExcludedWorlds.Count > 0)
            {
                ImGui.SameLine();
                Grey("(?? excluded: ??)".Loc(
                    shoppingHiddenExcludedWorlds.Count, string.Join(", ", shoppingHiddenExcludedWorlds)));
                Tooltip("Those worlds are on your excluded list, so their columns are left out of this table and out of the cheapest/spread numbers. Their rows are still in the log file - untick the world to bring them back."
                    .Loc());
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(240);
            if (ImGui.BeginCombo("##mbshoppingsort", ShoppingSortModeKeys[shoppingSortMode].Loc()))
            {
                for (var i = 0; i < ShoppingSortModeKeys.Length; i++)
                {
                    if (!ImGui.Selectable(ShoppingSortModeKeys[i].Loc(), shoppingSortMode == i))
                        continue;
                    shoppingSortMode = i;
                    SortShoppingEntries();
                }

                ImGui.EndCombo();
            }

            ImGui.Spacing();
            if (!conf.PriceSurveyShoppingList)
            {
                Grey("Looking up the shopping list is switched off, so nothing new is being recorded. Turn it on over on the Scan tab."
                    .Loc());
                ImGui.Spacing();
            }

            Grey("Each cell is the cheapest unit price that world had, and under it how many were on sale there. A grey ? = never looked up (it is not 0 gil); a grey - = confirmed nobody is selling."
                .Loc());
            ImGui.Spacing();

            if (shoppingEntries.Count == 0)
            {
                ImGui.TextWrapped(
                    shoppingLoadedAt == DateTime.MinValue
                        ? "Nothing has been loaded yet.".Loc()
                        : "No shopping prices recorded yet - put items in ?? and scan a world with the shopping option on."
                            .Loc(ShoppingList.FileName));
                return;
            }

            DrawShoppingTable();
        }

        private void DrawShoppingTable()
        {
            RefreshShoppingWanted();

            var worldCount = Math.Min(shoppingWorlds.Count, MaxWorldColumns);
            var columns = 3 + worldCount;

            const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
                                          ImGuiTableFlags.ScrollY | ImGuiTableFlags.ScrollX |
                                          ImGuiTableFlags.SizingFixedFit;

            if (!ImGui.BeginTable("##mbshoppingtable", columns, flags, new Vector2(0, -1)))
                return;

            ImGui.TableSetupScrollFreeze(1, 1);
            ImGui.TableSetupColumn("Item".Loc(), ImGuiTableColumnFlags.WidthFixed, 220);
            ImGui.TableSetupColumn("Wanted".Loc(), ImGuiTableColumnFlags.WidthFixed, 60);
            for (var i = 0; i < worldCount; i++)
                ImGui.TableSetupColumn(shoppingWorlds[i].Name, ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Go here".Loc(), ImGuiTableColumnFlags.WidthFixed, 150);
            ImGui.TableHeadersRow();

            foreach (var entry in shoppingEntries)
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(entry.Label);

                ImGui.TableNextColumn();
                if (shoppingWantedById.TryGetValue(entry.ItemId, out var wanted) && wanted > 0)
                    ImGui.TextUnformatted(wanted.ToString("N0"));
                else
                    Grey("—");

                for (var i = 0; i < worldCount; i++)
                {
                    ImGui.TableNextColumn();
                    DrawShoppingCell(entry, shoppingWorlds[i]);
                }

                ImGui.TableNextColumn();
                DrawShoppingBestCell(entry);
            }

            ImGui.EndTable();
        }

        private void DrawShoppingCell(ShoppingEntry entry, (uint WorldId, string Name, DateTime LatestAt) world)
        {
            if (!entry.ByWorld.TryGetValue(world.WorldId, out var row))
            {
                // 🔑 「這個世界沒查過」必須看得見，而且不可以長得像一個價格。
                Grey("?");
                Tooltip("This world has never been looked up for this item.".Loc());
                return;
            }

            if (!row.Answered)
            {
                Grey("?");
                Tooltip("?? had no result last time (??).".Loc(world.Name, row.Verdict));
                return;
            }

            var price = row.CheapestPrice;
            if (price < 0)
            {
                Grey("—");
                Tooltip("?? confirmed nobody is selling (??).".Loc(world.Name, FormatAge(row.AtUtc)));
                return;
            }

            var cheapest = entry.BestPrice >= 0 && price == entry.BestPrice;
            if (cheapest)
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.HealerGreen);
            else
                ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.Text]);
            ImGui.TextUnformatted(price.ToString("N0") + (row.CheapestIsHq ? " " + (char)SeIconChar.HighQuality : string.Empty));
            ImGui.PopStyleColor();

            // 🔑 「這裡買得到幾個」與「那是什麼時候的事」都放在列上：出門前要看的就是這兩個。
            //    不知道時畫 ?，絕不畫成 0——0 的意思是「確認一個都沒有」。
            var quantity = row.CheapestQuantity;
            Grey(quantity < 0 ? "×?" : "×" + quantity.ToString("N0"));

            Tooltip("??  ??  ?? listing(s)  NQ ??  HQ ??  source ??".Loc(
                world.Name,
                FormatAge(row.AtUtc),
                row.ListingCount < 0 ? "?" : row.ListingCount.ToString(),
                row.LowestNq < 0 ? "-" : row.LowestNq.ToString("N0") + " ×" + FormatQuantity(row.NqQuantity),
                row.LowestHq < 0 ? "-" : row.LowestHq.ToString("N0") + " ×" + FormatQuantity(row.HqQuantity),
                row.Source));
        }

        private static string FormatQuantity(long quantity)
            => quantity < 0 ? "?" : quantity.ToString("N0");

        private void DrawShoppingBestCell(ShoppingEntry entry)
        {
            if (entry.BestPrice < 0)
            {
                if (entry.HasAnyData)
                {
                    Grey("—");
                    Tooltip("Every world that was looked up had nobody selling this.".Loc());
                }
                else
                {
                    Grey("?");
                    Tooltip("No world has been looked up for this item yet.".Loc());
                }

                return;
            }

            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.HealerGreen);
            ImGui.TextUnformatted("?? at ?? gil".Loc(entry.BestWorldName, entry.BestPrice.ToString("N0")));
            ImGui.PopStyleColor();
            Tooltip(entry.WorstPrice > entry.BestPrice
                ? "?? is the cheapest looked-up world; the dearest was ?? at ?? (?? more per unit)."
                    .Loc(entry.BestWorldName, entry.WorstWorldName, entry.WorstPrice.ToString("N0"),
                        (entry.WorstPrice - entry.BestPrice).ToString("N0"))
                : "Only one world has a price for this item so far.".Loc());
        }

        private void RequestShoppingLoad()
        {
            if (shoppingLoadTask != null)
                return;
            shoppingLoadRequested = true;
            // 🔴 讀檔一律丟到執行緒池。
            shoppingLoadTask = Task.Run(ShoppingSurveyLog.LoadAll);
        }

        private void PumpShoppingLoad()
        {
            var task = shoppingLoadTask;
            if (task == null || !task.IsCompleted)
                return;

            shoppingLoadTask = null;
            List<ShoppingSurveyRow> rows;
            try
            {
                rows = task.GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                Log.Information(e, "[Marketbuddy] 採購：整理採購行情時發生例外。");
                return;
            }

            RebuildShopping(rows);
            shoppingLoadedAt = DateTime.UtcNow;
        }

        private void RebuildShopping(List<ShoppingSurveyRow> rows)
        {
            shoppingEntries.Clear();
            shoppingWorlds.Clear();
            shoppingHiddenExcludedWorlds.Clear();
            shoppingTotalRowsLoaded = rows.Count;

            var latestPerWorld = new Dictionary<uint, (string Name, DateTime At)>();
            var byItem = new Dictionary<uint, ShoppingEntry>();

            foreach (var row in rows)
            {
                // 🔴 被排除的世界整列不算。這一張表的用途就是「出發前決定要去哪個世界買」，
                //    列一個去不了的世界而且說它最便宜，比不列它糟得多。
                if (conf.IsWorldExcluded(row.WorldId))
                {
                    shoppingHiddenExcludedWorlds.Add(
                        row.WorldName.Length > 0 ? row.WorldName : "#" + row.WorldId);
                    continue;
                }

                if (!latestPerWorld.TryGetValue(row.WorldId, out var w) || row.AtUtc > w.At)
                    latestPerWorld[row.WorldId] = (row.WorldName, row.AtUtc);

                if (!byItem.TryGetValue(row.ItemId, out var entry))
                {
                    byItem[row.ItemId] = entry = new ShoppingEntry
                    {
                        ItemId = row.ItemId,
                        Label = ItemName(row.ItemId),
                    };
                    shoppingEntries.Add(entry);
                }

                // 同一個世界只留最新的那一列。
                if (entry.ByWorld.TryGetValue(row.WorldId, out var existing) && existing.AtUtc >= row.AtUtc)
                    continue;
                entry.ByWorld[row.WorldId] = row;
            }

            foreach (var (worldId, (name, at)) in latestPerWorld)
                shoppingWorlds.Add((worldId, name, at));

            shoppingWorlds.Sort((a, b) => a.WorldId.CompareTo(b.WorldId));

            foreach (var entry in shoppingEntries)
                entry.Summarise();

            SortShoppingEntries();
        }

        private void SortShoppingEntries()
        {
            switch (shoppingSortMode)
            {
                case 1:
                    shoppingEntries.Sort((a, b) =>
                    {
                        // 沒有價格的排最後，而不是排在「0 gil」的位置。
                        var left = a.BestPrice < 0 ? long.MaxValue : a.BestPrice;
                        var right = b.BestPrice < 0 ? long.MaxValue : b.BestPrice;
                        var c = left.CompareTo(right);
                        return c != 0 ? c : a.ItemId.CompareTo(b.ItemId);
                    });
                    return;
                case 2:
                    shoppingEntries.Sort((a, b) => a.ItemId.CompareTo(b.ItemId));
                    return;
                default:
                    shoppingEntries.Sort((a, b) =>
                    {
                        var c = b.Spread.CompareTo(a.Spread);
                        return c != 0 ? c : a.ItemId.CompareTo(b.ItemId);
                    });
                    return;
            }
        }

        /// <summary>採購清單檔換了新版本就把「我想買幾個」重建一次。</summary>
        private void RefreshShoppingWanted()
        {
            var generation = ShoppingList.Generation;
            if (generation == shoppingWantedGeneration)
                return;
            shoppingWantedGeneration = generation;

            shoppingWantedById.Clear();
            foreach (var item in ShoppingList.Items)
                shoppingWantedById[item.ItemId] = item.Wanted;
        }

        private static string SafeShoppingListPath()
        {
            try
            {
                return ShoppingList.FilePath;
            }
            catch
            {
                return ShoppingList.FileName;
            }
        }

        /// <summary>採購表格的一列：一件想買的道具，以及它在各世界的最新一筆記錄。</summary>
        private sealed class ShoppingEntry
        {
            public uint ItemId;
            public string Label = string.Empty;
            public readonly Dictionary<uint, ShoppingSurveyRow> ByWorld = new();

            /// <summary>所有查過的世界裡最便宜的單價（-1＝沒有任何世界有人在賣，或都還沒查）。</summary>
            public long BestPrice = -1;

            public string BestWorldName = string.Empty;

            /// <summary>所有查過的世界裡最貴的單價（-1＝同上）。</summary>
            public long WorstPrice = -1;

            public string WorstWorldName = string.Empty;

            /// <summary>最貴與最便宜的差（0＝只有一個世界有價格，或資料不足）。</summary>
            public long Spread;

            /// <summary>至少有一個世界問到了答案（用來分辨「沒人在賣」與「還不知道」）。</summary>
            public bool HasAnyData;

            public void Summarise()
            {
                BestPrice = -1;
                WorstPrice = -1;
                BestWorldName = string.Empty;
                WorstWorldName = string.Empty;
                Spread = 0;
                HasAnyData = false;

                foreach (var row in ByWorld.Values)
                {
                    if (!row.Answered)
                        continue;
                    HasAnyData = true;

                    var price = row.CheapestPrice;
                    if (price < 0)
                        continue;

                    if (BestPrice < 0 || price < BestPrice)
                    {
                        BestPrice = price;
                        BestWorldName = row.WorldName;
                    }

                    if (WorstPrice < 0 || price > WorstPrice)
                    {
                        WorstPrice = price;
                        WorstWorldName = row.WorldName;
                    }
                }

                if (BestPrice >= 0 && WorstPrice > BestPrice)
                    Spread = WorstPrice - BestPrice;
            }
        }

        // =====================================================================
        //  記錄檔載入與整理
        // =====================================================================

        private void RequestLoad()
        {
            if (loadTask != null)
                return;
            loadRequested = true;
            // 🔴 讀檔一律丟到執行緒池：這個檔案會長到幾萬列。
            loadTask = Task.Run(PriceSurveyLog.LoadAll);
        }

        private void PumpLoad()
        {
            var task = loadTask;
            if (task == null || !task.IsCompleted)
                return;

            loadTask = null;
            List<PriceSurveyRow> rows;
            try
            {
                rows = task.GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                Log.Information(e, "[Marketbuddy] 巡檢：整理比價資料時發生例外。");
                return;
            }

            Rebuild(rows);
            loadedAt = DateTime.UtcNow;
        }

        private void Rebuild(List<PriceSurveyRow> rows)
        {
            entries.Clear();
            worlds.Clear();
            worldLatest.Clear();
            hiddenExcludedWorlds.Clear();
            totalRowsLoaded = rows.Count;

            var latestPerWorld = new Dictionary<uint, (string Name, DateTime At)>();
            var byKey = new Dictionary<(uint ItemId, bool Hq), CompareEntry>();

            foreach (var row in rows)
            {
                // 🔴 被排除的世界整列不算：欄位、最低價、壓價幅度、排序全部跟著一致，
                //    不會出現「欄位藏起來了但最便宜的還是它」那種自相矛盾的畫面。
                //    ⚠️ 記錄檔<b>沒有</b>被改動，勾回來就整份回來了。
                if (conf.IsWorldExcluded(row.WorldId))
                {
                    hiddenExcludedWorlds.Add(row.WorldName.Length > 0 ? row.WorldName : "#" + row.WorldId);
                    continue;
                }

                if (!latestPerWorld.TryGetValue(row.WorldId, out var w) || row.AtUtc > w.At)
                    latestPerWorld[row.WorldId] = (row.WorldName, row.AtUtc);

                var key = (row.ItemId, row.Hq);
                if (!byKey.TryGetValue(key, out var entry))
                {
                    byKey[key] = entry = new CompareEntry
                    {
                        ItemId = row.ItemId,
                        Hq = row.Hq,
                        Label = ItemName(row.ItemId) + (row.Hq ? " " + (char)SeIconChar.HighQuality : string.Empty),
                    };
                    entries.Add(entry);
                }

                // 同一個世界只留最新的那一列。
                if (entry.ByWorld.TryGetValue(row.WorldId, out var existing) && existing.AtUtc >= row.AtUtc)
                    continue;
                entry.ByWorld[row.WorldId] = row;
            }

            foreach (var (worldId, (name, at)) in latestPerWorld)
            {
                worlds.Add((worldId, name, at));
                worldLatest[worldId] = at;
            }

            worlds.Sort((a, b) => a.WorldId.CompareTo(b.WorldId));

            foreach (var entry in entries)
                entry.Summarise();

            SortEntries();
        }

        private void SortEntries()
        {
            switch (sortMode)
            {
                case 1:
                    entries.Sort((a, b) =>
                    {
                        var c = a.ItemId.CompareTo(b.ItemId);
                        return c != 0 ? c : a.Hq.CompareTo(b.Hq);
                    });
                    return;
                case 2:
                    entries.Sort((a, b) => b.OurPrice.CompareTo(a.OurPrice));
                    return;
                default:
                    entries.Sort((a, b) =>
                    {
                        var c = b.WorstUndercut.CompareTo(a.WorstUndercut);
                        return c != 0 ? c : a.ItemId.CompareTo(b.ItemId);
                    });
                    return;
            }
        }

        // =====================================================================
        //  待處理
        // =====================================================================

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

        private void DrawPendingTab()
        {
            var pending = gui.Pending;
            if (pending == null)
            {
                ImGui.Spacing();
                Grey("The pending list is not available.".Loc());
                return;
            }

            // 讓 framework 執行緒知道「現在有人在看」，它才會去拍僱員與引擎狀態的快照。
            pending.NoteUiVisible();

            ImGui.Spacing();

            using (Disabled(pending.IsRecomputing))
            {
                if (ImGui.Button("Recalculate".Loc()))
                    pending.RequestRecompute("user button");
            }

            Tooltip(
                "Reads the survey log and your listings and works out which items are undercut or should come off the board. Pure arithmetic: no market query is sent, and no price is ever changed by this button."
                    .Loc());

            ImGui.SameLine();
            if (pending.IsRecomputing)
                Grey("Recomputing...".Loc());
            else if (pending.LastComputedAt == DateTime.MinValue)
                Grey("Not calculated in this session yet".Loc());
            else
                Grey("Last calculated ??".Loc(FormatAge(pending.LastComputedAt)));

            if (ImGui.Checkbox("Recalculate automatically when a survey finishes".Loc(),
                    ref conf.PendingRecomputeAfterSurvey))
                conf.Save();
            Tooltip(
                "On (default): the moment a world has been surveyed all the way to the end, this list is worked out again from the fresh data. It only calculates - it never changes a price, never delists anything and never sends an extra market query. Repricing always stays one button per row, pressed by you."
                    .Loc());

            ImGui.Checkbox("Also show the ones you skipped".Loc(), ref showSkipped);
            ImGui.SameLine();
            using (Disabled(pending.IsRecomputing))
            {
                if (ImGui.Button("Clear skip marks".Loc()))
                    PendingActions.ClearSkipped();
            }

            Tooltip("Puts every entry you skipped back on the list.".Loc());

            if (pending.StatusText.Length > 0)
            {
                ImGui.Spacing();
                ImGui.TextWrapped(pending.StatusText);
            }

            ImGui.Spacing();
            // 🔑 「被壓價」整個桶都是相對於家世界定義的，所以那是哪一個世界必須看得見。
            if (pending.HomeWorldId != 0)
                Grey("Home world: ??".Loc(pending.HomeWorldName));
            else
                Grey("Home world: ? (not logged in)".Loc());
            Grey("List file: ??".Loc(PendingActions.FileName));
            Tooltip(SafePendingPath());
            Grey("A grey ? means \"not known\" - never 0 gil. A suggested price from another world is a reference only: that is a different market."
                .Loc());
            // 🔑 藏了東西就要說出來：拿掉一個世界的建議價，
            //    與「那件東西真的沒人在賣」在畫面上分不出來。
            if (pending.ExcludedWorldsInLog.Count > 0)
            {
                Grey("?? excluded world(s) not used for suggestions: ??".Loc(
                    pending.ExcludedWorldsInLog.Count, string.Join(", ", pending.ExcludedWorldsInLog)));
                Tooltip("Those worlds are on your excluded list, so their prices are never used as a suggested price here. Nothing was deleted - the survey log still has every row, and unticking a world brings it back on the next recalculation. Your own world is never excluded from its own pricing."
                    .Loc());
            }

            ImGui.Spacing();
            ImGui.Separator();

            var rows = pending.Snapshot;
            var shown = 0;
            foreach (var kind in BucketOrder)
                shown += DrawBucket(pending, rows, kind);

            if (shown != 0)
                return;

            ImGui.Spacing();
            ImGui.TextWrapped(PendingActions.Loaded
                ? "Nothing is waiting for you right now.".Loc()
                : "Reading the list file...".Loc());
        }

        private int DrawBucket(PendingActionsBuilder pending, IReadOnlyList<PendingActionRow> rows,
            PendingActionKind kind)
        {
            var bucket = new List<PendingActionRow>();
            var hiddenSkipped = 0;
            foreach (var row in rows)
            {
                if (row.Kind != kind)
                    continue;
                if (row.Skipped && !showSkipped)
                {
                    hiddenSkipped++;
                    continue;
                }

                bucket.Add(row);
            }

            bucket.Sort(CompareBucketRows);

            var header = BucketTitle(kind).Loc() + $" ({bucket.Count})";
            ImGui.Spacing();
            if (!ImGui.CollapsingHeader(header + "###mbpendingbucket" + (int)kind,
                    ImGuiTreeNodeFlags.DefaultOpen))
                return bucket.Count + hiddenSkipped;

            Grey(BucketHelp(kind).Loc());

            if (hiddenSkipped > 0)
                Grey("?? entry(ies) hidden because you skipped them.".Loc(hiddenSkipped));

            if (bucket.Count == 0)
            {
                Grey("Nothing in this group.".Loc());
                return hiddenSkipped;
            }

            DrawBucketTable(pending, bucket, kind);
            return bucket.Count + hiddenSkipped;
        }

        /// <summary>
        /// 排序：能算出「可以省多少」的排前面（差額大的優先），其餘照道具編號。
        /// 差額不知道時排在最後——那些是「還缺資料」而不是「沒事」。
        /// </summary>
        private static int CompareBucketRows(PendingActionRow a, PendingActionRow b)
        {
            var da = Gap(a);
            var db = Gap(b);
            if (da != db)
                return db.CompareTo(da);
            var c = a.ItemId.CompareTo(b.ItemId);
            return c != 0 ? c : a.Slot.CompareTo(b.Slot);
        }

        /// <summary>目前價與建議價的差額；任一邊不知道時 -1（＝排最後，不是 0）。</summary>
        private static long Gap(PendingActionRow row)
            => row.CurrentPrice < 0 || row.SuggestedPrice < 0 ? -1 : row.CurrentPrice - row.SuggestedPrice;

        private static string BucketTitle(PendingActionKind kind) => kind switch
        {
            PendingActionKind.PriceCap => "Parked at the price cap",
            PendingActionKind.PriceAnomaly => "Left alone - the cheap price looks mistyped",
            PendingActionKind.BelowMinimum => "Should come off the board",
            _ => "Undercut on your home world",
        };

        private static string BucketHelp(PendingActionKind kind) => kind switch
        {
            PendingActionKind.PriceCap =>
                "Quick-listed while nobody was selling that item, so no price could be worked out and it was left at the cap on purpose. Nobody can buy these until you price them.",
            PendingActionKind.PriceAnomaly =>
                "The cheapest price found was so far below what everything else costs that it looks like somebody dropped a zero, so your price was left exactly as it was - nothing here was changed. Check it yourself: if the cheap listing is real, reprice by hand. This list is worked out again every time you recalculate, so an entry disappears once that listing is gone - the permanent record is the ANOMALY-HOLD line in /xllog.",
            PendingActionKind.BelowMinimum =>
                "Going by the current market these would end up under the minimum price you set, so relisting them would delist them instead. Decide whether to keep holding them.",
            _ =>
                "Somebody else on your home world is selling the same thing cheaper than you, going by the last survey.",
        };

        private void DrawBucketTable(PendingActionsBuilder pending, List<PendingActionRow> bucket,
            PendingActionKind kind)
        {
            const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
                                          ImGuiTableFlags.ScrollX | ImGuiTableFlags.SizingFixedFit;

            if (!ImGui.BeginTable("##mbpendingtable" + (int)kind, 6, flags))
                return;

            ImGui.TableSetupColumn("Item".Loc(), ImGuiTableColumnFlags.WidthFixed, 220);
            ImGui.TableSetupColumn("Retainer / slot".Loc(), ImGuiTableColumnFlags.WidthFixed, 150);
            ImGui.TableSetupColumn("Now".Loc(), ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Suggested".Loc(), ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Source".Loc(), ImGuiTableColumnFlags.WidthFixed, 180);
            ImGui.TableSetupColumn("Actions".Loc(), ImGuiTableColumnFlags.WidthFixed, 170);
            ImGui.TableHeadersRow();

            var index = 0;
            foreach (var row in bucket)
            {
                ImGui.TableNextRow();
                ImGui.PushID(index++);

                ImGui.TableNextColumn();
                var label = ItemName(row.ItemId) + (row.Hq ? " " + (char)SeIconChar.HighQuality : string.Empty);
                if (row.Skipped)
                    Grey(label);
                else
                    ImGui.TextUnformatted(label);
                if (row.Skipped)
                    Tooltip("You skipped this one.".Loc());

                ImGui.TableNextColumn();
                DrawPlacementCell(row);

                ImGui.TableNextColumn();
                if (row.CurrentPrice < 0)
                {
                    Grey("?");
                    Tooltip("The current price of this listing is not known.".Loc());
                }
                else
                {
                    ImGui.TextUnformatted(row.CurrentPrice.ToString("N0"));
                }

                ImGui.TableNextColumn();
                DrawSuggestedCell(row);

                ImGui.TableNextColumn();
                DrawSourceCell(row);

                ImGui.TableNextColumn();
                DrawRowActions(pending, row);

                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        private void DrawPlacementCell(PendingActionRow row)
        {
            if (row.RetainerId == 0 || row.Slot < 0)
            {
                // 🔑 「不知道它掛在哪」本身要看得見：藏起來的話使用者會以為清單只是漏了這件。
                Grey("?");
                Tooltip(
                    "Which retainer and slot this listing sits in is not known, so the reprice button cannot be used on it. Install AllaganTools (or open the retainer's sell list once) and recalculate."
                        .Loc());
                return;
            }

            var name = row.RetainerName.Length > 0 ? row.RetainerName : "?";
            ImGui.TextUnformatted("??  #??".Loc(name, row.Slot + 1));
            if (row.RetainerName.Length == 0)
                Tooltip("That retainer belongs to another character, so its name cannot be read from here.".Loc());
        }

        private static void DrawSuggestedCell(PendingActionRow row)
        {
            // 🔴 「疑似打錯的低價」這一桶的 SuggestedPrice 不是「建議你掛的價」，
            //    而是「被擋下來的那個可疑價」。畫成綠色的建議價會把意思完全弄反。
            if (row.Kind == PendingActionKind.PriceAnomaly)
            {
                if (row.SuggestedPrice < 0)
                {
                    Grey("?");
                    Tooltip("The suspicious price is not known.".Loc());
                    return;
                }

                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
                ImGui.TextUnformatted(row.SuggestedPrice.ToString("N0"));
                ImGui.PopStyleColor();
                Tooltip(
                    "The price that was refused - what the cheapest listing (or the last sale) was asking. Your own price was left exactly as it was; nothing was changed."
                        .Loc());
                return;
            }

            if (row.SuggestedPrice < 0)
            {
                // 🔴 絕不畫成 0：那是一個合法但荒謬的價格。
                Grey("?");
                Tooltip(row.SuggestionSource == "survey-excluded"
                    ? "The only price known for this item is on a world you excluded, so nothing is suggested. The source column says which world."
                        .Loc()
                    : "No usable reference price was found for this item.".Loc());
                return;
            }

            var reference = !row.SuggestionIsLocal;
            ImGui.PushStyleColor(ImGuiCol.Text,
                reference ? ImGuiColors.DalamudGrey : ImGuiColors.HealerGreen);
            ImGui.TextUnformatted(row.SuggestedPrice.ToString("N0"));
            ImGui.PopStyleColor();
            Tooltip(reference
                ? "Worked out from another world's listings, so treat it as a hint only - that is a different market."
                    .Loc()
                : "Your undercut settings applied to the cheapest listing that is not yours. Pressing the reprice button asks the server again and lets the relist engine decide the real price."
                    .Loc());
        }

        private static void DrawSourceCell(PendingActionRow row)
        {
            if (row.SuggestionSource.Length == 0)
            {
                Grey("?");
                Tooltip("No reference price: neither the live cache nor the survey log knows this item.".Loc());
                return;
            }

            var world = row.SuggestionWorld.Length > 0 ? row.SuggestionWorld : "?";
            var age = row.SuggestionAtUtc == DateTime.MinValue ? "?" : FormatAge(row.SuggestionAtUtc);

            switch (row.SuggestionSource)
            {
                case "live":
                    ImGui.TextUnformatted("live  ??  ??".Loc(world, age));
                    Tooltip("From the market data this plugin already had in memory for your own world.".Loc());
                    return;
                case "survey":
                    ImGui.TextUnformatted("survey  ??  ??".Loc(world, age));
                    Tooltip("From the cross-world survey log, the row for your own world.".Loc());
                    return;
                case PriceSourceTag.Sale:
                    // 🔑 這一格的年齡就是「那筆成交是多久以前的事」——新的定價規則只採
                    //    新鮮度窗內的成交，所以它是判讀「為什麼這一件走成交價」的關鍵欄位。
                    ImGui.TextUnformatted("last sale  ??  ??".Loc(world, age));
                    Tooltip(
                        "What this item actually sold for on your data centre, rounded down to 100 gil. The relist engine uses whichever is lower - this, or the cheapest listing on your own world."
                            .Loc());
                    return;
                case "anomaly-peer":
                    // 🔑 「拿什麼當正常價」必須在列上看得見：那是整個判定的全部依據。
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
                    ImGui.TextUnformatted("looks mistyped  normal ??  ??".Loc(world, age));
                    ImGui.PopStyleColor();
                    Tooltip(
                        "The next seller along is asking ?? gil, so the cheapest listing looks like somebody dropped a zero. This test compares one seller against another, so a market-wide crash never lands here - only a single odd listing does."
                            .Loc(world));
                    return;
                case "anomaly-own":
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
                    ImGui.TextUnformatted("looks mistyped  vs your ??  ??".Loc(world, age));
                    ImGui.PopStyleColor();
                    Tooltip(
                        "Nobody else was selling, so there was no second seller to compare against and your own ?? gil had to be the yardstick. This is the weaker of the two tests: it cannot tell \"somebody mistyped\" apart from \"you are asking too much\"."
                            .Loc(world));
                    return;
                case "survey-excluded":
                    // 🔑 「知道但故意不用」要看得見，而且要看得見是哪一個世界——
                    //    否則它跟「真的沒資料」在畫面上是同一個灰色問號。
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
                    ImGui.TextUnformatted("excluded  ??  ??".Loc(world, age));
                    ImGui.PopStyleColor();
                    Tooltip(
                        "The only price known for this item is on ??, which is on your excluded list, so it was not used. Untick that world and recalculate to use it again."
                            .Loc(world));
                    return;
                default:
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
                    ImGui.TextUnformatted("other world  ??  ??".Loc(world, age));
                    ImGui.PopStyleColor();
                    Tooltip(
                        "Your own world has no data for this item, so this is the cheapest listing found on another world. Buyers cannot reach it from here - use it as a hint, not as a price."
                            .Loc());
                    return;
            }
        }

        /// <summary>
        /// 一列上的兩顆按鈕。
        /// 🔴 「重新定價」<b>不會自己算一個價格寫進去</b>：它把那一格原封不動交給既有的
        /// <c>BatchReprice.StartQuickReprice</c>，由那條路徑重新向伺服器問一次行情、
        /// 套用使用者自己的降價設定、必要時依門檻下架。這裡沒有第二套改價實作。
        /// </summary>
        private static void DrawRowActions(PendingActionsBuilder pending, PendingActionRow row)
        {
            var addressable = row.RetainerId != 0 && row.Slot >= 0;
            var thisRetainer = addressable && row.RetainerId == pending.ActiveRetainerId;
            var canReprice = thisRetainer && pending.RepriceReady;

            using (Disabled(!canReprice))
            {
                if (ImGui.Button("Reprice".Loc()))
                    pending.RequestReprice(row.ItemId, row.Hq, row.Slot);
            }

            if (!canReprice)
            {
                string why;
                if (!addressable)
                    why = "This entry has no known retainer and slot, so it cannot be handed to the relist engine."
                        .Loc();
                else if (!thisRetainer)
                    why = "Open this retainer's sell list first - the button only works on the retainer in front of you."
                        .Loc();
                else
                    why = pending.RepriceBlockedReason.Length > 0
                        ? "Cannot reprice right now: ??".Loc(pending.RepriceBlockedReason)
                        : "Cannot reprice right now.".Loc();
                Tooltip(why);
            }
            else
            {
                Tooltip(
                    "Hands this one slot to the same single-item pricing the quick lister uses: it asks the server for the current listings and applies your own undercut settings. The suggested price in the table is only an estimate of where it will land."
                        .Loc());
            }

            ImGui.SameLine();
            using (Disabled(row.Skipped))
            {
                if (ImGui.Button("Skip".Loc()))
                    PendingActions.MarkSkipped(row.Key);
            }

            Tooltip(row.Skipped
                ? "Already skipped. Use \"Clear skip marks\" above to bring it back."
                    .Loc()
                : "Takes this entry off the list. Recalculating will not bring it back until you clear the skip marks."
                    .Loc());
        }

        private static string SafePendingPath()
        {
            try
            {
                return PendingActions.FilePath;
            }
            catch
            {
                return PendingActions.FileName;
            }
        }

        // =====================================================================
        //  銷售
        // =====================================================================

        /// <summary>銷售記錄檔的讀取工作（一定在執行緒池上）。</summary>
        private Task<List<RetainerSaleRow>>? salesTask;

        /// <summary>畫面上顯示的列（檔案列＋這個工作階段剛寫的列，已去重並由新到舊排序）。</summary>
        private readonly List<RetainerSaleRow> sales = [];

        /// <summary>已經載入的是哪一代記錄（<see cref="RetainerSalesLog.Revision"/>）；-1＝還沒載過。</summary>
        private long salesRevisionLoaded = -1;

        /// <summary>我方自己下架的那些列要不要一起顯示。預設不顯示：那不是賣出，只是把帳補平。</summary>
        private bool showOwnDelists;

        /// <summary>
        /// 「銷售」分頁畫的是時間序事件表（false，既有行為），還是<b>一件一列</b>的彙總（true）。
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

        private void DrawSalesTab()
        {
            PumpSalesLoad();
            if (salesTask == null && salesRevisionLoaded != RetainerSalesLog.Revision)
                RequestSalesLoad();

            ImGui.Spacing();

            if (ImGui.Checkbox("Record what disappears from your retainers' listings".Loc(),
                    ref conf.RetainerSalesLogEnabled))
                conf.Save();
            Tooltip(
                "On (default). While a retainer's sell list is open this reads that retainer's market container and gil, and writes down anything that vanished since the last time it was seen. Read-only: no packets, no hooks, nothing is ever listed, repriced or delisted because of it."
                    .Loc());

            ImGui.SameLine();
            using (Disabled(salesTask != null))
            {
                if (ImGui.Button("Reload".Loc()))
                    RequestSalesLoad();
            }

            // 🔑 一件一列的彙總才回答得了「這件到底掛不掛得掉」；時間序回答的是「剛剛發生了什麼」。
            ImGui.Checkbox("One row per item instead of a timeline".Loc(), ref salesByItem);
            Tooltip(
                "Adds up every entry for the same item so you can see whether it has ever actually sold, instead of listing the events in the order they happened."
                    .Loc());

            if (!salesByItem)
            {
                ImGui.Checkbox("Also show what Marketbuddy delisted itself".Loc(), ref showOwnDelists);
                Tooltip("Those are not sales - they are in the file so the numbers add up.".Loc());
            }

            ImGui.Spacing();
            DrawSalesSummary();

            ImGui.Spacing();
            Grey("Sales file: ??".Loc(RetainerSalesLog.FileName));
            Tooltip(SafeSalesPath());
            Grey("Baseline file: ?? (?? retainer(s) known)".Loc(RetainerMarketState.FileName,
                RetainerMarketState.Count));
            Tooltip(
                "A retainer only starts producing entries after its sell list has been open once - that first visit is what establishes the baseline to compare against."
                    .Loc());

            // 🔑 「有沒有真的在取樣」必須在列上看得見：取不到樣的話這個功能是完全靜默的，
            //    而「一件都沒賣掉」與「根本沒看過你的僱員」長得一模一樣。
            var watcher = gui.SalesWatcher;
            if (watcher != null)
            {
                if (watcher.LastSnapshotAt == DateTime.MinValue)
                    Grey("No snapshot taken in this session yet.".Loc());
                else
                    Grey("Last snapshot ?? (?? this session)".Loc(
                        FormatAge(watcher.LastSnapshotAt), watcher.SnapshotsTaken));

                if (watcher.LastSkipReason.Length > 0)
                    Grey("Last sample skipped: ??".Loc(watcher.LastSkipReason));
            }

            ImGui.Spacing();
            ImGui.Separator();

            if (salesTask != null && sales.Count == 0)
            {
                ImGui.Spacing();
                Grey("Reading the sales file...".Loc());
                return;
            }

            if (salesByItem)
                DrawSalesByItemTable();
            else
                DrawSalesTable();
        }

        /// <summary>
        /// 摘要。🔴 只加總<b>高信心</b>的列，而且低信心的件數一定要放在旁邊看得見——
        /// 少了那半句，一個偏低的數字看起來就像一個準確的數字。
        /// </summary>
        private void DrawSalesSummary()
        {
            var since = DateTime.UtcNow.AddDays(-SalesSummaryDays);
            var soldItems = 0;
            var soldEvents = 0;
            long received = 0;
            var unknownItems = 0;
            var receivedUnknown = false;

            foreach (var row in sales)
            {
                if (row.AtUtc < since)
                    continue;
                switch (row.Confidence)
                {
                    case RetainerSaleConfidence.Sold:
                        soldItems += row.Quantity;
                        soldEvents++;
                        if (row.Received >= 0)
                            received += row.Received;
                        else
                            receivedUnknown = true;
                        break;
                    case RetainerSaleConfidence.Unknown:
                        unknownItems += row.Quantity;
                        break;
                }
            }

            if (soldEvents == 0 && unknownItems == 0)
            {
                Grey("Nothing recorded in the last ?? days.".Loc(SalesSummaryDays));
                return;
            }

            ImGui.TextUnformatted("Last ?? days: ?? item(s) sold with high confidence, ?? gil received"
                .Loc(SalesSummaryDays, soldItems, received.ToString("N0")));
            if (receivedUnknown)
                Grey("Some of those sales have no amount, so the total is a lower bound.".Loc());
            if (unknownItems > 0)
                Grey("?? more item(s) only known to have disappeared - not counted.".Loc(unknownItems));
        }

        private void DrawSalesTable()
        {
            const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
                                          ImGuiTableFlags.ScrollX | ImGuiTableFlags.SizingFixedFit;

            var shown = 0;
            var hiddenDelists = 0;
            var visible = new List<RetainerSaleRow>();
            foreach (var row in sales)
            {
                if (row.Confidence == RetainerSaleConfidence.Delisted && !showOwnDelists)
                {
                    hiddenDelists++;
                    continue;
                }

                if (visible.Count >= MaxSalesRows)
                {
                    shown++;
                    continue;
                }

                visible.Add(row);
                shown++;
            }

            if (hiddenDelists > 0)
                Grey("?? entry(ies) hidden because Marketbuddy delisted them itself.".Loc(hiddenDelists));

            if (visible.Count == 0)
            {
                ImGui.Spacing();
                ImGui.TextWrapped(
                    "Nothing recorded yet. Open a retainer's sell list once to establish the baseline; anything that has gone from it the next time you look shows up here."
                        .Loc());
                return;
            }

            if (shown > visible.Count)
                Grey("Showing the newest ?? of ?? entries; the file has them all.".Loc(visible.Count, shown));

            if (!ImGui.BeginTable("##mbsalestable", 7, flags))
                return;

            ImGui.TableSetupColumn("When".Loc(), ImGuiTableColumnFlags.WidthFixed, 120);
            ImGui.TableSetupColumn("Retainer".Loc(), ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableSetupColumn("Item".Loc(), ImGuiTableColumnFlags.WidthFixed, 220);
            ImGui.TableSetupColumn("Qty".Loc(), ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableSetupColumn("Unit price".Loc(), ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Received".Loc(), ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Confidence".Loc(), ImGuiTableColumnFlags.WidthFixed, 170);
            ImGui.TableHeadersRow();

            var index = 0;
            foreach (var row in visible)
            {
                ImGui.TableNextRow();
                ImGui.PushID(index++);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.AtUtc.ToLocalTime().ToString("MM-dd HH:mm"));
                Tooltip(row.PrevAtUtc == DateTime.MinValue
                    ? "?? (the previous look is not recorded)".Loc(FormatAge(row.AtUtc))
                    : "Between ?? and ?? (local time)".Loc(
                        row.PrevAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                        row.AtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")));

                ImGui.TableNextColumn();
                if (row.RetainerName.Length > 0)
                {
                    ImGui.TextUnformatted(row.RetainerName);
                }
                else
                {
                    Grey("?");
                    Tooltip("That retainer's name was not readable when this was recorded.".Loc());
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(ItemName(row.ItemId)
                                      + (row.Hq ? " " + (char)SeIconChar.HighQuality : string.Empty));

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Quantity.ToString("N0"));

                ImGui.TableNextColumn();
                if (row.UnitPrice < 0)
                {
                    // 🔑 「不知道」要在列上看得見。畫成 0 會被讀成「有人用 0 gil 買走」。
                    Grey("?");
                    Tooltip(
                        "You had that item listed at more than one price and the missing pieces span both, so which price they went at cannot be worked out."
                            .Loc());
                }
                else
                {
                    ImGui.TextUnformatted(row.UnitPrice.ToString("N0"));
                }

                ImGui.TableNextColumn();
                if (row.Received < 0)
                {
                    Grey("?");
                    Tooltip("No amount could be tied to this entry.".Loc());
                }
                else
                {
                    ImGui.TextUnformatted(row.Received.ToString("N0"));
                }

                ImGui.TableNextColumn();
                DrawConfidenceCell(row);

                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        private static void DrawConfidenceCell(RetainerSaleRow row)
        {
            switch (row.Confidence)
            {
                case RetainerSaleConfidence.Sold:
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.HealerGreen);
                    ImGui.TextUnformatted("Sold".Loc());
                    ImGui.PopStyleColor();
                    Tooltip(GilMatchExplanation(row));
                    return;
                case RetainerSaleConfidence.Delisted:
                    Grey("Delisted by Marketbuddy".Loc());
                    Tooltip("Marketbuddy took this off the board itself, so it is not a sale.".Loc());
                    return;
                default:
                    Grey("Gone - reason unknown".Loc());
                    Tooltip(
                        "It is no longer listed, but the retainer's gil did not move by the matching amount, so this may just as well have been delisted by hand. Deliberately not counted as income."
                            .Loc());
                    return;
            }
        }

        /// <summary>
        /// 這一列為什麼算高信心。<c>gross</c> 出現在這裡是有意義的資訊：
        /// 它代表僱員收到的是<b>未扣稅</b>的金額，也就是市場稅不是由賣方負擔。
        /// </summary>
        private static string GilMatchExplanation(RetainerSaleRow row)
        {
            var delta = row.HasGilDelta ? row.GilDelta.ToString("N0") : "?";
            return row.Basis == "gross"
                ? "The retainer's gil went up by ?? which matches the listing price before tax."
                    .Loc(delta)
                : "The retainer's gil went up by ?? which matches the listing price minus ??% market tax."
                    .Loc(delta, row.TaxPercent);
        }

        private void RequestSalesLoad()
        {
            if (salesTask != null)
                return;
            salesRevisionLoaded = RetainerSalesLog.Revision;
            // 🔴 讀檔一律丟到執行緒池。
            salesTask = Task.Run(RetainerSalesLog.LoadAll);
        }

        private void PumpSalesLoad()
        {
            var task = salesTask;
            if (task == null || !task.IsCompleted)
                return;

            salesTask = null;
            List<RetainerSaleRow> rows;
            try
            {
                rows = task.GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                Log.Information(e, "[Marketbuddy] 僱員銷售：讀取記錄檔時發生例外。");
                return;
            }

            // 追加是非同步的，剛記下的列可能還沒落地 —— 補上這個工作階段寫過的列。
            // 去重用 Format()（決定性，同一列永遠產生同一行）。
            var seen = new HashSet<string>();
            foreach (var row in rows)
                seen.Add(RetainerSalesLog.Format(row));
            foreach (var row in RetainerSalesLog.SessionRows())
            {
                if (seen.Add(RetainerSalesLog.Format(row)))
                    rows.Add(row);
            }

            rows.Sort((a, b) => b.AtUtc.CompareTo(a.AtUtc));

            sales.Clear();
            sales.AddRange(rows);
            salesStamp++;
        }

        /// <summary>
        /// 一件一列的彙總表：這件道具賣掉過幾次、沒賣掉就下架過幾次。
        /// 🔴 「賣出」只算高信心（<see cref="RetainerSaleConfidence.Sold"/>）。信心不足的那些
        /// 有自己的一欄，永遠不併進賣出——把它們加進去等於用一個自信的數字蓋掉「我們其實不知道」。
        /// 🔴 表格上方一定要寫出<b>紀錄從什麼時候開始</b>：一張看起來很空的表，可能是
        /// 「東西都賣不掉」，也可能是「我們才看了兩天」——那兩件事會導出相反的決定。
        /// 📌 純顯示：這張表沒有任何會改價或下架的按鈕。
        /// </summary>
        private void DrawSalesByItemTable()
        {
            EnsureSalesAggregate();
            var snapshot = salesAggregate;

            // 觀察起點。首選是「第一次看到某位僱員的清單」那個時間（掛售年齡紀錄），
            // 那份還沒讀回來時退而用「紀錄裡最早的一筆事件」——兩者都是下界，都誠實。
            // 🔴 兩個都拿不到時<b>不可以</b>省略這一行：一張空表若不寫紀錄從何時開始，
            //    「都賣不掉」與「才剛開始看」看起來一模一樣。
            var since = RetainerListingAge.EarliestSeen();
            if (since == null && snapshot.FirstEventUtc != DateTime.MinValue)
                since = snapshot.FirstEventUtc;

            if (since is { } start)
            {
                Grey("Records kept since ?? - only high-confidence sales count as sold.".Loc(
                    FormatCoverage(start)));
                Tooltip(
                    "Nothing that happened before Marketbuddy first had one of your retainers' sell lists open is in these numbers."
                        .Loc());
            }
            else
            {
                Grey("No retainer has been looked at yet, so there is no basis for any of these numbers.".Loc());
            }

            if (snapshot.ByItemAndQuality.Count == 0)
            {
                ImGui.Spacing();
                ImGui.TextWrapped(
                    "Nothing recorded yet. Open a retainer's sell list once to establish the baseline; anything that has gone from it the next time you look shows up here."
                        .Loc());
                return;
            }

            ImGui.Spacing();
            ImGui.SetNextItemWidth(300);
            if (ImGui.BeginCombo("##mbsalesitemsort", SalesItemSortKeys[salesItemSortMode].Loc()))
            {
                for (var i = 0; i < SalesItemSortKeys.Length; i++)
                {
                    if (ImGui.Selectable(SalesItemSortKeys[i].Loc(), salesItemSortMode == i))
                        salesItemSortMode = i;
                }

                ImGui.EndCombo();
            }

            var list = new List<((uint ItemId, bool Hq) Key, ItemSaleHistory History)>(
                snapshot.ByItemAndQuality.Count);
            foreach (var (key, value) in snapshot.ByItemAndQuality)
                list.Add((key, value));

            list.Sort((a, b) => salesItemSortMode switch
            {
                // 賣出過的一律排在沒賣出過的後面，同組再比「沒賣掉就下架」的次數。
                1 => b.History.SoldEvents != a.History.SoldEvents
                    ? b.History.SoldEvents.CompareTo(a.History.SoldEvents)
                    : b.History.SoldQuantity.CompareTo(a.History.SoldQuantity),
                2 => b.History.LastEventUtc.CompareTo(a.History.LastEventUtc),
                _ => (a.History.SoldEvents == 0) != (b.History.SoldEvents == 0)
                    ? (a.History.SoldEvents == 0 ? -1 : 1)
                    : b.History.OffBoardEvents.CompareTo(a.History.OffBoardEvents),
            });

            if (list.Count > MaxSalesRows)
                Grey("Showing ?? of ?? items; sorting picks which ones, the file has them all.".Loc(MaxSalesRows, list.Count));

            const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
                                          ImGuiTableFlags.ScrollX | ImGuiTableFlags.SizingFixedFit;
            if (!ImGui.BeginTable("##mbsalesitemtable", 6, flags))
                return;

            ImGui.TableSetupColumn("Item".Loc(), ImGuiTableColumnFlags.WidthFixed, 240);
            ImGui.TableSetupColumn("Sold".Loc(), ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("Received".Loc(), ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("Delisted".Loc(), ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("Gone - unknown".Loc(), ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("Last sold".Loc(), ImGuiTableColumnFlags.WidthFixed, 120);
            ImGui.TableHeadersRow();

            var index = 0;
            foreach (var (key, history) in list)
            {
                if (index >= MaxSalesRows)
                    break;

                ImGui.TableNextRow();
                ImGui.PushID(index++);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(ItemName(key.ItemId)
                                      + (key.Hq ? " " + (char)SeIconChar.HighQuality : string.Empty));

                ImGui.TableNextColumn();
                if (history.SoldEvents > 0)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.HealerGreen);
                    ImGui.TextUnformatted(history.SoldEvents.ToString("N0"));
                    ImGui.PopStyleColor();
                    Tooltip("?? item(s) in total.".Loc(history.SoldQuantity));
                }
                else
                {
                    Grey("0");
                    Tooltip("Never seen to sell in the records kept so far.".Loc());
                }

                ImGui.TableNextColumn();
                if (history.SoldEvents == 0)
                {
                    // 🔑 沒賣出過就沒有「實收」這回事——畫 0 會被讀成「賣掉了但一毛沒拿到」。
                    Grey("—");
                }
                else
                {
                    ImGui.TextUnformatted(history.SoldGil.ToString("N0"));
                    if (history.SoldGilIncomplete)
                        Tooltip("Some of those sales have no amount, so the total is a lower bound.".Loc());
                }

                ImGui.TableNextColumn();
                Grey(history.DelistedEvents.ToString("N0"));
                Tooltip("Marketbuddy took this off the board itself, so it is not a sale.".Loc());

                ImGui.TableNextColumn();
                Grey(history.UnknownEvents.ToString("N0"));
                Tooltip(
                    "It is no longer listed, but the retainer's gil did not move by the matching amount, so this may just as well have been delisted by hand. Deliberately not counted as income."
                        .Loc());

                ImGui.TableNextColumn();
                if (history.LastSoldUtc == DateTime.MinValue)
                    Grey("—");
                else
                    ImGui.TextUnformatted(history.LastSoldUtc.ToLocalTime().ToString("MM-dd HH:mm"));

                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        /// <summary>只有 <see cref="sales"/> 換過內容才重算彙總。純計算、零 I/O。</summary>
        private void EnsureSalesAggregate()
        {
            if (salesAggregateStamp == salesStamp)
                return;
            salesAggregateStamp = salesStamp;
            salesAggregate = SalesHistorySnapshot.Build(sales);
        }

        /// <summary>觀察期：從哪一天起算、到現在多久。</summary>
        private static string FormatCoverage(DateTime sinceUtc)
        {
            var span = DateTime.UtcNow - sinceUtc;
            if (span < TimeSpan.Zero)
                span = TimeSpan.Zero;

            var length = span.TotalDays >= 1
                ? "?? day(s)".Loc((int)span.TotalDays)
                : span.TotalHours >= 1
                    ? "?? hour(s)".Loc((int)span.TotalHours)
                    : "less than an hour".Loc();

            return "?? (??)".Loc(sinceUtc.ToLocalTime().ToString("yyyy-MM-dd"), length);
        }

        private static string SafeSalesPath()
        {
            try
            {
                return RetainerSalesLog.FilePath;
            }
            catch
            {
                return RetainerSalesLog.FileName;
            }
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
                return "?? minute(s) ago".Loc((int)age.TotalMinutes);
            if (age.TotalDays < 1)
                return "?? hour(s) ago".Loc((int)age.TotalHours);
            return "?? day(s) ago".Loc((int)age.TotalDays);
        }

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

        /// <summary>長說明放 tooltip，不占列上版面。</summary>
        private static void Tooltip(string text)
        {
            if (!ImGui.IsItemHovered())
                return;
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30f);
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
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

        /// <summary>比價表格的一列：一件道具的一個品質，以及它在各世界的最新一筆記錄。</summary>
        private sealed class CompareEntry
        {
            public uint ItemId;
            public bool Hq;
            public string Label = string.Empty;
            public readonly Dictionary<uint, PriceSurveyRow> ByWorld = new();

            /// <summary>我方在這個品質上的最低掛售價（-1＝不知道）。</summary>
            public long OurPrice = -1;

            /// <summary>所有已掃世界裡最便宜的價格（-1＝都沒有資料）。</summary>
            public long BestPrice = -1;

            public string BestWorldName = string.Empty;

            /// <summary>被壓得最兇的那個世界壓了多少（0＝沒有人比我便宜、或資料不足）。</summary>
            public long WorstUndercut;

            /// <summary>至少有一個世界問到了答案（用來分辨「沒被壓價」與「還不知道」）。</summary>
            public bool HasAnyData;

            public void Summarise()
            {
                OurPrice = -1;
                BestPrice = -1;
                BestWorldName = string.Empty;
                WorstUndercut = 0;
                HasAnyData = false;

                foreach (var row in ByWorld.Values)
                {
                    if (row.OurPrice >= 0 && (OurPrice < 0 || row.OurPrice < OurPrice))
                        OurPrice = row.OurPrice;

                    if (row.Verdict is not ("ok" or "empty"))
                        continue;
                    HasAnyData = true;

                    var price = row.LowestForQuality;
                    if (price < 0)
                        continue;
                    if (BestPrice < 0 || price < BestPrice)
                    {
                        BestPrice = price;
                        BestWorldName = row.WorldName;
                    }

                    var undercut = row.UndercutBy;
                    if (undercut > WorstUndercut)
                        WorstUndercut = undercut;
                }
            }
        }
    }
}
