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
    internal partial class PluginUI
    {
        /// <summary>
        /// 僱員選單旁的「巡迴」面板。
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
            ImGui.TextUnformatted(DrawText.Loc("Multi-character round: ??/?? character(s)",
                (multi.RoundCompleted, multi.RoundTargets), static (t, a) => t.Loc(a.RoundCompleted, a.RoundTargets)));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(MultiCharacterRemainingText(multi));

            if (ImGui.Button(DrawText.Concat("Stop multi-character relisting".Loc(), "##mbmcstoppanel")))
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
            ImGui.TextUnformatted(DrawText.Loc("Retainer ??/??: ??",
                (tour.CurrentRetainerNumber, tour.TotalRetainers, tour.CurrentRetainerName),
                static (t, a) => t.Loc(a.CurrentRetainerNumber, a.TotalRetainers, a.CurrentRetainerName)));

            // 巡迴此刻驅動的引擎——重掛與下架是兩個不同的物件，不能寫死其中一個。
            var engine = tour.ActiveEngine;
            if (engine.IsRunning)
            {
                var currentIndex = Math.Min(engine.ProcessedSlots + 1, engine.TotalSlots);
                ImGui.TextUnformatted(ProgressText(tour.Mode == TourMode.Delist
                    ? "Delisting ??/??: ??"
                    : "Repricing ??/??: ??", currentIndex, engine.TotalSlots, engine.CurrentItemName));
            }

            if (ImGui.Button(DrawText.Concat("Cancel".Loc(), "##mbtourcancel")))
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
                : DrawText.Loc("Relist all retainers (lowest -??)", GetUndercutText());
            if (ImGui.Button(DrawText.Concat(tourLabel, "##mbtourstart")))
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

        /// <summary>單價門檻有沒有開著（0 = 停用 = 全部下架）。</summary>
        private bool DelistPriceFilterActive => conf.DelistAboveUnitPrice > 0;

        /// <summary>使用者的「跳過這些僱員」名單有沒有東西。</summary>
        private bool DelistSkipListActive => conf.DelistTourSkipRetainers.Count > 0;

        /// <summary>
        /// 下架按鈕上方那一行「這一次不會全部下架」的告示。
        /// 🔑 這是**列上**的資訊不是滑鼠提示：使用者正要按一顆紅色的「全部下架」，
        /// 如果實際上會留下東西，那件事必須在他按下去之前就看得見，不能藏在 tooltip 裡
        /// （tooltip 藏的是「為什麼」，不是「有沒有」）。
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
                ImGui.TextUnformatted(DrawText.Loc("Only unit prices above ?? gil", conf.DelistAboveUnitPrice,
                    static (t, price) => t.Loc(price.ToString("N0"))));
                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    // 呼叫端可能還在 BeginDisabled 區段內，不把 Alpha 推回 1 說明框會跟著變淡。
                    // ImRaii.PushStyle 的多載牽涉 ImPlot 組件（本專案沒參考），所以直接 new ImRaii.Style()。
                    using var tooltipAlpha = new ImRaii.Style().Push(ImGuiStyleVar.Alpha, 1f);
                    ImGui.SetTooltip(DelistPriceFilterTooltip());
                }
                if (skipOn)
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudYellow);
            }

            if (skipOn)
            {
                ImGui.TextUnformatted(DrawText.Loc("Skipping ?? retainer(s)", conf.DelistTourSkipRetainers.Count));
                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    using var tooltipAlpha = new ImRaii.Style().Push(ImGuiStyleVar.Alpha, 1f);
                    ImGui.SetTooltip(SkippedRetainerNames());
                }
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

        /// <summary>
        /// 全僱員下架。目的地同樣聽設定「下架收回至」，預設玩家背包。
        /// **刻意做成兩段式**：第一下只是「武裝」，按鈕會變成紅色的
        /// 確認鈕並開始倒數，要在倒數內再按一次才真的開始；倒數結束自動解除。
        /// 🔴 為什麼一定要二次確認：這個動作把一整輪上架的成果全部收回來，
        /// 誤按的代價遠高於誤按重掛。而且它就在重掛按鈕正下方，單擊誤觸的機率不低。
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
                // 而且是印在最後一道確認上的謊話。
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
                if (ImGui.Button(DrawText.Concat("Delist all retainers".Loc(), "##mbdelistarm")))
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
    }
}