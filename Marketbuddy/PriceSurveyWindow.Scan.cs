using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Lumina.Excel.Sheets;
using Marketbuddy.Common;
using static Marketbuddy.Common.Dalamud;
using static Marketbuddy.Common.ImGuiEx;

namespace Marketbuddy
{
    internal sealed partial class PriceSurveyWindow
    {
        // =====================================================================
        //  掃描
        // =====================================================================

        private void DrawScanTab()
        {
            ImGui.Spacing();

            // 🔴 世界名讀的是巡檢在 framework 執行緒拍好的快照，不是 PlayerState：
            //    繪製執行緒不解遊戲的原生指標。
            var worldName = survey.CurrentWorldName;
            if (worldName.Length == 0)
                Grey("Current world: ? (not logged in)".Loc());
            else
                ImGui.TextUnformatted(DrawText.Loc("Current world: ??", worldName));

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            var running = survey.IsRunning;
            var paused = survey.IsPaused;

            // 🔴 暫停中也不准改：這幾項全部參與「要掃哪些件」的計算，中途改了
            //    續跑接上去的那半段會用不同的規則，兩半合起來就不是同一份資料了。
            using (Disabled(running || paused))
            {
                if (ImGui.Checkbox("Take the list from InventoryTools' log file (covers every character's retainers)".Loc(),
                        ref conf.PriceSurveyAllCharacters))
                    conf.Save();
                TooltipInDisabled(
                    "Off = try in order: the retainer sell list if it is open (that retainer only), otherwise AllaganTools (every retainer of the current character), and the log file as a last resort.\nOn (default) = read InventoryTools' inventories.csv directly, covering every character's retainers; the downside is that its freshness depends on when InventoryTools last wrote to disk."
                        .Loc());

                if (ImGui.Checkbox("Only survey items the last round showed as undercut".Loc(), ref conf.PriceSurveyOnlyUndercut))
                    conf.Save();
                TooltipInDisabled(
                    "Needs data in the log file already. With no data at all this filters out every item, and the window says so instead of quietly doing nothing."
                        .Loc());

                if (ImGui.Checkbox("Also look up my shopping list on every world".Loc(),
                        ref conf.PriceSurveyShoppingList))
                    conf.Save();
                TooltipInDisabled(
                    "Off by default. The shopping list is a plain text file you write yourself (shopping_list.txt in this plugin's config folder), one item per line. With this on, every world you scan also records what your shopping list costs there, how many are on sale and when it was looked up - so you know which world to travel to before you leave.\nIt never buys anything. Items you are already selling cost no extra query - the same lookup fills in both lists."
                        .Loc());
            }

            DrawShoppingCost();

            using (Disabled(running || paused))
            {
                ImGui.SetNextItemWidth(200);
                if (ImGui.SliderInt("Skip items already surveyed within (hours)".Loc(), ref conf.PriceSurveySkipHours, 0, 168))
                    conf.Save();
                TooltipInDisabled(
                    "Items this world already answered within that many hours are skipped, so an interrupted survey can carry on. 0 = never skip, always ask again. Refusals and timeouts do not count as surveyed and are always asked again next round."
                        .Loc());

                ImGui.SetNextItemWidth(200);
                if (ImGui.SliderInt("Market cache that may be reused (seconds)".Loc(), ref conf.PriceSurveyCacheSeconds, 0, 3600))
                    conf.Save();
                TooltipInDisabled(
                    "0 (default) = ask the server again for every item. A survey is meant to record what this world looks like right now; mixing in values from minutes ago leads to wrong conclusions later. Only raise it when re-running the same round."
                        .Loc());
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            if (running)
                DrawRunningControls();
            else if (paused)
                DrawPausedControls();
            else
                DrawIdleControls();

            DrawTravelSection(running || paused);

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            GreyWrapped(DrawText.Loc(
                "The gap between items is currently ?? ms (shared with relisting's own throttle gate, which tunes itself).",
                MarketRequestGate.IntervalMs));
            Grey(DrawText.Loc("Log file: ??", PriceSurveyLog.FileName));
            Tooltip(SafeLogPath());

            var pending = PriceSurveyLog.PendingCount;
            if (pending > 0)
                Grey(DrawText.Loc("?? row(s) still being written to the log file.", pending));

            if (conf.PriceSurveyShoppingList)
            {
                Grey(DrawText.Loc("Shopping log file: ??", ShoppingSurveyLog.FileName));
                var shoppingPending = ShoppingSurveyLog.PendingCount;
                if (shoppingPending > 0)
                    Grey(DrawText.Loc("?? row(s) still being written to the shopping log file.", shoppingPending));
            }

            ImGui.Spacing();
            GreyWrapped("Whatever is looked up here is uploaded anonymously to Universalis by Dalamud itself (if you have that turned on in Dalamud). The survey itself contacts nothing on its own: the only setting that makes this plugin send its own HTTP request is \"Also price from the most recent sale, and use whichever is lower\", which reads prices from Universalis. The market price columns on the live sell list panel reuse that very same lookup and send no request of their own, so they only have data while that setting is on. Nothing else in this plugin talks to any website."
                .Loc());
        }

        /// <summary>
        /// 「去下一個世界」：排除清單、手動換世界、自動續跑三塊。
        /// 🔴 「前往」那顆按鈕<b>只換世界</b>：按下去請 Lifestream 送你過去，抵達之後什麼
        /// 都不會發生，要掃描得自己回到上面再按一次「掃描這個世界」。
        /// </summary>
        private void DrawTravelSection(bool running)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.TextUnformatted("Travel to another world".Loc());
            GreyWrapped("Pressing Travel never starts a scan by itself - press the scan button again once you get there. Only a round you arm yourself (below) carries on across worlds."
                .Loc());

            // 🔴 排除清單一律畫：Lifestream 沒裝的時候更需要看得見它，
            //    否則使用者連「為什麼少了一個世界」都查不到。
            DrawWorldExclusions();
            DrawTravelPicker(running);
            DrawAutoTour();
        }

