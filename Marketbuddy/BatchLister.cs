using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using Marketbuddy.Common;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    internal enum BatchListSource
    {
        /// <summary>玩家的四個背包（出售品視窗以「出售（玩家所持物品）」開著）。</summary>
        PlayerBags,

        /// <summary>這名僱員的物品欄（以「出售（僱員所持物品）」開著）。</summary>
        RetainerBags,
    }

    /// <summary>一次批次上架怎麼結束的；巡迴靠它決定換下一名還是整趟停。</summary>
    internal enum BatchListEnd
    {
        None,
        Finished,
        MarketFull,

        /// <summary>這名僱員做不下去（僱員背包視窗沒開、選單沒有「到市場出售」），換下一名不危險。</summary>
        NextRetainer,

        /// <summary>使用者介入、狀態不確定或環境不對：整趟停。</summary>
        Stopped,
    }

    /// <summary>
    /// 批次上架：使用者勾一批道具、按下開始之後，一件一件「查價 → 右鍵到市場出售 → 照算好的價按確認 → 等它真的出現在市場格」。
    /// 🔴 市場紅線：只在使用者按下按鈕（或按下全僱員補上架）時開始；不被任何事件觸發；查價走引擎同一條單一佇列，不平行；
    /// 低於門檻的只加進賣給商店的清單，不開始賣。任何使用者介入、狀態不確定都整批停，在途那一件的視窗交還使用者。
    /// 🔴 framework 執行緒專用；不跨幀保存原生指標：存的是容器＋格號＋格子指紋，每一步當幀重讀、逐項比對。
    /// </summary>
    internal sealed unsafe class BatchLister : IDisposable
    {
        private const int ContextMenuHiddenFramesRequired = 20;
        private const int ForeignContextMenuFrames = 10;
        private const int ProbeStartTimeoutMs = 10000;
        private const int ProbeTimeoutMs = 150000;
        private const int OpenPhaseTimeoutMs = 30000;
        private const int DisabledEntryRetryMs = 2000;
        private const int ListingOutcomeTimeoutMs = QuickListAckTiming.BatchOutcomeTimeoutMs;
        private const int PriceVerifyMs = 3000;
        private const int SettleTimeoutMs = 5000;
        private const int CandidateRefreshMs = 1000;
        private const int ViewTimeoutMs = 3000;

        private static readonly InventoryType[] PlayerBagTypes =
        [
            InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
        ];

        internal static readonly InventoryType[] RetainerBagTypes =
        [
            InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3,
            InventoryType.RetainerPage4, InventoryType.RetainerPage5, InventoryType.RetainerPage6,
            InventoryType.RetainerPage7,
        ];

        /// <summary>勾選時讀到的那一格；上架前那一格要逐項一模一樣才動。</summary>
        internal readonly record struct Pick(VendorSellSlot Slot, string Name);

        /// <summary>面板上的一種道具（同一種的幾疊合成一列）。</summary>
        internal sealed class Candidate
        {
            public required VendorSellKey Key { get; init; }
            public required string Name { get; init; }
            public required List<VendorSellSlot> Slots { get; init; }
            public required int Quantity { get; init; }

            /// <summary>從市場快取估的價（只給人看）；-1＝不知道。</summary>
            public required long Estimate { get; init; }

            /// <summary>在 InventoryTools 的製作清單上。</summary>
            public required bool Protected { get; init; }
        }

        private enum Phase
        {
            Probe,
            WaitProbe,
            Open,
            WaitListing,
            VerifyPrice,
            Settle,
        }

        private sealed class Job
        {
            public required Pick Pick { get; init; }
            public Phase Phase = Phase.Probe;
            public DateTime PhaseStartedUtc;
            public long Token;
            public uint Price;
            public string PriceNote = string.Empty;
            public short MarketSlot = -1;
            public DateTime DisabledSinceUtc = DateTime.MinValue;
        }

        private static long nextToken;

        private readonly MarketGuiEventHandler gui;
        private readonly BatchReprice engine;
        private readonly BatchDelist delist;
        private readonly MultiRetainerTour tour;
        private readonly QuickLister? quickLister;
        private readonly VendorSeller? vendorSeller;

        private readonly List<Job> jobs = new();
        private int jobIndex;
        private int contextMenuHiddenFrames;
        private int contextMenuVisibleFrames;
        private int refusalStreak;
        private bool suppressionHeld;
        private ulong runContentId;
        private ulong runRetainerId;
        private bool drivenByTour;

        private readonly List<uint> readLoaded = new();
        private readonly List<VendorSellSlot> readSlots = new();
        private readonly HashSet<VendorSellKey> playerTicks = new();
        private ulong playerTicksContentId;
        private DateTime lastViewedUtc = DateTime.MinValue;
        private DateTime nextRefreshUtc = DateTime.MinValue;
        private HashSet<uint> protectedIds = new();
        private bool protectedRead;

        private Configuration conf => Configuration.GetOrLoad();

        public bool IsRunning { get; private set; }
        public BatchListSource Source { get; private set; }
        public int TotalJobs => jobs.Count;
        public int FinishedJobs => Math.Min(jobIndex, jobs.Count);
        public string CurrentItemName => IsRunning && jobIndex < jobs.Count ? jobs[jobIndex].Pick.Name : string.Empty;

        /// <summary>執行中卡在哪個前置條件上（給面板顯示）；空字串＝沒有卡住。</summary>
        public string WaitingFor { get; private set; } = string.Empty;

        public int ListedCount { get; private set; }
        public int VendorQueuedCount { get; private set; }
        public int SkippedCount => Skipped.Count;

        /// <summary>低於門檻、留在僱員物品欄沒上架的疊數（也算在 <see cref="SkippedCount"/> 裡）。</summary>
        public int BelowThresholdKept { get; private set; }

        /// <summary>這一次（或上一次）跳過的道具與原因。</summary>
        public List<(string Name, string Reason)> Skipped { get; } = new();

        public BatchListEnd End { get; private set; } = BatchListEnd.None;
        public string EndReason { get; private set; } = string.Empty;

        /// <summary>面板上目前列得出來的道具；沒人在看時不重算。</summary>
        public IReadOnlyList<Candidate> Candidates { get; private set; } = Array.Empty<Candidate>();

        public BatchListSource CandidateSource { get; private set; }

        /// <summary>候選來源的背包視窗有沒有開著（沒開就沒辦法右鍵）。</summary>
        public bool CandidateWindowOpen { get; private set; }

        public BatchLister(MarketGuiEventHandler gui, BatchReprice engine, BatchDelist delist, MultiRetainerTour tour,
            QuickLister? quickLister, VendorSeller? vendorSeller)
        {
            this.gui = gui;
            this.engine = engine;
            this.delist = delist;
            this.tour = tour;
            this.quickLister = quickLister;
            this.vendorSeller = vendorSeller;
            Framework.Update += OnFrameworkUpdate;
        }

        public void Dispose()
        {
            Framework.Update -= OnFrameworkUpdate;
            if (IsRunning)
                Stop("plugin unloading", BatchListEnd.Stopped);
            ReleaseSuppressionIfHeld();
        }

        /// <summary>右鍵選單的 hook 與 Addon 表第 99 列都在，才做得了。</summary>
        public bool IsAvailable => quickLister?.CanPutUpForSale == true;

        // ------------------------------------------------------------------ 面板：候選與勾選

        public void MarkViewed() => lastViewedUtc = DateTime.UtcNow;

        public bool IsTicked(Candidate c) => CandidateSource == BatchListSource.PlayerBags
            ? playerTicks.Contains(c.Key)
            : IsReplenishPick(BatchReprice.ActiveRetainerId(), c.Key);

        public void SetTicked(Candidate c, bool ticked)
        {
            if (IsRunning)
                return;
            if (CandidateSource == BatchListSource.PlayerBags)
            {
                if (ticked)
                    playerTicks.Add(c.Key);
                else
                    playerTicks.Remove(c.Key);
                return;
            }

            var retainerId = BatchReprice.ActiveRetainerId();
            if (retainerId == 0)
                return;
            var picks = conf.ReplenishPicks;
            picks.RemoveAll(p => p.RetainerId == retainerId && p.ItemId == c.Key.ItemId && p.Hq == c.Key.Hq);
            if (ticked)
                picks.Add(new ReplenishPick { RetainerId = retainerId, ItemId = c.Key.ItemId, Hq = c.Key.Hq });
            conf.Save();
        }

        public void ClearTicks()
        {
            if (IsRunning)
                return;
            if (CandidateSource == BatchListSource.PlayerBags)
            {
                playerTicks.Clear();
                return;
            }

            var retainerId = BatchReprice.ActiveRetainerId();
            if (retainerId != 0 && conf.ReplenishPicks.RemoveAll(p => p.RetainerId == retainerId) > 0)
                conf.Save();
        }

        internal static bool IsReplenishPick(ulong retainerId, VendorSellKey key)
        {
            if (retainerId == 0)
                return false;
            foreach (var p in Configuration.GetOrLoad().ReplenishPicks)
            {
                if (p.RetainerId == retainerId && p.ItemId == key.ItemId && p.Hq == key.Hq)
                    return true;
            }

            return false;
        }

        internal static bool HasReplenishPicks(ulong retainerId)
        {
            foreach (var p in Configuration.GetOrLoad().ReplenishPicks)
            {
                if (p.RetainerId == retainerId)
                    return true;
            }

            return false;
        }

        /// <summary>這一次按下開始會上架的格數（勾選的道具的每一疊）。</summary>
        public int TickedStacks()
        {
            var n = 0;
            foreach (var c in Candidates)
            {
                if (IsTicked(c))
                    n += c.Slots.Count;
            }

            return n;
        }

        private void RefreshCandidates(DateTime now)
        {
            nextRefreshUtc = now.AddMilliseconds(CandidateRefreshMs);
            var contentId = PlayerState.ContentId;
            if (contentId != playerTicksContentId)
            {
                playerTicks.Clear();
                playerTicksContentId = contentId;
                protectedRead = false;
            }

            var retainerOwner = VendorSeller.GetRetainerInventoryAddonId();
            CandidateSource = retainerOwner != 0 ? BatchListSource.RetainerBags : BatchListSource.PlayerBags;
            CandidateWindowOpen = retainerOwner != 0 || VendorSeller.GetInventoryAddonId() != 0;

            if (!protectedRead)
            {
                protectedIds = BagSorter.ReadProtectedIds(out _);
                protectedRead = true;
            }

            Candidates = BuildCandidates(CandidateSource == BatchListSource.RetainerBags ? RetainerBagTypes : PlayerBagTypes,
                protectedIds, withEstimate: true);
        }

        private List<Candidate> BuildCandidates(InventoryType[] types, HashSet<uint> protectedItems, bool withEstimate)
        {
            readLoaded.Clear();
            readSlots.Clear();
            VendorSeller.CaptureContainers(types, readLoaded, readSlots);

            var sheet = DataManager.GetExcelSheet<Item>();
            var byKey = new Dictionary<VendorSellKey, List<VendorSellSlot>>();
            var order = new List<VendorSellKey>();
            foreach (var s in readSlots)
            {
                if (!byKey.TryGetValue(s.Key, out var list))
                {
                    byKey[s.Key] = list = new List<VendorSellSlot>();
                    order.Add(s.Key);
                }

                list.Add(s);
            }

            HashSet<ulong>? own = null;
            var result = new List<Candidate>(order.Count);
            foreach (var key in order)
            {
                if (key.ItemId == uint.MaxValue || sheet == null || !sheet.TryGetRow(key.ItemId, out var row))
                    continue;
                if (!BatchListRules.IsListable(row.IsUntradable, row.ItemSearchCategory.RowId, row.IsCollectable, false))
                    continue;

                var name = row.Name.ExtractText();
                if (key.Hq)
                    name += $" {(char)SeIconChar.HighQuality}";
                var slots = byKey[key];
                var qty = 0;
                foreach (var s in slots)
                    qty += s.Quantity;
                result.Add(new Candidate
                {
                    Key = key, Name = name, Slots = slots, Quantity = qty,
                    Estimate = withEstimate ? Estimate(key, own ??= OwnRetainerIds()) : -1,
                    Protected = protectedItems.Contains(key.ItemId),
                });
            }

            return result;
        }

        /// <summary>只讀被動收下的市場快取，不送查詢；查不到回 -1（面板畫 ?）。</summary>
        private long Estimate(VendorSellKey key, HashSet<ulong> own)
        {
            if (!MarketDataCache.TryGet(key.ItemId, conf.MarketDataCacheSeconds, out var listings, out _) ||
                listings.Count == 0)
                return -1;
            var lowest = RelistPricing.LowestCompetitor(listings, key.Hq, conf.BatchCompareHqOnly, own,
                out _, out var ownIsLowest, out var ownLowest);
            if (ownIsLowest && ownLowest > 0)
                return ownLowest;
            return lowest > 0 ? RelistPricing.ApplyUndercut(lowest, conf.UndercutRuleSnapshot()) : -1;
        }

        private static HashSet<ulong> OwnRetainerIds()
        {
            var result = new HashSet<ulong>();
            var retainerManager = RetainerManager.Instance();
            if (retainerManager == null)
                return result;
            foreach (var retainer in retainerManager->Retainers)
            {
                if (retainer.RetainerId != 0)
                    result.Add(retainer.RetainerId);
            }

            return result;
        }

        /// <summary>按下開始時要做的格：勾選道具的每一疊，照容器與格號排。</summary>
        public List<Pick> TickedPicks()
        {
            var picks = new List<Pick>();
            foreach (var c in Candidates)
            {
                if (!IsTicked(c))
                    continue;
                foreach (var s in c.Slots)
                    picks.Add(new Pick(s, c.Name));
            }

            return picks;
        }

        /// <summary>
        /// 全僱員補上架用：這名僱員物品欄裡在「可補上架清單」上的每一疊，扣掉 <paramref name="exclude"/>（剛下架回來的）。
        /// 🔴 只在 framework 執行緒、站在這名僱員面前時呼叫。
        /// </summary>
        internal List<Pick> ReplenishPicksFor(ulong retainerId, IReadOnlySet<VendorSellKey> exclude)
        {
            var picks = new List<Pick>();
            foreach (var c in BuildCandidates(RetainerBagTypes, new HashSet<uint>(), withEstimate: false))
            {
                if (exclude.Contains(c.Key) || !IsReplenishPick(retainerId, c.Key))
                    continue;
                foreach (var s in c.Slots)
                    picks.Add(new Pick(s, c.Name));
            }

            return picks;
        }

        // ------------------------------------------------------------------ 開始／停止

        public bool CanStart(BatchListSource source, bool forTour, out string reason)
        {
            reason = string.Empty;
            if (IsRunning)
                return false;
            if (!IsAvailable)
            {
                reason = "the right-click menu hook is not available".Loc();
                return false;
            }

            if (!(forTour ? conf.ReplenishTourEnabled : conf.BatchListEnabled))
            {
                reason = "the feature is turned off in /mbuddy".Loc();
                return false;
            }

            if (!gui.IsRetainerSellListOpen)
            {
                reason = "retainer sell list is not open".Loc();
                return false;
            }

            if (IPCManager.IsLocked)
            {
                reason = "locked via IPC by another plugin".Loc();
                return false;
            }

            if (AutoRetainerBridge.IsBusy)
            {
                reason = "AutoRetainer is busy (or MultiMode is enabled), stop it first".Loc();
                return false;
            }

            if (OtherTaskRunning(forTour))
            {
                reason = "another Marketbuddy task is running".Loc();
                return false;
            }

            if (Commons.GetUnitBase("RetainerSell") != null)
            {
                reason = "close the price adjustment window first".Loc();
                return false;
            }

            if (AddonHelpers.GetReadyAddon("SelectYesno") != null)
            {
                reason = "answer the open yes/no box first".Loc();
                return false;
            }

            if (BatchReprice.ActiveRetainerId() == 0)
            {
                reason = "no active retainer".Loc();
                return false;
            }

            if (OwnerAddonId(source) == 0)
            {
                reason = source == BatchListSource.PlayerBags
                    ? "open your inventory window".Loc()
                    : "open the retainer's inventory window".Loc();
                return false;
            }

            return true;
        }

        /// <summary>開始一批。回 false＝沒開始（原因已印在聊天視窗，巡迴驅動時不印）。</summary>
        public bool Start(IReadOnlyList<Pick> picks, BatchListSource source, bool forTour)
        {
            if (!CanStart(source, forTour, out var reason))
            {
                if (!forTour && reason.Length > 0)
                    ChatGui.PrintError("[Marketbuddy] Cannot start: ??".Loc(reason));
                Log.Information($"[BatchList] 沒有開始（{(forTour ? "全僱員補上架" : "使用者按鈕")}）：{(reason.Length > 0 ? reason : "已在跑")}");
                return false;
            }

            if (picks.Count == 0)
            {
                if (!forTour)
                    ChatGui.PrintError("[Marketbuddy] Cannot start: ??".Loc("nothing is ticked to list".Loc()));
                return false;
            }

            jobs.Clear();
            foreach (var pick in picks)
                jobs.Add(new Job { Pick = pick });
            jobIndex = 0;
            jobs[0].PhaseStartedUtc = DateTime.UtcNow;
            ListedCount = 0;
            VendorQueuedCount = 0;
            BelowThresholdKept = 0;
            Skipped.Clear();
            End = BatchListEnd.None;
            EndReason = string.Empty;
            WaitingFor = string.Empty;
            refusalStreak = 0;
            userStopReason = null;
            contextMenuHiddenFrames = 0;
            contextMenuVisibleFrames = 0;
            Source = source;
            drivenByTour = forTour;
            runContentId = PlayerState.ContentId;
            runRetainerId = BatchReprice.ActiveRetainerId();
            IsRunning = true;
            AutoRetainerBridge.AcquireSuppression("batch listing");
            suppressionHeld = true;

            Log.Information(
                $"[BatchList] 開始：{jobs.Count} 疊，來源={(source == BatchListSource.PlayerBags ? "玩家背包" : "僱員物品欄")}，" +
                $"{(forTour ? "全僱員補上架" : "使用者按鈕")}，僱員={BatchReprice.RetainerNameOf(runRetainerId)}");
            if (!forTour)
                ChatGui.Print("[Marketbuddy] Batch listing ?? stack(s)...".Loc(jobs.Count));
            return true;
        }

        public void CancelByButton() => Stop("cancelled by user".Loc(), BatchListEnd.Stopped);

        /// <summary>全艦隊急停：同取消按鈕，只換原因（在途那一件交給使用者）。沒在跑就什麼都不做。</summary>
        internal void EmergencyStop(string reason) => Stop(reason, BatchListEnd.Stopped);

        /// <summary>
        /// 使用者（或別的插件）介入。只記下來，下一個 Framework.Update 才真的停：
        /// 呼叫端是右鍵選單 hook 與上架視窗的事件處理，停的時候要撤待處理項、取消引擎查價，不在那個堆疊上做。
        /// </summary>
        public void StopByUser(string reason, string detail)
        {
            if (!IsRunning || userStopReason != null)
                return;
            userStopReason = reason;
            Log.Information($"[BatchList] 使用者介入：{reason}（{detail}）");
        }

        private string? userStopReason;

        // ------------------------------------------------------------------ 每幀

        private void OnFrameworkUpdate(IFramework framework)
        {
            if (IsRunning)
            {
                TrackContextMenu();
                var blocked = userStopReason ?? RunBlockedReason();
                if (blocked != null)
                {
                    Stop(blocked, BatchListEnd.Stopped);
                    return;
                }

                Tick(DateTime.UtcNow);
                return;
            }

            var now = DateTime.UtcNow;
            if (conf.BatchListEnabled && gui.IsRetainerSellListOpen && now >= nextRefreshUtc &&
                (now - lastViewedUtc).TotalMilliseconds < ViewTimeoutMs)
                RefreshCandidates(now);
        }

        private void TrackContextMenu()
        {
            var menu = Commons.GetUnitBase("ContextMenu");
            if (menu != null && menu->IsVisible)
            {
                contextMenuHiddenFrames = 0;
                if (contextMenuVisibleFrames < int.MaxValue)
                    contextMenuVisibleFrames++;
            }
            else
            {
                contextMenuVisibleFrames = 0;
                if (contextMenuHiddenFrames < int.MaxValue)
                    contextMenuHiddenFrames++;
            }
        }

        private string? RunBlockedReason()
        {
            if (!(drivenByTour ? conf.ReplenishTourEnabled : conf.BatchListEnabled))
                return "the feature was turned off".Loc();
            if (!gui.IsRetainerSellListOpen)
                return "the retainer sell list was closed".Loc();
            if (Keys[VirtualKey.ESCAPE])
                return "ESC pressed".Loc();
            if (IPCManager.IsLocked)
                return "locked via IPC by another plugin".Loc();
            if (AutoRetainerBridge.IsBusy)
                return "AutoRetainer became busy".Loc();
            if (PlayerState.ContentId != runContentId)
                return "you switched characters".Loc();
            if (BatchReprice.ActiveRetainerId() != runRetainerId)
                return "you moved to another retainer".Loc();
            if (OtherTaskRunning(drivenByTour))
                return "another Marketbuddy task started".Loc();
            if (AddonHelpers.GetReadyAddon("SelectYesno") != null)
                return "a yes/no box is open - answer it yourself".Loc();

            // 上架視窗只准是我們自己這一件的（排著待處理、按了確認、延後中或交出去的）。
            var sell = Commons.GetUnitBase("RetainerSell");
            if (sell != null && quickLister is { } ql && !ql.IsQuickListPending && !ql.OwnsSellWindow((nint)sell))
                return "manual price adjustment detected".Loc();

            if (QuickLister.HeldConfirmInput(out _, out _))
                return "a confirm key or controller button was pressed".Loc();

            // 我們自己的選單在開的那一次呼叫裡就收掉了；連續看得到好幾幀＝別人開的。
            if (contextMenuVisibleFrames >= ForeignContextMenuFrames)
                return "another right-click menu is open".Loc();
            return null;
        }

        private bool OtherTaskRunning(bool forTour)
            => delist.IsRunning || gui.Survey?.IsRunning == true || vendorSeller?.IsRunning == true ||
               (tour.IsRunning && !forTour) || (engine.IsRunning && !engine.IsProbeRunning) ||
               (!IsRunning && engine.IsRunning) || (!IsRunning && (quickLister?.PendingCount ?? 0) > 0);

        private void Tick(DateTime now)
        {
            var job = jobs[jobIndex];
            switch (job.Phase)
            {
                case Phase.Probe:
                    TickProbe(job, now);
                    break;
                case Phase.WaitProbe:
                    TickWaitProbe(job, now);
                    break;
                case Phase.Open:
                    TickOpen(job, now);
                    break;
                case Phase.WaitListing:
                    TickWaitListing(job, now);
                    break;
                case Phase.VerifyPrice:
                    TickVerifyPrice(job, now);
                    break;
                case Phase.Settle:
                    TickSettle(job, now);
                    break;
            }
        }

        private void SetPhase(Job job, Phase phase, DateTime now)
        {
            job.Phase = phase;
            job.PhaseStartedUtc = now;
            WaitingFor = string.Empty;
        }

        private void TickProbe(Job job, DateTime now)
        {
            if (!SourceStillMatches(job, out var why))
            {
                Skip(job, why);
                return;
            }

            if (CheckMarketFull(now, job))
                return;

            job.Token = ++nextToken;
            var key = job.Pick.Slot.Key;
            if (engine.StartPriceProbe(job.Token, key.ItemId, key.Hq, job.Pick.Name))
            {
                SetPhase(job, Phase.WaitProbe, now);
                return;
            }

            WaitingFor = "waiting for the price engine: ??".Loc(engine.LastStartRefusalReason);
            if ((now - job.PhaseStartedUtc).TotalMilliseconds > ProbeStartTimeoutMs)
                Stop("the price check could not start (??)".Loc(engine.LastStartRefusalReason), BatchListEnd.Stopped);
        }

        private void TickWaitProbe(Job job, DateTime now)
        {
            if (!engine.TryTakeProbeResult(job.Token, out var result))
            {
                if ((now - job.PhaseStartedUtc).TotalMilliseconds > ProbeTimeoutMs)
                    Stop("the price check did not finish in time".Loc(), BatchListEnd.Stopped);
                return;
            }

            Log.Information(
                $"[BatchList] 查價：{Describe(job)} 結論={result.Outcome} 價={result.Price} 說明={result.Note}");
            switch (result.Outcome)
            {
                case PriceProbeOutcome.List:
                    job.Price = result.Price;
                    job.PriceNote = result.Note;
                    SetPhase(job, Phase.Open, now);
                    return;
                case PriceProbeOutcome.BelowVendor:
                case PriceProbeOutcome.BelowMin:
                    HandleBelowThreshold(job, result);
                    return;
                case PriceProbeOutcome.Aborted:
                    Stop(result.Note.Length > 0 ? result.Note : "the price check was cancelled".Loc(), BatchListEnd.Stopped);
                    return;
                default:
                    Skip(job, result.Note);
                    return;
            }
        }

        /// <summary>低於門檻：不上架。玩家背包的交給賣給商店的清單（只加進清單，不開始賣）；僱員物品欄的留著、只記摘要。</summary>
        private void HandleBelowThreshold(Job job, PriceProbeResult result)
        {
            var p = job.Pick.Slot;
            if (Source == BatchListSource.PlayerBags && vendorSeller != null)
            {
                var sheet = DataManager.GetExcelSheet<Item>();
                var priceLow = sheet != null && sheet.TryGetRow(p.Key.ItemId, out var row) ? row.PriceLow : 0u;
                var vendorUnit = priceLow == 0 ? 0u : p.Key.Hq ? priceLow + 1 : priceLow;
                if (vendorSeller.AddSortPicks([new VendorSeller.SortPick(p, job.Pick.Name, vendorUnit)]) > 0)
                {
                    VendorQueuedCount++;
                    Log.Information($"[BatchList] {Describe(job)} 低於門檻（{result.Note}），加進賣給商店的清單");
                    Advance();
                    return;
                }
            }

            BelowThresholdKept++;
            Skip(job, "below your threshold (??), left where it is".Loc(result.Note));
        }

        private void TickOpen(Job job, DateTime now)
        {
            if ((now - job.PhaseStartedUtc).TotalMilliseconds > OpenPhaseTimeoutMs)
            {
                Stop("could not open the right-click menu in time (??)".Loc(WaitingFor), BatchListEnd.Stopped);
                return;
            }

            if (KeysHeld(out var heldText))
            {
                WaitingFor = heldText;
                return;
            }

            var menu = Commons.GetUnitBase("ContextMenu");
            if (contextMenuHiddenFrames < ContextMenuHiddenFramesRequired ||
                (menu != null && !AddonPressGuard.CanPress("ContextMenu", (nint)menu, PressKind.Terminal)))
            {
                WaitingFor = "waiting for the right-click menu to close".Loc();
                return;
            }

            if (Commons.GetUnitBase("RetainerSell") != null)
            {
                WaitingFor = "waiting for the sale window to close".Loc();
                return;
            }

            WaitingFor = string.Empty;
            if (!SourceStillMatches(job, out var why))
            {
                Skip(job, why);
                return;
            }

            if (CheckMarketFull(now, job))
                return;

            var ownerAddonId = OwnerAddonId(Source);
            if (ownerAddonId == 0)
            {
                if (Source == BatchListSource.RetainerBags)
                    Stop("the retainer's inventory window is not open".Loc(), BatchListEnd.NextRetainer);
                else
                    Stop("open your inventory window".Loc(), BatchListEnd.Stopped);
                return;
            }

            var p = job.Pick.Slot;
            job.Token = ++nextToken;
            var result = quickLister!.OpenAndSelectPutUpForSale((InventoryType)p.Container, p.Slot, ownerAddonId,
                p.Key, p.Quantity, job.Price, job.Token, out var menuDump);
            // 選單在這一次呼叫裡開了又關，每幀的觀察看不到它：從這裡重新數隱藏幀。
            contextMenuHiddenFrames = 0;
            switch (result)
            {
                case VendorSellMenuResult.Fired:
                    Log.Information($"[BatchList] 已選「到市場出售」：{Describe(job)} 價={job.Price}（{job.PriceNote}）");
                    SetPhase(job, Phase.WaitListing, now);
                    return;
                case VendorSellMenuResult.SlotMismatch:
                    Skip(job, "the slot no longer matches what was planned".Loc());
                    return;
                case VendorSellMenuResult.EntryDisabled:
                    if (job.DisabledSinceUtc == DateTime.MinValue)
                        job.DisabledSinceUtc = now;
                    if ((now - job.DisabledSinceUtc).TotalMilliseconds <= DisabledEntryRetryMs)
                    {
                        WaitingFor = "\"Put Up for Sale\" is greyed out, trying again".Loc();
                        return;
                    }

                    Log.Information($"[BatchList] 右鍵選單內容：{menuDump}");
                    Stop("\"Put Up for Sale\" stayed greyed out in the right-click menu".Loc(), BatchListEnd.Stopped);
                    return;
                case VendorSellMenuResult.EntryMissing:
                    Log.Information($"[BatchList] 右鍵選單內容：{menuDump}");
                    Stop("the right-click menu has no \"Put Up for Sale\"".Loc(),
                        drivenByTour ? BatchListEnd.NextRetainer : BatchListEnd.Stopped);
                    return;
                default:
                    if (menuDump.Length > 0)
                        Log.Information($"[BatchList] 右鍵選單內容：{menuDump}");
                    Log.Information($"[BatchList] 選單結果={result}：{Describe(job)}");
                    Stop("the right-click menu opened but nothing was selected - close it and check".Loc(),
                        BatchListEnd.Stopped);
                    return;
            }
        }

        private void TickWaitListing(Job job, DateTime now)
        {
            if (!quickLister!.TryTakeBatchOutcome(job.Token, out var outcome))
            {
                if ((now - job.PhaseStartedUtc).TotalMilliseconds > ListingOutcomeTimeoutMs)
                    Stop("could not tell whether ?? went up for sale - check the sell list".Loc(job.Pick.Name),
                        BatchListEnd.Stopped);
                return;
            }

            Log.Information($"[BatchList] 上架結果：{Describe(job)} {outcome.State} 市場格={outcome.MarketSlot}");
            switch (outcome.State)
            {
                case BatchListingState.Listed:
                    BatchListRules.NoteWindowOutcome(ref refusalStreak, windowOpened: true);
                    job.MarketSlot = outcome.MarketSlot;
                    SetPhase(job, Phase.VerifyPrice, now);
                    return;
                case BatchListingState.WindowNotOpened:
                    if (BatchListRules.NoteWindowOutcome(ref refusalStreak, windowOpened: false))
                    {
                        Stop("the game refused ?? items in a row".Loc(BatchListRules.MaxConsecutiveRefusals),
                            BatchListEnd.Stopped);
                        return;
                    }

                    Skip(job, "the game did not open the sale window for it".Loc());
                    return;
                case BatchListingState.FillFailed:
                    Stop("could not fill in the price - the sale window is left to you".Loc(), BatchListEnd.Stopped);
                    return;
                case BatchListingState.LeftRetainer:
                    Stop("you moved to another retainer".Loc(), BatchListEnd.Stopped);
                    return;
                case BatchListingState.HandedToUser:
                    Stop("the sale window was handed to you".Loc(), BatchListEnd.Stopped);
                    return;
                default:
                    Stop("could not tell whether ?? went up for sale - check the sell list".Loc(job.Pick.Name),
                        BatchListEnd.Stopped);
                    return;
            }
        }

        /// <summary>市場格出現之後當幀讀價核對；價格欄可能比道具晚一點才更新，給一小段時間。</summary>
        private void TickVerifyPrice(Job job, DateTime now)
        {
            var inventoryManager = InventoryManager.Instance();
            var slot = RetainerMarketPrice.IsValidSlot(job.MarketSlot)
                ? QuickLister.ReadSlotChecked(InventoryType.RetainerMarket, job.MarketSlot)
                : null;
            var holdsItem = slot != null && slot->ItemId == job.Pick.Slot.Key.ItemId;
            if (holdsItem && RetainerMarketPrice.TryGet(inventoryManager, job.MarketSlot, out var price) &&
                price == job.Price)
            {
                ListedCount++;
                SetPhase(job, Phase.Settle, now);
                return;
            }

            if ((now - job.PhaseStartedUtc).TotalMilliseconds <= PriceVerifyMs)
                return;

            var seen = holdsItem && RetainerMarketPrice.TryGet(inventoryManager, job.MarketSlot, out var p) ? p.ToString("N0") : "?";
            Log.Information($"[BatchList] 價格核對不符：{Describe(job)} 市場格={job.MarketSlot} 預期={job.Price} 讀到={seen} 格子還是這件={holdsItem}");
            Stop("?? went up but its price reads ?? instead of ?? gil - check it".Loc(job.Pick.Name, seen, job.Price.ToString("N0")),
                BatchListEnd.Stopped);
        }

        private void TickSettle(Job job, DateTime now)
        {
            if (Commons.GetUnitBase("RetainerSell") == null && contextMenuHiddenFrames >= ContextMenuHiddenFramesRequired)
            {
                Advance();
                return;
            }

            WaitingFor = "waiting for the sale window to close".Loc();
            if ((now - job.PhaseStartedUtc).TotalMilliseconds > SettleTimeoutMs)
                Stop("the sale window did not close".Loc(), BatchListEnd.Stopped);
        }

        /// <summary>市場已滿＝這一批正常收工。讀不到容器就等（不猜）。回 true＝已處理（收工或在等）。</summary>
        private bool CheckMarketFull(DateTime now, Job job)
        {
            var full = quickLister!.TryReadFullMarket(out var occupied, out var inFlight, out var capacity);
            if (capacity == 0)
            {
                WaitingFor = "waiting for the retainer's market data".Loc();
                if ((now - job.PhaseStartedUtc).TotalMilliseconds > OpenPhaseTimeoutMs)
                    Stop("the retainer's market could not be read".Loc(), BatchListEnd.Stopped);
                return true;
            }

            if (!full)
                return false;
            Log.Information($"[BatchList] 市場已滿：已佔 {occupied}＋在途 {inFlight}／{capacity}");
            Stop("this retainer's market is full".Loc(), BatchListEnd.MarketFull);
            return true;
        }

        private bool SourceStillMatches(Job job, out string why)
        {
            why = string.Empty;
            var p = job.Pick.Slot;
            if (VendorSeller.TryReadSlot((InventoryType)p.Container, p.Slot, out var current) && current is { } c && c == p)
                return true;
            why = "the slot no longer matches what was planned".Loc();
            return false;
        }

        /// <summary>
        /// 別的插件（AutoRetainer 的快捷出售、TCToolbox 的轉移）也掛在同一支開選單的函式上，按住它們的鍵時會在同一個選單上選別的項目；
        /// 它們的鍵可以設成任何鍵（含滑鼠側鍵）⇒ 除了 Dalamud 看得到的修飾鍵，還要確認沒有任何實體按鍵按著。按著就等。
        /// </summary>
        private bool KeysHeld(out string what)
        {
            what = string.Empty;
            var code = conf.QuickListKeyCode;
            if (Keys[VirtualKey.SHIFT] || Keys[VirtualKey.CONTROL] || Keys[VirtualKey.MENU] ||
                (code != 0 && Keys.IsVirtualKeyValid(code) && Keys[code]))
            {
                what = "release Shift / Ctrl / Alt to carry on".Loc();
                return true;
            }

            var held = FirstHeldPhysicalKey();
            if (held == 0)
                return false;
            what = "release every key and mouse button to carry on (??)".Loc((VirtualKey)held);
            return true;
        }

        /// <summary>第一個實體上按著的虛擬鍵碼（含滑鼠鍵）；都沒按回 0。只看最高位元（現在按著），不看切換狀態。</summary>
        private static int FirstHeldPhysicalKey()
        {
            for (var vk = 0x01; vk <= 0xFE; vk++)
            {
                if ((GetAsyncKeyState(vk) & 0x8000) != 0)
                    return vk;
            }

            return 0;
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private static uint OwnerAddonId(BatchListSource source) => source == BatchListSource.PlayerBags
            ? VendorSeller.GetInventoryAddonId()
            : VendorSeller.GetRetainerInventoryAddonId();

        private void Skip(Job job, string why)
        {
            Skipped.Add((job.Pick.Name, why));
            Log.Information($"[BatchList] 跳過 {Describe(job)}：{why}");
            Advance();
        }

        private void Advance()
        {
            jobIndex++;
            WaitingFor = string.Empty;
            if (jobIndex >= jobs.Count)
            {
                Finish();
                return;
            }

            jobs[jobIndex].PhaseStartedUtc = DateTime.UtcNow;
        }

        private void Finish()
        {
            IsRunning = false;
            End = BatchListEnd.Finished;
            EndReason = string.Empty;
            ReleaseSuppressionIfHeld();
            Log.Information($"[BatchList] 完成：上架 {ListedCount}、改賣商店 {VendorQueuedCount}、跳過 {SkippedCount}");
            if (!drivenByTour)
                ChatGui.Print("[Marketbuddy] Batch listing finished: ?? listed, ?? added to the shop list, ?? skipped"
                    .Loc(ListedCount, VendorQueuedCount, SkippedCount));
        }

        private void Stop(string reason, BatchListEnd end)
        {
            if (!IsRunning)
                return;
            IsRunning = false;
            End = end;
            EndReason = reason;
            WaitingFor = string.Empty;
            // 在途那一件：還沒按的不再替使用者按，窗還在就交給他；查價中就一起停。
            if (jobIndex < jobs.Count && jobs[jobIndex].Token != 0)
                quickLister?.ReleaseBatch(jobs[jobIndex].Token);
            engine.CancelProbe();
            ReleaseSuppressionIfHeld();
            var left = Math.Max(0, jobs.Count - jobIndex);
            Log.Information(
                $"[BatchList] 停止（{reason}，{end}）：上架 {ListedCount}、改賣商店 {VendorQueuedCount}、跳過 {SkippedCount}、剩 {left} 疊沒處理");
            if (end == BatchListEnd.MarketFull)
            {
                if (!drivenByTour)
                    ChatGui.Print("[Marketbuddy] Batch listing stopped: this retainer's market is full (?? listed, ?? added to the shop list, ?? skipped)"
                        .Loc(ListedCount, VendorQueuedCount, SkippedCount));
                return;
            }

            ChatGui.PrintError("[Marketbuddy] Batch listing stopped: ?? (?? listed, ?? added to the shop list, ?? skipped)"
                .Loc(reason, ListedCount, VendorQueuedCount, SkippedCount));
        }

        private void ReleaseSuppressionIfHeld()
        {
            if (!suppressionHeld)
                return;
            suppressionHeld = false;
            AutoRetainerBridge.ReleaseSuppression();
        }

        private static string Describe(Job job)
            => $"{(InventoryType)job.Pick.Slot.Container}#{job.Pick.Slot.Slot} {job.Pick.Name}(#{job.Pick.Slot.Key.ItemId}) x{job.Pick.Slot.Quantity}";
    }
}
