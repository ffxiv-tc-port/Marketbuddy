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

        public PriceSurveyWindow(PriceSurvey survey)
        {
            this.survey = survey;
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
