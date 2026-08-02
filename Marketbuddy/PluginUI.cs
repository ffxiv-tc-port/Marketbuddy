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

        /// <summary>
        /// 兩塊側邊面板之間的垂直間距。單純是視覺留白——**不是**排版邏輯的一部分：
        /// 即時掛單面板的位置是從重掛面板**實際量到的下緣**算出來的，不是猜的高度，
        /// 所以重掛面板長高長矮（堆疊列開關、佇列列出現、跑起來變成進度列）都不會重疊。
        /// </summary>
        private const float SidePanelGap = 4f;

        public void Draw()
        {
            DrawSettingsWindow();
            // 順序即排版：重掛面板先畫，畫完把它的下緣交給即時掛單面板當作起點。
            // 兩者在同一個 ImGui frame 內先後執行，所以這個值永遠是「這一幀的」，不會慢一拍。
            var repriceBottom = DrawRepriceWindow();
            DrawRetainerListOverlay();
            marketbuddy.LiveSellList.Draw(repriceBottom);
        }

        /// <summary>
        /// 僱員選單旁的「巡迴」面板。2026-08-03 由**一顆浮在僱員選單標題列上的裸按鈕**
        /// （無背景、無標題、ItemSpacing 被壓成 1 px）改成獨立面板，形式比照出售品視窗旁
        /// 那一欄。理由：那顆按鈕會用到的堆疊上限／降價設定全部藏在另一個視窗裡，
        /// 使用者在僱員選單前看不到自己按下去會發生什麼事，跑起來也沒有任何進度。
        ///
        /// 設定列是 <see cref="DrawSharedPricingRows"/>——與出售品視窗那一欄**同一份**
        /// config 欄位，不是第二套設定。
        /// </summary>
        private void DrawRetainerListOverlay()
        {
            if (!conf.BatchRepriceEnabled ||
                !marketbuddy.MarketGuiEventHandler.AddonRetainerList_Frame(out var topLeft, out var size))
                return;

            var tour = marketbuddy.MultiReprice;

            ImGui.SetNextWindowPos(new Vector2(
                topLeft.X + size.X + conf.RetainerPanelOffset.X,
                topLeft.Y + conf.RetainerPanelOffset.Y));

            if (!ImGui.Begin("Marketbuddy_retainerlist",
                    ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                    ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize |
                    ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoSavedSettings))
            {
                ImGui.End();
                return;
            }

            ImGui.TextUnformatted("All retainers".Loc());
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Show or hide these controls in /mbuddy".Loc());

            ImGui.Separator();
            DrawSharedPricingRows();

            ImGui.Separator();
            if (tour.IsRunning)
                DrawTourProgress(tour);
            else
                DrawTourStartButton(tour);

            ImGui.End();
        }

        /// <summary>
        /// 巡迴進行中的進度。兩層都要顯示，因為它們回答不同的問題：
        /// 僱員層（第幾個／共幾個／誰）來自巡迴本身，道具層（第幾件／共幾件／哪一件）
        /// 來自巡迴此刻正在驅動的那個引擎。在僱員之間移動時引擎沒在跑，只會有僱員那一行。
        /// </summary>
        private void DrawTourProgress(MultiRetainerReprice tour)
        {
            ImGui.TextUnformatted("Retainer ??/??: ??".Loc(
                tour.CurrentRetainerNumber, tour.TotalRetainers, tour.CurrentRetainerName));

            var engine = marketbuddy.BatchReprice;
            if (engine.IsRunning)
            {
                var currentIndex = Math.Min(engine.ProcessedSlots + 1, engine.TotalSlots);
                ImGui.TextUnformatted(
                    "Repricing ??/??: ??".Loc(currentIndex, engine.TotalSlots, engine.CurrentItemName));
            }

            if (ImGui.Button("Cancel".Loc() + "##mbtourcancel"))
                tour.CancelByButton();
        }

        private void DrawTourStartButton(MultiRetainerReprice tour)
        {
            var canStart = tour.CanStart(out var reason);
            // When AutoRetainer is the only blocker, keep the button clickable
            // so pressing it explains the situation instead of doing nothing.
            var disabled = !canStart && !AutoRetainerBridge.IsBusy;
            if (disabled)
                ImGui.BeginDisabled();
            // 降價設成 0 時「（最低價 -0gil）」是純噪音，卻佔掉按鈕一半寬度：
            // 括號整個收掉，定價規則改用滑鼠提示交代（提示不佔版面）。
            var tourLabel = UndercutIsZero
                ? "Relist all retainers".Loc()
                : "Relist all retainers (lowest -??)".Loc(GetUndercutText());
            if (ImGui.Button(tourLabel + "##mbtourstart"))
                tour.Start();
            if (disabled)
            {
                ImGui.EndDisabled();
                if (!string.IsNullOrEmpty(reason) && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(reason);
            }
            else if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(PricingRuleText());
            }
        }

        /// <summary>
        /// 重掛面板。2026-08-03 由「釘在出售品視窗標題列上的一條浮動列」改成**獨立視窗**，
        /// 形式比照 <see cref="LiveSellList"/>（使用者確認那個面板的手感是對的）。
        ///
        /// 舊版把勾選框、兩個數字輸入框、單位選單與按鈕全部 <c>SameLine()</c> 擠在一行，
        /// 而且 <c>ItemSpacing</c> 被壓成 1 px、視窗無背景，所以「哪個輸入框對應哪個標籤」
        /// 完全看不出來。這裡改成一列一件事、每個輸入框緊跟著自己的單位標籤。
        ///
        /// 2026-08-03 第二次調整：位置由「原生視窗左下角下方」改成**右上角**，
        /// 也就是即時掛單面板原本的位置，即時掛單改接在它下面（同一欄、重掛在上）。
        /// 兩者共用 <see cref="Configuration.LiveSellListOffset"/> 當作整欄的位移，
        /// 所以拖滑桿是兩塊一起動；重掛面板高度變化時，下面那塊是照**量到的下緣**
        /// 重新定位的，不會重疊。
        ///
        /// ⚠️ **行為零變更**：控制項、它們讀寫的設定欄位、以及各自的顯示條件
        /// （堆疊列吃 <c>AdjustMaxStackSizeInSellList</c>、按鈕吃 <c>BatchRepriceEnabled</c>、
        /// 佇列列吃待處理數）全部原封不動，只換了容器與排版。
        /// </summary>
        /// <returns>面板下緣的螢幕 Y 座標；面板這一幀沒有畫出來時回傳 null。</returns>
        private float? DrawRepriceWindow()
        {
            var showStack = conf.AdjustMaxStackSizeInSellList;
            var showBatch = conf.BatchRepriceEnabled;
            var quickListPending = marketbuddy.QuickLister?.PendingCount ?? 0;
            if ((!showStack && !showBatch && quickListPending == 0) ||
                !marketbuddy.MarketGuiEventHandler.AddonRetainerSellList_Frame(out var topLeft, out var size))
                return null;

            // 貼在原生視窗的**右上角**——這一欄的最上面。即時掛單面板接在下面。
            ImGui.SetNextWindowPos(new Vector2(
                topLeft.X + size.X + conf.LiveSellListOffset.X,
                topLeft.Y + conf.LiveSellListOffset.Y));

            if (!ImGui.Begin("Marketbuddy_reprice",
                    ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                    ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize |
                    ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoSavedSettings))
            {
                ImGui.End();
                return null;
            }

            ImGui.TextUnformatted("Relisting".Loc());
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Show or hide these controls in /mbuddy".Loc());

            if (quickListPending > 0)
            {
                ImGui.Separator();
                ImGui.TextUnformatted("Quick-list queue: ?? pending".Loc(quickListPending));
            }

            if (showStack)
            {
                ImGui.Separator();
                DrawSharedPricingRows();
            }

            if (showBatch)
            {
                ImGui.Separator();
                DrawBatchRepriceRow();
            }

            var bottom = ImGui.GetWindowPos().Y + ImGui.GetWindowSize().Y;
            ImGui.End();
            return bottom;
        }

        /// <summary>
        /// 堆疊上限 + 降價設定。**刻意抽成共用**：出售品視窗旁的重掛面板與僱員選單旁的
        /// 巡迴面板讀寫的是同一組設定欄位，抽出來就不可能長成互相打架的兩套設定。
        /// 一列一件事，輸入框的單位標籤（「個」）由 InputInt 自己畫在右邊，
        /// 所以數字跟它的單位永遠黏在一起。
        /// </summary>
        private void DrawSharedPricingRows()
        {
            if (ImGui.Checkbox("Limit stack size to".Loc() + " ", ref conf.UseMaxStackSize))
                conf.Save();
            ImGui.SameLine();
            ImGui.SetNextItemWidth(70);
            if (ImGui.InputInt("items".Loc(), ref conf.MaximumStackSize, 0))
                MaximumStackSizeChanged();

            // 降價獨立成一列：標籤在前，接著金額，再接著單位選單（gil / %）。
            ImGui.TextUnformatted("undercut".Loc());
            ImGui.SameLine();
            ImGui.SetNextItemWidth(90);
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
            ImGui.SetNextItemWidth(70);
            DrawUndercutTypeSelector();
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
            // ⚠️ 舊字面是「全部重掛」，但這顆按鈕的範圍其實只有**這一個僱員**
            // （全僱員巡迴是僱員選單上的另一顆）。字面改成明確的範圍。
            var label = UndercutIsZero
                ? "Relist this retainer".Loc()
                : "Relist this retainer (lowest -??)".Loc(GetUndercutText());
            if (ImGui.Button(label + "##mbbatchstart"))
                engine.Start();
            if (disabled)
            {
                ImGui.EndDisabled();
                if (!string.IsNullOrEmpty(reason) && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(reason);
            }
            else if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(PricingRuleText());
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
            ImGui.TextUnformatted("Reuse market data seen in the last".Loc());
            ImGui.SameLine();
            // 步進 60（一分鐘）／快速步進 300（五分鐘）：跑一輪多角色時常用的值是
            // 1800（30 分），用預設的無步進版本只能手動輸入，按不出來。
            ImGui.SetNextItemWidth(160);
            if (ImGui.InputInt("seconds (0 = always ask the server)".Loc() + "##mbcachettl",
                    ref conf.MarketDataCacheSeconds, 60, 300))
            {
                conf.MarketDataCacheSeconds = Math.Clamp(conf.MarketDataCacheSeconds, 0, 3600);
                conf.Save();
            }

            DrawNestIndicator(2);
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextWrapped(
                "Market board answers are delivered to every plugin at once, so anything you (or another plugin) looked up recently is already here and can be reused without asking the server again - that is the single biggest speed-up available without touching the game. Nothing is ever triggered by receiving data; it is only remembered. Listings belong to a world, not to a character, so the cache now survives character switches and is only dropped when you change world - raise this if you work through several characters in one sitting. It never stores \"nobody is selling this\" unless this plugin confirmed it itself."
                    .Loc());
            ImGui.PopStyleColor();

            DrawNestIndicator(1);
            if (ImGui.Button("Clear price cache (?? items)".Loc(MarketDataCache.FreshCount(conf.MarketDataCacheSeconds)) +
                             "##mbclearcache"))
                MarketDataCache.Clear();

            ImGui.Spacing();
            if (ImGui.Checkbox("Show a live sell list next to the game's one".Loc(), ref conf.LiveSellListOverlay))
                conf.Save();

            DrawNestIndicator(1);
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextWrapped(
                "Batch repricing writes prices straight into the retainer's market container without opening any game window - which is exactly why it is fast, but it also means the game's sell list never redraws and keeps showing the prices it had when you opened it (a just-listed item stays at 999,999,999 on screen even though the server already has the right price). This panel is drawn by the plugin and re-read every frame, so it is always current. It only reads: no game windows are touched and nothing is clicked for you."
                    .Loc());
            ImGui.PopStyleColor();

            // 重掛面板與即時掛單面板現在是**同一欄**（重掛在上、即時掛單接在下面），
            // 所以位置只留一個滑桿，拖它就是整欄一起動。
            // 範圍是可負值：舊版下限寫死 1，往左／往上微調不了。
            if (conf.LiveSellListOverlay || conf.AdjustMaxStackSizeInSellList || conf.BatchRepriceEnabled)
            {
                ImGui.Spacing();
                ImGui.DragFloat2("Side panel position (relative to the sell list's top right)".Loc(),
                    ref conf.LiveSellListOffset, 1f, -4000f, 4000f, "%.0f");
                if (ImGui.IsItemDeactivatedAfterEdit())
                    conf.Save();

                DrawNestIndicator(1);
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
                ImGui.TextWrapped(
                    "Moves both side panels at once: the relisting controls sit at the top right of the game's sell list and the live listing table is stacked directly underneath them."
                        .Loc());
                ImGui.PopStyleColor();
            }

            if (conf.BatchRepriceEnabled)
            {
                ImGui.Spacing();
                ImGui.DragFloat2("All-retainers panel position (relative to the retainer list's top right)".Loc(),
                    ref conf.RetainerPanelOffset, 1f, -4000f, 4000f, "%.0f");
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

        /// <summary>
        /// 使用者沒有設定降價（0 gil 或 0%）。此時「（最低價 -0gil）」不帶任何資訊，
        /// 只會把按鈕撐寬，所以標題把那段括號整個省掉。
        /// </summary>
        private bool UndercutIsZero =>
            conf.UndercutUsePercent ? conf.UndercutPercent == 0 : conf.UndercutPrice == 0;

        /// <summary>按鈕的滑鼠提示：把從標題省掉的定價規則交代清楚，而且完全不佔版面。</summary>
        private string PricingRuleText() => UndercutIsZero
            ? "Prices at the lowest listing (no undercut)".Loc()
            : "Prices at the lowest listing minus ??".Loc(GetUndercutText());

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