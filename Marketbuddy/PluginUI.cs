using System;
using System.Collections.Generic;
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

        /// <summary>
        /// 多角色重掛「武裝」失敗的原因。
        /// 🔑 顯示在按鈕旁邊而不是丟到聊天視窗：使用者是在這個視窗裡按的按鈕，
        /// 答案要出現在他正在看的地方。
        /// </summary>
        private string multiCharArmError = string.Empty;

        /// <summary>跨世界價格巡檢的獨立視窗。</summary>
        private readonly PriceSurveyWindow surveyWindow;

        // passing in the image here just for simplicity
        public PluginUI(Marketbuddy plugin)
        {
            marketbuddy = plugin;
            surveyWindow = new PriceSurveyWindow(plugin.PriceSurvey, plugin.MarketGuiEventHandler);
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
        /// 🔑 2026-09-08 起是暫停而不是結束——進度留著，重開視窗按「繼續掃描」就從原地接下去；
        /// 關著的期間一件都不會問，而且<b>不會</b>自己恢復。
        /// 🔴 2026-09-11 起關視窗<b>同時解除自動續跑的武裝</b>：暫停對「正在換世界、
        /// 但沒有在掃描」的狀態是 no-op，少了那一步整條鏈會在看不見的地方繼續跑。
        /// </summary>
        private void DrawSurveyWindow()
        {
            var wasVisible = _surveyVisible;
            surveyWindow.Draw(ref _surveyVisible);
            if (wasVisible && !_surveyVisible)
                surveyWindow.OnClosed();
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

            var tour = marketbuddy.MultiTour;

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
            {
                DrawTourProgress(tour);
            }
            else
            {
                DrawTourStartButton(tour);
                DrawDelistTourButton(tour);
            }

            DrawMultiCharacterStatusLine();

            ImGui.End();
        }

        /// <summary>
        /// 僱員選單旁那一欄的多角色狀態列。只在**已武裝**時出現。
        /// 🔑 這一輪跑到哪裡是「隨時掃視」的資訊，所以畫在列上；
        /// 「還有誰沒跑」是「起疑才查」的，放滑鼠提示。
        /// 🔴 停止鈕跟狀態列放在一起：正在被自動操作的畫面上，
        /// 「怎麼叫它停」必須看得見，不能只藏在 /mbuddy 裡。
        /// </summary>
        private void DrawMultiCharacterStatusLine()
        {
            if (!conf.MultiCharTourEnabled)
                return;
            var multi = marketbuddy.MultiCharTour;
            if (!multi.IsArmed)
                return;

            ImGui.Separator();
            ImGui.TextUnformatted("Multi-character round: ??/?? character(s)"
                .Loc(multi.RoundCompleted, multi.RoundTargets));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(MultiCharacterRemainingText(multi));

            if (ImGui.Button("Stop multi-character relisting".Loc() + "##mbmcstoppanel"))
                multi.Stop("stopped by user".Loc());
        }

        private static string MultiCharacterRemainingText(MultiCharacterTour multi)
        {
            var names = string.Join(", ", multi.RemainingNames());
            return names.Length == 0
                ? "Nothing left to visit this round.".Loc()
                : "Still to visit: ??".Loc(names);
        }

        /// <summary>
        /// 巡迴進行中的進度。兩層都要顯示，因為它們回答不同的問題：
        /// 僱員層（第幾個／共幾個／誰）來自巡迴本身，道具層（第幾件／共幾件／哪一件）
        /// 來自巡迴此刻正在驅動的那個引擎。在僱員之間移動時引擎沒在跑，只會有僱員那一行。
        /// </summary>
        private void DrawTourProgress(MultiRetainerTour tour)
        {
            ImGui.TextUnformatted("Retainer ??/??: ??".Loc(
                tour.CurrentRetainerNumber, tour.TotalRetainers, tour.CurrentRetainerName));

            // 巡迴此刻驅動的引擎——重掛與下架是兩個不同的物件，不能寫死其中一個。
            var engine = tour.ActiveEngine;
            if (engine.IsRunning)
            {
                var currentIndex = Math.Min(engine.ProcessedSlots + 1, engine.TotalSlots);
                ImGui.TextUnformatted(tour.Mode == TourMode.Delist
                    ? "Delisting ??/??: ??".Loc(currentIndex, engine.TotalSlots, engine.CurrentItemName)
                    : "Repricing ??/??: ??".Loc(currentIndex, engine.TotalSlots, engine.CurrentItemName));
            }

            if (ImGui.Button("Cancel".Loc() + "##mbtourcancel"))
                tour.CancelByButton();
        }

        private void DrawTourStartButton(MultiRetainerTour tour)
        {
            // 巡迴驅動的是同一個重掛引擎，所以定價方式也是同一個 —— 告示要一起出現，
            // 否則使用者會以為新的定價方式只作用在出售品視窗那顆按鈕上。
            DrawLastSoldNotice();

            var canStart = tour.CanStart(TourMode.Reprice, out var reason);
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
                tour.Start(TourMode.Reprice);
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

        /// <summary>單價門檻有沒有開著（0 = 停用 = 全部下架，也就是一直以來的行為）。</summary>
        private bool DelistPriceFilterActive => conf.DelistAboveUnitPrice > 0;

        /// <summary>使用者的「跳過這些僱員」名單有沒有東西。</summary>
        private bool DelistSkipListActive => conf.DelistTourSkipRetainers.Count > 0;

        /// <summary>
        /// 下架按鈕上方那一行「這一次不會全部下架」的告示。
        ///
        /// 🔑 這是**列上**的資訊不是滑鼠提示：使用者正要按一顆紅色的「全部下架」，
        /// 如果實際上會留下東西，那件事必須在他按下去之前就看得見，不能藏在 tooltip 裡
        /// （tooltip 藏的是「為什麼」，不是「有沒有」）。
        /// 兩個門檻都沒開時整行不畫——按鈕自己已經寫著會全部下架，再加一行「沒有篩選」
        /// 只是噪音。
        /// </summary>
        /// <param name="includeSkipList">
        /// 僱員名單只對巡迴有意義；出售品視窗那顆「本僱員全下架」傳 false。
        /// </param>
        private void DrawDelistFilterSummary(bool includeSkipList)
        {
            var priceOn = DelistPriceFilterActive;
            var skipOn = includeSkipList && DelistSkipListActive;
            if (!priceOn && !skipOn)
                return;

            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudYellow);
            if (priceOn)
            {
                ImGui.TextUnformatted("Only unit prices above ?? gil".Loc(conf.DelistAboveUnitPrice.ToString("N0")));
                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(DelistPriceFilterTooltip());
                if (skipOn)
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudYellow);
            }

            if (skipOn)
            {
                ImGui.TextUnformatted("Skipping ?? retainer(s)".Loc(conf.DelistTourSkipRetainers.Count));
                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(SkippedRetainerNames());
            }
        }

        /// <summary>
        /// 單價門檻的說明。⚠️ 一定要講明白比的是**單價**：一堆 5 件、每件 1,000 gil
        /// 的掛單算 1,000 不是 5,000，照總價去設門檻會整批留錯東西。
        /// </summary>
        private string DelistPriceFilterTooltip() =>
            "Listings priced at or below this stay on the market. The threshold is the price per item, not the price of the whole stack: a stack of 5 at 1,000 gil each counts as 1,000, not 5,000. Set it back to 0 in /mbuddy to delist everything again."
                .Loc();

        /// <summary>
        /// 跳過名單的名字。⚠️ 名單存的是僱員 id，而僱員屬於角色——別的角色的僱員在這裡
        /// **查不到名字**，那種情況要照實說「查不到」，不是把它從清單裡藏掉。
        /// </summary>
        private string SkippedRetainerNames()
        {
            var known = MultiRetainerTour.EnumerateRetainers();
            var lines = new List<string>();
            var unknown = 0;
            foreach (var id in conf.DelistTourSkipRetainers)
            {
                var name = string.Empty;
                foreach (var r in known)
                {
                    if (r.RetainerId != id)
                        continue;
                    name = r.Name;
                    break;
                }

                if (name.Length == 0)
                    unknown++;
                else
                    lines.Add(name);
            }

            if (unknown > 0)
                lines.Add("?? not on this character".Loc(unknown));
            return "These retainers are left alone by the delist tour:".Loc() + "\n" + string.Join("\n", lines);
        }

        /// <summary>下架巡迴的「已武裝」到期時間；<see cref="DateTime.MinValue"/> = 沒武裝。</summary>
        private DateTime delistArmedUntil = DateTime.MinValue;

        /// <summary>武裝之後多久自動解除。夠久可以看清楚字、夠短不會一直掛著。</summary>
        private static readonly TimeSpan DelistArmWindow = TimeSpan.FromSeconds(6);

        /// <summary>
        /// 全僱員下架。目的地同樣聽設定「下架收回至」，預設玩家背包。
        /// **刻意做成兩段式**：第一下只是「武裝」，按鈕會變成紅色的
        /// 確認鈕並開始倒數，要在倒數內再按一次才真的開始；倒數結束自動解除。
        ///
        /// 🔴 為什麼一定要二次確認：這個動作把一整輪上架的成果全部收回來，
        /// 誤按的代價遠高於誤按重掛。而且它就在重掛按鈕正下方，單擊誤觸的機率不低。
        /// 兩段式的好處是「一次滑鼠失誤絕對不夠」，又不必額外教使用者按住某個修飾鍵。
        ///
        /// 視覺上也刻意跟重掛拉開：分隔線 + 紅字標題 + 紅色按鈕。
        /// </summary>
        private void DrawDelistTourButton(MultiRetainerTour tour)
        {
            ImGui.Separator();

            var armed = delistArmedUntil > DateTime.UtcNow;
            if (!armed && delistArmedUntil != DateTime.MinValue)
                delistArmedUntil = DateTime.MinValue;

            var canStart = tour.CanStart(TourMode.Delist, out var reason);
            var disabled = !canStart && !AutoRetainerBridge.IsBusy;
            if (disabled)
            {
                delistArmedUntil = DateTime.MinValue;
                armed = false;
                ImGui.BeginDisabled();
            }

            // 🔴 篩選狀態畫在按鈕**上面**，不是藏在提示裡：按下去會發生什麼事，
            // 必須在按下去之前就看得見。
            var filtered = DelistPriceFilterActive || DelistSkipListActive;
            DrawDelistFilterSummary(true);

            if (armed)
            {
                var left = (int)Math.Ceiling((delistArmedUntil - DateTime.UtcNow).TotalSeconds);
                ImGui.PushStyleColor(ImGuiCol.Button, ImGuiColors.DalamudRed);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ImGuiColors.DalamudRed);
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, ImGuiColors.DalamudRed);
                // 🔴 有篩選時**不能**再寫「EVERYTHING」——那會變成一句謊話，
                // 而且是印在最後一道確認上的謊話。沒有篩選時字句與以前逐字相同。
                if (ImGui.Button((filtered
                        ? "Confirm: take the filtered listings off the market (??)"
                        : "Confirm: take EVERYTHING off the market (??)").Loc(left) + "##mbdeliststart"))
                {
                    delistArmedUntil = DateTime.MinValue;
                    tour.Start(TourMode.Delist);
                }

                ImGui.PopStyleColor(3);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip((conf.DelistToRetainerInventory
                        ? "Every listing of every retainer goes back into that retainer's own inventory. Click again to go ahead, or wait for this to time out."
                        : "Every listing of every retainer goes back into your own bags. Click again to go ahead, or wait for this to time out.").Loc());
            }
            else
            {
                if (ImGui.Button("Delist all retainers".Loc() + "##mbdelistarm"))
                    delistArmedUntil = DateTime.UtcNow + DelistArmWindow;
                if (!disabled && ImGui.IsItemHovered())
                    ImGui.SetTooltip((conf.DelistToRetainerInventory
                        ? "Takes every listing of every retainer off the market and back into that retainer's own inventory (\"Delist destination\" in the settings). Stops when a retainer's inventory fills up. Asks for confirmation first."
                        : "Takes every listing of every retainer off the market and back into your own bags, so identical items from different retainers stack together. Stops when your bags fill up - just clear space and press it again. Asks for confirmation first.").Loc());
            }

            if (disabled)
            {
                ImGui.EndDisabled();
                if (!string.IsNullOrEmpty(reason) && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(reason);
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
                DrawSingleRetainerDelistRow();
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
            if (marketbuddy.MultiTour.IsRunning)
                return;

            DrawLastSoldNotice();

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

        /// <summary>
        /// 重掛按鈕上方那一行「這一次不是照最低價定價」的告示。
        ///
        /// 🔑 這是**列上**的資訊不是滑鼠提示：按下去會用完全不同的一套價格，那件事必須在
        /// 按下去之前就看得見（形式比照下架那邊的 <see cref="DrawDelistFilterSummary"/>）。
        /// 每一件實際會掛多少則在即時掛單面板的「重掛後」欄——那是逐件的金額，
        /// 藏在 tooltip 裡會讓人看不到自己按下去會賣多少錢。
        /// </summary>
        private void DrawLastSoldNotice()
        {
            if (!conf.RelistUseLastSoldPrice)
                return;

            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudYellow);
            ImGui.TextWrapped("Pricing from the most recent sale, rounded down to 100 gil".Loc());
            ImGui.PopStyleColor();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(PricingRuleText());

            if (conf.LiveSellListOverlay)
                return;

            // 面板關著就看不到逐件的金額——那正是「價格要看得見才送出」被架空的情況，
            // 所以在這裡講出來，並提供一個一鍵打開的按鈕（不替使用者偷偷改設定）。
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextWrapped("Turn on the live listings panel to see what each item would be relisted at.".Loc());
            ImGui.PopStyleColor();
            if (ImGui.SmallButton("Show it".Loc() + "##mbshowlivelist"))
            {
                conf.LiveSellListOverlay = true;
                conf.Save();
            }
        }

        /// <summary>本僱員全下架的「已武裝」到期時間；<see cref="DateTime.MinValue"/> = 沒武裝。</summary>
        private DateTime sellListDelistArmedUntil = DateTime.MinValue;

        /// <summary>
        /// 「本僱員全下架」——把這一名僱員的掛單全部下架收回。
        /// 目的地聽設定「下架收回至」（<see cref="Configuration.DelistToRetainerInventory"/>），
        /// 預設是玩家背包（跨僱員合併堆疊用）；提示文字會跟著目的地換句話說。
        ///
        /// 🔑 這顆按鈕**沒有自己的執行邏輯**：它直接驅動 <see cref="BatchDelist"/>，
        /// 也就是全僱員巡迴在每一名僱員身上跑的那同一個引擎。所以二次確認、等那一格
        /// 真的空掉才算數、背包滿了乾淨停手並照實回報——全部自動一致，不會分岔成兩套。
        /// 全僱員版本就是「巡迴 + 對每個僱員跑這一個引擎」。
        ///
        /// 🔴 二次確認**沒有因為只有一名僱員就放寬**：它一樣是把一整批上架成果收回來，
        /// 而且就在重掛按鈕正下方，手滑的代價一樣高。形式比照僱員選單那顆。
        /// </summary>
        private void DrawSingleRetainerDelistRow()
        {
            var engine = marketbuddy.BatchDelist;
            if (engine.IsRunning)
            {
                ImGui.Separator();
                var currentIndex = Math.Min(engine.ProcessedSlots + 1, engine.TotalSlots);
                ImGui.TextUnformatted(
                    "Delisting ??/??: ??".Loc(currentIndex, engine.TotalSlots, engine.CurrentItemName));
                ImGui.SameLine();
                if (ImGui.Button("Cancel".Loc() + "##mbselldelistcancel"))
                    engine.CancelByButton();
                return;
            }

            // 巡迴在跑的時候引擎歸巡迴所有，不要在這裡再開第二個入口。
            // （重掛那一列也是同樣的處理。）
            if (marketbuddy.MultiTour.IsRunning || marketbuddy.BatchReprice.IsRunning)
                return;

            ImGui.Separator();

            var armed = sellListDelistArmedUntil > DateTime.UtcNow;
            if (!armed && sellListDelistArmedUntil != DateTime.MinValue)
                sellListDelistArmedUntil = DateTime.MinValue;

            var canStart = engine.CanStart(out var reason);
            var disabled = !canStart && !AutoRetainerBridge.IsBusy;
            if (disabled)
            {
                sellListDelistArmedUntil = DateTime.MinValue;
                armed = false;
                ImGui.BeginDisabled();
            }

            // 單價門檻作用在引擎上，所以這顆按鈕也照它走——那件事要在這裡講，
            // 不能讓使用者按完才發現只下架了一半。
            // ⚠️ 僱員跳過名單**不**作用在這顆按鈕（它是針對眼前這一名僱員的明確指令），
            // 所以這裡傳 false，不會顯示名單。
            DrawDelistFilterSummary(false);

            if (armed)
            {
                var left = (int)Math.Ceiling((sellListDelistArmedUntil - DateTime.UtcNow).TotalSeconds);
                ImGui.PushStyleColor(ImGuiCol.Button, ImGuiColors.DalamudRed);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ImGuiColors.DalamudRed);
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, ImGuiColors.DalamudRed);
                if (ImGui.Button((DelistPriceFilterActive
                        ? "Confirm: take the filtered listings off the market (??)"
                        : "Confirm: take this retainer's listings off the market (??)").Loc(left) + "##mbselldeliststart"))
                {
                    sellListDelistArmedUntil = DateTime.MinValue;
                    engine.Start();
                }

                ImGui.PopStyleColor(3);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip((conf.DelistToRetainerInventory
                        ? "Every listing of this retainer goes back into the retainer's own inventory. Click again to go ahead, or wait for this to time out."
                        : "Every listing of this retainer goes back into your own bags. Click again to go ahead, or wait for this to time out.").Loc());
            }
            else
            {
                if (ImGui.Button("Delist this retainer".Loc() + "##mbselldelistarm"))
                    sellListDelistArmedUntil = DateTime.UtcNow + DelistArmWindow;
                if (!disabled && ImGui.IsItemHovered())
                    ImGui.SetTooltip((conf.DelistToRetainerInventory
                        ? "Takes every listing of this retainer off the market and back into the retainer's own inventory (\"Delist destination\" in the settings). Asks for confirmation first."
                        : "Takes every listing of this retainer off the market and back into your own bags, so identical items from different retainers stack together. Asks for confirmation first.").Loc());
            }

            if (disabled)
            {
                ImGui.EndDisabled();
                if (!string.IsNullOrEmpty(reason) && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(reason);
            }
        }

        /// <summary>
        /// 兩個「不要全部下架」的篩選：單價門檻與僱員跳過名單。
        ///
        /// 🔴 刻意畫在「下架收回至」正下方、同樣的最外層縮排：三項一起構成「按下下架
        /// 按鈕會發生什麼事」，拆散在不同段落會讓人以為它們管的範圍不一樣。
        ///
        /// 兩項預設都是關的（門檻 0、名單空），關著的時候整個下架流程與加它們之前
        /// 逐字相同。
        /// </summary>
        private void DrawDelistFilterSettings()
        {
            ImGui.Spacing();
            ImGui.TextUnformatted("Only delist listings priced above".Loc());
            ImGui.SameLine();
            ImGui.SetNextItemWidth(110);
            if (ImGui.InputInt("gil per item".Loc() + "##mbdelistaboveprice", ref conf.DelistAboveUnitPrice, 0))
            {
                conf.DelistAboveUnitPrice =
                    Math.Clamp(conf.DelistAboveUnitPrice, 0, Configuration.MAX_PRICE);
                conf.Save();
            }

            // 🔴 「現在是停用的」這件事本身要在**列上**看得見，不能只靠「值是 0」讓人自己推。
            // 停用是預設狀態，而預設狀態下按鈕會把東西全部收回來——那是要當場講清楚的事。
            DrawNestIndicator(1);
            if (DelistPriceFilterActive)
            {
                ImGui.TextUnformatted("Listings at ?? gil per item or below stay on the market"
                    .Loc(conf.DelistAboveUnitPrice.ToString("N0")));
            }
            else
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
                ImGui.TextUnformatted("Off - every listing gets delisted".Loc());
                ImGui.PopStyleColor();
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(DelistPriceFilterTooltip());

            DrawNestIndicator(1);
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextWrapped(
                "The threshold is the price per item, the same number the sell list shows in its unit price column - not the price of the whole stack. It applies to both manual delist buttons (\"Delist this retainer\" and \"Delist all retainers\"). It deliberately does NOT touch the two automatic delists above, which exist to take cheap listings off the board: those two do the opposite job, so filtering them by \"only the expensive ones\" would just cancel them out. If a price cannot be read for some reason, that listing is left alone."
                    .Loc());
            ImGui.PopStyleColor();

            // 🔴 僱員清單是這個設定視窗裡唯一**會長高**的區塊（最多九名僱員），而視窗是
            // AlwaysAutoResize + NoScrollbar：長過螢幕就沒有捲軸可以救。所以收進摺疊標題裡。
            // ⚠️ 但「有幾名被跳過」必須留在**摺起來也看得見**的那一行上——把狀態藏進
            // 要展開才看得到的地方，等於使用者永遠不知道自己設過這個東西。
            ImGui.Spacing();
            var skipCount = conf.DelistTourSkipRetainers.Count;
            if (!ImGui.CollapsingHeader((skipCount == 0
                    ? "Retainers the delist tour leaves alone: none".Loc()
                    : "Retainers the delist tour leaves alone: ?? skipped".Loc(skipCount)) + "##mbskipretainers"))
                return;

            DrawNestIndicator(1);
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextWrapped(
                "Ticked retainers are never visited by \"Delist all retainers\" - keep the ones you want selling high-value goods here. This only affects the delist tour: relisting still visits them, otherwise their prices would slowly go stale, and \"Delist this retainer\" still works when you are standing at that retainer, because that is an explicit instruction about that one retainer."
                    .Loc());
            ImGui.PopStyleColor();

            var retainers = MultiRetainerTour.EnumerateRetainers();
            if (retainers.Count == 0)
            {
                // ⚠️ 「列不出來」跟「名單是空的」是兩件事，必須分開講：
                // 名單其實有東西卻畫成一片空白，會讓使用者以為設定掉了。
                DrawNestIndicator(1);
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
                ImGui.TextUnformatted(conf.DelistTourSkipRetainers.Count == 0
                    ? "Open the retainer list once and your retainers appear here".Loc()
                    : "?? retainer(s) on the list - open the retainer list to see which"
                        .Loc(conf.DelistTourSkipRetainers.Count));
                ImGui.PopStyleColor();
                return;
            }

            foreach (var retainer in retainers)
            {
                DrawNestIndicator(1);
                var skip = conf.DelistTourSkipRetainers.Contains(retainer.RetainerId);
                if (ImGui.Checkbox($"{retainer.Name}##mbskipret{retainer.RetainerId}", ref skip))
                {
                    if (skip)
                    {
                        if (!conf.DelistTourSkipRetainers.Contains(retainer.RetainerId))
                            conf.DelistTourSkipRetainers.Add(retainer.RetainerId);
                    }
                    else
                    {
                        conf.DelistTourSkipRetainers.Remove(retainer.RetainerId);
                    }

                    conf.Save();
                }

                ImGui.SameLine();
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
                ImGui.TextUnformatted("(?? listed)".Loc(retainer.Listed));
                ImGui.PopStyleColor();
            }

            // ⚠️ 名單裡屬於**別的角色**的 id 在這裡查不到名字。把它們默默不畫，
            // 使用者就會以為名單只有上面那幾筆；「有幾筆看不到」必須寫在列上。
            var unknown = 0;
            foreach (var id in conf.DelistTourSkipRetainers)
            {
                var found = false;
                foreach (var retainer in retainers)
                {
                    if (retainer.RetainerId != id)
                        continue;
                    found = true;
                    break;
                }

                if (!found)
                    unknown++;
            }

            if (unknown > 0)
            {
                DrawNestIndicator(1);
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
                ImGui.TextUnformatted("?? more on the list are not this character's retainers".Loc(unknown));
                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(
                        "The list stores retainer IDs rather than names, so it survives renames and works per character. Another character's retainer names cannot be looked up from here, but they are still skipped when you play that character."
                            .Loc());
            }
        }

        /// <summary>
        /// 設定視窗。2026-09-06 由「一整條垂直清單 ＋ 分隔線」改成**分頁**：原本三百多行
        /// 設定串在同一個 AlwaysAutoResize ＋ NoScrollbar 的視窗裡，長過螢幕就沒有捲軸
        /// 可以救，找一項設定也只能從頭掃到尾。
        ///
        /// ⚠️ **純版面重組，行為零變更**：控制項、它們讀寫的設定欄位、預設值、範圍、
        /// 顯示條件與存檔時機全部原封不動，只是換了容器。
        ///
        /// 🔴 IPC 鎖的告示刻意留在**分頁列之外的最上面**：那是「整個外掛已經停擺」的狀態，
        /// 藏進任何一個分頁都等於要使用者先猜對分頁才知道自己被鎖住了。
        /// </summary>
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

            DrawIpcLockNotice();

            // 🔑 視窗是 AlwaysAutoResize，而每個分頁的內容寬度天生不一樣——沒有一個共同的
            // 寬度下限，每切一次分頁整個視窗就跳一次大小。這只是視覺上的錨，不碰任何設定。
            ImGui.Dummy(new Vector2(SettingsContentWidth, 0f));

            if (ImGui.BeginTabBar("##mbsettingstabs"))
            {
                if (ImGui.BeginTabItem("Prices".Loc() + "##mbtabprices"))
                {
                    DrawPriceSettingsTab();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Relisting".Loc() + "##mbtabrelist"))
                {
                    DrawRelistSettingsTab();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Delisting".Loc() + "##mbtabdelist"))
                {
                    DrawDelistSettingsTab();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Panels & diagnostics".Loc() + "##mbtabpanels"))
                {
                    DrawPanelSettingsTab();
                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }

            ImGui.End();
        }

        /// <summary>
        /// 分頁內容的共同寬度**下限**。視窗是 AlwaysAutoResize，沒有下限的話每切一次分頁
        /// 視窗就跳一次大小。內容比它寬時視窗照樣會自己長大，所以這是下限不是上限。
        /// </summary>
        private const float SettingsContentWidth = 620f;

        /// <summary>
        /// 別的外掛透過 IPC 送來的停止鎖。
        /// 🔴 畫在分頁列**上面**：這是「整個外掛已經停擺」的狀態，不能藏進某一個分頁。
        /// </summary>
        private void DrawIpcLockNotice()
        {
            // 🔴 先拍快照再判空：直接讀 Count 之後又列舉集合，中間可能被 IPC 執行緒插入。
            var locks = IPCManager.SnapshotLocks();
            if (locks.Length == 0)
                return;

            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudRed);
            ImGui.TextWrapped(
                "Lock commands has been received from these plugins and Marketbuddy operation is fully halted:"
                    .Loc());
            ImGui.TextUnformatted($"{string.Join("\n", locks)}");
            if (ImGui.Button("Release locks".Loc()))
            {
                IPCManager.ClearLocks();
            }

            ImGui.PopStyleColor();
        }

        /// <summary>
        /// 「比價」分頁：市場比價視窗那一套互動——查詢卡住時自動重試、自動開窗、
        /// 修飾鍵、點一個價格要做什麼，以及那些動作共用的降價幅度。
        /// </summary>
        private void DrawPriceSettingsTab()
        {
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
        }

        /// <summary>
        /// 「重掛」分頁：上架與重掛——堆疊上限、快速上架按鍵、一鍵重掛按鈕與它的定價
        /// 守衛（HQ 比價、NPC 收購價、最低價）、比價資料快取，最後是完成通知與多角色。
        ///
        /// 🔴 多角色重掛刻意留在**這個分頁的最後面**、位置與順序都不往上搬：
        /// 它是唯一一條由 AutoRetainer 事件接手的鏈，把開關搬到顯眼的地方
        /// 只會讓它更容易被誤開。
        /// </summary>
        private void DrawRelistSettingsTab()
        {
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

            ImGui.Spacing();
            if (ImGui.Checkbox("Show a one-click relist button in the retainer sell list".Loc(),
                    ref conf.BatchRepriceEnabled))
                conf.Save();

            DrawNestIndicator(1);
            if (ImGui.Checkbox("Price from the most recent sale instead, rounded down to 100 gil".Loc(),
                    ref conf.RelistUseLastSoldPrice))
                conf.Save();

            DrawNestIndicator(2);
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextWrapped(
                "Takes the price the item most recently actually sold for anywhere on your data centre and rounds it down to the nearest 100 gil. The figure comes from the Universalis web API - the same source PriceInsight uses - so the game's own market board is never queried and no listing is looked up in game. Items with no sale on record keep their usual pricing (lowest listing minus your undercut); nothing is guessed. What each item would be relisted at is shown in the live listings panel before you press the button."
                    .Loc());
            ImGui.PopStyleColor();

            DrawNestIndicator(2);
            if (!conf.RelistUseLastSoldPrice) PushStyleDisabled();
            if (ImGui.Checkbox("Ignore quality: use the newer of that item's HQ and NQ sales".Loc(),
                    ref conf.RelistLastSoldIgnoreQuality))
                conf.Save();
            if (!conf.RelistUseLastSoldPrice) PopStyleDisabled();

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

            // 純通知，掛在重掛那一組底下：只有重掛收尾會叫它，下架不會。
            DrawNestIndicator(1);
            if (ImGui.Checkbox("Ask TataruPraise to say a line when relisting finishes".Loc(),
                    ref conf.TataruPraiseOnRelistDone))
                conf.Save();

            DrawNestIndicator(2);
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextWrapped(
                "Needs the TataruPraise plugin; without it this does nothing at all. It only sends a notification - nothing is triggered, nothing about relisting changes. It speaks once per finished run: once when the all-retainer relist tour ends, and once when a single retainer's relist ends on its own. Each retainer inside a tour stays quiet, and so does the automatic pricing of a just quick-listed item."
                    .Loc());
            ImGui.PopStyleColor();

            DrawMultiCharacterSettings();
        }

        /// <summary>
        /// 「下架」分頁：下架收回到哪裡，以及兩個「不要全部下架」的篩選。
        /// </summary>
        private void DrawDelistSettingsTab()
        {
            // 🔴 「下架收回至」管的範圍比「批次重掛」大：改價流程裡的自動下架**以及**
            // 兩顆手動下架按鈕（出售品視窗的「本僱員全下架」、僱員選單的「全僱員下架」）
            // 通通聽它。所以它跟兩個下架篩選同屬這一個分頁、同一個最外層縮排——
            // 三項一起構成「按下下架按鈕會發生什麼事」。
            ImGui.Spacing();
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
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextWrapped(
                "Where delisted items go - this applies to every delist this plugin does: the automatic ones during relisting (the two settings above) and both manual buttons, \"Delist this retainer\" in the sell list and \"Delist all retainers\" in the retainer menu. Player inventory is the default because it is the only choice that merges stacks: identical items spread across several retainers only combine when they all land in the same container, and each retainer's inventory is separate from every other one's. Your bags are smaller than what nine retainers can list, so a full-inventory stop is normal - clear space and press the button again to carry on."
                    .Loc());
            ImGui.PopStyleColor();

            DrawDelistFilterSettings();
        }

        /// <summary>
        /// 「面板與診斷」分頁：即時掛單面板、兩塊側邊面板的位置，以及診斷記錄的等級。
        /// </summary>
        private void DrawPanelSettingsTab()
        {
            ImGui.Spacing();
            if (ImGui.Checkbox("Show a live sell list next to the game's one".Loc(), ref conf.LiveSellListOverlay))
                conf.Save();

            DrawNestIndicator(1);
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextWrapped(
                "Batch repricing writes prices straight into the retainer's market container without opening any game window - which is exactly why it is fast, but it also means the game's sell list never redraws and keeps showing the prices it had when you opened it (a just-listed item stays at 999,999,999 on screen even though the server already has the right price). This panel is drawn by the plugin and re-read every frame, so it is always current. It only reads: no game windows are touched and nothing is clicked for you."
                    .Loc());
            ImGui.PopStyleColor();

            DrawNestIndicator(1);
            if (ImGui.Checkbox("Also show market prices and how long each item has been listed".Loc(),
                    ref conf.LiveSellListMarketColumns))
                conf.Save();

            DrawNestIndicator(2);
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextWrapped(
                "Four extra columns, purely informational: the cheapest listing on your own world, the cheapest anywhere on your data centre, what the item last actually sold for (with the date), and how long it has been sitting on the board. The first three come from the Universalis web API in the same request the relist pricing uses, so nothing is asked of the game's market board; where Universalis has nothing the cell shows a grey question mark - never a zero. The game never says when something was listed, so the age is counted from the moment Marketbuddy watched it go up; anything that was already listed before then is marked with a ~ and counted from when the plugin loaded. Nothing here triggers anything - an item sitting there for a month is never relisted or delisted for you."
                    .Loc());
            ImGui.PopStyleColor();

            DrawNestIndicator(2);
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextWrapped(
                "The age clock is fed by the same look at the sell list that the sales log uses, so turning off \"Record what disappears from your retainers' listings\" stops it too."
                    .Loc());
            ImGui.PopStyleColor();

            DrawNestIndicator(1);
            if (ImGui.Checkbox("Also show how often each item has actually sold".Loc(),
                    ref conf.LiveSellListSalesHistoryColumn))
                conf.Save();

            DrawNestIndicator(2);
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextWrapped(
                "One extra column: how many times that item has sold, and how many times it came off the board without selling - counted from your own sales records, so no web request and nothing asked of the game. It answers \"is it even worth listing this again\" while you are standing at the sell list. Where there is no basis for an answer - the recording is off, or Marketbuddy has never had that retainer's list open - the cell shows a grey question mark instead of a zero, because \"we never watched\" and \"it never sold\" lead to opposite decisions. Purely informational: nothing is ever delisted or repriced because of these numbers."
                    .Loc());
            ImGui.PopStyleColor();

            ImGui.Spacing();
            if (ImGui.Checkbox("Show a running total next to the market board's results".Loc(),
                    ref conf.MarketBuyTallyOverlay))
                conf.Save();

            DrawNestIndicator(1);
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextWrapped(
                "Buying one thing off the market board usually means clicking a lot of rows, and the game never tells you where you are up to. This panel keeps the running count and the running gil for the trip, and adds a small table showing what the cheapest few listings add up to, so \"is this enough\" and \"what will this cost\" are answered on screen instead of in your head. Type in how many you want and the row that gets you there is highlighted. It only reads: nothing is ever bought, clicked or queried for you."
                    .Loc());
            ImGui.PopStyleColor();

            if (conf.MarketBuyTallyOverlay)
            {
                DrawNestIndicator(1);
                ImGui.SetNextItemWidth(70);
                if (ImGui.InputInt("rows in the running-total table".Loc() + "##mbbuytallyrows",
                        ref conf.MarketBuyPreviewRows, 0))
                {
                    conf.MarketBuyPreviewRows = Math.Clamp(conf.MarketBuyPreviewRows, 3, 20);
                    conf.Save();
                }

                ImGui.Spacing();
                ImGui.SetNextItemWidth(200);
                ImGui.DragFloat2("Buy panel position (relative to the results window's top right)".Loc(),
                    ref conf.MarketBuyPanelOffset, 1f, -4000f, 4000f, "%.0f");
                if (ImGui.IsItemDeactivatedAfterEdit())
                    conf.Save();
            }

            // 重掛面板與即時掛單面板現在是**同一欄**（重掛在上、即時掛單接在下面），
            // 所以位置只留一個滑桿，拖它就是整欄一起動。
            // 範圍是可負值：舊版下限寫死 1，往左／往上微調不了。
            if (conf.LiveSellListOverlay || conf.AdjustMaxStackSizeInSellList || conf.BatchRepriceEnabled)
            {
                ImGui.Spacing();
                ImGui.SetNextItemWidth(200);
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
                ImGui.SetNextItemWidth(200);
                ImGui.DragFloat2("All-retainers panel position (relative to the retainer list's top right)".Loc(),
                    ref conf.RetainerPanelOffset, 1f, -4000f, 4000f, "%.0f");
                if (ImGui.IsItemDeactivatedAfterEdit())
                    conf.Save();
            }

            ImGui.Spacing();
            if (ImGui.Checkbox("Write the detailed market diagnostics at Information level".Loc(),
                    ref conf.VerboseMarketDiagnostics))
                conf.Save();

            DrawNestIndicator(1);
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextWrapped(
                "Off by default. The per-item query lines and the request-gate trace are always written to the log either way - this only decides whether they show up at Information level or stay at Debug. Turn it on when someone asks you for a market-board log; leaving it on just makes the log noisier. Refusals, timeouts and market errors are reported regardless of this setting."
                    .Loc());
            ImGui.PopStyleColor();

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            DrawPriceSurveyEntry();
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

            DrawNestIndicator(1);
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextWrapped(
                "Take your own listings around the worlds and look up what each of them is going for, one item at a time, then compare. Nothing happens on its own: you press a button once per world, and travelling to the next world is a separate button - unless you arm a round yourself in the survey window, which is off by default. It only ever sends the same market query the game's own search sends - no price is changed, nothing is listed or delisted. Results are appended to price_survey.csv in this plugin's config folder."
                    .Loc());
            ImGui.PopStyleColor();

            // 純通知，掛在巡檢那一組底下：只有「整份掃完」會叫它，按停與讓路都不會。
            DrawNestIndicator(1);
            if (ImGui.Checkbox("Ask TataruPraise to say a line when a survey finishes".Loc(),
                    ref conf.TataruPraiseOnSurveyDone))
                conf.Save();

            DrawNestIndicator(2);
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextWrapped(
                "Needs the TataruPraise plugin; without it this does nothing at all. It only sends a notification - no extra market query is sent and nothing about the survey changes. It speaks once, and only when this world's list was surveyed all the way to the end: stopping it yourself, standing down for a relist or a delist, giving up after the first item got no answer, or failing to build the list at all, all stay quiet."
                    .Loc());
            ImGui.PopStyleColor();

            if (ImGui.Button("Open the price survey window".Loc()))
                SurveyVisible = true;
        }

        /// <summary>
        /// 多角色重掛的設定與操作。
        /// 🔴 依市場紅線：預設關閉、手動武裝、隨時可停、重啟不殘留、一輪跑完自己解除。
        /// </summary>
        private void DrawMultiCharacterSettings()
        {
            ImGui.Spacing();
            if (ImGui.Checkbox("Relist across characters together with AutoRetainer's multi mode".Loc(),
                    ref conf.MultiCharTourEnabled))
                conf.Save();

            DrawNestIndicator(1);
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextWrapped(
                "Needs AutoRetainer. Each time AutoRetainer has finished a character and is about to log out to the next one, it hands over and this plugin runs one all-retainer relist tour, then hands control straight back. You have to arm it by hand for each round: arming is never saved, so restarting the game or reloading the plugin always leaves it off, and it disarms itself as soon as every character in the round is done. Nothing walks your character anywhere - a character that is not standing within reach of a summoning bell is simply skipped."
                    .Loc());
            ImGui.PopStyleColor();

            if (!conf.MultiCharTourEnabled)
                return;

            var multi = marketbuddy.MultiCharTour;
            DrawNestIndicator(1);
            if (multi.IsArmed)
            {
                ImGui.TextUnformatted("Round: ??/?? character(s) done, ?? item(s) repriced"
                    .Loc(multi.RoundCompleted, multi.RoundTargets, multi.RoundRepriced));

                DrawNestIndicator(1);
                var current = multi.CurrentCharacterName;
                ImGui.TextUnformatted(current.Length > 0
                    ? "Now: ??".Loc(current)
                    : "Waiting for AutoRetainer to hand over.".Loc());
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(MultiCharacterRemainingText(multi));

                DrawNestIndicator(1);
                if (ImGui.Button("Stop multi-character relisting".Loc() + "##mbmcstopconfig"))
                    multi.Stop("stopped by user".Loc());
                return;
            }

            if (ImGui.Button("Arm for one round".Loc() + "##mbmcarm"))
                multiCharArmError = multi.TryArm(out var reason) ? string.Empty : reason;

            if (multiCharArmError.Length > 0)
            {
                DrawNestIndicator(1);
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudRed);
                ImGui.TextWrapped("Cannot arm: ??".Loc(multiCharArmError));
                ImGui.PopStyleColor();
            }
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
        private string PricingRuleText()
        {
            // 新的定價方式開著時，「最低價 -N」那句話就不是這顆按鈕會做的事了——
            // 講錯的規則比不講更糟。查不到成交紀錄的道具仍然走舊規則，所以兩句都要說。
            if (conf.RelistUseLastSoldPrice)
            {
                return "Prices at the most recent sale on the data centre, rounded down to 100 gil.".Loc() + "\n" +
                       (UndercutIsZero
                           ? "Items with no sale on record: the lowest listing (no undercut).".Loc()
                           : "Items with no sale on record: the lowest listing minus ??.".Loc(GetUndercutText()));
            }

            return UndercutIsZero
                ? "Prices at the lowest listing (no undercut)".Loc()
                : "Prices at the lowest listing minus ??".Loc(GetUndercutText());
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