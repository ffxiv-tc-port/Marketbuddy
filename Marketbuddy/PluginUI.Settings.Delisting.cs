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
        /// 「下架」分頁：下架收回到哪裡，以及兩個「不要全部下架」的篩選。
        /// </summary>
        private void DrawDelistSettingsTab()
        {
            // 🔴 「下架收回至」管的範圍比「批次重掛」大：改價流程裡的自動下架**以及**
            // 兩顆手動下架按鈕（出售品視窗的「本僱員全下架」、僱員選單的「全僱員下架」）
            // 通通聽它。所以它跟兩個下架篩選同屬這一個分頁、同一個最外層縮排——
            // 三項一起構成「按下下架按鈕會發生什麼事」。
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

            HelpMarker(
                "Where delisted items go - this applies to every delist this plugin does: the automatic ones during relisting (the two under \"Price safeguards\") and both manual buttons, \"Delist this retainer\" in the sell list and \"Delist all retainers\" in the retainer menu. Player inventory is the default because it is the only choice that merges stacks: identical items spread across several retainers only combine when they all land in the same container, and each retainer's inventory is separate from every other one's. Your bags are smaller than what nine retainers can list, so a full-inventory stop is normal - clear space and press the button again to carry on."
                    .Loc());

            DrawDelistFilterSettings();
        }

        /// <summary>
        /// 兩個「不要全部下架」的篩選：單價門檻與僱員跳過名單。
        /// 🔴 刻意畫在「下架收回至」正下方、同樣的最外層縮排：三項一起構成「按下下架
        /// 按鈕會發生什麼事」，拆散在不同段落會讓人以為它們管的範圍不一樣。
        /// 兩項預設都是關的（門檻 0、名單空）。
        /// </summary>
        private void DrawDelistFilterSettings()
        {
            ImGui.Spacing();
            var aboveRowX = ImGui.GetCursorPosX();
            ImGui.TextWrapped("Only delist listings priced above".Loc());
            var aboveLabel = DrawText.Concat("gil per item".Loc(), "##mbdelistaboveprice");
            SameLineOrBelow(aboveRowX, LabeledItemWidth(110, aboveLabel));
            ImGui.SetNextItemWidth(110);
            if (ImGui.InputInt(aboveLabel, ref conf.DelistAboveUnitPrice, 0))
            {
                conf.DelistAboveUnitPrice =
                    Math.Clamp(conf.DelistAboveUnitPrice, 0, Configuration.MAX_PRICE);
                conf.Save();
            }

            HelpMarker(
                "The threshold is the price per item, the same number the sell list shows in its unit price column - not the price of the whole stack. It applies to both manual delist buttons (\"Delist this retainer\" and \"Delist all retainers\"). It deliberately does NOT touch the two automatic delists under \"Price safeguards\", which exist to take cheap listings off the board: those two do the opposite job, so filtering them by \"only the expensive ones\" would just cancel them out. If a price cannot be read for some reason, that listing is left alone."
                    .Loc());

            // 🔴 「現在是停用的」這件事本身要在**列上**看得見，不能只靠「值是 0」讓人自己推。
            // 停用是預設狀態，而預設狀態下按鈕會把東西全部收回來——那是要當場講清楚的事。
            DrawNestIndicator(1);
            if (DelistPriceFilterActive)
            {
                ImGui.TextWrapped(DrawText.Loc("Listings at ?? gil per item or below stay on the market",
                    conf.DelistAboveUnitPrice, static (t, price) => t.Loc(price.ToString("N0"))));
            }
            else
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
                ImGui.TextUnformatted("Off - every listing gets delisted".Loc());
                ImGui.PopStyleColor();
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(DelistPriceFilterTooltip());

            // 僱員清單（最多九名）收進摺疊標題，免得把這一頁撐長。
            // ⚠️ 但「有幾名被跳過」必須留在**摺起來也看得見**的那一行上——把狀態藏進
            // 要展開才看得到的地方，等於使用者永遠不知道自己設過這個東西。
            ImGui.Spacing();
            var skipCount = conf.DelistTourSkipRetainers.Count;
            if (!ImGui.CollapsingHeader(DrawText.Concat(skipCount == 0
                    ? "Retainers the delist tour leaves alone: none".Loc()
                    : DrawText.Loc("Retainers the delist tour leaves alone: ?? skipped", skipCount), "##mbskipretainers")))
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
                ImGui.TextWrapped(conf.DelistTourSkipRetainers.Count == 0
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
                ImGui.TextUnformatted(DrawText.Loc("(?? listed)", retainer.Listed));
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
                ImGui.TextWrapped("?? more on the list are not this character's retainers".Loc(unknown));
                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(
                        "The list stores retainer IDs rather than names, so it survives renames and works per character. Another character's retainer names cannot be looked up from here, but they are still skipped when you play that character."
                            .Loc());
            }
        }
    }
}