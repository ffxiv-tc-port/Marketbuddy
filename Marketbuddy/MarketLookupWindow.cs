using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Marketbuddy.Common;
using static Marketbuddy.Common.Dalamud;
using static Marketbuddy.Common.ImGuiEx;

namespace Marketbuddy
{
    /// <summary>
    /// 行情查詢：任一件可上市場的道具在各世界最近一次查到的最低價與成交摘要（資料來自觀測記錄：全市場掃描、兩台連線）。
    /// <para>🔴 純顯示：沒有任何會上架、改價、下架或送市場查詢的按鈕。</para>
    /// <para>🔴 執行緒：自家僱員與家世界只在 framework 執行緒拍（<see cref="OnFrameworkUpdate"/>），繪製只讀拍好的快照；
    /// 道具表列舉與觀測查詢在執行緒池上，繪製只收結果。</para>
    /// </summary>
    internal sealed class MarketLookupWindow : IDisposable
    {
        private const string LogTag = "[行情查詢]";
        private const int MaxResults = 500;
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(10);

        private readonly PriceSurvey survey;

        private Task<List<LookupCatalogEntry>>? catalogTask;
        private List<LookupCatalogEntry>? catalog;
        private bool catalogFailed;

        private string query = string.Empty;
        private string? resultsFor;
        private List<int> results = [];
        private int totalMatches;
        private bool autoSelectPending;

        private uint selectedItem;
        private string selectedName = string.Empty;

        private Task<List<MarketObservation>>? lookupTask;
        private uint lookupTaskFor;
        private List<LookupRow> rows = [];
        private uint rowsFor;
        private int cheapestNq = -1;
        private int cheapestHq = -1;
        private DateTime nextRefreshAt = DateTime.MinValue;
        private int seenSerial = -1;
        private bool seenLoaded;
        private Facts? rowsFacts;

        private sealed record Facts(HashSet<ulong> Own, uint HomeWorld, IReadOnlyList<(uint WorldId, string Name)> DataCentreWorlds);

        private volatile Facts? facts;
        private int factsRequested;
        private int disposed;

        private uint dcWorldsFor = uint.MaxValue;
        private IReadOnlyList<(uint WorldId, string Name)> dcWorlds = [];

        public bool Visible;

        public MarketLookupWindow(PriceSurvey survey)
        {
            this.survey = survey;
            Framework.Update += OnFrameworkUpdate;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            Framework.Update -= OnFrameworkUpdate;
        }

        /// <summary>打開視窗；有給查詢字（名稱或 id）就填進搜尋框，只有一件符合（或完全相同）時直接選它。</summary>
        internal void Open(string? search)
        {
            Visible = true;
            if (!string.IsNullOrWhiteSpace(search))
            {
                query = search.Trim();
                autoSelectPending = true;
            }

            RequestFacts();
        }

        private void RequestFacts() => Volatile.Write(ref factsRequested, 1);

        private Configuration conf => Configuration.GetOrLoad();

        // =====================================================================
        //  framework 執行緒
        // =====================================================================

        private void OnFrameworkUpdate(IFramework framework)
        {
            if (Volatile.Read(ref factsRequested) == 0 || Volatile.Read(ref disposed) != 0)
                return;
            Volatile.Write(ref factsRequested, 0);
            try
            {
                var own = OwnRetainers.Snapshot(conf);
                var home = PlayerState.ContentId == 0 ? 0u : PlayerState.HomeWorld.RowId;
                facts = new Facts(own, home, DataCentreWorldsOf(home));
            }
            catch (Exception e)
            {
                Log.Information(e, $"{LogTag} 讀自家僱員與家世界失敗；「自己的」與沒資料的世界這次標不出來。");
            }
        }

        /// <summary>家世界所在資料中心的世界（名稱非空）；不靠 <c>IsPublic</c>（台服正式世界全是 false）。</summary>
        private IReadOnlyList<(uint WorldId, string Name)> DataCentreWorldsOf(uint homeWorldId)
        {
            if (homeWorldId == dcWorldsFor)
                return dcWorlds;
            var list = new List<(uint, string)>();
            var sheet = DataManager.GetExcelSheet<World>();
            if (homeWorldId != 0 && sheet != null && sheet.TryGetRow(homeWorldId, out var home))
            {
                var dc = home.DataCenter.RowId;
                foreach (var row in sheet)
                {
                    if (row.DataCenter.RowId != dc)
                        continue;
                    var name = row.Name.ExtractText();
                    if (name.Length > 0)
                        list.Add((row.RowId, name));
                }
            }

            dcWorldsFor = homeWorldId;
            dcWorlds = list;
            return list;
        }