        /// <summary>
        /// 「哪些世界不要碰」。<b>排除清單只有一份真值</b>（設定裡那一份），
        /// 換世界選單、自動續跑、能不能在這裡開始掃描、待處理清單的建議價、
        /// 以及「最近成交價重掛」全部讀它。
        /// </summary>
        private void DrawWorldExclusions()
        {
            // 🔴 先抄一份：下面的核取方塊會改設定裡那個 List，邊改邊列舉會擲例外。
            var excluded = excludedScratch;
            excluded.Clear();
            excluded.AddRange(conf.PriceSurveyExcludedWorlds);
            if (excluded.Count == 0)
            {
                Grey("No world is excluded.".Loc());
            }
            else
            {
                scratch.Clear();
                foreach (var worldId in excluded)
                {
                    if (scratch.Length > 0)
                        scratch.Append(", ");
                    scratch.Append(WorldNameOf(worldId));
                }

                ImGui.TextWrapped(DrawText.Loc("Excluded: ??", DrawText.Reuse(ref excludedNames, scratch)));
            }

            // 🔴 「掛售所在的世界被勾進去了」必須在列上看得見，不能只靠 tooltip：
            //    定價不理會那個勾，但換世界、掃描與兩張表仍然照勾的算——看不見的話
            //    使用者會以為自己勾錯了，或以為定價也跟著排除了。
            var sellingWorldId = Configuration.SellingWorldId();
            if (sellingWorldId != 0 && conf.IsWorldExcluded(sellingWorldId))
                GreyWrapped(DrawText.Loc("?? is where your own items are listed, so pricing still reads it.",
                    WorldNameOf(sellingWorldId)));

            if (TooltipHovered())
                Tooltip(
                    "An excluded world is left out of the travel list, is never picked by automatic world hopping, and cannot be scanned even while you stand on it. Rows already in the log files are kept - the compare and shopping tables just stop showing those columns, and say so.\nRamuh (4034) ships excluded because that world is shut down on this service, so travelling there always fails. That is a normal setting rather than a hard-coded rule: if it ever comes back, untick it here."
                        .Loc() + "\n" +
                    "Excluded worlds are also left out of the pending list's suggested prices and of the most-recent-sale relist price, so a world that shut down cannot set your prices any more. Items whose only sales are on those worlds are left on their usual pricing instead of being given a made-up price."
                        .Loc() + "\n" +
                    "The one exception is the world your own items are listed on: pricing always reads that world, even while it is ticked here. Ticking it still keeps that world out of the travel list, out of automatic world hopping, out of scanning, and out of the compare and shopping tables."
                        .Loc());

            if (!ImGui.CollapsingHeader(DrawText.Concat("Choose which worlds to leave out".Loc(), "###mbsurveyexclude")))
                return;

            var worldsHere = survey.DataCentreWorlds;
            if (worldsHere.Count == 0)
                Grey("The world list is built once you are logged in.".Loc());

            var listed = new HashSet<uint>();
            foreach (var (worldId, name) in worldsHere)
            {
                listed.Add(worldId);
                var isExcluded = conf.IsWorldExcluded(worldId);
                if (ImGui.Checkbox(name + "##mbsurveyexclude" + worldId, ref isExcluded))
                    conf.SetWorldExcluded(worldId, isExcluded);

                if (worldId == survey.CurrentWorldId)
                {
                    ImGui.SameLine();
                    Grey("(you are here)".Loc());
                }

                DrawSellingWorldNote(worldId, sellingWorldId, isExcluded);
            }

            // 🔑 排除清單上但不在這個資料中心的世界也要列得出來，否則使用者換了資料中心
            //    之後就再也勾不掉它——那會變成一個看得見卻改不動的設定。
            foreach (var worldId in excluded)
            {
                if (listed.Contains(worldId))
                    continue;
                var isExcluded = true;
                if (ImGui.Checkbox(WorldNameOf(worldId) + "##mbsurveyexclude" + worldId, ref isExcluded))
                    conf.SetWorldExcluded(worldId, isExcluded);
                ImGui.SameLine();
                Grey("(not on this data centre)".Loc());
                DrawSellingWorldNote(worldId, sellingWorldId, isExcluded);
            }
        }

