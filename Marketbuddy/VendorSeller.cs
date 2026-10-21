using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Marketbuddy.Common;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    /// <summary>
    /// 重掛時因為「低於 NPC 收購價／低於自訂門檻」而下架回背包的東西，交給僱員賣給商店。
    /// 🔴 市場自動化紅線：下架那一端只記帳；真的賣一定要使用者按按鈕（兩段式確認），沒有任何接手鏈。
    /// 🔴 只賣 <see cref="VendorSellPlanner"/> 判成可賣的格子，每一件賣之前當場重查；
    /// 選單一律走 <see cref="QuickLister.OpenAndSelectVendorSell"/>（與快速上架同一段守衛與送出路徑）。
    /// 🔴 只從 framework／繪製執行緒呼叫。不跨幀保存任何原生指標：存的是容器＋格號，每一步重新取。
    /// </summary>
    internal sealed unsafe class VendorSeller : IDisposable
    {
        /// <summary>最後一次下架之後這麼久之內，還沒回到背包的量算「在路上」（與下架引擎等伺服器的上限同值）。</summary>
        private static readonly TimeSpan SettleWindow = TimeSpan.FromSeconds(20);

        /// <summary>距離上一次下架／賣出超過這麼久，下一次下架就開新的一輪，舊的整份作廢。</summary>
        private static readonly TimeSpan RoundIdleLimit = TimeSpan.FromMinutes(30);

        private const int PlanRefreshMs = 500;

        /// <summary>
        /// 右鍵選單要連續隱藏這麼多幀才開下一個：與 AutoRetainer 快速出售的守衛同值，
        /// 比本外掛自己的重按守衛（看到一幀隱藏就解除）保守。
        /// </summary>
        private const int ContextMenuHiddenFramesRequired = 20;

        private const int OpenPhaseTimeoutMs = 30000;
        private const int SaleAckTimeoutMs = 10000;

        private static readonly InventoryType[] BagContainers =
        [
            InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
        ];

        private static readonly InventoryType[] ArmoryContainers =
        [
            InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead,
            InventoryType.ArmoryBody, InventoryType.ArmoryHands, InventoryType.ArmoryLegs,
            InventoryType.ArmoryFeets, InventoryType.ArmoryEar, InventoryType.ArmoryNeck,
            InventoryType.ArmoryWrist, InventoryType.ArmoryRings,
        ];

        private static readonly string[] InventoryAddonNames = ["InventoryExpansion", "InventoryLarge", "Inventory"];

        private sealed class Round
        {
            public required ulong ContentId { get; init; }
            public readonly VendorSellBaselines Baselines = new();
            public required DateTime StartedUtc { get; init; }
            public DateTime LastActivityUtc;
            public readonly List<VendorSellDelist> Delists = new();
            public readonly Dictionary<VendorSellKey, int> Sold = new();
            public readonly HashSet<VendorSellKey> Excluded = new();

            /// <summary>動到 <see cref="Delists"/> 或 <see cref="Sold"/> 就 +1（重算判斷「輸入有沒有變」用）。</summary>
            public int Version;
        }

        private enum JobPhase
        {
            Open,
            WaitSale,
        }

        private sealed class SellJob
        {
            public required VendorSellSlot Planned { get; init; }
            public required string Name { get; init; }
            public required uint VendorUnitPrice { get; init; }

            /// <summary>來自背包分揀（使用者勾的背包原有堆疊），不是這一輪下架回來的。</summary>
            public bool FromSort { get; init; }

            public JobPhase Phase = JobPhase.Open;
            public DateTime PhaseStartedUtc;
        }

        /// <summary>背包分揀交過來的一格：交接當下讀到的樣子，賣之前那一格要逐項一模一樣才賣。</summary>
        internal readonly record struct SortPick(VendorSellSlot Slot, string Name, uint VendorUnitPrice);

        private readonly MarketGuiEventHandler gui;
        private readonly BatchReprice reprice;
        private readonly BatchDelist delist;
        private readonly MultiRetainerTour tour;
        private readonly QuickLister? quickLister;

        private Round? round;
        private List<VendorSellVerdict> plan = new();
        private DateTime planRefreshedAt = DateTime.MinValue;

        // 上一次真的重算時讀到的背包與輸入：讀到的一模一樣就沿用那一份 plan（兩份清單輪流用，不每次配置）。
        private List<uint> readLoaded = new();
        private List<VendorSellSlot> readSlots = new();
        private List<uint> evaluatedLoaded = new();
        private List<VendorSellSlot> evaluatedSlots = new();
        private Round? evaluatedRound;
        private int evaluatedVersion;
        private List<VendorSellVerdict>? evaluatedPlan;
        private DateTime evaluatedUtc;

        // 重算時的「現在」那一份背包；Evaluate 不留住它，所以每次重算沿用同一份。
        private readonly VendorSellSnapshot liveRead = new();

        // 下架前讀整份背包用（Stage 只抄走那一種道具的格子，不留住這兩份）。
        private readonly List<uint> baselineLoaded = new();
        private readonly List<VendorSellSlot> baselineSlots = new();

        /// <summary>上一次寫進記錄檔的計畫摘要；一樣就不再寫。</summary>
        private VendorSellPlanSummary? loggedSummary;

        private readonly List<SellJob> jobs = new();
        private int jobIndex;
        private int contextMenuHiddenFrames;
        private bool suppressionHeld;

        private readonly List<SortPick> sortPicks = new();
        private readonly HashSet<VendorSellKey> sortExcluded = new();
        private ulong sortContentId;
        private ulong runContentId;
        private bool runHasRoundJobs;

        private Configuration conf => Configuration.GetOrLoad();

        public bool IsRunning { get; private set; }
        public int TotalJobs => jobs.Count;
        public int FinishedJobs => Math.Min(jobIndex, jobs.Count);
        public string CurrentItemName => IsRunning && jobIndex < jobs.Count ? jobs[jobIndex].Name : string.Empty;

        /// <summary>執行中卡在哪個前置條件上（給面板顯示）；空字串＝沒有卡住。</summary>
        public string WaitingFor { get; private set; } = string.Empty;

        public int SoldStacks { get; private set; }
        public long SoldGil { get; private set; }
        public int SkippedStacks { get; private set; }

        public IReadOnlyList<VendorSellVerdict> Plan => plan;
        public DateTime? RoundStartedUtc => round?.StartedUtc;
        public bool IsAvailable => quickLister?.CanVendorSell == true;

        /// <summary>面板要不要畫這一段：正在賣、背包分揀交了東西過來，或功能開著而這一輪還有東西可列。</summary>
        public bool ShouldShow => IsRunning || sortPicks.Count > 0 ||
                                  (conf.VendorSellAfterDelist && round != null &&
                                   plan.Exists(v => v.State != VendorSellState.Done));

        public IReadOnlyList<SortPick> SortPicks => sortPicks;

        /// <summary><see cref="SortPicks"/> 每變一次 +1（面板判斷要不要重組列文字用）。</summary>
        public int SortVersion { get; private set; }

        public VendorSeller(MarketGuiEventHandler gui, BatchReprice reprice, BatchDelist delist,
            MultiRetainerTour tour, QuickLister? quickLister)
        {
            this.gui = gui;
            this.reprice = reprice;
            this.delist = delist;
            this.tour = tour;
            this.quickLister = quickLister;
            Framework.Update += OnFrameworkUpdate;
        }

        public void Dispose()
        {
            Framework.Update -= OnFrameworkUpdate;
            IsRunning = false;
            jobs.Clear();
            sortPicks.Clear();
            ReleaseSuppressionIfHeld();
        }

        // ------------------------------------------------------------------ 背包分揀交接

        /// <summary>
        /// 整份換成背包分揀勾選的格子（只換清單，不開始賣）。正在賣時不收。回傳收下幾格。
        /// 🔴 賣的是背包原有的堆疊：每一格送出前都要與這裡記下的樣子逐項相同（道具、品質、數量、指紋），否則跳過。
        /// </summary>
        public int SetSortPicks(IReadOnlyList<SortPick> picks)
        {
            if (IsRunning)
                return 0;
            sortPicks.Clear();
            sortExcluded.Clear();
            SortVersion++;
            sortContentId = PlayerState.ContentId;
            foreach (var pick in picks)
            {
                if (pick.VendorUnitPrice == 0 || pick.Slot.NeedsConfirmation || pick.Slot.Armory ||
                    pick.Slot.Key.ItemId == 0 || pick.Slot.Key.ItemId == uint.MaxValue)
                    continue;
                sortPicks.Add(pick);
            }

            Log.Information($"[Marketbuddy] VendorSell: 背包分揀交來 {sortPicks.Count} 格（勾選 {picks.Count} 格）");
            return sortPicks.Count;
        }

        public void ClearSortPicks()
        {
            if (IsRunning)
                return;
            sortPicks.Clear();
            sortExcluded.Clear();
            SortVersion++;
        }

        public bool IsSortExcluded(VendorSellKey key) => sortExcluded.Contains(key);

        public void SetSortExcluded(VendorSellKey key, bool excluded)
        {
            if (IsRunning)
                return;
            if (excluded)
                sortExcluded.Add(key);
            else
                sortExcluded.Remove(key);
        }

        private bool IsRoundSellableSlot(VendorSellSlot slot)
        {
            foreach (var v in plan)
            {
                if (v.State != VendorSellState.Sellable || v.Key != slot.Key || IsExcluded(v.Key))
                    continue;
                foreach (var s in v.NewSlots)
                {
                    if (s.Container == slot.Container && s.Slot == slot.Slot)
                        return true;
                }
            }

            return false;
        }

        /// <summary>這一格這一次會賣：沒取消勾選、而且不是這一輪下架那邊已經會賣的同一格。</summary>
        private bool IsActiveSortPick(SortPick pick)
            => !sortExcluded.Contains(pick.Slot.Key) && !IsRoundSellableSlot(pick.Slot);

        // ------------------------------------------------------------------ 下架端的記帳

        /// <summary>
        /// <see cref="BatchReprice"/> 在「送出下架回背包」之前呼叫。功能開著就確保有一輪；
        /// 這種道具在這一輪還沒有「下架前」背包就當場讀一份暫存（每種道具各自一份）。回 true＝這一次下架要記帳。
        /// </summary>
        internal bool NoteBeforeDelist(uint itemId, bool hq)
        {
            if (!conf.VendorSellAfterDelist || IsRunning)
                return false;

            var contentId = PlayerState.ContentId;
            if (contentId == 0)
                return false;

            var key = new VendorSellKey(itemId, hq);
            var now = DateTime.UtcNow;
            var sameRound = round != null && round.ContentId == contentId && now - round.LastActivityUtc < RoundIdleLimit;
            if (sameRound && round!.Baselines.Has(key))
                return true;

            var inventoryManager = InventoryManager.Instance();
            if (inventoryManager == null)
                return false;
            baselineLoaded.Clear();
            baselineSlots.Clear();
            Capture(inventoryManager, BagContainers, false, baselineLoaded, baselineSlots);
            Capture(inventoryManager, ArmoryContainers, true, baselineLoaded, baselineSlots);

            if (!sameRound)
            {
                round = new Round { ContentId = contentId, StartedUtc = now, LastActivityUtc = now };
                plan = new List<VendorSellVerdict>();
                loggedSummary = null;
                Log.Information(
                    $"[Marketbuddy] VendorSell: 開始新的一輪，每種道具在這一輪第一次下架前各記一次背包（現在 {baselineSlots.Count} 格有東西）");
            }

            round!.Baselines.Stage(key, baselineLoaded, baselineSlots);
            return true;
        }

        /// <summary>下架被遊戲收下（回傳 0）之後呼叫。</summary>
        internal void NoteDelisted(uint itemId, bool hq, int quantity, VendorSellCause cause,
            uint vendorUnitPrice, string name)
        {
            if (round == null)
                return;

            var key = new VendorSellKey(itemId, hq);
            var hadBaseline = round.Baselines.Has(key);
            if (!round.Baselines.Commit(key))
            {
                MarketDiag.Trace($"[Marketbuddy] VendorSell: {name} 沒有下架前的背包，這一次下架不記帳");
                return;
            }

            var sheet = DataManager.GetExcelSheet<Item>();
            var unique = sheet != null && sheet.TryGetRow(itemId, out var row) && row.IsUnique;
            var now = DateTime.UtcNow;
            round.Delists.Add(new VendorSellDelist(key, quantity, cause,
                vendorUnitPrice, unique, name, now));
            round.Version++;
            round.LastActivityUtc = now;
            planRefreshedAt = DateTime.MinValue;
            MarketDiag.Trace($"[Marketbuddy] VendorSell: 記下 {name} x{quantity}（{cause}，NPC 單價 {vendorUnitPrice}，" +
                             (hadBaseline ? "沿用這一輪的下架前背包）" : "剛記下這種道具的下架前背包）"));
        }

        // ------------------------------------------------------------------ 面板用

        public bool IsExcluded(VendorSellKey key) => round?.Excluded.Contains(key) == true;

        public void SetExcluded(VendorSellKey key, bool excluded)
        {
            if (round == null || IsRunning)
                return;
            if (excluded)
                round.Excluded.Add(key);
            else
                round.Excluded.Remove(key);
        }

        public void ClearRound()
        {
            if (IsRunning)
                return;
            round = null;
            plan = new List<VendorSellVerdict>();
            Log.Information("[Marketbuddy] VendorSell: 使用者清除了這一輪的清單");
        }

        /// <summary>勾選中、可以直接賣的格數與預估金額。</summary>
        public (int Stacks, long Gil) SellableSummary()
        {
            var stacks = 0;
            var gil = 0L;
            foreach (var v in plan)
            {
                if (v.State != VendorSellState.Sellable || IsExcluded(v.Key))
                    continue;
                stacks += v.NewSlots.Count;
                gil += v.EstimatedGil;
            }

            foreach (var pick in sortPicks)
            {
                if (!IsActiveSortPick(pick))
                    continue;
                stacks++;
                gil += (long)pick.Slot.Quantity * pick.VendorUnitPrice;
            }

            return (stacks, gil);
        }

        public int CountState(VendorSellState state)
        {
            var n = 0;
            foreach (var v in plan)
            {
                if (v.State == state)
                    n++;
            }

            return n;
        }

        public bool CanStart(out string reason)
        {
            reason = string.Empty;
            if (IsRunning)
                return false;
            if (!IsAvailable)
            {
                reason = "the right-click menu hook is not available".Loc();
                return false;
            }

            if (round == null && sortPicks.Count == 0)
            {
                reason = "nothing has been delisted into your bags this round".Loc();
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

            if (OtherTaskRunning())
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

            if (GetInventoryAddonId() == 0)
            {
                reason = "open your inventory window".Loc();
                return false;
            }

            if (SellableSummary().Stacks == 0)
            {
                reason = "nothing is ticked to sell".Loc();
                return false;
            }

            return true;
        }

        public void Start()
        {
            if (!CanStart(out var reason))
            {
                if (AutoRetainerBridge.IsBusy)
                {
                    AutoRetainerBridge.ArmAvailabilityNotice();
                    ChatGui.PrintError("[Marketbuddy] AutoRetainer is running - this action was skipped. You will be told when it finishes.".Loc());
                    return;
                }

                if (reason.Length > 0)
                    ChatGui.PrintError("[Marketbuddy] Cannot start: ??".Loc(reason));
                return;
            }

            // 按下去的這一刻重算一次：面板上的清單最多是半秒前的。
            RefreshPlan();
            jobs.Clear();
            var gil = 0L;
            foreach (var v in plan)
            {
                if (v.State != VendorSellState.Sellable || IsExcluded(v.Key))
                    continue;
                foreach (var slot in v.NewSlots)
                {
                    jobs.Add(new SellJob { Planned = slot, Name = v.Name, VendorUnitPrice = v.VendorUnitPrice });
                    gil += (long)slot.Quantity * v.VendorUnitPrice;
                }
            }

            runHasRoundJobs = jobs.Count > 0;
            var fromSort = 0;
            foreach (var pick in sortPicks)
            {
                if (!IsActiveSortPick(pick))
                    continue;
                jobs.Add(new SellJob
                {
                    Planned = pick.Slot, Name = pick.Name, VendorUnitPrice = pick.VendorUnitPrice, FromSort = true,
                });
                gil += (long)pick.Slot.Quantity * pick.VendorUnitPrice;
                fromSort++;
            }

            if (jobs.Count == 0)
            {
                ChatGui.PrintError("[Marketbuddy] Cannot start: ??".Loc("nothing is ticked to sell".Loc()));
                return;
            }

            jobIndex = 0;
            SoldStacks = 0;
            SoldGil = 0;
            SkippedStacks = 0;
            WaitingFor = string.Empty;
            contextMenuHiddenFrames = 0;
            jobs[0].PhaseStartedUtc = DateTime.UtcNow;
            runContentId = PlayerState.ContentId;
            IsRunning = true;
            AutoRetainerBridge.AcquireSuppression("vendor sell");
            suppressionHeld = true;

            Log.Information(
                $"[Marketbuddy] VendorSell: 開始交給僱員賣給商店，{jobs.Count} 格（其中背包分揀 {fromSort} 格），預估 {gil:N0} gil");
            ChatGui.Print("[Marketbuddy] Having the retainer sell ?? stack(s) to the shop (about ?? gil)..."
                .Loc(jobs.Count, gil.ToString("N0")));
        }

        public void CancelByButton() => Stop("cancelled by user".Loc());

        // ------------------------------------------------------------------ 每幀

        private void OnFrameworkUpdate(IFramework framework)
        {
            var contentId = PlayerState.ContentId;
            var roundStale = round != null && contentId != round.ContentId;
            var sortStale = sortPicks.Count > 0 && contentId != sortContentId;
            if (roundStale || sortStale || (IsRunning && contentId != runContentId))
            {
                if (IsRunning)
                    Stop("you switched characters".Loc());
                if (roundStale)
                {
                    round = null;
                    plan = new List<VendorSellVerdict>();
                }

                if (sortStale)
                    ClearSortPicks();
                return;
            }

            if (IsRunning)
            {
                TrackContextMenu();
                TickRun();
                return;
            }

            if (round == null || !conf.VendorSellAfterDelist || !gui.IsRetainerSellListOpen)
                return;
            if ((DateTime.UtcNow - planRefreshedAt).TotalMilliseconds >= PlanRefreshMs)
                RefreshPlan(force: false);
        }

        /// <param name="force">false＝背包、這一輪的輸入都和上一次重算時一樣、而且兩個時刻都已過了等待期，就沿用上一份。</param>
        private void RefreshPlan(bool force = true)
        {
            planRefreshedAt = DateTime.UtcNow;
            if (round == null)
            {
                plan = new List<VendorSellVerdict>();
                return;
            }

            var inventoryManager = InventoryManager.Instance();
            if (inventoryManager == null)
                return;
            readLoaded.Clear();
            readSlots.Clear();
            Capture(inventoryManager, BagContainers, false, readLoaded, readSlots);
            Capture(inventoryManager, ArmoryContainers, true, readLoaded, readSlots);

            var nowUtc = DateTime.UtcNow;
            if (!force && ReferenceEquals(plan, evaluatedPlan) && ReferenceEquals(round, evaluatedRound) &&
                round.Version == evaluatedVersion && PastSettleWindow(round, evaluatedUtc) &&
                PastSettleWindow(round, nowUtc) && SameRead())
                return;

            liveRead.Refill(readLoaded, readSlots);
            plan = VendorSellPlanner.Evaluate(round.Baselines.BeforeOf, liveRead,
                round.Delists, round.Sold, nowUtc, SettleWindow);
            evaluatedPlan = plan;
            evaluatedRound = round;
            evaluatedVersion = round.Version;
            evaluatedUtc = nowUtc;
            (evaluatedLoaded, readLoaded) = (readLoaded, evaluatedLoaded);
            (evaluatedSlots, readSlots) = (readSlots, evaluatedSlots);
            LogSummaryIfChanged(round, nowUtc);
        }

        /// <summary>
        /// 計畫穩定下來（不在賣、每一件都過了等待期）而且摘要和上一次寫的不同，才寫一行；
        /// 連續快速上架時每件都會改到計畫，等停手才寫，所以不會變成一件一行。
        /// </summary>
        private void LogSummaryIfChanged(Round r, DateTime nowUtc)
        {
            if (IsRunning || !PastSettleWindow(r, nowUtc))
                return;
            var summary = VendorSellPlanner.Summarize(plan);
            if (summary == loggedSummary || (summary.Sellable == 0 && summary.Manual == 0))
                return;
            loggedSummary = summary;
            var top = summary.Manual > 0 ? $"，最多的原因：{summary.TopReason}（{summary.TopReasonCount} 種）" : string.Empty;
            Log.Information(
                $"[Marketbuddy] VendorSell: 賣給商店的清單：可賣 {summary.Sellable} 種（{summary.Stacks} 格，約 {summary.Gil:N0} gil）、" +
                $"需要手動 {summary.Manual} 種{top}");
        }

        /// <summary>Evaluate 裡唯一看時鐘的判斷是「離該件最後一次下架未滿等待期」；全部都滿了，換哪個時刻算結果都一樣。</summary>
        private static bool PastSettleWindow(Round r, DateTime atUtc)
        {
            foreach (var d in r.Delists)
            {
                if (atUtc - d.AtUtc < SettleWindow)
                    return false;
            }

            return true;
        }

        private bool SameRead()
        {
            if (readLoaded.Count != evaluatedLoaded.Count || readSlots.Count != evaluatedSlots.Count)
                return false;
            for (var i = 0; i < readLoaded.Count; i++)
            {
                if (readLoaded[i] != evaluatedLoaded[i])
                    return false;
            }

            for (var i = 0; i < readSlots.Count; i++)
            {
                if (readSlots[i] != evaluatedSlots[i])
                    return false;
            }

            return true;
        }

        private void TrackContextMenu()
        {
            var menu = Commons.GetUnitBase("ContextMenu");
            if (menu != null && menu->IsVisible)
                contextMenuHiddenFrames = 0;
            else if (contextMenuHiddenFrames < int.MaxValue)
                contextMenuHiddenFrames++;
        }

        private void TickRun()
        {
            var blocked = RunBlockedReason();
            if (blocked != null)
            {
                Stop(blocked);
                return;
            }

            var job = jobs[jobIndex];
            var now = DateTime.UtcNow;
            if (job.Phase == JobPhase.Open)
                TickOpen(job, now);
            else
                TickWaitSale(job, now);
        }

        private void TickOpen(SellJob job, DateTime now)
        {
            if ((now - job.PhaseStartedUtc).TotalMilliseconds > OpenPhaseTimeoutMs)
            {
                Stop("could not open the right-click menu in time (??)".Loc(WaitingFor));
                return;
            }

            // 按鍵與選單這兩道是「等」不是「停」：放開按鍵、選單收好，下一幀自然會接著跑。
            if (ModifierHeld())
            {
                WaitingFor = "release Shift / Ctrl / Alt to carry on".Loc();
                return;
            }

            var menu = Commons.GetUnitBase("ContextMenu");
            if (contextMenuHiddenFrames < ContextMenuHiddenFramesRequired ||
                (menu != null && !AddonPressGuard.CanPress("ContextMenu", (nint)menu, PressKind.Terminal)))
            {
                WaitingFor = "waiting for the right-click menu to close".Loc();
                return;
            }

            if (AddonHelpers.GetReadyAddon("SelectYesno") != null)
            {
                Stop("a yes/no box is open - answer it yourself".Loc());
                return;
            }

            WaitingFor = string.Empty;

            // 開選單前的最後一次核對：整份重算，而且那一格要逐項對上計畫。
            RefreshPlan();
            if (!StillPlanned(job, out var why))
            {
                Skip(job, why);
                return;
            }

            var ownerAddonId = GetInventoryAddonId();
            if (ownerAddonId == 0)
            {
                Stop("open your inventory window".Loc());
                return;
            }

            var p = job.Planned;
            var result = quickLister!.OpenAndSelectVendorSell((InventoryType)p.Container, p.Slot, ownerAddonId,
                p.Key, p.Quantity, out var menuDump);
            // 選單在這一次呼叫裡開了又關，每幀的觀察看不到它：從這裡重新數隱藏幀。
            contextMenuHiddenFrames = 0;
            switch (result)
            {
                case VendorSellMenuResult.Fired:
                    job.Phase = JobPhase.WaitSale;
                    job.PhaseStartedUtc = now;
                    Log.Information(
                        $"[Marketbuddy] VendorSell: 已選「委託僱員出售物品」：{DescribeSlot(p)} {job.Name} x{p.Quantity}");
                    return;
                case VendorSellMenuResult.SlotMismatch:
                    Skip(job, "the slot no longer matches what was planned".Loc());
                    return;
                default:
                    if (menuDump.Length > 0)
                        Log.Information($"[Marketbuddy] VendorSell: 右鍵選單內容：{menuDump}");
                    Stop(DescribeMenuResult(result));
                    return;
            }
        }

        private void TickWaitSale(SellJob job, DateTime now)
        {
            if (AddonHelpers.GetReadyAddon("SelectYesno") != null)
            {
                Stop("the game asked for confirmation - answer it yourself".Loc());
                return;
            }

            var p = job.Planned;
            if (!TryReadSlot((InventoryType)p.Container, p.Slot, out var current))
            {
                // 讀不到不等於賣掉了：照樣等，時限到了再停。
                if ((now - job.PhaseStartedUtc).TotalMilliseconds > SaleAckTimeoutMs)
                    Stop("the sale of ?? was not confirmed in time".Loc(job.Name));
                return;
            }

            if (current == null)
            {
                if (job.FromSort)
                    RemoveSortPick(p);
                else if (round != null)
                {
                    round.Sold[p.Key] = (round.Sold.TryGetValue(p.Key, out var s) ? s : 0) + p.Quantity;
                    round.Version++;
                    round.LastActivityUtc = now;
                }

                SoldStacks++;
                SoldGil += (long)p.Quantity * job.VendorUnitPrice;
                Log.Information($"[Marketbuddy] VendorSell: 已賣出 {DescribeSlot(p)} {job.Name} x{p.Quantity}");
                Advance();
                return;
            }

            if (current.Value != p)
            {
                // 那一格變成別的樣子而不是變空：分不出賣了沒有，照實停手，不記成賣出。
                Stop("that slot changed while waiting for the sale (??) - check it".Loc(job.Name));
                return;
            }

            if ((now - job.PhaseStartedUtc).TotalMilliseconds > SaleAckTimeoutMs)
                Stop("the sale of ?? was not confirmed in time".Loc(job.Name));
        }

        private bool StillPlanned(SellJob job, out string why)
        {
            why = string.Empty;
            var p = job.Planned;
            if (job.FromSort)
            {
                // 背包原有的堆疊沒有「這一輪下架量」可以對：那一格現在要和交接當下記的一模一樣。
                if (TryReadSlot((InventoryType)p.Container, p.Slot, out var current) && current is { } c && c == p)
                    return true;
                why = "the slot no longer matches what was planned".Loc();
                return false;
            }

            foreach (var v in plan)
            {
                if (v.Key != p.Key)
                    continue;
                if (v.State != VendorSellState.Sellable)
                {
                    why = "it is no longer safe to sell (??)".Loc(ManualReasonShort(v.Reason, v.State));
                    return false;
                }

                foreach (var slot in v.NewSlots)
                {
                    if (slot == p)
                        return true;
                }

                break;
            }

            why = "the slot no longer matches what was planned".Loc();
            return false;
        }

        private void RemoveSortPick(VendorSellSlot slot)
        {
            for (var i = sortPicks.Count - 1; i >= 0; i--)
            {
                if (sortPicks[i].Slot == slot)
                    sortPicks.RemoveAt(i);
            }

            SortVersion++;
        }

        private void Skip(SellJob job, string why)
        {
            if (job.FromSort)
                RemoveSortPick(job.Planned);
            SkippedStacks++;
            Log.Information($"[Marketbuddy] VendorSell: 跳過 {DescribeSlot(job.Planned)} {job.Name}：{why}");
            ChatGui.PrintError("[Marketbuddy] ??: skipped - ??".Loc(job.Name, why));
            Advance();
        }

        private void Advance()
        {
            jobIndex++;
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
            WaitingFor = string.Empty;
            ReleaseSuppressionIfHeld();
            planRefreshedAt = DateTime.MinValue;
            Log.Information(
                $"[Marketbuddy] VendorSell: 完成，賣出 {SoldStacks} 格（約 {SoldGil:N0} gil），跳過 {SkippedStacks} 格");
            ChatGui.Print("[Marketbuddy] Sold to the shop: ?? stack(s) (about ?? gil), ?? skipped"
                .Loc(SoldStacks, SoldGil.ToString("N0"), SkippedStacks));
        }

        private void Stop(string reason)
        {
            if (!IsRunning)
                return;
            IsRunning = false;
            WaitingFor = string.Empty;
            ReleaseSuppressionIfHeld();
            planRefreshedAt = DateTime.MinValue;
            Log.Information(
                $"[Marketbuddy] VendorSell: 中止（{reason}），賣出 {SoldStacks} 格（約 {SoldGil:N0} gil），跳過 {SkippedStacks} 格，" +
                $"剩 {Math.Max(0, jobs.Count - jobIndex)} 格沒處理");
            ChatGui.PrintError("[Marketbuddy] Selling to the shop stopped: ?? (?? sold, about ?? gil, ?? skipped)"
                .Loc(reason, SoldStacks, SoldGil.ToString("N0"), SkippedStacks));
        }

        private void ReleaseSuppressionIfHeld()
        {
            if (!suppressionHeld)
                return;
            suppressionHeld = false;
            AutoRetainerBridge.ReleaseSuppression();
        }

        private string? RunBlockedReason()
        {
            if (runHasRoundJobs && !conf.VendorSellAfterDelist)
                return "the feature was turned off".Loc();
            if (!gui.IsRetainerSellListOpen)
                return "the retainer sell list was closed".Loc();
            if (Keys[VirtualKey.ESCAPE])
                return "ESC pressed".Loc();
            if (IPCManager.IsLocked)
                return "locked via IPC by another plugin".Loc();
            if (AutoRetainerBridge.IsBusy)
                return "AutoRetainer became busy".Loc();
            if (Commons.GetUnitBase("RetainerSell") != null)
                return "manual price adjustment detected".Loc();
            if (OtherTaskRunning())
                return "another Marketbuddy task started".Loc();
            return null;
        }

        private bool OtherTaskRunning()
            => reprice.IsRunning || delist.IsRunning || tour.IsRunning || gui.Survey?.IsRunning == true ||
               (quickLister?.PendingCount ?? 0) > 0;

        /// <summary>
        /// 快速上架（本外掛）與 AutoRetainer／TCToolbox 的快捷右鍵都掛在同一支開選單的函式上，
        /// 按住修飾鍵時它們會搶著替這個選單選別的項目 —— 所以按著就等。
        /// </summary>
        private bool ModifierHeld()
        {
            if (Keys[VirtualKey.SHIFT] || Keys[VirtualKey.CONTROL] || Keys[VirtualKey.MENU])
                return true;
            var code = conf.QuickListKeyCode;
            return code != 0 && Keys.IsVirtualKeyValid(code) && Keys[code];
        }

        // ------------------------------------------------------------------ 讀背包

        /// <summary>讀四個背包（不含兵裝庫）給背包分揀用，格子的樣子與賣之前的核對用同一套。🔴 framework 執行緒限定。</summary>
        internal static void CaptureBags(List<uint> loaded, List<VendorSellSlot> slots)
        {
            var inventoryManager = InventoryManager.Instance();
            if (inventoryManager != null)
                Capture(inventoryManager, BagContainers, false, loaded, slots);
        }

        private static void Capture(InventoryManager* inventoryManager, InventoryType[] types, bool armory,
            List<uint> loaded, List<VendorSellSlot> slots)
        {
            foreach (var type in types)
            {
                var container = inventoryManager->GetInventoryContainer(type);
                // 判 Items 而不是 GetInventorySlot 的回傳值：Items 為 null 時它回的是非 null 的假指標。
                if (container == null || !container->IsLoaded || container->Items == null)
                    continue;

                loaded.Add((uint)type);
                for (var i = 0; i < container->Size; i++)
                {
                    var item = container->GetInventorySlot(i);
                    if (item == null || item->ItemId == 0)
                        continue;
                    slots.Add(ToSlot(type, (short)i, item, armory));
                }
            }
        }

        /// <summary>讀某一格現在的內容。回 false＝這一刻讀不到；回 true 而 <paramref name="slot"/> 為 null＝空格。</summary>
        private static bool TryReadSlot(InventoryType type, short index, out VendorSellSlot? slot)
        {
            slot = null;
            var inventoryManager = InventoryManager.Instance();
            var container = inventoryManager == null ? null : inventoryManager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded || container->Items == null ||
                index < 0 || index >= container->Size)
                return false;
            var item = container->GetInventorySlot(index);
            if (item == null)
                return false;
            if (item->ItemId != 0)
                slot = ToSlot(type, index, item, Array.IndexOf(ArmoryContainers, type) >= 0);
            return true;
        }

        private static VendorSellSlot ToSlot(InventoryType type, short index, InventoryItem* item, bool armory)
        {
            // 連結型格子的 ItemId 欄位裝的是別的東西：給一個不會跟任何道具相同的 id。
            var itemId = item->IsSymbolic ? uint.MaxValue : item->ItemId;
            var hq = (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
            var detail = VendorSellPlanner.DetailSeed;
            detail = VendorSellPlanner.MixDetail(detail, item->SpiritbondOrCollectability);
            detail = VendorSellPlanner.MixDetail(detail, item->Condition);
            detail = VendorSellPlanner.MixDetail(detail, item->CrafterContentId);
            detail = VendorSellPlanner.MixDetail(detail, item->GlamourId);
            var hasMateria = false;
            for (var m = 0; m < item->Materia.Length; m++)
            {
                detail = VendorSellPlanner.MixDetail(detail, item->Materia[m]);
                detail = VendorSellPlanner.MixDetail(detail, item->MateriaGrades[m]);
                hasMateria |= item->Materia[m] != 0;
            }

            for (var s = 0; s < item->Stains.Length; s++)
                detail = VendorSellPlanner.MixDetail(detail, item->Stains[s]);

            // 遊戲在這兩種情形賣給商店前會跳確認框（Addon 397／398），本外掛不替使用者回答。
            var collectable = (item->Flags & InventoryItem.ItemFlags.Collectable) != 0;
            var needsConfirmation = hasMateria || (!collectable && item->SpiritbondOrCollectability >= 10000);
            return new VendorSellSlot((uint)type, index, new VendorSellKey(itemId, hq), item->Quantity, detail,
                armory, needsConfirmation);
        }

        /// <summary>背包視窗的 addon id（右鍵選單的父視窗）；沒開回 0。</summary>
        private static uint GetInventoryAddonId()
        {
            var agent = AgentInventory.Instance();
            var id = agent == null ? 0u : agent->AgentInterface.GetAddonId();
            if (id != 0 && AddonHelpers.IsAddonReady(GetAddonById(id)))
                return id;

            foreach (var name in InventoryAddonNames)
            {
                var addon = AddonHelpers.GetReadyAddon(name);
                if (addon != null)
                    return addon->Id;
            }

            return 0;
        }

        private static AtkUnitBase* GetAddonById(uint id)
        {
            var stage = AtkStage.Instance();
            if (stage == null || stage->RaptureAtkUnitManager == null)
                return null;
            return stage->RaptureAtkUnitManager->GetAddonById((ushort)id);
        }

        // ------------------------------------------------------------------ 文字

        internal static string DescribeSlot(VendorSellSlot slot)
        {
            var type = (InventoryType)slot.Container;
            var bag = Array.IndexOf(BagContainers, type);
            return bag >= 0
                ? "Bag ?? slot ??".Loc(bag + 1, slot.Slot + 1)
                : "Armoury chest (??) slot ??".Loc(type, slot.Slot + 1);
        }

        internal static string CauseText(IReadOnlyList<VendorSellCause> causes)
        {
            var parts = new List<string>(causes.Count);
            foreach (var cause in causes)
                parts.Add(cause == VendorSellCause.BelowVendor
                    ? "below the NPC price".Loc()
                    : "below your minimum".Loc());
            return string.Join("、", parts);
        }

        internal static string ManualReasonShort(VendorSellManualReason reason, VendorSellState state)
        {
            if (state == VendorSellState.Waiting)
                return "still on its way back to your bags".Loc();
            return reason switch
            {
                VendorSellManualReason.MergedIntoExistingStack => "merged into a stack you already had".Loc(),
                VendorSellManualReason.ExistingStackChanged => "a stack you already had moved or changed".Loc(),
                VendorSellManualReason.UnknownContainer => "turned up somewhere that could not be checked".Loc(),
                VendorSellManualReason.QuantityMismatch => "the amount does not match".Loc(),
                VendorSellManualReason.NotArrived => "never showed up in your bags".Loc(),
                VendorSellManualReason.NoVendorPrice => "NPC shops do not buy this".Loc(),
                VendorSellManualReason.NeedsConfirmation => "the game asks for confirmation when selling this".Loc(),
                VendorSellManualReason.InArmory => "it went into the armoury chest".Loc(),
                _ => "?",
            };
        }

        private static string DescribeMenuResult(VendorSellMenuResult result) => result switch
        {
            VendorSellMenuResult.EntryMissing => "the right-click menu has no \"Have Retainer Sell Items\"".Loc(),
            VendorSellMenuResult.EntryDisabled => "\"Have Retainer Sell Items\" is greyed out in the right-click menu".Loc(),
            VendorSellMenuResult.NotSent => "the right-click menu was not ready yet - close it and press the button again".Loc(),
            VendorSellMenuResult.MenuUnavailable => "the right-click menu could not be found".Loc(),
            _ => "the right-click menu opened but nothing was selected - close it and check".Loc(),
        };
    }
}
