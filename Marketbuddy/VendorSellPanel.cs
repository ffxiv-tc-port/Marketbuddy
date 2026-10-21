using System;
using System.Collections.Generic;
using System.Globalization;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Marketbuddy.Common;

namespace Marketbuddy
{
    /// <summary>
    /// 出售品視窗旁重掛面板裡的「賣給商店」一段。🔴 按鈕是兩段式：第一下只武裝，倒數內再按一次才賣
    /// （賣掉之後只能在僱員的回購清單裡買回少數幾件，誤按的代價與下架相當）。
    /// 常看的放列上（件數、金額、需要手動的件數），原因與位置放滑鼠提示。
    /// </summary>
    internal sealed class VendorSellPanel
    {
        private static readonly TimeSpan ArmWindow = TimeSpan.FromSeconds(6);

        private readonly VendorSeller seller;
        private DateTime armedUntil = DateTime.MinValue;

        private TextSlot<(int Current, int Total, string Name)> progressText;
        private TextSlot<(int Stacks, string Gil)> soldSoFarText;
        private TextSlot<(int Stacks, string Gil, int Left)> confirmLabel;
        private TextSlot<(int Stacks, string Gil)> armLabel;
        private readonly List<VendorSellVerdict> manualRows = new();

        // 清單裡每一件的文字：verdict 建好就不再變，所以整份 plan 換掉（或 CultureInfo 換了）才重組。
        private readonly Dictionary<VendorSellVerdict, (string Id, string Line)> rowTexts = new();
        private IReadOnlyList<VendorSellVerdict>? rowTextsPlan;
        private CultureInfo? rowTextsCulture;

        private readonly record struct SortRow(VendorSellKey Key, string Id, string Line, string Tooltip);

        private readonly List<SortRow> sortRows = new();
        private int sortRowsVersion = -1;
        private CultureInfo? sortRowsCulture;

        public VendorSellPanel(VendorSeller seller)
        {
            this.seller = seller;
        }

        public void Draw()
        {
            ImGui.Separator();
            ImGui.TextUnformatted("Sell to the shop".Loc());
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Items the relist just delisted into your bags because they were below the NPC price or below your minimum. Pressing the button has a retainer sell them to the shop through the game's own right-click \"Have Retainer Sell Items\", one stack at a time. Only stacks that turned up in a slot of their own this round, holding exactly the delisted amount, are sold - anything that merged into a stack you already had is left alone and listed under \"by hand\"."
                        .Loc());

            if (seller.IsRunning)
            {
                armedUntil = DateTime.MinValue;
                DrawProgress();
                return;
            }

            DrawStartButton();
            DrawDetails();
        }

        private void DrawProgress()
        {
            var current = Math.Min(seller.FinishedJobs + 1, seller.TotalJobs);
            ImGui.TextUnformatted(progressText.Get((current, seller.TotalJobs, seller.CurrentItemName),
                static k => "Selling ??/??: ??".Loc(k.Current, k.Total, k.Name)));
            ImGui.SameLine();
            if (ImGui.Button(DrawText.Concat("Cancel".Loc(), "##mbvendorsellcancel")))
                seller.CancelByButton();

            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextUnformatted(soldSoFarText.Get((seller.SoldStacks, DrawText.N0(seller.SoldGil)),
                static k => "?? sold so far (about ?? gil)".Loc(k.Stacks, k.Gil)));
            if (seller.WaitingFor.Length > 0)
                ImGui.TextUnformatted(seller.WaitingFor);
            ImGui.PopStyleColor();
        }