        /// <summary>
        /// 勾起來的是「自己掛售的那個世界」時，在那一列上寫明定價不受影響。
        /// 🔑 靜默忽略使用者的勾選是最糟的一種處置：他看得到勾勾、看不到它對定價沒作用。
        /// </summary>
        private static void DrawSellingWorldNote(uint worldId, uint sellingWorldId, bool isExcluded)
        {
            if (worldId == 0 || worldId != sellingWorldId || !isExcluded)
                return;

            ImGui.SameLine();
            Grey("(pricing still reads this world)".Loc());
            Tooltip(
                "This is where your own items are listed, so the pending list's suggested price and the most-recent-sale relist price always read this world. The tick still keeps it out of the travel list, out of automatic world hopping, out of scanning, and out of the compare and shopping tables."
                    .Loc());
        }

        /// <summary>
        /// 世界 id → 看得懂的名字。查不到就畫成 <c>#id</c>——
        /// 🔑 那是誠實的「我只知道 id」，不是假裝知道名字。
        /// </summary>
        private string WorldNameOf(uint worldId)
        {
            // 用索引走：對介面型別的清單 foreach 每次都配置一個列舉器。
            var dataCentre = survey.DataCentreWorlds;
            for (var i = 0; i < dataCentre.Count; i++)
            {
                if (dataCentre[i].WorldId == worldId)
                    return dataCentre[i].Name;
            }

            foreach (var world in worlds)
            {
                if (world.WorldId == worldId && world.Name.Length > 0)
                    return world.Name;
            }

            return "#" + worldId;
        }

