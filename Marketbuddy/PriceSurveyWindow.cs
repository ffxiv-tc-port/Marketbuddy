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
    ///
    /// <list type="bullet">
    ///   <item><b>掃描</b>：這個世界要掃什麼、按下去開始、跑起來的進度。
    ///         🔴 這裡的「掃描這個世界」是整個功能<b>唯一</b>的啟動入口。</item>
    ///   <item><b>比價</b>：把記錄檔讀回來，一件一列、每個世界一欄，看誰在壓價。</item>
    ///   <item><b>待處理</b>：把「掛在上限價還沒定價」「被壓價」「該下架」三桶列成一張
    ///         有按鈕的工作清單，並且<b>存進檔案</b>，跨工作階段活著。
    ///         🔴 那些按鈕一顆都不會自己按下去——每一次改價都是使用者按的。</item>
    ///   <item><b>銷售</b>：僱員的掛售清單兩次之間少了什麼，一件一列，<b>每一列標信心</b>。
    ///         🔴 這裡刻意<b>沒有</b>「本週收益」這種總計：低信心的列本來就可能不是賣出，
    ///         把它們加總成一個數字等於用一個自信的數字蓋掉「我們其實不知道」。</item>
    /// </list>
    ///
    /// <para>
    /// 🔑 UI 規則：「不知道」必須在列上看得見。沒掃過的世界畫灰色的 <c>?</c>、
    /// 確認過沒人在賣畫灰色的 <c>—</c>，<b>絕不畫成 0</b>——0 在價格欄是一個合法但荒謬的值，
    /// 看起來像「那個世界真的有人用 0 gil 在賣」。長說明放 tooltip，列上只留判讀得出來的東西。
    /// </para>
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

        /// <summary>比價分頁的資料（已經整理成一件一列）。</summary>
        private readonly List<CompareEntry> entries = [];

        /// <summary>記錄檔裡出現過的世界，以及那個世界最後一次被掃到的時間。</summary>
        private readonly List<(uint WorldId, string Name, DateTime LatestAt)> worlds = [];

        private int totalRowsLoaded;

        /// <summary>換世界下拉選單目前選到第幾個。</summary>
        private int travelChoice;

        /// <summary>每個世界最後一次被掃到的時間（下拉選單上標「還沒掃過」用）。</summary>
        private readonly Dictionary<uint, DateTime> worldLatest = new();

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
            if (!loadRequested)
                RequestLoad();

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

        /// <summary>視窗被關掉時呼叫：巡檢跟著停。看不見的東西不應該還在跑。</summary>
        public void OnClosed()
        {
            survey.RequestStop("The survey window was closed".Loc());
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
            using (Disabled(running))
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
            else
                DrawIdleControls();

            DrawTravelSection(running);

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

            ImGui.Spacing();
            Grey("Whatever is looked up here is uploaded anonymously to Universalis by Dalamud itself (if you have that turned on in Dalamud). This plugin never contacts any website on its own."
                .Loc());
        }

        /// <summary>
        /// 「去下一個世界」。
        /// 🔴 這顆按鈕<b>只換世界</b>：按下去請 Lifestream 送你過去，抵達之後什麼都不會發生，
        /// 要掃描得自己回到上面再按一次「掃描這個世界」。刻意不串起來——
        /// 一顆按鈕就跑完八個世界那種東西是無人值守的自動化，不是這個功能要做的事。
        /// </summary>
        private void DrawTravelSection(bool running)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.TextUnformatted("Travel to another world".Loc());
            Grey("Travelling never starts a scan by itself - press the scan button again once you get there.".Loc());

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
            using (Disabled(running))
            {
                if (ImGui.Button("Travel there".Loc()))
                    survey.RequestChangeWorld(targets[travelChoice].Name);
            }

            if (survey.TravelStatus.Length > 0)
                ImGui.TextWrapped(survey.TravelStatus);
        }

        /// <summary>下拉選單上的一列：世界名，加上「這個世界上次掃到什麼時候」。</summary>
        private string WorldLabel((uint WorldId, string Name) target)
            => worldLatest.TryGetValue(target.WorldId, out var at)
                ? "?? (scanned ??)".Loc(target.Name, FormatAge(at))
                : "?? (not scanned yet)".Loc(target.Name);

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

        private void DrawRunningControls()
        {
            if (ImGui.Button("Stop the price survey".Loc(), new Vector2(180, 0)))
                survey.RequestStop("Stopped by the user".Loc());

            ImGui.Spacing();

            if (survey.State == PriceSurvey.SurveyState.Preparing)
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
            totalRowsLoaded = rows.Count;

            var latestPerWorld = new Dictionary<uint, (string Name, DateTime At)>();
            var byKey = new Dictionary<(uint ItemId, bool Hq), CompareEntry>();

            foreach (var row in rows)
            {
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

        /// <summary>三個桶的顯示順序。先處理最急的（掛在上限價＝現在沒人買得起）。</summary>
        private static readonly PendingActionKind[] BucketOrder =
        [
            PendingActionKind.PriceCap,
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
            PendingActionKind.BelowMinimum => "Should come off the board",
            _ => "Undercut on your home world",
        };

        private static string BucketHelp(PendingActionKind kind) => kind switch
        {
            PendingActionKind.PriceCap =>
                "Quick-listed while nobody was selling that item, so no price could be worked out and it was left at the cap on purpose. Nobody can buy these until you price them.",
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
            if (row.SuggestedPrice < 0)
            {
                // 🔴 絕不畫成 0：那是一個合法但荒謬的價格。
                Grey("?");
                Tooltip("No usable reference price was found for this item.".Loc());
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

            ImGui.Checkbox("Also show what Marketbuddy delisted itself".Loc(), ref showOwnDelists);
            Tooltip("Those are not sales - they are in the file so the numbers add up.".Loc());

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
