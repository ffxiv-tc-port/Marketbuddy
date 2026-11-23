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
        /// <summary>「上架」：堆疊上限、快速上架按鍵、批次上架面板。</summary>
        private void DrawListingSettingsPage()
        {
            if (ImGui.Checkbox("Limit stack size to".Loc(), ref conf.UseMaxStackSize))
                conf.Save();

            ImGui.SameLine();
            ImGui.SetNextItemWidth(FontPx(45));
            if (ImGui.InputInt("items".Loc(), ref conf.MaximumStackSize, 0))
                MaximumStackSizeChanged();

            DrawNestIndicator(1);
            if (ImGui.Checkbox("Adjust maximum stack size in retainer sell list UI".Loc(),
                    ref conf.AdjustMaxStackSizeInSellList))
                conf.Save();

            ImGui.Spacing();
            ImGui.TextWrapped("Quick listing: hold this key and right-click an item to put it up for sale".Loc());
            DrawNestIndicator(1);
            ImGui.SetNextItemWidth(FontPx(100));
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

            HelpMarker(
                "Pick a key that is not used elsewhere: CTRL already pastes the clipboard price when the sale window opens, and avoid AutoRetainer's own quick-sale key if you have one bound."
                    .Loc());

            // 🔴 市場紅線：預設關、開著也只是多一個面板，要按才動。
            ImGui.Spacing();
            if (ImGui.Checkbox("Show a batch listing panel next to the retainer sell list".Loc(),
                    ref conf.BatchListEnabled))
                conf.Save();
            HelpMarker(
                "Off by default. Tick items in the panel and press the button: each stack is priced one at a time with the same market check and the same pricing rules as the relist (no parallel queries, the server spacing is kept), and only then put up for sale at that price through the game's own right-click \"Put Up for Sale\". Anything that would sell below the NPC price or below your minimum is not listed; items nobody is selling are skipped rather than listed at the cap. Nothing starts on its own, and ESC, Cancel, a right-click, a confirm key or AutoRetainer starting stops it."
                    .Loc());
            DrawBelowThresholdMemory();
            DrawBatchListExclusions();
        }

        /// <summary>賣給商店：併入原有堆疊也整疊賣（預設關，只在上一層開著時可勾）。</summary>
        private void DrawVendorSellMergedStacks()
        {
            var parentOn = conf.VendorSellAfterDelist;
            DrawNestIndicator(1);
            if (!parentOn)
                ImGui.BeginDisabled();
            if (ImGui.Checkbox("Also sell the whole stack when it merged into one you already had".Loc(),
                    ref conf.VendorSellMergedStacks))
                conf.Save();
            if (!parentOn)
                ImGui.EndDisabled();
            HelpMarker(
                "Off by default. Selling to a shop cannot be undone, and this sells the WHOLE stack - including what you already had in it, not just the delisted part. With it on, a delisted item that merged into a stack you already had is offered for sale only when that item had already been delisted for a low price before this round (it is in the low-price delist record), it is not on an InventoryTools craft list, the old stack changed in nothing but its amount, and the amount it grew by is exactly what was delisted. The list shows how many of your own are sold along with it. It is only sold when you press \"Sell to the shop\" yourself; \"Tidy up this character\" never sells these. Everything else that merged is still listed as \"by hand\"."
                    .Loc());
        }

        /// <summary>批次上架的「低於門檻」記憶：開關、天數、目前記住幾種與全部清除。</summary>
        private void DrawBelowThresholdMemory()
        {
            DrawNestIndicator(1);
            if (ImGui.Checkbox("Remember items whose price check came out below your threshold".Loc(),
                    ref conf.BatchListRememberBelowThreshold))
                conf.Save();
            HelpMarker(
                "On by default. When batch listing (or \"Replenish all retainers\") checks an item and it comes out below the NPC price or below your minimum, that is remembered; for the next few days the item is not checked again and is treated as below your threshold straight away (from your bags it goes on the sell-to-shop list, from a retainer it stays where it is). An item is checked again as soon as its remembered price would pass your current thresholds (for example after you lower your minimum), once the days run out, or when you hold Ctrl while pressing the batch listing button. It drops out of the memory as soon as a check finds a price it can be listed at. Nothing is listed or sold because of it."
                    .Loc());
            if (!conf.BatchListRememberBelowThreshold)
                return;

            DrawNestIndicator(2);
            var rowX = ImGui.GetCursorPosX();
            ImGui.TextWrapped("remember for".Loc());
            var daysLabel = DrawText.Concat("days (1-30)".Loc(), "##mbbelowdays");
            SameLineOrBelow(rowX, LabeledItemWidth(FontPx(60), daysLabel));
            ImGui.SetNextItemWidth(FontPx(60));
            if (ImGui.InputInt(daysLabel, ref conf.BatchListBelowThresholdDays, 0))
            {
                conf.BatchListBelowThresholdDays =
                    Math.Clamp(conf.BatchListBelowThresholdDays, 1, BelowThresholdRule.MaxDays);
                conf.Save();
            }

            DrawNestIndicator(2);
            if (!BelowThresholdLedger.Loaded)
            {
                ImGui.TextDisabled("Reading the record...".Loc());
                return;
            }

            ImGui.TextUnformatted(DrawText.Loc("?? item(s) remembered", BelowThresholdLedger.Count));
            ImGui.SameLine();
            if (ImGui.SmallButton("Forget all".Loc() + "##mbbelowclear"))
            {
                Common.Dalamud.Log.Information($"[BatchList] 低於門檻記憶：使用者全部清除 {BelowThresholdLedger.Count} 種");
                BelowThresholdLedger.Clear();
            }
        }

        /// <summary>「重掛定價」：一鍵重掛按鈕與它的定價參考（最近成交、巡檢備援、HQ 比價、比價資料快取）與完成通知。</summary>
        private void DrawRelistPricingPage()
        {
            if (ImGui.Checkbox("Show a one-click relist button in the retainer sell list".Loc(),
                    ref conf.BatchRepriceEnabled))
                conf.Save();

            ImGui.Spacing();
            if (ImGui.Checkbox("Also price from the most recent sale, and use whichever is lower".Loc(),
                    ref conf.RelistUseLastSoldPrice))
                conf.Save();
            HelpMarker(
                "Adds a second reference price: what the item most recently actually sold for anywhere on your data centre, rounded down to the nearest 100 gil. Each item is then relisted at whichever is lower - that sale price, or the cheapest listing on your own world that is not yours. The sale figure comes from the Universalis web API, the same source PriceInsight uses. Items with no recent sale on record simply use the cheapest listing, as they always did; nothing is guessed. What each item would be relisted at is shown in the live listings panel before you press the button."
                    .Loc());

            // 推／彈只看這個區域變數：上面的勾選框同一幀改得到設定欄位。
            var lastSoldOn = conf.RelistUseLastSoldPrice;
            DrawNestIndicator(1);
            if (!lastSoldOn) PushStyleDisabled();
            if (ImGui.Checkbox("Ignore quality: use the newer of that item's HQ and NQ sales".Loc(),
                    ref conf.RelistLastSoldIgnoreQuality))
                conf.Save();

            DrawNestIndicator(1);
            var saleAgeRowX = ImGui.GetCursorPosX();
            ImGui.TextWrapped("Only use sales from the last".Loc());
            var saleAgeLabel = DrawText.Concat("day(s) (0 = any age)".Loc(), "##mbsalemaxage");
            SameLineOrBelow(saleAgeRowX, LabeledItemWidth(70, saleAgeLabel));
            ImGui.SetNextItemWidth(FontPx(70));
            if (ImGui.InputInt(saleAgeLabel, ref conf.RelistLastSoldMaxAgeDays, 0))
            {
                conf.RelistLastSoldMaxAgeDays = Math.Clamp(conf.RelistLastSoldMaxAgeDays, 0, 3650);
                conf.Save();
            }

            HelpMarker(
                "A sale from months ago cannot tell you what it takes to sell the item today, and it would otherwise override the current listings. Sales older than this are ignored and the item just uses the cheapest listing instead. Sales whose date Universalis does not give are treated as too old."
                    .Loc());

            DrawNestIndicator(1);
            if (ImGui.Checkbox("Don't follow a lower last sale that is one of your own prices".Loc(),
                    ref conf.RelistSkipOwnSalePrice))
                conf.Save();
            HelpMarker(
                "Off by default. When the most recent sale is lower than the cheapest listing on your world, the item is normally priced at that sale. Often that sale is just one of your own retainers' listings being bought, and following it keeps dragging your price back down to your own earlier price. With this on, a sale at exactly a price your retainers have up now, or had up in the last ?? days (same item and quality), is not followed: the item is priced from the cheapest listing instead, and the chat line and the log say so. When nobody else is listing the item the sale is still used. It can misjudge: if someone else listed at exactly the same price (for example by copying yours) and that sold, it is treated as yours too and not followed that time. Your prices come from the retainer sell lists Marketbuddy has already seen (\"Record what disappears from your retainers' listings\"); nothing new is saved for this."
                    .Loc(OwnPriceIndex.WindowDays));
            if (conf.RelistSkipOwnSalePrice)
                DrawOwnSalePriceStatus();
            if (!lastSoldOn) PopStyleDisabled();

            ImGui.Spacing();
            var fallbackRowX = ImGui.GetCursorPosX();
            ImGui.TextWrapped("If the market query fails, fall back to survey rows newer than".Loc());
            var fallbackLabel = DrawText.Concat("hour(s) (0 = off)".Loc(), "##mbsurveyfallback");
            SameLineOrBelow(fallbackRowX, LabeledItemWidth(70, fallbackLabel));
            ImGui.SetNextItemWidth(FontPx(70));
            if (ImGui.InputInt(fallbackLabel, ref conf.RelistSurveyFallbackHours, 0))
            {
                conf.RelistSurveyFallbackHours = Math.Clamp(conf.RelistSurveyFallbackHours, 0, 8760);
                conf.Save();
            }

            HelpMarker(
                "The server sometimes refuses a market query without saying so. When that happens the cross-world survey log already holds a price for your own world, so it is used instead of giving up on the item. Only your own world is ever used - another world being cheaper does not mean you are being undercut at home. Survey rows only record one lowest price, so the mistyped-price check has to fall back on comparing against your own listing."
                    .Loc());

            ImGui.Spacing();
            if (ImGui.Checkbox("HQ items only undercut other HQ listings".Loc(), ref conf.BatchCompareHqOnly))
                conf.Save();

            if (ImGui.Checkbox("Treat every character's retainers as your own".Loc(),
                    ref conf.TreatAllCharactersRetainersAsOwn))
            {
                OwnRetainers.Invalidate();
                conf.Save();
            }

            var (otherTotal, fromSellLists, fromAutoRetainer) = OwnRetainers.OtherCounts;
            HelpMarker(
                "On by default. Your alts' listings are not undercut: when the cheapest listing belongs to any of your retainers, the slot is left alone (a quick-listed item matches it), just as it already was for the character you are on. Retainers are known from every sell list Marketbuddy has seen and from AutoRetainer's character data; with neither available only the current character counts. Known right now: ?? other retainer(s) (?? from sell lists, ?? from AutoRetainer)."
                    .Loc(CountOrUnknown(otherTotal), CountOrUnknown(fromSellLists), CountOrUnknown(fromAutoRetainer)));

            ImGui.Spacing();
            var cacheRowX = ImGui.GetCursorPosX();
            ImGui.TextWrapped("Reuse market data seen in the last".Loc());
            var cacheLabel = DrawText.Concat("seconds (0 = always ask the server)".Loc(), "##mbcachettl");
            SameLineOrBelow(cacheRowX, LabeledItemWidth(160, cacheLabel));
            // 步進 60（一分鐘）／快速步進 300（五分鐘）：跑一輪多角色時常用的值是
            // 1800（30 分），用預設的無步進版本只能手動輸入，按不出來。
            ImGui.SetNextItemWidth(FontPx(160));
            if (ImGui.InputInt(cacheLabel, ref conf.MarketDataCacheSeconds, 60, 300))
            {
                conf.MarketDataCacheSeconds = Math.Clamp(conf.MarketDataCacheSeconds, 0, 3600);
                conf.Save();
            }

            HelpMarker(
                "Market board answers are delivered to every plugin at once, so anything you (or another plugin) looked up recently is already here and can be reused without asking the server again - that is the single biggest speed-up available without touching the game. Nothing is ever triggered by receiving data; it is only remembered. Listings belong to a world, not to a character, so the cache now survives character switches and is only dropped when you change world - raise this if you work through several characters in one sitting. It never stores \"nobody is selling this\" unless this plugin confirmed it itself."
                    .Loc());

            DrawNestIndicator(1);
            if (ImGui.Button(DrawText.Concat(
                    DrawText.Loc("Clear price cache (?? items)", MarketDataCache.FreshCount(conf.MarketDataCacheSeconds)),
                    "##mbclearcache")))
                MarketDataCache.Clear();

            // 純通知：只有重掛收尾會叫它，下架不會。
            ImGui.Spacing();
            if (ImGui.Checkbox("Ask TataruPraise to say a line when relisting finishes".Loc(),
                    ref conf.TataruPraiseOnRelistDone))
                conf.Save();
            HelpMarker(
                "Needs the TataruPraise plugin; without it this does nothing at all. It only sends a notification - nothing is triggered, nothing about relisting changes. It speaks once per finished run: once when the all-retainer relist tour ends, and once when a single retainer's relist ends on its own. Each retainer inside a tour stays quiet, and so does the automatic pricing of a just quick-listed item."
                    .Loc());
        }

        /// <summary>「價格防護」：重掛時自動下架的兩條規則、下架後賣給商店，以及疑似打錯價的保護。</summary>
        private void DrawPriceSafeguardsPage()
        {
            if (ImGui.Checkbox("Delist items whose market net (after tax) is below the NPC vendor price".Loc(),
                    ref conf.BatchDelistBelowVendor))
                conf.Save();

            DrawNestIndicator(1);
            ImGui.SetNextItemWidth(FontPx(45));
            if (ImGui.InputInt("% market tax (fallback when live rates are unknown)".Loc(),
                    ref conf.MarketTaxPercent, 0))
            {
                conf.MarketTaxPercent = Math.Clamp(conf.MarketTaxPercent, 0, 25);
                conf.Save();
            }

            ImGui.Spacing();
            var minPriceRowX = ImGui.GetCursorPosX();
            ImGui.TextWrapped("Delist items whose target price is below".Loc());
            var minPriceLabel = DrawText.Concat("gil (0 = off)".Loc(), "##mbbatchminprice");
            SameLineOrBelow(minPriceRowX, LabeledItemWidth(90, minPriceLabel));
            ImGui.SetNextItemWidth(FontPx(90));
            if (ImGui.InputInt(minPriceLabel, ref conf.BatchMinPrice, 0))
            {
                if (conf.BatchMinPrice < 0)
                    conf.BatchMinPrice = 0;
                conf.Save();
            }

            ImGui.Spacing();
            if (ImGui.Checkbox("Offer to have a retainer sell those delisted items to the shop".Loc(),
                    ref conf.VendorSellAfterDelist))
                conf.Save();
            HelpMarker(
                "Off by default. Only the two automatic delists above are recorded, and only when they go back into your own bags (\"Delist destination\" is Player inventory). Nothing is ever sold on its own: once something has been delisted a \"Sell to the shop\" button appears next to the sell list, and pressing it (twice, to confirm) has the retainer sell the ticked stacks through the game's own right-click \"Have Retainer Sell Items\", one at a time. A stack is only sold if it turned up in a slot of its own this round and holds exactly the delisted amount; anything that merged into a stack you already had, or no longer matches, is listed as \"by hand\" and left alone."
                    .Loc());
            DrawVendorSellMergedStacks();

            ImGui.Spacing();
            if (ImGui.Checkbox("Do not follow a price that looks mistyped".Loc(),
                    ref conf.AnomalyGuardEnabled))
                conf.Save();
            HelpMarker(
                "When the cheapest listing (or the last sale) is far below what every other seller is asking, it is usually somebody who dropped a zero - and following it down would give your item away. Those slots are left exactly as they are, nothing is changed, and each one is listed under \"Left alone\" on the pending tab with the numbers it was judged on. The test compares one seller against the next seller along, not against what the item used to cost, so an item everybody is dumping still gets repriced normally."
                    .Loc());

            var anomalyOn = conf.AnomalyGuardEnabled;
            DrawNestIndicator(1);
            if (!anomalyOn) PushStyleDisabled();
            var anomalyMinRowX = ImGui.GetCursorPosX();
            ImGui.TextWrapped("only when the normal price is at least".Loc());
            var anomalyMinLabel = DrawText.Concat("gil or more (0 = off)".Loc(), "##mbanomalymin");
            SameLineOrBelow(anomalyMinRowX, LabeledItemWidth(110, anomalyMinLabel));
            ImGui.SetNextItemWidth(FontPx(110));
            if (ImGui.InputInt(anomalyMinLabel, ref conf.AnomalyGuardMinNormalPrice, 0))
            {
                if (conf.AnomalyGuardMinNormalPrice < 0)
                    conf.AnomalyGuardMinNormalPrice = 0;
                conf.Save();
            }

            DrawNestIndicator(1);
            var anomalyRatioRowX = ImGui.GetCursorPosX();
            ImGui.TextWrapped("and the cheap price is at least".Loc());
            var anomalyRatioLabel = DrawText.Concat("times cheaper than that".Loc(), "##mbanomalyratio");
            SameLineOrBelow(anomalyRatioRowX, LabeledItemWidth(45, anomalyRatioLabel));
            ImGui.SetNextItemWidth(FontPx(45));
            if (ImGui.InputInt(anomalyRatioLabel, ref conf.AnomalyGuardRatio, 0))
                conf.Save();

            DrawNestIndicator(1);
            if (ImGui.Checkbox("Judge by the recent sale median when there is one (both directions)".Loc(),
                    ref conf.AnomalyGuardUseSaleHistory))
                conf.Save();
            HelpMarker(
                "On by default. The yardstick is the consensus of the latest sales of the item on your data centre (how many and how far back is set below): the median of those sales after odd one-off prices - the multiplier above (or more) away from the first median - are left out, and only when at least ?? sales are left. The cheapest listing and the last sale are both compared against it instead of against the next seller. A price that is the multiplier above (or more) away from it in either direction - typically somebody moving gil between their own characters with a silly price - is ignored, unless something else seen right now backs it up (another seller or the last sale in the same range), which is what a real market move looks like. If that leaves nothing to price by, the slot is left alone as above (a new listing can use the fallback below instead). Items without enough sales are judged the old way, except that a next seller far above the consensus or above the ordinary listings on other worlds is treated as an odd one-off listing and not used as the yardstick. The gil threshold above only applies to the next-seller test. Needs \"Also price from the most recent sale, and use whichever is lower\" on, because that is where the sales come from."
                    .Loc(PriceConsensus.MinEntries));
            if (conf.AnomalyGuardUseSaleHistory)
            {
                DrawNestIndicator(2);
                var historyMinRowX = ImGui.GetCursorPosX();
                ImGui.TextWrapped("only when the median is at least".Loc());
                var historyMinLabel = DrawText.Concat("gil or more (0 = always)".Loc(), "##mbanomalyhistmin");
                SameLineOrBelow(historyMinRowX, LabeledItemWidth(110, historyMinLabel));
                ImGui.SetNextItemWidth(FontPx(110));
                if (ImGui.InputInt(historyMinLabel, ref conf.AnomalyHistoryMinMedian, 0))
                {
                    if (conf.AnomalyHistoryMinMedian < 0)
                        conf.AnomalyHistoryMinMedian = 0;
                    conf.Save();
                }

                if (!conf.RelistUseLastSoldPrice)
                {
                    DrawNestIndicator(2);
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
                    ImGui.TextWrapped("Not in effect: \"Also price from the most recent sale, and use whichever is lower\" is off, so there are no sales to take a median of.".Loc());
                    ImGui.PopStyleColor();
                }
            }

            if (!anomalyOn) PopStyleDisabled();

            // 🔑 「設成這樣等於沒開」必須在畫面上看得見：靜靜地不作用與「開著」長得一模一樣。
            if (conf.AnomalyGuardEnabled
                && (conf.AnomalyGuardRatio < PriceAnomalyGuard.MinRatio
                    || conf.AnomalyGuardRatio > PriceAnomalyGuard.MaxRatio
                    || conf.AnomalyGuardMinNormalPrice <= 0))
            {
                DrawNestIndicator(1);
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
                ImGui.TextWrapped(
                    "With these numbers the protection never does anything: the multiplier has to be between ?? and ??, and the gil threshold above 0."
                        .Loc(PriceAnomalyGuard.MinRatio, PriceAnomalyGuard.MaxRatio));
                ImGui.PopStyleColor();
            }

            DrawNewListingFallbackSettings();
        }

        /// <summary>新上架退路的開關，以及共識價的筆數與窗（窗與筆數也是異常價保護「用成交紀錄判」的基準，所以不隨開關停用）。</summary>
        private void DrawNewListingFallbackSettings()
        {
            ImGui.Spacing();
            if (ImGui.Checkbox("When listing something new that nobody else is selling, price it from recent sales or other worlds".Loc(),
                    ref conf.NewListingFallbackEnabled))
                conf.Save();
            HelpMarker(
                "On by default. Only for putting new things on the board: batch listing (including the all-retainers top-up and the tidy round) and a quick-listed slot still parked at the price cap. When nobody else is listing the item on your own world and the last sale is missing or older than your limit, the price is the consensus of the latest sales on your data centre (see below), rounded down to 100 gil; with fewer than ?? usable sales it is the cheapest ordinary listing on another world of your data centre (Universalis plus your own cross-world survey, inside the same number of days, with your undercut applied). When the only reference price was set aside as an odd one-off (mistyped or moving gil) and there is a consensus price, the consensus is used instead of skipping the item. Everything else still applies afterwards: your minimum price, the NPC vendor check and the recently-delisted skip. Listings that already have a price are never touched by this - relisting them works as before - and once listed, the survey and relisting pick them up like anything else. Needs \"Also price from the most recent sale, and use whichever is lower\" on."
                    .Loc(PriceConsensus.MinEntries));

            DrawNestIndicator(1);
            var entriesRowX = ImGui.GetCursorPosX();
            ImGui.TextWrapped("Consensus price: the latest".Loc());
            var entriesLabel = DrawText.Concat("sales (3-50)".Loc(), "##mbconsensusentries");
            SameLineOrBelow(entriesRowX, LabeledItemWidth(45, entriesLabel));
            ImGui.SetNextItemWidth(FontPx(45));
            if (ImGui.InputInt(entriesLabel, ref conf.PriceConsensusEntries, 0))
            {
                conf.PriceConsensusEntries = Math.Clamp(conf.PriceConsensusEntries,
                    PriceConsensus.MinEntries, PriceConsensus.MaxEntries);
                conf.Save();
            }

            DrawNestIndicator(1);
            var windowRowX = ImGui.GetCursorPosX();
            ImGui.TextWrapped("only sales and other-world listings from the last".Loc());
            var windowLabel = DrawText.Concat("days (1-90)".Loc(), "##mbconsensuswindow");
            SameLineOrBelow(windowRowX, LabeledItemWidth(45, windowLabel));
            ImGui.SetNextItemWidth(FontPx(45));
            if (ImGui.InputInt(windowLabel, ref conf.PriceConsensusWindowDays, 0))
            {
                conf.PriceConsensusWindowDays = Math.Clamp(conf.PriceConsensusWindowDays,
                    PriceConsensus.MinWindowDays, PriceConsensus.MaxWindowDays);
                conf.Save();
            }

            if (conf.NewListingFallbackEnabled && !conf.RelistUseLastSoldPrice)
            {
                DrawNestIndicator(1);
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
                ImGui.TextWrapped("Not in effect: \"Also price from the most recent sale, and use whichever is lower\" is off, so there are no sales or other-world listings to fall back on.".Loc());
                ImGui.PopStyleColor();
            }
        }

        /// <summary>「成交價是自家掛的價時不採用」開著時的資料狀態；沒有紀錄要在列上看得見，不能長得跟「有在作用」一樣。</summary>
        private void DrawOwnSalePriceStatus()
        {
            OwnPriceEcho.Refresh();
            string text;
            var warn = true;
            if (!conf.RetainerSalesLogEnabled)
                text = "\"Record what disappears from your retainers' listings\" is off, so your own prices are not being recorded and this has little or nothing to go on.".Loc();
            else if (!OwnPriceEcho.Ready || !RetainerMarketState.Loaded)
                text = "Reading your retainers' records...".Loc();
            else if (OwnPriceEcho.Count == 0)
                text = "No records yet: open each retainer's sell list once so Marketbuddy can see your prices. Until then every last sale is used as before.".Loc();
            else
            {
                text = "Knows ?? of your own prices (item, quality and price).".Loc(OwnPriceEcho.Count);
                warn = false;
            }

            DrawNestIndicator(2);
            ImGui.PushStyleColor(ImGuiCol.Text, warn ? ImGuiColors.DalamudOrange : ImGuiColors.DalamudGrey);
            ImGui.TextWrapped(text);
            ImGui.PopStyleColor();
        }

        /// <summary>
        /// 「全僱員補上架與多角色重掛」。
        /// 🔴 多角色重掛刻意放在這一頁的最後面：它是唯一一條由 AutoRetainer 事件接手的鏈，
        /// 放在最顯眼的地方只會讓它更容易被誤開。依市場紅線：預設關閉、手動武裝、隨時可停、重啟不殘留、一輪跑完自己解除。
        /// </summary>
        private void DrawTourSettingsPage()
        {
            if (ImGui.Checkbox("Offer \"Replenish all retainers\" next to the retainer list".Loc(),
                    ref conf.ReplenishTourEnabled))
                conf.Save();
            HelpMarker(
                "Off by default. Visits every retainer like the relist tour, relists what is already up, then fills free market slots from that retainer's own inventory - but only with the items you ticked for that retainer in the batch listing panel (open the sell list with \"Sell items in your retainer's inventory\" to tick them). Items just delisted on this visit are not put straight back up. Items below your thresholds stay in the retainer's inventory and are only mentioned in the summary. It is never part of the multi-character round."
                    .Loc());

            ImGui.Spacing();
            if (ImGui.Checkbox("Offer \"Tidy up this character\" next to the retainer list".Loc(), ref conf.TidyRoundEnabled))
                conf.Save();
            HelpMarker(
                "Off by default. One button (pressed twice to confirm) that visits each of this character's retainers in turn and, for each one: relists what is up, has the retainer sell to the shop what that relisting just took off the board into your bags, then fills free market slots from the retainer's inventory with the items you ticked for it. Every step keeps its own safeguards (thresholds, the sell-to-shop checks, the ticked items). Needs \"Offer to have a retainer sell those delisted items to the shop\" on. ESC, Cancel or the fleet emergency stop ends the whole round. It only runs when you press it - never from AutoRetainer, never in the multi-character round."
                    .Loc());

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            if (ImGui.Checkbox("Relist across characters together with AutoRetainer's multi mode".Loc(),
                    ref conf.MultiCharTourEnabled))
                conf.Save();
            HelpMarker(
                "Needs AutoRetainer. Each time AutoRetainer has finished a character and is about to log out to the next one, it hands over and this plugin runs one all-retainer relist tour, then hands control straight back. You have to arm it by hand for each round: arming is never saved, so restarting the game or reloading the plugin always leaves it off, and it disarms itself as soon as every character in the round is done. Nothing walks your character anywhere - a character that is not standing within reach of a summoning bell is simply skipped, unless you turn on \"In a Company Workshop, let AutoRetainer walk up to a nearby bell that is out of reach\"."
                    .Loc());

            // 子選項一律畫出來（關著時停用），不然沒開上一層的人永遠不知道有這個選項。
            var tourOn = conf.MultiCharTourEnabled;
            DrawNestIndicator(1);
            if (!tourOn) ImGui.BeginDisabled();
            if (ImGui.Checkbox("In a Company Workshop, let AutoRetainer walk up to a nearby bell that is out of reach".Loc(),
                    ref conf.MultiCharTourApproachWorkshopBell))
                conf.Save();
            if (!tourOn) ImGui.EndDisabled();
            var bellHelp = tourOn
                ? "Only inside a Company Workshop and only for a summoning bell within 20 m: AutoRetainer walks your character up to it the same way it does before opening a workshop bell itself, then this plugin opens the bell. Off: such a character waits out the timeout and is skipped, as before.".Loc()
                : "Turn on \"Relist across characters together with AutoRetainer's multi mode\" first.".Loc();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                WrappedTooltip(bellHelp);
            HelpMarker(bellHelp);

            if (!tourOn)
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

        /// <summary>-1＝不知道，畫成 ?（不畫成 0）。</summary>
        private static string CountOrUnknown(int count) => count < 0 ? "?" : count.ToString();

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