        private void DrawTravelPicker(bool running)
        {
            if (survey.LifestreamMissing)
            {
                GreyWrapped("Lifestream is not installed, so this plugin cannot move you between worlds. Travel by hand, then press the scan button again."
                    .Loc());
                return;
            }

            var targets = survey.TravelTargets;
            if (targets.Count == 0)
            {
                GreyWrapped("Lifestream does not report any world you can travel to from here.".Loc());
                return;
            }

            if (travelChoice >= targets.Count)
                travelChoice = 0;

            ImGui.SetNextItemWidth(260);
            if (ImGui.BeginCombo("##mbsurveyworld", WorldLabel(targets[travelChoice])))
            {
                for (var i = 0; i < targets.Count; i++)
                {
                    if (ImGui.Selectable(WorldLabel(targets[i]), travelChoice == i))
                        travelChoice = i;
                }

                ImGui.EndCombo();
            }

            ImGui.SameLine();

            // 🔑 武裝中也鎖住：那一輪自己在決定要去哪裡，手動插一腳只會讓它等到逾時
            //    然後解除武裝——按鈕看起來有反應、結果卻是把整輪弄掉。
            var armed = survey.IsTourArmed;
            using (Disabled(running || armed))
            {
                if (ImGui.Button("Travel there".Loc()))
                    survey.RequestChangeWorld(targets[travelChoice].Name);
            }

            if (armed)
                GreyWrapped("An armed round is choosing the worlds. Disarm it below if you want to travel by hand."
                    .Loc());

            if (survey.IsPaused)
                GreyWrapped(DrawText.Loc(
                    "A round is paused on ??. Continue it or stop it before travelling - a paused round cannot be picked up on a different world.",
                    survey.WorldName));

            if (survey.TravelStatus.Length > 0)
                ImGui.TextWrapped(survey.TravelStatus);
        }

        /// <summary>
        /// 「掃完就自動去資料最舊的世界接著掃」。
        /// </summary>
        /// <remarks>
        /// 🔴 兩道人為閘門：設定<b>預設關</b>（打開也只是讓按鈕可按），以及一定要使用者
        /// 親手按「武裝一輪」。武裝狀態不存檔，而且只有「整份掃完」才會往下一個世界走。
        /// </remarks>
        private void DrawAutoTour()
        {
            var armed = survey.IsTourArmed;

            ImGui.Spacing();
            ImGui.TextUnformatted("Carry on with the world that has the oldest data".Loc());

            using (Disabled(armed))
            {
                if (ImGui.Checkbox("Allow automatic world hopping".Loc(), ref conf.PriceSurveyAutoTour))
                    conf.Save();
            }

            Tooltip(
                "Off by default, and turning it on only makes the Arm button clickable - nothing starts on its own.\nOnce armed, finishing a world's list makes this plugin ask Lifestream to travel to the world whose survey data is the oldest (never surveyed counts as oldest) and start scanning there. Excluded worlds are never picked, each world is visited at most once per round, and the round stops at the limit below.\nArming is never remembered: reloading the plugin or restarting the game always leaves it disarmed. Anything other than finishing a world - a pause, a stop, closing this window, a timeout, AutoRetainer or a relist taking over, Lifestream refusing - disarms it on the spot and says why."
                    .Loc());

            if (conf.PriceSurveyAutoTour)
            {
                using (Disabled(armed))
                {
                    ImGui.SetNextItemWidth(200);
                    if (ImGui.SliderInt("At most this many worlds per round".Loc(),
                            ref conf.PriceSurveyAutoTourMaxWorlds, 1, Configuration.MAX_TOUR_WORLDS))
                        conf.Save();
                }

                Tooltip("A hard cap on one armed round, on top of \"each world at most once\". There are only eight worlds here, so eight means \"every world I can reach\"."
                    .Loc());
            }

            if (!armed)
            {
                var canArm = survey.CanArmTour(out var why);
                using (Disabled(!canArm))
                {
                    if (ImGui.Button("Arm one round".Loc(), new Vector2(180, 0)))
                        survey.RequestArmTour();
                }

                if (!canArm && conf.PriceSurveyAutoTour)
                {
                    ImGui.SameLine();
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
                    ImGui.TextWrapped(why);
                    ImGui.PopStyleColor();
                }
            }
            else
            {
                if (ImGui.Button("Disarm".Loc(), new Vector2(180, 0)))
                    survey.RequestDisarmTour("Disarmed by the user".Loc());

                ImGui.SameLine();
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudYellow);
                ImGui.TextWrapped(DrawText.Loc("Armed: ?? / ?? world change(s) used",
                    (survey.TourWorldsChanged, survey.TourMaxWorlds), static (t, a) => t.Loc(a.Item1, a.Item2)));
                ImGui.PopStyleColor();

                if (survey.TourTravelTargetName.Length > 0)
                    Grey(DrawText.Loc("Target world: ??", survey.TourTravelTargetName));
            }

            if (survey.TourStatus.Length > 0)
                ImGui.TextWrapped(survey.TourStatus);
        }