        private void DrawStartButton()
        {
            var (stacks, gil) = seller.SellableSummary();
            var waiting = seller.CountState(VendorSellState.Waiting);
            var manual = seller.CountState(VendorSellState.Manual);

            if (!seller.IsAvailable)
            {
                armedUntil = DateTime.MinValue;
                Grey("Not available: the right-click menu hook did not load".Loc());
            }
            else if (stacks == 0)
            {
                armedUntil = DateTime.MinValue;
                Grey(waiting > 0
                    ? DrawText.Loc("Waiting for ?? item(s) to come back to your bags", waiting)
                    : "Nothing ticked can be sold automatically".Loc());
            }
            else
            {
                DrawArmedButton(stacks, gil);
                if (waiting > 0)
                    Grey(DrawText.Loc("?? more still on the way back to your bags", waiting));
            }

            if (seller.SortPicks.Count > 0)
                Grey(DrawText.Loc("?? stack(s) from the bag sort - untick them under \"What gets sold\"",
                    seller.SortPicks.Count));

            if (manual > 0)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
                ImGui.TextUnformatted(DrawText.Loc("?? item(s) have to be sold by hand", manual));
                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Open the list below to see why each one was left alone.".Loc());
            }
        }

        private void DrawArmedButton(int stacks, long gil)
        {
            var armed = armedUntil > DateTime.UtcNow;
            if (!armed && armedUntil != DateTime.MinValue)
                armedUntil = DateTime.MinValue;

            var canStart = seller.CanStart(out var reason);
            // AutoRetainer 是唯一的阻擋時照樣讓人按，按下去會說明原因（與重掛按鈕相同）。
            var disabled = !canStart && !AutoRetainerBridge.IsBusy;
            if (disabled)
            {
                armedUntil = DateTime.MinValue;
                armed = false;
                ImGui.BeginDisabled();
            }

            var gilText = DrawText.N0(gil);
            if (armed)
            {
                var left = (int)Math.Ceiling((armedUntil - DateTime.UtcNow).TotalSeconds);
                ImGui.PushStyleColor(ImGuiCol.Button, ImGuiColors.DalamudRed);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ImGuiColors.DalamudRed);
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, ImGuiColors.DalamudRed);
                if (ImGui.Button(confirmLabel.Get((stacks, gilText, left), static k =>
                        "Confirm: sell to the shop (?? stack(s), about ?? gil) (??)".Loc(k.Stacks, k.Gil, k.Left) +
                        "##mbvendorsellstart")))
                {
                    armedUntil = DateTime.MinValue;
                    seller.Start();
                }

                ImGui.PopStyleColor(3);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(
                        "Only the ticked stacks in the list below are sold, each one checked again right before it goes. Click again to go ahead, or wait for this to time out."
                            .Loc());
            }
            else
            {
                if (ImGui.Button(armLabel.Get((stacks, gilText), static k =>
                        "Sell to the shop (?? stack(s), about ?? gil)".Loc(k.Stacks, k.Gil) + "##mbvendorsellarm")))
                    armedUntil = DateTime.UtcNow + ArmWindow;
                if (!disabled && ImGui.IsItemHovered())
                    ImGui.SetTooltip(
                        "Has the retainer sell the ticked stacks to the shop at the NPC price. The gil is an estimate from the item data. Asks for confirmation first; ESC or Cancel stops it at any time."
                            .Loc());
            }

            if (disabled)
            {
                ImGui.EndDisabled();
                if (!string.IsNullOrEmpty(reason) && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(reason);
            }
        }

        private void DrawDetails()
        {
            var plan = seller.Plan;
            if (!ImGui.TreeNode(DrawText.Concat("What gets sold".Loc(), "##mbvendorselllist")))
                return;

            var culture = CultureInfo.CurrentCulture;
            if (!ReferenceEquals(plan, rowTextsPlan) || !ReferenceEquals(culture, rowTextsCulture))
            {
                rowTexts.Clear();
                rowTextsPlan = plan;
                rowTextsCulture = culture;
            }

            var manual = manualRows;
            manual.Clear();
            for (var i = 0; i < plan.Count; i++)
            {
                var v = plan[i];
                switch (v.State)
                {
                    case VendorSellState.Sellable:
                        DrawSellableRow(v);
                        break;
                    case VendorSellState.Waiting:
                        Grey(RowText(v).Line);
                        break;
                    case VendorSellState.Manual:
                        manual.Add(v);
                        break;
                }
            }

            DrawSortPicks();

            if (manual.Count > 0)
            {
                ImGui.Spacing();
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
                ImGui.TextUnformatted(DrawText.Loc("By hand (??)", manual.Count));
                ImGui.PopStyleColor();
                foreach (var v in manual)
                {
                    ImGui.TextUnformatted(RowText(v).Line);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(ManualReasonLong(v));
                }

                manual.Clear();
            }

            ImGui.Spacing();
            if (ImGui.SmallButton(DrawText.Concat("Forget this list".Loc(), "##mbvendorsellclear")))
                seller.ClearRound();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip((seller.RoundStartedUtc is { } started
                        ? "This list started at ??.".Loc(started.ToLocalTime().ToString("HH:mm")) + "\n"
                        : string.Empty) +
                    "Forgets the delists recorded so far; nothing in your bags is touched. A new list also starts by itself when you delist again after 30 minutes without delisting or selling."
                        .Loc());

            ImGui.TreePop();
        }

        /// <summary>背包分揀交過來的格子，一種道具一列（同一種的格子一起勾、一起取消）。</summary>
        private void DrawSortPicks()
        {
            var picks = seller.SortPicks;
            if (picks.Count == 0)
                return;

            if (sortRowsVersion != seller.SortVersion || !ReferenceEquals(sortRowsCulture, CultureInfo.CurrentCulture))
            {
                sortRowsVersion = seller.SortVersion;
                sortRowsCulture = CultureInfo.CurrentCulture;
                RebuildSortRows(picks);
            }

            ImGui.Spacing();
            ImGui.TextUnformatted(DrawText.Loc("From the bag sort (??)", sortRows.Count));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Stacks you ticked in the bag sort (/mbuddy, Clear-out tab). These are stacks you already had, so each one is only sold if that slot still holds exactly the same item, quality and amount as when you handed it over; anything else is skipped."
                        .Loc());
            foreach (var row in sortRows)
            {
                var include = !seller.IsSortExcluded(row.Key);
                if (ImGui.Checkbox(row.Id, ref include))
                    seller.SetSortExcluded(row.Key, !include);
                ImGui.SameLine();
                ImGui.TextUnformatted(row.Line);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(row.Tooltip);
            }

            if (ImGui.SmallButton(DrawText.Concat("Forget the bag sort picks".Loc(), "##mbvendorsellsortclear")))
                seller.ClearSortPicks();
        }

        private void RebuildSortRows(IReadOnlyList<VendorSeller.SortPick> picks)
        {
            sortRows.Clear();
            var index = new Dictionary<VendorSellKey, int>();
            var qty = new List<int>();
            var gil = new List<long>();
            var where = new List<List<string>>();
            var names = new List<string>();
            foreach (var p in picks)
            {
                if (!index.TryGetValue(p.Slot.Key, out var i))
                {
                    i = qty.Count;
                    index[p.Slot.Key] = i;
                    qty.Add(0);
                    gil.Add(0);
                    where.Add(new List<string>());
                    names.Add(p.Name);
                }

                qty[i] += p.Slot.Quantity;
                gil[i] += (long)p.Slot.Quantity * p.VendorUnitPrice;
                where[i].Add(VendorSeller.DescribeSlot(p.Slot) + " x" + p.Slot.Quantity);
            }

            foreach (var (key, i) in index)
            {
                sortRows.Add(new SortRow(key,
                    $"##mbvendorsellsort{key.ItemId}{(key.Hq ? "h" : "n")}",
                    "?? x??  about ?? gil".Loc(names[i], qty[i], gil[i].ToString("N0")),
                    "Sells: ??".Loc(string.Join(", ", where[i]))));
            }
        }

        private void DrawSellableRow(VendorSellVerdict v)
        {
            var include = !seller.IsExcluded(v.Key);
            var text = RowText(v);
            if (ImGui.Checkbox(text.Id, ref include))
                seller.SetExcluded(v.Key, !include);
            ImGui.SameLine();
            ImGui.TextUnformatted(text.Line);
            if (!ImGui.IsItemHovered())
                return;

            var where = new List<string>(v.NewSlots.Count);
            foreach (var slot in v.NewSlots)
                where.Add(VendorSeller.DescribeSlot(slot) + " x" + slot.Quantity);
            ImGui.SetTooltip("Sells: ??\nNPC price: ?? gil each".Loc(string.Join(", ", where),
                v.VendorUnitPrice.ToString("N0")));
        }

        private (string Id, string Line) RowText(VendorSellVerdict v)
        {
            if (!rowTexts.TryGetValue(v, out var text))
                rowTexts[v] = text = MakeRowText(v);
            return text;
        }

        private static (string Id, string Line) MakeRowText(VendorSellVerdict v) => v.State switch
        {
            VendorSellState.Sellable => ($"##mbvendorsell{v.Key.ItemId}{(v.Key.Hq ? "h" : "n")}",
                "?? x??  ??  about ?? gil".Loc(v.Name, v.Arrived, VendorSeller.CauseText(v.Causes),
                    v.EstimatedGil.ToString("N0"))),
            VendorSellState.Waiting => (string.Empty, "?? ??/?? - still on its way back".Loc(v.Name, v.Arrived, v.Remaining)),
            _ => (string.Empty, "?? x??: ??".Loc(v.Name, v.Remaining, VendorSeller.ManualReasonShort(v.Reason, v.State))),
        };

        private static string ManualReasonLong(VendorSellVerdict v) => v.Reason switch
        {
            VendorSellManualReason.MergedIntoExistingStack =>
                "It went into a stack you already had (?? before, ?? now), so selling that slot would sell yours too. Split it or sell it by hand."
                    .Loc(v.ExistingBefore, v.ExistingNow),
            VendorSellManualReason.ExistingStackChanged =>
                "You already had some of this before the delist, and that stack has moved or changed since, so there is no telling which slot holds the delisted ones."
                    .Loc(),
            VendorSellManualReason.UnknownContainer =>
                "It turned up in a bag that could not be read before the delist, so it cannot be told apart from what you already had."
                    .Loc(),
            VendorSellManualReason.QuantityMismatch =>
                "The new slot(s) hold ?? but ?? were delisted and not sold yet - something else went in or came out."
                    .Loc(v.Arrived, v.Remaining),
            VendorSellManualReason.NotArrived =>
                "The delist was accepted but nothing new showed up in your bags or armoury chest.".Loc(),
            VendorSellManualReason.NoVendorPrice =>
                "NPC shops do not buy this item, so it cannot be sold this way.".Loc(),
            VendorSellManualReason.NeedsConfirmation =>
                "It has materia attached, is fully spiritbonded, or is a unique item, so the game asks yes/no when you sell it. Those boxes are never answered for you."
                    .Loc(),
            VendorSellManualReason.InArmory =>
                "It went into the armoury chest; only your four bags are sold automatically.".Loc(),
            _ => "?",
        };

        private static void Grey(string text)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextUnformatted(text);
            ImGui.PopStyleColor();
        }
    }
}