        // =====================================================================
        //  繪製
        // =====================================================================

        public void Draw()
        {
            if (!Visible)
                return;

            var minSize = new Vector2(FontPx(560), FontPx(300));
            ImGui.SetNextWindowSize(new Vector2(FontPx(860), FontPx(480)), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSizeConstraints(minSize, Vector2.Max(minSize, ImGui.GetMainViewport().WorkSize));
            if (!ImGui.Begin(DrawText.Concat("Market lookup".Loc(), "###MarketbuddyMarketLookup"), ref Visible,
                    ImGuiWindowFlags.NoCollapse))
            {
                ImGui.End();
                return;
            }

            PumpCatalog();
            if (!survey.Link.ObservationsLoaded)
                survey.Link.EnsureObservationsLoaded();

            Grey("Read only: nothing here lists, reprices or delists anything.".Loc());
            Tooltip("Prices come from the observation file: every item a full market scan looked up, and everything the other computer sent over the two-computer link. Nothing here sends a market query; to refresh a world, scan it again in the price survey window."
                .Loc());

            ImGui.SetNextItemWidth(FontPx(260));
            ImGui.InputTextWithHint("##mblookupquery", "Item name or id".Loc(), ref query, 100);
            ImGui.SameLine();
            DrawSearchStatus();

            UpdateResults();

            var listWidth = FontPx(260);
            ImGui.BeginChild("##mblookupresults", new Vector2(listWidth, 0), true);
            DrawResults();
            ImGui.EndChild();

            ImGui.SameLine();
            ImGui.BeginChild("##mblookupdetail", Vector2.Zero, false);
            DrawDetail();
            ImGui.EndChild();

            ImGui.End();
        }

        private void PumpCatalog()
        {
            if (catalog != null || catalogFailed)
                return;
            if (catalogTask == null)
            {
                var sheet = DataManager.GetExcelSheet<Item>();
                catalogTask = Task.Run(() => BuildCatalog(sheet));
                return;
            }

            if (!catalogTask.IsCompleted)
                return;
            if (catalogTask.IsCompletedSuccessfully)
            {
                catalog = catalogTask.Result;
                resultsFor = null;
            }
            else
            {
                catalogFailed = true;
                Log.Information(catalogTask.Exception, $"{LogTag} 道具清單建不起來，搜尋停用。");
            }

            catalogTask = null;
        }

        /// <summary>可上市場：有市場分類而且可交易（與全市場掃描同一條判準）。執行緒池上跑。</summary>
        private static List<LookupCatalogEntry> BuildCatalog(Lumina.Excel.ExcelSheet<Item>? sheet)
        {
            var list = new List<LookupCatalogEntry>();
            if (sheet == null)
                return list;
            foreach (var row in sheet)
            {
                if (row.ItemSearchCategory.RowId == 0 || row.IsUntradable)
                    continue;
                var name = row.Name.ExtractText();
                list.Add(new LookupCatalogEntry(row.RowId, name, MarketLookupSearch.Fold(name)));
            }

            return list;
        }

        private void DrawSearchStatus()
        {
            if (catalogFailed)
                Grey("The item list could not be built.".Loc());
            else if (catalog == null)
                Grey("Building the item list...".Loc());
            else if (resultsFor is { Length: > 0 })
                Grey(totalMatches > results.Count
                    ? DrawText.Loc("?? match(es), showing the first ??", (totalMatches, results.Count),
                        static (t, a) => t.Loc(a.totalMatches, a.Count))
                    : DrawText.Loc("?? match(es)", totalMatches));
        }

        /// <summary>只在搜尋字或道具清單變了才重算。</summary>
        private void UpdateResults()
        {
            if (catalog == null || string.Equals(resultsFor, query, StringComparison.Ordinal))
                return;
            resultsFor = query;
            results = MarketLookupSearch.Filter(catalog, query, MaxResults, out totalMatches);

            if (!autoSelectPending)
                return;
            autoSelectPending = false;
            if (results.Count == 0)
                return;
            var top = catalog[results[0]];
            var q = MarketLookupSearch.Fold(query);
            if (results.Count == 1 || top.Folded == q || top.ItemId.ToString() == q)
                Select(top);
        }

        private void DrawResults()
        {
            if (catalog == null)
                return;
            if (results.Count == 0)
            {
                ImGui.PushTextWrapPos(0);
                Grey(query.Trim().Length == 0
                    ? "Type part of an item's name, or its id.".Loc()
                    : "No marketable item matches.".Loc());
                ImGui.PopTextWrapPos();
                return;
            }

            // Begin 把這個區域變數的位址記進 ImGui；離開迴圈的每一條路都要 End。
            var clipper = default(ImGuiListClipper);
            clipper.Begin(results.Count);
            try
            {
                while (clipper.Step())
                {
                    for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                    {
                        var e = catalog[results[i]];
                        ImGui.PushID(i);
                        if (ImGui.Selectable(e.Name.Length > 0 ? e.Name : DrawText.Concat("#", (int)e.ItemId),
                                e.ItemId == selectedItem))
                            Select(e);
                        ImGui.PopID();
                    }
                }
            }
            finally
            {
                clipper.End();
            }
        }

        private void Select(in LookupCatalogEntry e)
        {
            if (selectedItem == e.ItemId)
                return;
            selectedItem = e.ItemId;
            selectedName = e.Name.Length > 0 ? e.Name : $"#{e.ItemId}";
            rows = [];
            rowsFor = 0;
            cheapestNq = cheapestHq = -1;
            nextRefreshAt = DateTime.MinValue;
            RequestFacts();
        }

        private void DrawDetail()
        {
            if (selectedItem == 0)
            {
                Grey("Pick an item on the left.".Loc());
                return;
            }

            ImGui.TextUnformatted(selectedName);
            ImGui.SameLine();
            Grey(DrawText.Concat("#", (int)selectedItem));

            var link = survey.Link;
            if (!link.ObservationsLoaded)
            {
                Grey("Loading the observation file...".Loc());
                return;
            }

            PumpLookup(link);

            if (rowsFor != selectedItem)
            {
                Grey("Looking up...".Loc());
                return;
            }

            if (!HasAnyData())
            {
                ImGui.PushTextWrapPos(0);
                Grey("No world has data for this item yet. A full market scan in the price survey window (or the other computer over the link) fills this in."
                    .Loc());
                ImGui.PopTextWrapPos();
            }
            else
            {
                DrawCheapestLine();
            }

            DrawTable();
            Grey(DrawText.Loc("? = not known   — = asked, nobody selling / no sales on that page   pale = older than ?? hours",
                MarketLookupModel.StaleHours));
        }

        private bool HasAnyData()
        {
            foreach (var row in rows)
            {
                if (row.HasData)
                    return true;
            }

            return false;
        }

        /// <summary>每 <see cref="RefreshInterval"/>、記錄檔讀完或併入一批時，在執行緒池上重查一次（觀測索引的鎖可能被寫檔執行緒佔著）。</summary>
        private void PumpLookup(SurveyLink link)
        {
            var now = DateTime.UtcNow;
            if (lookupTask != null)
            {
                if (!lookupTask.IsCompleted)
                    return;
                if (lookupTask.IsCompletedSuccessfully && lookupTaskFor == selectedItem)
                {
                    var f = facts;
                    rowsFacts = f;
                    rows = MarketLookupModel.BuildRows(f?.DataCentreWorlds ?? [], lookupTask.Result, now,
                        MarketLookupModel.StaleHours);
                    cheapestNq = MarketLookupModel.Cheapest(rows, hq: false);
                    cheapestHq = MarketLookupModel.Cheapest(rows, hq: true);
                    rowsFor = selectedItem;
                }
                else if (!lookupTask.IsCompletedSuccessfully)
                {
                    Log.Information(lookupTask.Exception, $"{LogTag} 查道具 {lookupTaskFor} 的觀測失敗。");
                }

                lookupTask = null;
            }

            var serial = link.DataSerial;
            if (rowsFor == selectedItem && now < nextRefreshAt && serial == seenSerial && seenLoaded
                && ReferenceEquals(rowsFacts, facts))
                return;

            seenSerial = serial;
            seenLoaded = true;
            nextRefreshAt = now + RefreshInterval;
            RequestFacts();
            var itemId = selectedItem;
            lookupTaskFor = itemId;
            lookupTask = Task.Run(() => link.LatestObservationsForItem(itemId));
        }

        private void DrawCheapestLine()
        {
            ImGui.TextUnformatted("Cheapest:".Loc());
            DrawCheapestPart("NQ".Loc(), cheapestNq, hq: false);
            DrawCheapestPart("HQ".Loc(), cheapestHq, hq: true);
        }

        private void DrawCheapestPart(string label, int index, bool hq)
        {
            ImGui.SameLine();
            if (index < 0 || index >= rows.Count)
            {
                Grey(DrawText.Concat(label, " ?"));
                Tooltip("No world with data has this quality on sale.".Loc());
                return;
            }

            var row = rows[index];
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.HealerGreen);
            ImGui.TextUnformatted(DrawText.Concat(DrawText.Concat(label, " "),
                DrawText.Concat(DrawText.Concat(MarketLookupModel.PriceText(row, hq), " @ "), row.WorldName)));
            ImGui.PopStyleColor();
            if (TooltipHovered())
                Tooltip(row.Stale
                    ? "Every world's data for this quality is older than ?? hours; this is the cheapest of the old ones (??)."
                        .Loc(MarketLookupModel.StaleHours, FormatAge(row.Obs.AtUtc))
                    : "Seen ??.".Loc(FormatAge(row.Obs.AtUtc)));
        }

