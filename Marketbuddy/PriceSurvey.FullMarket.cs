using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FFXIVClientStructs.FFXIV.Client.Game;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    // 全市場掃描：這台自己的道具表（可上市場、可交易）× 這個資料中心的每個世界，最舊的先掃。
    // 🔴 一輪只由使用者按開始（這台的按鈕或主機遠端指令）；跑完、換世界失敗、讓路都停下待命，不自己接著掃。
    // 🔴 Universalis 只在一輪開始時批次抓一次更新時間（LastSoldPriceSource.FetchUploadTimesAsync），失敗就用本機觀測時間排。
    internal sealed unsafe partial class PriceSurvey
    {
        /// <summary>
        /// 武裝一輪全市場掃描。🔴 只從 framework 執行緒呼叫（<see cref="TourConsumeRequests"/>、<see cref="ExecuteRemote"/>、遠端繼續）。
        /// </summary>
        private void ArmFullMarket(bool byHost)
        {
            if (tourArmed)
                return;

            if (!CanArmTour(out var why))
            {
                TourStatus = "Cannot arm: ??".Loc(why);
                return;
            }

            EnsureFullItems();
            Link.EnsureObservationsLoaded();
            CancelFullTasks();

            tourArmed = true;
            tourProxy = false;
            tourFull = true;
            tourByHost = byHost;
            lastHalt = string.Empty;
            tourVisited.Clear();
            tourWorldsChanged = 0;
            tourMaxWorlds = Configuration.MAX_TOUR_WORLDS;
            tourAdvancePending = false;
            tourTravelTargetId = 0;
            tourTravelTargetName = string.Empty;
            fullWorldsStarted = 0;
            fullWorldsTotal = 0;
            fullUploads = null;
            fullUploadsReady = false;
            fullChooseTask = null;
            fullRoundWorlds = [];
            fullPendingPlanned = false;

            fullCts = new CancellationTokenSource();
            var token = fullCts.Token;
            fullUploadDone = 0;
            fullUploadTotal = 0;
            fullUploadTask = LastSoldPriceSource.FetchUploadTimesAfterAsync(CurrentWorldId, fullItemsTask!, (done, total) =>
            {
                fullUploadDone = done;
                fullUploadTotal = total;
            }, token);

            string opening;
            if (IsRunning)
            {
                // 遠端「繼續」接回暫停中的那個世界：它算這一輪的第一個，收場後 Finish 接手往下挑。
                tourVisited.Add(worldId);
                fullWorldsStarted = 1;
                EnterTourPhase(TourPhase.Scanning, 0);
                opening = "the scan already running";
            }
            else
            {
                EnterTourPhase(TourPhase.Choosing, FullPlanTimeoutSeconds);
                opening = "asking Universalis, then choosing the stalest world";
            }

            TourStatus = "Full market scan armed: asking Universalis which items nobody has updated lately...".Loc();
            Log.Information(
                $"[Marketbuddy] 全市場掃描武裝{(byHost ? "（主機遠端開始）" : "（這台按的）")}：起點 {CurrentWorldName}({CurrentWorldId})，" +
                $"開場動作={opening}，新鮮跳過 {conf.FullMarketFreshHours} 小時、沒資料降頻 {conf.FullMarketNoDataDays} 天，" +
                $"身上 Gil={(CurrentGil >= 0 ? CurrentGil.ToString() : "?")}。武裝狀態不存檔；跑完一輪就待命。");
            ChatGui.Print("[Marketbuddy] " + TourStatus);
        }

        /// <summary>道具表在 framework 執行緒取，列舉在執行緒池上（與 <see cref="PriceSurveyItemSourceHelpers.KnownItem"/> 同一個做法）。</summary>
        private void EnsureFullItems()
        {
            if (fullItemsTask is { IsFaulted: false, IsCanceled: false })
                return;
            var build = PriceSurveyItemSourceHelpers.MarketableItemsBuilder();
            fullItemsTask = Task.Run(build);
        }

        private void CancelFullTasks()
        {
            var cts = fullCts;
            fullCts = null;
            try
            {
                cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 已經收了。
            }

            fullChooseTask = null;
        }

        /// <summary>
        /// 排一份「幫這些世界算待掃清單」的工作（回傳的委派在執行緒池上跑）。在 framework 執行緒上抄好要用的東西：
        /// 道具清單、這一輪的世界（判「每個世界都沒資料」用）、Universalis 時間、設定。
        /// </summary>
        private Func<Dictionary<uint, WorldPlan>> FullPlanJob(IReadOnlyList<(uint WorldId, string Name)> targets)
        {
            var items = fullItemsTask!.Result;
            var round = new List<uint>();
            foreach (var (id, _) in fullRoundWorlds.Count > 0 ? fullRoundWorlds : targets)
                round.Add(id);
            var wanted = new List<uint>();
            foreach (var (id, _) in targets)
                wanted.Add(id);
            var uploads = fullUploads;
            var fresh = conf.FullMarketFreshHours;
            var noData = conf.FullMarketNoDataDays;
            var link = Link;
            return () =>
            {
                var now = DateTime.UtcNow;
                var briefs = new Dictionary<uint, Dictionary<uint, ItemBrief>>();
                foreach (var id in round)
                    briefs[id] = link.WorldBrief(id);
                foreach (var id in wanted)
                {
                    if (!briefs.ContainsKey(id))
                        briefs[id] = link.WorldBrief(id);
                }

                var roundBriefs = new List<IReadOnlyDictionary<uint, ItemBrief>>();
                foreach (var id in round)
                    roundBriefs.Add(briefs[id]);
                var nothing = FullMarketPlanner.NothingAnywhere(roundBriefs);
                var result = new Dictionary<uint, WorldPlan>();
                foreach (var id in wanted)
                    result[id] = FullMarketPlanner.PlanWorld(id, items, briefs[id], nothing, uploads, now, fresh, noData);
                return result;
            };
        }

        /// <summary>這一輪還可以去的世界：站的這個（沒排除）＋換世界選單那一份（Lifestream 去得了、沒排除）。</summary>
        private List<(uint WorldId, string Name)> FullCandidateWorlds()
        {
            var result = new List<(uint, string)>();
            if (CurrentWorldId != 0 && !conf.IsWorldExcluded(CurrentWorldId))
                result.Add((CurrentWorldId, CurrentWorldName));
            foreach (var target in travelTargets)
            {
                if (!conf.IsWorldExcluded(target.WorldId))
                    result.Add(target);
            }

            return result;
        }

        /// <summary>
        /// 全市場掃描挑下一個世界（<see cref="TourChooseNextWorld"/> 的分支，前面的讓路與清單新鮮度檢查已經過了）：
        /// 等 Universalis 時間（或它失敗）→ 在執行緒池上算每個世界的待掃件數 → 待掃最多的先去（一樣多挑上次最舊的），
        /// 在那個世界把它的清單掃完才換。沒有世界還有待掃＝這一輪結束、待命。
        /// </summary>
        private void TourChooseFullMarketWorld()
        {
            var now = DateTime.UtcNow;
            if (fullUploadTask is { IsCompleted: false })
            {
                TourStatus = fullUploadTotal > 0
                    ? "Asking Universalis which items nobody has updated lately (?? / ?? batch(es))...".Loc(fullUploadDone, fullUploadTotal)
                    : "Asking Universalis which items nobody has updated lately...".Loc();
                if (now > tourDeadline)
                    DisarmTour("planning the full market scan took too long".Loc());
                return;
            }

            if (!fullUploadsReady)
            {
                fullUploadsReady = true;
                var (times, why) = fullUploadTask is { IsCompletedSuccessfully: true } t ? t.Result : (null, "task failed");
                fullUploads = times;
                Log.Information(times != null
                    ? $"[Marketbuddy] 全市場掃描：Universalis 更新時間取得 {times.Count} 組世界×道具（{times.Requests} 次請求）。"
                    : $"[Marketbuddy] 全市場掃描：Universalis 更新時間沒有取得（{why}），這一輪改用兩台的觀測時間排先後，不會因此停下。");
            }

            if (fullItemsTask is not { IsCompleted: true } || !Link.ObservationsLoaded || !Link.Progress.IsLoaded)
            {
                TourStatus = "Reading the observation file...".Loc();
                if (now > tourDeadline)
                    DisarmTour("the observation file never finished loading".Loc());
                return;
            }

            if (!fullItemsTask.IsCompletedSuccessfully || fullItemsTask.Result.Count == 0)
            {
                DisarmTour("the list of marketable items could not be built".Loc());
                return;
            }

            fullItemList ??= new PriceSurveyItemList
            {
                SourceKey = "full-market",
                SourceLabel = "Full market (?? marketable item(s))".Loc(fullItemsTask.Result.Count),
                Items = [],
                OwnRetainerIds = [],
            };

            if (fullChooseTask == null)
            {
                var candidates = new List<(uint WorldId, string Name)>();
                foreach (var world in FullCandidateWorlds())
                {
                    if (!tourVisited.Contains(world.WorldId))
                        candidates.Add(world);
                }

                if (fullRoundWorlds.Count == 0)
                    fullRoundWorlds = FullCandidateWorlds();
                if (candidates.Count == 0)
                {
                    FinishFullRound();
                    return;
                }

                fullChooseCandidates = candidates;
                var job = FullPlanJob(candidates);
                fullChooseTask = Task.Run(job);
                TourStatus = "Working out which world is the stalest...".Loc();
                return;
            }

            if (!fullChooseTask.IsCompleted)
                return;

            var task = fullChooseTask;
            fullChooseTask = null;
            if (!task.IsCompletedSuccessfully)
            {
                DisarmTour("the full market plan could not be worked out (??)".Loc(task.Exception?.InnerException?.GetType().Name ?? "?"));
                return;
            }

            var plans = task.Result;
            var pending = 0;
            var withWork = 0;
            var choices = new List<(uint WorldId, int Pending, DateTime LastUtc)>();
            foreach (var (id, plan) in plans)
            {
                pending += plan.Queue.Count;
                if (plan.Queue.Count > 0)
                    withWork++;
                else
                    tourVisited.Add(id);
                var last = Link.Progress.TryGet(SurveyRoundProgress.ModeFull, string.Empty, id, out var row) ? row.AtUtc : DateTime.MinValue;
                choices.Add((id, plan.Queue.Count, last));
            }

            fullPendingEstimate = pending;
            fullPendingPlanned = true;
            if (fullWorldsTotal == 0)
            {
                fullWorldsTotal = fullWorldsStarted + withWork;
                Log.Information(
                    $"[Marketbuddy] 全市場掃描計畫：{withWork} 個世界還有待掃，共 {pending} 件，" +
                    $"預估 {FullMarketPlanner.EstimateSeconds(pending, MarketRequestGate.IntervalMs) / 3600:F1} 小時" +
                    $"（每件 {MarketRequestGate.IntervalMs} ms，不含換世界與被拒重問），Universalis 時間={(fullUploads != null ? "有" : "沒有")}。");
            }

            var best = FullMarketPlanner.ChooseWorld(choices);
            if (best == 0)
            {
                FinishFullRound();
                return;
            }

            var bestName = string.Empty;
            foreach (var (id, name) in fullChooseCandidates)
            {
                if (id == best)
                    bestName = name;
            }

            tourVisited.Add(best);
            fullWorldsStarted++;
            Log.Information(
                $"[Marketbuddy] 全市場掃描：第 {fullWorldsStarted}/{FullWorldsTotal} 個世界挑中 {bestName}({best})，" +
                $"待掃 {plans[best].Queue.Count} 件。");

            if (best == CurrentWorldId)
            {
                startRequested = true;
                EnterTourPhase(TourPhase.Scanning, 0);
                TourStatus = "Full market: scanning ?? (world ?? / ??)".Loc(bestName, fullWorldsStarted, FullWorldsTotal);
                return;
            }

            tourTravelTargetId = best;
            tourTravelTargetName = bestName;
            if (!ChangeWorldOnce(bestName))
            {
                DisarmTour(TravelStatus.Length > 0 ? TravelStatus : "Lifestream did not accept the travel request".Loc());
                return;
            }

            tourWorldsChanged++;
            StartTravelWatch(best);
            TourStatus = "Full market: travelling to ?? (world ?? / ??)".Loc(bestName, fullWorldsStarted, FullWorldsTotal);
        }

        /// <summary>候選世界的名字（挑完之後用；framework 執行緒）。</summary>
        private List<(uint WorldId, string Name)> fullChooseCandidates = [];

        /// <summary>這一輪的每個世界都沒有待掃了：解除武裝、待命，原因寫清楚（不是錯誤）。</summary>
        private void FinishFullRound()
        {
            var reason = "the full market round is finished; waiting for the next press of Start".Loc();
            DisarmTour(reason);
            lastHalt = string.Empty;
        }

        /// <summary>
        /// 還沒開始時的預估（主機畫面「要跑多久」）：每 2 分鐘在執行緒池上用兩台的觀測算一次待掃件數（不問 Universalis）。
        /// framework 執行緒（<see cref="BuildLinkStatus"/>）呼叫。
        /// </summary>
        private void KickFullPreview()
        {
            if (fullPreviewTask is { IsCompleted: true } done)
            {
                fullPreviewTask = null;
                if (done.IsCompletedSuccessfully && !(tourArmed && tourFull))
                {
                    fullPendingEstimate = done.Result;
                    fullPendingPlanned = false;
                }
            }

            var now = DateTime.UtcNow;
            if (fullPreviewTask != null || tourArmed || State != SurveyState.Idle || now < nextFullPreviewAt || !LoggedIn)
                return;
            nextFullPreviewAt = now + FullPreviewInterval;
            EnsureFullItems();
            Link.EnsureObservationsLoaded();
            if (fullItemsTask is not { IsCompletedSuccessfully: true } || !Link.ObservationsLoaded || !TravelTargetsAreFresh())
                return;
            var job = FullPlanJob(FullCandidateWorlds());
            fullPreviewTask = Task.Run(() =>
            {
                var total = 0;
                foreach (var plan in job().Values)
                    total += plan.Queue.Count;
                return total;
            });
        }

        private long fullPreviewWantedTicks;

        /// <summary>畫面上看得到全市場那一段（任何執行緒）：之後幾秒內 framework 執行緒會順便算預估。</summary>
        internal void NoteFullPreviewWanted() => Interlocked.Exchange(ref fullPreviewWantedTicks, DateTime.UtcNow.Ticks);

        /// <summary>沒開連線的人只有在看那一段時才算預估（才會去讀觀測記錄檔與道具表）。</summary>
        private void KickFullPreviewIfWanted()
        {
            if (DateTime.UtcNow.Ticks - Interlocked.Read(ref fullPreviewWantedTicks) < TimeSpan.TicksPerSecond * 10)
                KickFullPreview();
        }

        /// <summary>角色身上的 Gil。🔴 只在 framework 執行緒讀，每 2 秒一次；沒登入時是 -1。</summary>
        private void UpdateGilSnapshot()
        {
            if (!LoggedIn || !conf.PriceSurveyEnabled)
            {
                CurrentGil = -1;
                return;
            }

            var now = DateTime.UtcNow;
            if (now < nextGilAt)
                return;
            nextGilAt = now.AddSeconds(2);
            var inventory = InventoryManager.Instance();
            // 換 CS 世代時特徵碼可能解析不到：那時函式指標是 null，呼叫＝當機，所以先判。
            CurrentGil = inventory == null || InventoryManager.MemberFunctionPointers.GetInventoryItemCount == null
                ? -1
                : inventory->GetInventoryItemCount(1);
        }
    }
}
