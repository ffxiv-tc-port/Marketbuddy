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
        /// 「重掛」分頁：上架與重掛——堆疊上限、快速上架按鍵、一鍵重掛按鈕與它的定價
        /// 守衛（HQ 比價、NPC 收購價、最低價）、比價資料快取，最後是完成通知與多角色。
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
            ImGui.TextWrapped("Quick listing: hold this key and right-click an item to put it up for sale".Loc());
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
            if (ImGui.Checkbox("Also price from the most recent sale, and use whichever is lower".Loc(),
                    ref conf.RelistUseLastSoldPrice))
                conf.Save();

            DrawNestIndicator(2);
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextWrapped(
                "Adds a second reference price: what the item most recently actually sold for anywhere on your data centre, rounded down to the nearest 100 gil. Each item is then relisted at whichever is lower - that sale price, or the cheapest listing on your own world that is not yours. The sale figure comes from the Universalis web API, the same source PriceInsight uses. Items with no recent sale on record simply use the cheapest listing, as they always did; nothing is guessed. What each item would be relisted at is shown in the live listings panel before you press the button."
                    .Loc());
            ImGui.PopStyleColor();

            DrawNestIndicator(2);
            if (!conf.RelistUseLastSoldPrice) PushStyleDisabled();
            if (ImGui.Checkbox("Ignore quality: use the newer of that item's HQ and NQ sales".Loc(),
                    ref conf.RelistLastSoldIgnoreQuality))
                conf.Save();
            if (!conf.RelistUseLastSoldPrice) PopStyleDisabled();

            DrawNestIndicator(2);
            if (!conf.RelistUseLastSoldPrice) PushStyleDisabled();
            var saleAgeRowX = ImGui.GetCursorPosX();
            ImGui.TextWrapped("Only use sales from the last".Loc());
            var saleAgeLabel = DrawText.Concat("day(s) (0 = any age)".Loc(), "##mbsalemaxage");
            SameLineOrBelow(saleAgeRowX, LabeledItemWidth(70, saleAgeLabel));
            ImGui.SetNextItemWidth(70);
            if (ImGui.InputInt(saleAgeLabel, ref conf.RelistLastSoldMaxAgeDays, 0))
            {
                conf.RelistLastSoldMaxAgeDays = Math.Clamp(conf.RelistLastSoldMaxAgeDays, 0, 3650);
                conf.Save();
            }

            DrawNestIndicator(3);
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextWrapped(
                "A sale from months ago cannot tell you what it takes to sell the item today, and it would otherwise override the current listings. Sales older than this are ignored and the item just uses the cheapest listing instead. Sales whose date Universalis does not give are treated as too old."
                    .Loc());
            ImGui.PopStyleColor();
            if (!conf.RelistUseLastSoldPrice) PopStyleDisabled();

            DrawNestIndicator(1);
            var fallbackRowX = ImGui.GetCursorPosX();
            ImGui.TextWrapped("If the market query fails, fall back to survey rows newer than".Loc());
            var fallbackLabel = DrawText.Concat("hour(s) (0 = off)".Loc(), "##mbsurveyfallback");
            SameLineOrBelow(fallbackRowX, LabeledItemWidth(70, fallbackLabel));
            ImGui.SetNextItemWidth(70);
            if (ImGui.InputInt(fallbackLabel, ref conf.RelistSurveyFallbackHours, 0))
            {
                conf.RelistSurveyFallbackHours = Math.Clamp(conf.RelistSurveyFallbackHours, 0, 8760);
                conf.Save();
            }

            DrawNestIndicator(2);
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextWrapped(
                "The server sometimes refuses a market query without saying so. When that happens the cross-world survey log already holds a price for your own world, so it is used instead of giving up on the item. Only your own world is ever used - another world being cheaper does not mean you are being undercut at home. Survey rows only record one lowest price, so the mistyped-price check has to fall back on comparing against your own listing."
                    .Loc());
            ImGui.PopStyleColor();

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
            var minPriceRowX = ImGui.GetCursorPosX();
            ImGui.TextWrapped("Delist items whose target price is below".Loc());
            var minPriceLabel = DrawText.Concat("gil (0 = off)".Loc(), "##mbbatchminprice");
            SameLineOrBelow(minPriceRowX, LabeledItemWidth(90, minPriceLabel));
            ImGui.SetNextItemWidth(90);
            if (ImGui.InputInt(minPriceLabel, ref conf.BatchMinPrice, 0))
            {
                if (conf.BatchMinPrice < 0)
                    conf.BatchMinPrice = 0;
                conf.Save();
            }

            DrawNestIndicator(1);
            if (ImGui.Checkbox("Offer to have a retainer sell those delisted items to the shop".Loc(),
                    ref conf.VendorSellAfterDelist))
                conf.Save();

            DrawNestIndicator(2);
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextWrapped(
                "Off by default. Only the two automatic delists above are recorded, and only when they go back into your own bags (\"Delist destination\" is Player inventory). Nothing is ever sold on its own: once something has been delisted a \"Sell to the shop\" button appears next to the sell list, and pressing it (twice, to confirm) has the retainer sell the ticked stacks through the game's own right-click \"Have Retainer Sell Items\", one at a time. A stack is only sold if it turned up in a slot of its own this round and holds exactly the delisted amount; anything that merged into a stack you already had, or no longer matches, is listed as \"by hand\" and left alone."
                    .Loc());
            ImGui.PopStyleColor();

            DrawNestIndicator(1);
            if (ImGui.Checkbox("Do not follow a price that looks mistyped".Loc(),
                    ref conf.AnomalyGuardEnabled))
                conf.Save();

            DrawNestIndicator(2);
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextWrapped(
                "When the cheapest listing (or the last sale) is far below what every other seller is asking, it is usually somebody who dropped a zero - and following it down would give your item away. Those slots are left exactly as they are, nothing is changed, and each one is listed under \"Left alone\" on the pending tab with the numbers it was judged on. The test compares one seller against the next seller along, not against what the item used to cost, so an item everybody is dumping still gets repriced normally."
                    .Loc());
            ImGui.PopStyleColor();

            DrawNestIndicator(2);
            if (!conf.AnomalyGuardEnabled) PushStyleDisabled();
            var anomalyMinRowX = ImGui.GetCursorPosX();
            ImGui.TextWrapped("only when the normal price is at least".Loc());
            var anomalyMinLabel = DrawText.Concat("gil or more (0 = off)".Loc(), "##mbanomalymin");
            SameLineOrBelow(anomalyMinRowX, LabeledItemWidth(110, anomalyMinLabel));
            ImGui.SetNextItemWidth(110);
            if (ImGui.InputInt(anomalyMinLabel, ref conf.AnomalyGuardMinNormalPrice, 0))
            {
                if (conf.AnomalyGuardMinNormalPrice < 0)
                    conf.AnomalyGuardMinNormalPrice = 0;
                conf.Save();
            }

            DrawNestIndicator(2);
            var anomalyRatioRowX = ImGui.GetCursorPosX();
            ImGui.TextWrapped("and the cheap price is at least".Loc());
            var anomalyRatioLabel = DrawText.Concat("times cheaper than that".Loc(), "##mbanomalyratio");
            SameLineOrBelow(anomalyRatioRowX, LabeledItemWidth(45, anomalyRatioLabel));
            ImGui.SetNextItemWidth(45);
            if (ImGui.InputInt(anomalyRatioLabel, ref conf.AnomalyGuardRatio, 0))
                conf.Save();
            if (!conf.AnomalyGuardEnabled) PopStyleDisabled();

            // 🔑 「設成這樣等於沒開」必須在畫面上看得見：靜靜地不作用與「開著」長得一模一樣。
            if (conf.AnomalyGuardEnabled
                && (conf.AnomalyGuardRatio < PriceAnomalyGuard.MinRatio
                    || conf.AnomalyGuardRatio > PriceAnomalyGuard.MaxRatio
                    || conf.AnomalyGuardMinNormalPrice <= 0))
            {
                DrawNestIndicator(2);
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
                ImGui.TextWrapped(
                    "With these numbers the protection never does anything: the multiplier has to be between ?? and ??, and the gil threshold above 0."
                        .Loc(PriceAnomalyGuard.MinRatio, PriceAnomalyGuard.MaxRatio));
                ImGui.PopStyleColor();
            }

            DrawNestIndicator(1);
            var cacheRowX = ImGui.GetCursorPosX();
            ImGui.TextWrapped("Reuse market data seen in the last".Loc());
            var cacheLabel = DrawText.Concat("seconds (0 = always ask the server)".Loc(), "##mbcachettl");
            SameLineOrBelow(cacheRowX, LabeledItemWidth(160, cacheLabel));
            // 步進 60（一分鐘）／快速步進 300（五分鐘）：跑一輪多角色時常用的值是
            // 1800（30 分），用預設的無步進版本只能手動輸入，按不出來。
            ImGui.SetNextItemWidth(160);
            if (ImGui.InputInt(cacheLabel, ref conf.MarketDataCacheSeconds, 60, 300))
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
            if (ImGui.Button(DrawText.Concat(
                    DrawText.Loc("Clear price cache (?? items)", MarketDataCache.FreshCount(conf.MarketDataCacheSeconds)),
                    "##mbclearcache")))
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

            DrawBatchListSettings();
            DrawMultiCharacterSettings();
        }

        /// <summary>批次上架與全僱員補上架的開關。🔴 市場紅線：預設關、開著也只是多一個面板／按鈕，要按才動。</summary>
        private void DrawBatchListSettings()
        {
            ImGui.Spacing();
            if (ImGui.Checkbox("Show a batch listing panel next to the retainer sell list".Loc(),
                    ref conf.BatchListEnabled))
                conf.Save();

            DrawNestIndicator(1);
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextWrapped(
                "Off by default. Tick items in the panel and press the button: each stack is priced one at a time with the same market check and the same pricing rules as the relist (no parallel queries, the server spacing is kept), and only then put up for sale at that price through the game's own right-click \"Put Up for Sale\". Anything that would sell below the NPC price or below your minimum is not listed; items nobody is selling are skipped rather than listed at the cap. Nothing starts on its own, and ESC, Cancel, a right-click, a confirm key or AutoRetainer starting stops it."
                    .Loc());
            ImGui.PopStyleColor();

            DrawNestIndicator(1);
            if (ImGui.Checkbox("Offer \"Replenish all retainers\" next to the retainer list".Loc(),
                    ref conf.ReplenishTourEnabled))
                conf.Save();

            DrawNestIndicator(2);
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextWrapped(
                "Off by default. Visits every retainer like the relist tour, relists what is already up, then fills free market slots from that retainer's own inventory - but only with the items you ticked for that retainer in the batch listing panel (open the sell list with \"Sell items in your retainer's inventory\" to tick them). Items just delisted on this visit are not put straight back up. Items below your thresholds stay in the retainer's inventory and are only mentioned in the summary. It is never part of the multi-character round."
                    .Loc());
            ImGui.PopStyleColor();
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
                ImGui.TextWrapped("Round: ??/?? character(s) done, ?? item(s) repriced"
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
    }
}