        private void DrawTable()
        {
            const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollX |
                                          ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit;
            var height = Math.Max(ImGui.GetTextLineHeightWithSpacing() * 4,
                ImGui.GetContentRegionAvail().Y - ImGui.GetTextLineHeightWithSpacing());
            if (!ImGui.BeginTable("##mblookuptable", 7, flags, new Vector2(0, height)))
                return;

            ImGui.TableSetupScrollFreeze(1, 1);
            ImGui.TableSetupColumn("World".Loc(), ImGuiTableColumnFlags.WidthFixed, FontPx(110));
            ImGui.TableSetupColumn("NQ lowest".Loc(), ImGuiTableColumnFlags.WidthFixed, FontPx(130));
            ImGui.TableSetupColumn("HQ lowest".Loc(), ImGuiTableColumnFlags.WidthFixed, FontPx(130));
            ImGui.TableSetupColumn("Listings".Loc(), ImGuiTableColumnFlags.WidthFixed, FontPx(60));
            ImGui.TableSetupColumn("Latest sale".Loc(), ImGuiTableColumnFlags.WidthFixed, FontPx(150));
            ImGui.TableSetupColumn("Sold (7 days)".Loc(), ImGuiTableColumnFlags.WidthFixed, FontPx(80));
            ImGui.TableSetupColumn("Data from".Loc(), ImGuiTableColumnFlags.WidthFixed, FontPx(100));
            ImGui.TableHeadersRow();

            var own = facts?.Own;
            var home = facts?.HomeWorld ?? 0;
            var clipper = default(ImGuiListClipper);
            clipper.Begin(rows.Count);
            try
            {
                while (clipper.Step())
                {
                    for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                        DrawRow(i, own, home);
                }
            }
            finally
            {
                clipper.End();
            }

            ImGui.EndTable();
        }

