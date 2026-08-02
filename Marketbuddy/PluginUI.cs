using System;
using System.Numerics;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Bindings.ImGui;

namespace Marketbuddy
{
    // It is good to have this be disposable in general, in case you ever need it
    // to do any cleanup
    internal class PluginUI : IDisposable
    {
        private Configuration conf => Configuration.GetOrLoad();

        private Marketbuddy marketbuddy;

        private bool _settingsVisible;

        // passing in the image here just for simplicity
        public PluginUI(Marketbuddy plugin)
        {
            marketbuddy = plugin;
            SettingsVisible = false;
        }

        public bool SettingsVisible
        {
            get => _settingsVisible;
            set => _settingsVisible = value;
        }

        public void Dispose()
        {
        }

        public void Draw()
        {
            DrawSettingsWindow();
            DrawOverlayWindow();
            DrawRetainerListOverlay();
            marketbuddy.LiveSellList.Draw();
        }

        private void DrawRetainerListOverlay()
        {
            if (!conf.BatchRepriceEnabled ||
                !marketbuddy.MarketGuiEventHandler.AddonRetainerList_Position(out Vector2 position)) return;

            var tour = marketbuddy.MultiReprice;
            var windowVisible = true;
            ImGui.SetNextWindowPos(position);

            var hSpace = new Vector2(1, 0);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, hSpace);
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, hSpace);
            ImGui.PushStyleVar(ImGuiStyleVar.ItemInnerSpacing, hSpace);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
            if (ImGui.Begin("Marketbuddy_retainerlist", ref windowVisible,
                    ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollWithMouse |
                    ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoBackground))
            {
                if (tour.IsRunning)
                {
                    ImGui.TextUnformatted("Retainer ??/??: ??".Loc(
                        tour.CurrentRetainerNumber, tour.TotalRetainers, tour.CurrentRetainerName));
                    ImGui.SameLine();
                    if (ImGui.Button("Cancel".Loc() + "##mbtourcancel"))
                        tour.CancelByButton();
                }
                else
                {
                    var canStart = tour.CanStart(out var reason);
                    var disabled = !canStart && !AutoRetainerBridge.IsBusy;
                    if (disabled)
                        ImGui.BeginDisabled();
                    if (ImGui.Button("Relist all retainers (lowest -??)".Loc(GetUndercutText()) + "##mbtourstart"))
                        tour.Start();
                    if (disabled)
                    {
                        ImGui.EndDisabled();
                        if (!string.IsNullOrEmpty(reason) && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                            ImGui.SetTooltip(reason);
                    }
                }
            }

            ImGui.PopStyleVar(5);
            ImGui.End();
        }