        /// <summary>
        /// 下拉選單上的一列：世界名，加上「這個世界掃到哪了」。
        /// 🔑 權威來源是 <see cref="PriceSurveyWorldLog"/>（它記得那一輪的完整清單有幾件），
        /// 所以「掃完了」與「掃到一半」分得出來。沒有那一列時退回行情記錄檔裡的最後時間戳
        /// ——那只知道「有掃過一些」，所以話要說得比較弱，<b>不可以講成掃完了</b>。
        /// </summary>
        private string WorldLabel((uint WorldId, string Name) target)
        {
            if (PriceSurveyWorldLog.TryGet(target.WorldId, out var row))
            {
                var freshness = PriceSurveyWorldLog.Classify(
                    row, conf.PriceSurveySkipHours, survey.CurrentContentId, DateTime.UtcNow);
                switch (freshness)
                {
                    case SurveyWorldFreshness.Complete:
                        return DrawText.Loc("?? (all ?? done, ??)",
                            (target.Name, row.PlannedTotal, FormatAge(row.AtUtc)),
                            static (t, a) => t.Loc(a.Item1, a.Item2, a.Item3));
                    case SurveyWorldFreshness.Partial:
                        return DrawText.Loc("?? (?? / ?? done, ??)",
                            (target.Name, row.Surveyed, row.PlannedTotal, FormatAge(row.AtUtc)),
                            static (t, a) => t.Loc(a.Item1, a.Item2, a.Item3, a.Item4));
                    case SurveyWorldFreshness.Stale:
                        return DrawText.Loc("?? (?? - past the keep-for window, will be surveyed again)",
                            target.Name, FormatAge(row.AtUtc));
                    case SurveyWorldFreshness.OtherCharacter:
                        return DrawText.Loc("?? (?? - another character's list)", target.Name, FormatAge(row.AtUtc));
                    case SurveyWorldFreshness.Untracked:
                        return DrawText.Loc("?? (last visited ??)", target.Name, FormatAge(row.AtUtc));
                }
            }

            return worldLatest.TryGetValue(target.WorldId, out var at)
                ? DrawText.Loc("?? (has some data, ??)", target.Name, FormatAge(at))
                : DrawText.Loc("?? (not scanned yet)", target.Name);
        }