        private void DrawRow(int index, IReadOnlySet<ulong>? own, uint homeWorld)
        {
            var row = rows[index];
            ImGui.TableNextRow();
            ImGui.PushID(index);
            var pale = row.Stale || !row.HasData;
            if (pale)
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.WorldId == homeWorld
                ? DrawText.Concat(row.WorldName, DrawText.Concat(" ", "(home)".Loc()))
                : row.WorldName);
            if (TooltipHovered())
                Tooltip(row.HasData
                    ? "Looked up ?? (local time); ??; ??.".Loc(
                        row.Obs.AtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), SourceText(row.Obs.Source),
                        row.Obs.Verdict == "empty" ? "nobody was selling".Loc() : "listings found".Loc())
                    : "No observation of this item on this world yet.".Loc());

            ImGui.TableNextColumn();
            DrawPriceCell(row, hq: false, index == cheapestNq, own);
            ImGui.TableNextColumn();
            DrawPriceCell(row, hq: true, index == cheapestHq, own);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(MarketLookupModel.CountText(row));

            ImGui.TableNextColumn();
            DrawLastSaleCell(row);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(MarketLookupModel.RecentSalesText(row));
            if (TooltipHovered())
                Tooltip(!row.HasData || !row.Obs.Sales.Known
                    ? "Sales were not recorded with this lookup (cached answer, or recorded by an older version)."
                        .Loc()
                    : MarketLookupModel.RecentSalesCapped(row.Obs.Sales)
                        ? "The game sends one page of the newest ?? sales; all of them were in the last 7 days, so there were at least this many."
                            .Loc(MarketLookupModel.GamePageSize)
                        : "Sales in the last 7 days on the page the game sent with this lookup.".Loc());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.HasData ? FormatAge(row.Obs.AtUtc) : MarketLookupModel.Unknown);
            if (row.Stale && TooltipHovered())
                Tooltip("Older than ?? hours: prices may have moved since.".Loc(MarketLookupModel.StaleHours));

            if (pale)
                ImGui.PopStyleColor();
            ImGui.PopID();
        }

        private static void DrawPriceCell(in LookupRow row, bool hq, bool cheapest, IReadOnlySet<ulong>? own)
        {
            var text = MarketLookupModel.PriceText(row, hq);
            var mark = cheapest;
            if (mark)
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.HealerGreen);
            ImGui.TextUnformatted(text);
            if (mark)
                ImGui.PopStyleColor();

            var owner = MarketLookupModel.OwnerOf(row, hq, own);
            if (owner == LookupOwner.Own)
            {
                ImGui.SameLine();
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
                ImGui.TextUnformatted("(yours)".Loc());
                ImGui.PopStyleColor();
            }

            if (!TooltipHovered())
                return;
            if (!row.HasData)
                Tooltip("No observation of this item on this world yet.".Loc());
            else if (owner == LookupOwner.NoListing)
                Tooltip("Nobody was selling this quality when it was looked up.".Loc());
            else if (owner == LookupOwner.Own)
                Tooltip("The cheapest listing is one of your retainers'.".Loc());
            else if (owner == LookupOwner.Unknown)
                Tooltip("Whose listing this is cannot be told (the seller was not recorded, or your retainers are not known yet).".Loc());
            else if (cheapest)
                Tooltip("Cheapest world for this quality.".Loc());
        }

        private static void DrawLastSaleCell(in LookupRow row)
        {
            if (!MarketLookupModel.TryLastSale(row, out var price, out var at, out var hq))
            {
                ImGui.TextUnformatted(MarketLookupModel.LastSaleText(row));
                if (TooltipHovered())
                    Tooltip(!row.HasData || !row.Obs.Sales.Known
                        ? "Sales were not recorded with this lookup (cached answer, or recorded by an older version)."
                            .Loc()
                        : "No sale of either quality on the page the game sent.".Loc());
                return;
            }

            ImGui.TextUnformatted(DrawText.Concat(DrawText.Concat(DrawText.N0(price), hq ? DrawText.Concat(" ", "HQ".Loc()) : string.Empty),
                DrawText.Concat(" · ", FormatAge(at))));
            if (TooltipHovered())
                Tooltip("Sold at ?? (local time).".Loc(at.ToLocalTime().ToString("yyyy-MM-dd HH:mm")));
        }

        private static string SourceText(string source) => source switch
        {
            "live" => "asked the server".Loc(),
            "cache" => "answer reused from the market data cache".Loc(),
            _ => source,
        };

        private static string FormatAge(DateTime atUtc)
        {
            if (atUtc == DateTime.MinValue)
                return MarketLookupModel.Unknown;
            var age = DateTime.UtcNow - atUtc;
            if (age.TotalMinutes < 1)
                return "just now".Loc();
            if (age.TotalHours < 1)
                return DrawText.Loc("?? minute(s) ago", (int)age.TotalMinutes);
            if (age.TotalDays < 1)
                return DrawText.Loc("?? hour(s) ago", (int)age.TotalHours);
            return DrawText.Loc("?? day(s) ago", (int)age.TotalDays);
        }

        private static bool TooltipHovered() => ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);

        private static void Tooltip(string text)
        {
            if (!ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                return;
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30f);
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }

        private static void Grey(string text)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextUnformatted(text);
            ImGui.PopStyleColor();
        }
    }
}
