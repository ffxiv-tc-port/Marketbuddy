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
        /// 重掛面板：獨立視窗，形式比照 <see cref="LiveSellList"/>，與即時掛單面板同一欄（重掛在上）。
        /// 兩者共用 <see cref="Configuration.LiveSellListOffset"/> 當作整欄的位移，
        /// 所以拖滑桿是兩塊一起動；重掛面板高度變化時，下面那塊是照**量到的下緣**
        /// 重新定位的，不會重疊。
        /// </summary>
        /// <returns>面板下緣的螢幕 Y 座標；面板這一幀沒有畫出來時回傳 null。</returns>
        private float? DrawRepriceWindow()
        {
            var showStack = conf.AdjustMaxStackSizeInSellList;
            var showBatch = conf.BatchRepriceEnabled;
            var quickListPending = marketbuddy.QuickLister?.PendingCount ?? 0;
            var showVendorSell = vendorSellPanel != null && marketbuddy.VendorSeller?.ShouldShow == true;
            var showBatchList = batchListPanel != null &&
                                (conf.BatchListEnabled || marketbuddy.BatchLister?.IsRunning == true);
            if ((!showStack && !showBatch && quickListPending == 0 && !showVendorSell && !showBatchList) ||
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
                ImGui.TextUnformatted(DrawText.Loc("Quick-list queue: ?? pending", quickListPending));
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
                DrawNeedsPricingResult();
                DrawSingleRetainerDelistRow();
            }

            if (showBatchList)
                batchListPanel!.Draw();

            if (showVendorSell)
                vendorSellPanel!.Draw();

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
            if (ImGui.Checkbox(DrawText.Concat("Limit stack size to".Loc(), " "), ref conf.UseMaxStackSize))
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
                // 同一具引擎也跑「只下架」批次，進度列的措辭要跟著這一批走。
                ImGui.TextUnformatted(ProgressText(engine.IsDelistOnlyBatch
                    ? "Delisting ??/??: ??"
                    : "Repricing ??/??: ??", currentIndex, engine.TotalSlots, engine.CurrentItemName));
                ImGui.SameLine();
                if (ImGui.Button(DrawText.Concat("Cancel".Loc(), "##mbbatchcancel")))
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
            // ⚠️ 這顆按鈕的範圍只有**這一個僱員**（全僱員巡迴是僱員選單上的另一顆），
            // 所以字面要寫明範圍。
            var label = UndercutIsZero
                ? "Relist this retainer".Loc()
                : DrawText.Loc("Relist this retainer (lowest -??)", GetUndercutText());
            if (ImGui.Button(DrawText.Concat(label, "##mbbatchstart")))
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
            ImGui.TextWrapped(
                "Pricing from the recent sale price or the cheapest listing, whichever is lower".Loc());
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
            if (ImGui.SmallButton(DrawText.Concat("Show it".Loc(), "##mbshowlivelist")))
            {
                conf.LiveSellListOverlay = true;
                conf.Save();
            }
        }

        /// <summary>
        /// 重掛跑完之後的結果區：查不到行情、因此刻意留在上限價的那幾件。
        /// 🔑 判準與 log 那一行是<b>同一次判定</b>（引擎的 <c>NeedsPricing</c>），這裡不重算。
        /// 🔴 清單純顯示；下架要使用者自己按，而且要按兩次——重掛流程不會自己下架它們。
        /// </summary>
        private void DrawNeedsPricingResult()
        {
            var engine = marketbuddy.BatchReprice;
            var capped = engine.NeedsPricing;
            if (capped.Count == 0 || engine.IsRunning || marketbuddy.MultiTour.IsRunning ||
                marketbuddy.BatchDelist.IsRunning)
            {
                capDelistArmedUntil = DateTime.MinValue;
                return;
            }

            ImGui.Separator();
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
            ImGui.TextUnformatted(DrawText.Loc("?? item(s) had no market data - still at the price cap", capped.Count));
            ImGui.PopStyleColor();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Nobody was selling these, so there was no price to work from and they were deliberately left at the cap - which means nobody can buy them. Price them by hand, or take them off the board with the button below. They are also on the \"To do\" list in the cross-world survey window."
                        .Loc());

            var retainer = engine.NeedsPricingRetainerName;
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextUnformatted(retainer.Length > 0
                ? DrawText.Loc("Retainer: ??", retainer)
                : "Retainer: ?".Loc());
            for (var i = 0; i < capped.Count; i++)
                ImGui.TextUnformatted(DrawText.Loc("#??  ??", (capped[i].Slot + 1, capped[i].Name),
                    static (t, a) => t.Loc(a.Item1, a.Name)));
            ImGui.PopStyleColor();

            var armed = capDelistArmedUntil > DateTime.UtcNow;
            if (!armed && capDelistArmedUntil != DateTime.MinValue)
                capDelistArmedUntil = DateTime.MinValue;

            var canStart = engine.CanStart(out var reason, false);
            var disabled = !canStart && !AutoRetainerBridge.IsBusy;
            if (disabled)
            {
                capDelistArmedUntil = DateTime.MinValue;
                armed = false;
                ImGui.BeginDisabled();
            }

            if (armed)
            {
                var left = (int)Math.Ceiling((capDelistArmedUntil - DateTime.UtcNow).TotalSeconds);
                ImGui.PushStyleColor(ImGuiCol.Button, ImGuiColors.DalamudRed);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ImGuiColors.DalamudRed);
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, ImGuiColors.DalamudRed);
                if (ImGui.Button("Confirm: take those ?? off the market (??)".Loc(capped.Count, left) +
                                 "##mbcapdeliststart"))
                {
                    capDelistArmedUntil = DateTime.MinValue;
                    engine.StartDelistNeedsPricing();
                }

                ImGui.PopStyleColor(3);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(
                        "Only the listings above are touched, and no price is changed. Click again to go ahead, or wait for this to time out."
                            .Loc());
            }
            else
            {
                if (ImGui.Button(DrawText.Concat("Delist the ones with no market data".Loc(), "##mbcapdelistarm")))
                    capDelistArmedUntil = DateTime.UtcNow + DelistArmWindow;
                if (!disabled && ImGui.IsItemHovered())
                    ImGui.SetTooltip((conf.DelistToRetainerInventory
                        ? "Takes just those listings off the market and back into the retainer's own inventory (\"Delist destination\" in the settings). Everything else this retainer sells is left alone. Asks for confirmation first."
                        : "Takes just those listings off the market and back into your own bags. Everything else this retainer sells is left alone. Asks for confirmation first.").Loc());
            }

            if (disabled)
            {
                ImGui.EndDisabled();
                if (!string.IsNullOrEmpty(reason) && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(reason);
            }
        }

        /// <summary>
        /// 「本僱員全下架」——把這一名僱員的掛單全部下架收回。
        /// 🔑 這顆按鈕**沒有自己的執行邏輯**：它直接驅動 <see cref="BatchDelist"/>，
        /// 也就是全僱員巡迴在每一名僱員身上跑的那同一個引擎。所以二次確認、等那一格
        /// 真的空掉才算數、背包滿了乾淨停手並照實回報——全部自動一致，不會分岔成兩套。
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
                    ProgressText("Delisting ??/??: ??", currentIndex, engine.TotalSlots, engine.CurrentItemName));
                ImGui.SameLine();
                if (ImGui.Button(DrawText.Concat("Cancel".Loc(), "##mbselldelistcancel")))
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
                if (ImGui.Button(DrawText.Concat("Delist this retainer".Loc(), "##mbselldelistarm")))
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
    }
}