        private void DrawOverlayWindow()
        {
            var showStack = conf.AdjustMaxStackSizeInSellList;
            var showBatch = conf.BatchRepriceEnabled;
            var quickListPending = marketbuddy.QuickLister?.PendingCount ?? 0;
            if ((!showStack && !showBatch && quickListPending == 0) ||
                !marketbuddy.MarketGuiEventHandler.AddonRetainerSellList_Position(out Vector2 position)) return;

            var windowVisible = true;
            ImGui.SetNextWindowPos(position);

            var hSpace = new Vector2(1, 0);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, hSpace);
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, hSpace);
            ImGui.PushStyleVar(ImGuiStyleVar.ItemInnerSpacing, hSpace);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
            if (ImGui.Begin("Marketbuddy_stacklimit", ref windowVisible,
                    ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollWithMouse |
                    ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoBackground))
            {
                if (quickListPending > 0)
                    ImGui.TextUnformatted("Quick-list queue: ?? pending".Loc(quickListPending));

                if (showStack)
                {
                    if (ImGui.Checkbox("Limit stack size to".Loc() + " ", ref conf.UseMaxStackSize))
                        conf.Save();

                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(30);
                    if (ImGui.InputInt("items".Loc(), ref conf.MaximumStackSize, 0))
                        MaximumStackSizeChanged();

                    ImGui.SameLine();
                    ImGui.Dummy(new(20, 1));

                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(30);
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
                    ImGui.SetNextItemWidth(40);
                    DrawUndercutTypeSelector();
                    ImGui.SameLine();
                    ImGui.Text("undercut".Loc());
                }

                if (showBatch)
                    DrawBatchRepriceRow();
            }

            ImGui.PopStyleVar(5);
            ImGui.End();
        }

        private void DrawBatchRepriceRow()
        {
            var engine = marketbuddy.BatchReprice;
            if (engine.IsRunning)
            {
                var currentIndex = Math.Min(engine.ProcessedSlots + 1, engine.TotalSlots);
                ImGui.TextUnformatted(
                    "Repricing ??/??: ??".Loc(currentIndex, engine.TotalSlots, engine.CurrentItemName));
                ImGui.SameLine();
                if (ImGui.Button("Cancel".Loc() + "##mbbatchcancel"))
                    engine.CancelByButton();
                return;
            }

            // While the all-retainers tour is driving, the tour owns the engine;
            // don't offer a second start button in the sell list.
            if (marketbuddy.MultiReprice.IsRunning)
                return;

            var canStart = engine.CanStart(out var reason);
            // When AutoRetainer is the only blocker, keep the button clickable
            // so pressing it explains the situation instead of doing nothing.
            var disabled = !canStart && !AutoRetainerBridge.IsBusy;
            if (disabled)
                ImGui.BeginDisabled();
            if (ImGui.Button("Relist all (lowest -??)".Loc(GetUndercutText()) + "##mbbatchstart"))
                engine.Start();
            if (disabled)
            {
                ImGui.EndDisabled();
                if (!string.IsNullOrEmpty(reason) && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(reason);
            }
        }

        public void DrawSettingsWindow()
        {
            if (!SettingsVisible) return;

            if (!ImGui.Begin("Marketbuddy config".Loc() + "###MarketbuddyConfig", ref _settingsVisible,
                    ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize))
            {
                ImGui.End();
                return;
            }

            if(IPCManager.Locks.Count > 0)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudRed);
                ImGui.TextWrapped(
                    "Lock commands has been received from these plugins and Marketbuddy operation is fully halted:"
                        .Loc());
                ImGui.TextUnformatted($"{string.Join("\n", IPCManager.Locks)}");
                if(ImGui.Button("Release locks".Loc()))
                {
                    IPCManager.Locks.Clear();
                }
                ImGui.PopStyleColor();
            }

            if (ImGui.Checkbox("Auto-retry a market search that looks throttled/stuck".Loc(), ref conf.AutoRequeryOnThrottle))
                conf.Save();

            DrawNestIndicator(1);
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextWrapped(
                "Applies to any market price list window - the market board search and the retainer sell \"compare prices\" popup alike. No packets, hooks or memory edits: if the server keeps rejecting the query, this simply gives up after a few tries and the window is left as-is."
                    .Loc());
            ImGui.PopStyleColor();


            ImGui.Spacing();
            if (ImGui.Checkbox("Open current prices list when adjusting a price".Loc(), ref conf.AutoOpenComparePrices))
                conf.Save();

            DrawNestIndicator(1);
            if (ImGui.Checkbox(
                    "Holding SHIFT ??".Loc(conf.AutoOpenComparePrices
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
                    "Holding ALT ??".Loc(conf.AutoOpenHistory ? "prevents the above".Loc() : "does the above".Loc()),
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

            DrawNestIndicator(1);
            if (ImGui.Checkbox(
                    "Clicking a price copies that price with a ?? undercut to the clipboard".Loc(GetUndercutText()),
                    ref conf.SaveToClipboard))
                conf.Save();

            DrawNestIndicator(1);
            if (ImGui.Checkbox(
                    "Clicking a price sets your price as that price with a ?? undercut".Loc(GetUndercutText()),
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

            ImGui.Spacing();
            if (ImGui.Checkbox("Limit stack size to".Loc(), ref conf.UseMaxStackSize))
                conf.Save();

            ImGui.SameLine();
            ImGui.SetNextItemWidth(45);
            if (ImGui.InputInt("items".Loc(), ref conf.MaximumStackSize, 0))
                MaximumStackSizeChanged();

            DrawNestIndicator(1);
            if (ImGui.Checkbox("Adjust maximum stack size in retainer sell list UI".Loc(),
                    ref conf.AdjustMaxStackSizeInSellList))
                conf.Save();

            if (conf.AdjustMaxStackSizeInSellList)
            {
                DrawNestIndicator(2);
                ImGui.DragFloat2("Position (relative to top left)".Loc(), ref conf.AdjustMaxStackSizeInSellListOffset,
                        1f, 1, float.MaxValue, "%.0f");
                if (ImGui.IsItemDeactivatedAfterEdit())
                    conf.Save();
            }

            ImGui.Spacing();
            if (ImGui.Checkbox("Show a one-click relist button in the retainer sell list".Loc(),
                    ref conf.BatchRepriceEnabled))
                conf.Save();

            DrawNestIndicator(1);
            if (ImGui.Checkbox("HQ items only undercut other HQ listings".Loc(), ref conf.BatchCompareHqOnly))
                conf.Save();

            DrawNestIndicator(1);
            if (ImGui.Checkbox("Delist items whose market net (after tax) is below the NPC vendor price".Loc(),
                    ref conf.BatchDelistBelowVendor))
                conf.Save();

            DrawNestIndicator(2);
            ImGui.SetNextItemWidth(45);
            if (ImGui.InputInt("% market tax (fallback when live rates are unknown)".Loc(),
                    ref conf.MarketTaxPercent, 0))
            {
                conf.MarketTaxPercent = Math.Clamp(conf.MarketTaxPercent, 0, 25);
                conf.Save();
            }

            DrawNestIndicator(1);
            ImGui.TextUnformatted("Delist items whose target price is below".Loc());
            ImGui.SameLine();
            ImGui.SetNextItemWidth(90);
            if (ImGui.InputInt("gil (0 = off)".Loc() + "##mbbatchminprice", ref conf.BatchMinPrice, 0))
            {
                if (conf.BatchMinPrice < 0)
                    conf.BatchMinPrice = 0;
                conf.Save();
            }

            DrawNestIndicator(1);
            ImGui.TextUnformatted("Delist destination".Loc());
            ImGui.SameLine();
            ImGui.SetNextItemWidth(160);
            if (ImGui.BeginCombo("##mbdelistdest",
                    conf.DelistToRetainerInventory ? "Retainer inventory".Loc() : "Player inventory".Loc()))
            {
                if (ImGui.Selectable("Player inventory".Loc(), !conf.DelistToRetainerInventory))
                {
                    conf.DelistToRetainerInventory = false;
                    conf.Save();
                }

                if (ImGui.Selectable("Retainer inventory".Loc(), conf.DelistToRetainerInventory))
                {
                    conf.DelistToRetainerInventory = true;
                    conf.Save();
                }

                ImGui.EndCombo();
            }

            DrawNestIndicator(1);
            if (ImGui.Button("Clear price cache (?? items)".Loc(marketbuddy.BatchReprice.PriceCacheCount) +
                             "##mbclearcache"))
                marketbuddy.BatchReprice.ClearPriceCache();

            ImGui.Spacing();
            if (ImGui.Checkbox("Show a live sell list next to the game's one".Loc(), ref conf.LiveSellListOverlay))
                conf.Save();

            DrawNestIndicator(1);
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextWrapped(
                "Batch repricing writes prices straight into the retainer's market container without opening any game window - which is exactly why it is fast, but it also means the game's sell list never redraws and keeps showing the prices it had when you opened it (a just-listed item stays at 999,999,999 on screen even though the server already has the right price). This panel is drawn by the plugin and re-read every frame, so it is always current. It only reads: no game windows are touched and nothing is clicked for you."
                    .Loc());
            ImGui.PopStyleColor();

            if (conf.LiveSellListOverlay)
            {
                DrawNestIndicator(2);
                ImGui.DragFloat2("Position (relative to the sell list's top right)".Loc(),
                    ref conf.LiveSellListOffset, 1f, -4000f, 4000f, "%.0f");
                if (ImGui.IsItemDeactivatedAfterEdit())
                    conf.Save();
            }

            ImGui.Spacing();
            ImGui.TextUnformatted("Quick listing: hold this key and right-click an item to put it up for sale".Loc());
            DrawNestIndicator(1);
            ImGui.SetNextItemWidth(100);
            if (ImGui.BeginCombo("##mbquicklistkey", QuickListKeyLabel(conf.QuickListKeyCode)))
            {
                foreach (var code in QuickLister.SelectableKeyCodes)
                {
                    if (ImGui.Selectable(QuickListKeyLabel(code), conf.QuickListKeyCode == code))
                    {
                        conf.QuickListKeyCode = code;
                        conf.Save();
                    }
                }

                ImGui.EndCombo();
            }

            DrawNestIndicator(1);
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextWrapped(
                "Pick a key that is not used elsewhere: CTRL already pastes the clipboard price when the sale window opens, and avoid AutoRetainer's own quick-sale key if you have one bound."
                    .Loc());
            ImGui.PopStyleColor();

            ImGui.End();
        }

        private static string QuickListKeyLabel(int keyCode)
        {
            return keyCode switch
            {
                0 => "None".Loc(),
                0x10 => "SHIFT",
                0x11 => "CTRL",
                0x12 => "ALT",
                _ => $"0x{keyCode:X}",
            };
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

        private string GetUndercutText(bool escape = false)
        {
            if (conf.UndercutUsePercent)
            {
                return $"{conf.UndercutPercent}%" + (escape?"%":"");
            }
            else
            {
                return $"{conf.UndercutPrice}gil";
            }
        }

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