        private void DrawIdleControls()
        {
            var canStart = survey.CanStart(out var why);
            using (Disabled(!canStart))
            {
                if (ImGui.Button("Scan this world".Loc(), new Vector2(180, 0)))
                    survey.RequestStart();
            }

            if (!canStart)
            {
                ImGui.SameLine();
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
                ImGui.TextWrapped(why);
                ImGui.PopStyleColor();
            }

            if (!conf.PriceSurveyEnabled)
            {
                ImGui.Spacing();
                if (ImGui.Button("Enable this feature".Loc()))
                {
                    conf.PriceSurveyEnabled = true;
                    conf.Save();
                }

                ImGui.SameLine();
                GreyWrapped("(off by default; enabling it only makes the button clickable, nothing happens on its own)".Loc());
            }

            DrawThisWorldProgress();
            DrawWorldMemoryControls();

            if (survey.StatusText.Length == 0)
                return;

            ImGui.Spacing();
            if (survey.LastRunLookedUnsupported)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudRed);
                ImGui.TextWrapped(survey.StatusText);
                ImGui.PopStyleColor();
            }
            else
            {
                ImGui.TextWrapped(survey.StatusText);
            }
        }

        /// <summary>
        /// 閒置時，把「這個世界上次掃到哪」直接寫在按鈕底下。
        /// 🔑 這一行存在的理由：續掃本來就會跳過保留時間內問過的道具，可是進度列
        /// 每一輪都從 0 重新算，看起來就像整份重來。把「上次到哪、按下去會從哪接」
        /// 寫在列上，使用者才看得出來它其實沒有重來。
        /// </summary>
        private void DrawThisWorldProgress()
        {
            var worldId = survey.CurrentWorldId;
            if (worldId == 0)
                return;

            ImGui.Spacing();

            if (!PriceSurveyWorldLog.TryGet(worldId, out var row))
            {
                Grey(PriceSurveyWorldLog.Loaded
                    ? "This world has not been surveyed yet.".Loc()
                    : "Reading the world progress file...".Loc());
                return;
            }

            var freshness = PriceSurveyWorldLog.Classify(
                row, conf.PriceSurveySkipHours, survey.CurrentContentId, DateTime.UtcNow);

            switch (freshness)
            {
                case SurveyWorldFreshness.Complete:
                    Grey(DrawText.Loc("This world: all ?? item(s) surveyed ??.",
                        (row.PlannedTotal, FormatAge(row.AtUtc)), static (t, a) => t.Loc(a.Item1, a.Item2)));
                    Tooltip(
                        "Scanning again now would find every item still inside the keep-for window, so it would have nothing left to ask."
                            .Loc());
                    return;
                case SurveyWorldFreshness.Partial:
                    ImGui.TextWrapped(DrawText.Loc(
                        "This world: ?? / ?? item(s) surveyed ?? - scanning again carries on from item ??.",
                        (row.Surveyed, row.PlannedTotal, FormatAge(row.AtUtc)),
                        static (t, a) => t.Loc(a.Item1, a.Item2, a.Item3, a.Item1 + 1)));
                    Tooltip(
                        "Items already answered inside the keep-for window are skipped, so the ones below were never lost. Refusals and timeouts are always asked again."
                            .Loc());
                    return;
                case SurveyWorldFreshness.Stale:
                    GreyWrapped(DrawText.Loc(
                        "This world: last surveyed ??, which is past the keep-for window - the next scan starts over.",
                        FormatAge(row.AtUtc)));
                    Tooltip("Raise \"Skip items already surveyed within (hours)\" if you want a longer memory.".Loc());
                    return;
                case SurveyWorldFreshness.OtherCharacter:
                    GreyWrapped(DrawText.Loc(
                        "This world: the ?? record was made by another character with a list that only covered that character.",
                        FormatAge(row.AtUtc)));
                    return;
                case SurveyWorldFreshness.Untracked:
                    GreyWrapped(DrawText.Loc(
                        "This world: last visited ??. \"Skip items already surveyed within\" is 0, so nothing is ever skipped.",
                        FormatAge(row.AtUtc)));
                    return;
            }
        }

        /// <summary>「忘記掃過哪些世界」——「掃過」的第四種失效條件，也是唯一一種由使用者觸發的。</summary>
        private void DrawWorldMemoryControls()
        {
            var known = PriceSurveyWorldLog.Count;
            if (known == 0)
                return;

            ImGui.Spacing();
            Grey(DrawText.Loc("?? world(s) remembered", known));
            SameLineIfFits(ButtonWidth("Forget which worlds were scanned".Loc()));
            using (Disabled(PriceSurveyWorldLog.IsLoading))
            {
                if (ImGui.Button("Forget which worlds were scanned".Loc()))
                    PriceSurveyWorldLog.Clear();
            }

            if (TooltipHovered())
                Tooltip(
                    "Clears ?? only. Every price this survey has ever recorded stays in ??, and nothing in the game is touched."
                        .Loc(PriceSurveyWorldLog.FileName, PriceSurveyLog.FileName));
        }

        /// <summary>
        /// 暫停中的操作列。
        /// 🔴 這裡的「繼續掃描」是<b>離開暫停唯一的前進方向</b>，而且一定要使用者按。
        /// </summary>
        private void DrawPausedControls()
        {
            var canResume = survey.CanResume(out var why);
            using (Disabled(!canResume))
            {
                if (ImGui.Button("Continue the survey".Loc(), new Vector2(180, 0)))
                    survey.RequestResume();
            }

            ImGui.SameLine();
            if (ImGui.Button("Stop and discard".Loc(), new Vector2(180, 0)))
                survey.RequestStop("The paused round was discarded".Loc());

            Tooltip(
                "Discarding only throws away this round's remaining queue. Everything already surveyed stays in the log file, so starting again would skip it anyway."
                    .Loc());

            if (!canResume)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
                ImGui.TextWrapped(why);
                ImGui.PopStyleColor();
            }

            ImGui.Spacing();

            var total = Math.Max(1, survey.Total);
            ImGui.ProgressBar(survey.Processed / (float)total, new Vector2(-1, 0),
                progressText.Get((survey.Processed, survey.Total), static k => $"{k.Item1} / {k.Item2}"));

            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudYellow);
            ImGui.TextWrapped(DrawText.Loc("Paused: ??", survey.PauseReason));
            ImGui.PopStyleColor();

            var pausedFor = survey.PausedForSeconds;
            if (pausedFor >= 0)
                Grey("Paused for ??".Loc(FormatDuration(pausedFor)));

            GreyWrapped(DrawText.Loc("World ??  Source ??", survey.WorldName, survey.SourceLabel));
            GreyWrapped("Nothing is being asked while paused, and it will not resume on its own - press Continue.".Loc());
            GreyWrapped(statsText.Get(
                (survey.RecordedRows, survey.OkCount, survey.EmptyCount, survey.RefusedCount,
                    survey.TimeoutCount, survey.CacheCount),
                static k => "?? row(s) recorded  ?? found  ?? nobody selling  ?? refused  ?? timed out  ?? from cache".Loc(
                    k.Item1, k.Item2, k.Item3, k.Item4, k.Item5, k.Item6)));
            if (survey.SkippedAlreadyDone > 0)
                Grey(DrawText.Loc("?? item(s) skipped (surveyed within the keep-for window)", survey.SkippedAlreadyDone));
        }

        private void DrawRunningControls()
        {
            var preparing = survey.State == PriceSurvey.SurveyState.Preparing;

            // 🔑 準備階段還沒有佇列可以留著，所以那時候只給「停止」——
            //    給一顆按下去等於停止的「暫停」比沒有那顆更糟。
            using (Disabled(preparing))
            {
                if (ImGui.Button("Pause".Loc(), new Vector2(180, 0)))
                    survey.RequestPause("Paused by the user".Loc());
            }

            Tooltip(
                "Keeps this round's queue and progress. Nothing is asked while paused, and it never continues on its own - you press Continue."
                    .Loc());

            ImGui.SameLine();
            if (ImGui.Button("Stop the price survey".Loc(), new Vector2(180, 0)))
                survey.RequestStop("Stopped by the user".Loc());

            ImGui.Spacing();

            if (preparing)
            {
                ImGui.TextWrapped(survey.StatusText);
                return;
            }

            var total = Math.Max(1, survey.Total);
            ImGui.ProgressBar(survey.Processed / (float)total, new Vector2(-1, 0),
                progressText.Get((survey.Processed, survey.Total), static k => $"{k.Item1} / {k.Item2}"));

            var remaining = survey.EstimatedRemainingSeconds;
            if (remaining >= 0)
                ImGui.TextUnformatted(remainingText.Get(remaining, static r => "About ?? left".Loc(FormatDuration(r))));

            ImGui.TextUnformatted(DrawText.Loc("Current: ??", ItemName(survey.CurrentItemId)));
            GreyWrapped(DrawText.Loc("World ??  Source ??", survey.WorldName, survey.SourceLabel));
            GreyWrapped(statsText.Get(
                (survey.RecordedRows, survey.OkCount, survey.EmptyCount, survey.RefusedCount,
                    survey.TimeoutCount, survey.CacheCount),
                static k => "?? row(s) recorded  ?? found  ?? nobody selling  ?? refused  ?? timed out  ?? from cache".Loc(
                    k.Item1, k.Item2, k.Item3, k.Item4, k.Item5, k.Item6)));
            if (survey.SkippedAlreadyDone > 0)
                Grey(DrawText.Loc("?? item(s) skipped (surveyed within the keep-for window)", survey.SkippedAlreadyDone));

            // 🔑 「這一輪多花的時間花在哪」要看得見：多查了幾件、記了幾列。
            if (survey.ShoppingExtraQueued > 0 || survey.ShoppingRecordedRows > 0)
                GreyWrapped(DrawText.Loc("Shopping list: ?? extra item(s) queued, ?? row(s) recorded, ?? skipped",
                    (survey.ShoppingExtraQueued, survey.ShoppingRecordedRows, survey.ShoppingSkippedAlreadyDone),
                    static (t, a) => t.Loc(a.Item1, a.Item2, a.Item3)));
        }
    }
}
