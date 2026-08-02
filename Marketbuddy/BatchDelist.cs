using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using Marketbuddy.Common;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    /// <summary>
    /// 巡迴（<see cref="MultiRetainerTour"/>）每個僱員身上跑的那一段工作。
    /// <see cref="BatchReprice"/>（重掛）與 <see cref="BatchDelist"/>（下架）都實作它，
    /// 所以巡迴的導航（選僱員 → 進出售品 → 做事 → 離開）只有一份。
    /// </summary>
    internal interface IRetainerBatchEngine
    {
        bool IsRunning { get; }
        bool CanStart(out string reason);
        void Start();

        /// <summary>整批被取消／中止，附原因。巡迴收到就整個停下來。</summary>
        event Action<string>? BatchAborted;

        int TotalSlots { get; }
        int ProcessedSlots { get; }
        int RepricedCount { get; }
        int SkippedCount { get; }
        int DelistedCount { get; }
        int FailedCount { get; }
        string CurrentItemName { get; }
    }

    /// <summary>
    /// 「把這名僱員上架中的道具全部下架回它自己的物品欄」批次引擎。
    ///
    /// 走的是遊戲自己的取回函式 <c>InventoryManager.MoveFromRetainerMarketToRetainerInventory()</c>
    /// ——**不是** <c>MoveItemSlot</c>。這個差別很重要：<c>MoveItemSlot</c> 對僱員這種
    /// 伺服器權威容器只會更新本機、假裝成功；而這個函式（離線反編譯 TC 7.20 客戶端證實）
    /// 會寫一筆待處理交易記錄再送封包給伺服器，也就是真的來回。因此這裡**不**用
    /// 「送出即算成功」，每一件都等到那一格真的空掉才算數。
    ///
    /// 🔴 嚴格手動：只有使用者在僱員選單上按下按鈕（且通過二次確認）才會跑。
    /// 沒有任何事件驅動的接手鏈，關掉視窗／按 ESC／IPC 鎖定／AutoRetainer 開始運作
    /// 都會立刻停手。停手原因一律照實回報，不會把「撞到限制」講成「正常結束」。
    /// </summary>
    internal sealed unsafe class BatchDelist : IRetainerBatchEngine, IDisposable
    {
        /// <summary>僱員市場容器的格數上限；每個索引兩端都會檢查。</summary>
        private const int MaxMarketSlots = 20;

        /// <summary>
        /// 送出取回請求之後，等那一格真的空掉的上限。
        ///
        /// ⚠️ 20 秒看起來很久，但這個數字**不是隨手取的**：先前在別的地方量過僱員容器
        /// 的伺服器來回，本機狀態被伺服器推翻／確認的時間實測落在 3.9～10.6 秒。
        /// 門檻壓在 10 秒等於把「比較慢但其實成功」的那條長尾誤判成失敗，
        /// 然後停掉整輪並跟使用者說伺服器沒回應——那是最糟的一種假警報。
        /// 逾時只是最後的保險絲，設寬一點的代價只有「真的壞掉時多等一下」。
        /// </summary>
        private const int MoveAckTimeoutMs = 20000;

        /// <summary>連續兩次取回之間的最小間隔，避免一口氣把 20 筆交易全丟給伺服器。</summary>
        private const int MoveThrottleMs = 250;

        /// <summary>每一格的看門狗（佇列層的最後保險，必須寬於 MoveAckTimeoutMs）。</summary>
        private const int SlotWatchdogSeconds = 30;

        // ------------------------------------------------------------------
        // MoveFromRetainerMarketToRetainerInventory 的回傳值。
        //
        // 🔎 2026-08-03 對 TC 7.20 客戶端離線反編譯得到（函式 rva 0x83D470，
        // 特徵碼 `E8 ?? ?? ?? ?? 45 85 F6 75 22` 在 .text 內**唯一命中**）：
        //   • 先取來源格，取不到 → 回 4
        //   • 條件閘門（cond 0x88）不過 → 顯示遊戲訊息後回 10
        //   • 呼叫容量檢查子函式（rva 0x83D280）：它把 RetainerPage1..7 對這件道具
        //     還能吃下多少加總起來跟要取回的數量比，不夠 → 回 0x19(25)
        //     （這是**會算堆疊**的檢查，比我們自己數空格準）
        //   • 該道具是 Unique 而僱員身上已經有一個 → 回 0x1C(28)
        //   • 通過 → 寫入待處理交易並送出封包，回 0
        // 失敗時遊戲自己也會在紀錄裡印一行（0x19 → 訊息 0x4D8、0x1C → 訊息 0x11E9），
        // 所以我們的訊息是補充不是唯一線索。
        // ------------------------------------------------------------------
        internal const int MoveOk = 0;
        private const int MoveErrNoSourceSlot = 4;
        private const int MoveErrNotAllowedNow = 10;
        private const int MoveErrNoSpace = 0x19;
        private const int MoveErrUniqueAlreadyHeld = 0x1C;

        private enum DelistPhase
        {
            Fire,
            WaitAck
        }

        /// <summary>
        /// ⚠️ 刻意**不**在開始時就把「第 N 格」抄下來當作要處理的目標。
        /// 一件道具被取回之後，市場容器裡剩下的格子有沒有被壓縮，我們無法離線證明；
        /// 只要它會壓縮，事先抄下來的索引就會全部錯位，變成「下架到別的東西」。
        /// 所以每一件都是在**執行的當下**去找「目前第一個有東西的格子」，
        /// 這對壓縮與不壓縮兩種行為都正確。這裡只記錄名稱等顯示用資訊。
        /// </summary>
        private sealed class DelistJob
        {
            public DelistPhase Phase = DelistPhase.Fire;
            public short Slot = -1;
            public uint ItemId;
            public string Name = string.Empty;
            public DateTime FiredAt;
        }

        private readonly MarketGuiEventHandler gui;
        private readonly TickTaskQueue queue = new();

        private DateTime lastMove = DateTime.MinValue;
        private bool suppressionHeld;

        public bool IsRunning => queue.IsRunning;
        public int TotalSlots { get; private set; }
        public int ProcessedSlots { get; private set; }

        /// <summary>下架不會改價，永遠是 0；只為了滿足巡迴共用的統計介面。</summary>
        public int RepricedCount => 0;

        public int SkippedCount { get; private set; }
        public int DelistedCount { get; private set; }
        public int FailedCount { get; private set; }
        public string CurrentItemName { get; private set; } = string.Empty;

        public event Action<string>? BatchAborted;

        public BatchDelist(MarketGuiEventHandler gui)
        {
            this.gui = gui;
            queue.Aborted += OnQueueAborted;
            queue.Completed += OnQueueCompleted;
            Framework.Update += OnFrameworkUpdate;
        }

        public void Dispose()
        {
            Framework.Update -= OnFrameworkUpdate;
            // 先拆掉處理器，卸載時的中止才會是安靜的。
            queue.Aborted -= OnQueueAborted;
            queue.Completed -= OnQueueCompleted;
            if (queue.IsRunning)
                queue.Abort("plugin unloading");
            ReleaseSuppressionIfHeld();
        }

        public bool CanStart(out string reason)
        {
            reason = string.Empty;
            if (IsRunning)
                return false;
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

            if (!gui.IsRetainerSellListOpen)
            {
                reason = "retainer sell list is not open".Loc();
                return false;
            }

            // 下架流程自己完全不開任何原生視窗，所以一個開著的改價視窗只可能是
            // 使用者（或別的外掛）正在手動操作 —— 站開。
            if (Commons.GetUnitBase("RetainerSell") != null)
            {
                reason = "close the price adjustment window first".Loc();
                return false;
            }

            var retainerManager = RetainerManager.Instance();
            var active = retainerManager == null ? null : retainerManager->GetActiveRetainer();
            if (active == null)
            {
                reason = "no active retainer".Loc();
                return false;
            }

            if (active->MarketItemCount == 0)
            {
                reason = "this retainer has nothing listed".Loc();
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

            var inventoryManager = InventoryManager.Instance();
            var retainerManager = RetainerManager.Instance();
            if (inventoryManager == null || retainerManager == null)
                return;

            var listed = CountListedSlots(inventoryManager);
            if (listed == 0)
            {
                ChatGui.PrintError("[Marketbuddy] Cannot start: ??".Loc("this retainer has nothing listed".Loc()));
                return;
            }

            TotalSlots = listed;
            ProcessedSlots = 0;
            SkippedCount = 0;
            DelistedCount = 0;
            FailedCount = 0;
            CurrentItemName = string.Empty;
            lastMove = DateTime.MinValue;

            // 🔑 開始前的空間檢查刻意是**告知而不是攔阻**。
            // 我們數得到的只有「空格數」，但道具會併進已有的同款堆疊，所以空格數是
            // 一個會低估的下界 —— 拿它當閘門會產生「其實放得下卻拒絕開始」的假拒絕。
            // 真正會算堆疊的檢查是遊戲自己那顆（見上面回傳值 0x19 的說明），
            // 所以權威判定交給它，這裡只負責讓使用者事先知道空間可能不夠。
            var freeSlots = CountFreeRetainerInventorySlots(inventoryManager);
            if (freeSlots < listed)
            {
                ChatGui.PrintError(
                    "[Marketbuddy] Heads up: the retainer's inventory has only ?? free slot(s) for ?? listing(s). If it runs out, the run stops there and says so."
                        .Loc(freeSlots, listed));
            }

            AutoRetainerBridge.AcquireSuppression("batch delist");
            suppressionHeld = true;

            for (var i = 0; i < listed; i++)
            {
                var job = new DelistJob();
                queue.Enqueue($"delist #{i + 1}", TimeSpan.FromSeconds(SlotWatchdogSeconds), () => TickJob(job));
            }

            var active = retainerManager->GetActiveRetainer();
            var retainerName = active == null ? string.Empty : active->NameString;
            ChatGui.Print("[Marketbuddy] Delisting ?? item(s) (retainer: ??)...".Loc(listed, retainerName));
        }

        public void CancelByButton() => queue.Abort("cancelled by user".Loc());

        private void ReleaseSuppressionIfHeld()
        {
            if (!suppressionHeld)
                return;
            suppressionHeld = false;
            AutoRetainerBridge.ReleaseSuppression();
        }

        private void OnFrameworkUpdate(IFramework framework)
        {
            if (!queue.IsRunning)
                return;

            // 與 BatchReprice 同一組護欄，每個 tick 都評估。
            if (!gui.IsRetainerSellListOpen)
            {
                queue.Abort("the retainer sell list was closed".Loc());
                return;
            }

            if (Keys[VirtualKey.ESCAPE])
            {
                queue.Abort("ESC pressed".Loc());
                return;
            }

            if (IPCManager.IsLocked)
            {
                queue.Abort("locked via IPC by another plugin".Loc());
                return;
            }

            if (Commons.GetUnitBase("RetainerSell") != null)
            {
                queue.Abort("manual price adjustment detected".Loc());
                return;
            }

            if (AutoRetainerBridge.IsBusy)
            {
                queue.Abort("AutoRetainer became busy".Loc());
                return;
            }

            queue.Update();
        }

        private TickTaskResult TickJob(DelistJob job)
        {
            var now = DateTime.UtcNow;

            switch (job.Phase)
            {
                case DelistPhase.Fire:
                {
                    var inventoryManager = InventoryManager.Instance();
                    if (inventoryManager == null)
                        return TickTaskResult.Continue; // 看門狗兜底

                    var slot = FindFirstListedSlot(inventoryManager, out var slotIndex);
                    if (slot == null)
                    {
                        // 已經沒有掛單了：比開始時預期的少（別人買走、或本來就估多了）。
                        // 這不是失敗，直接收工。
                        return TickTaskResult.Done;
                    }

                    if ((now - lastMove).TotalMilliseconds < MoveThrottleMs)
                        return TickTaskResult.Continue;

                    job.Slot = slotIndex;
                    job.ItemId = slot->ItemId;
                    job.Name = DescribeItem(slot);
                    CurrentItemName = job.Name;

                    var quantity = (uint)Math.Max(1, slot->Quantity);
                    var result = inventoryManager->MoveFromRetainerMarketToRetainerInventory(
                        InventoryType.RetainerMarket, (ushort)slotIndex, quantity);
                    lastMove = now;
                    Log.Information(
                        $"[Marketbuddy] BatchDelist: slot {slotIndex} ({job.Name}) qty {quantity}, move returned {result}");

                    if (result != MoveOk)
                    {
                        // 🔴 送出被遊戲當場擋下來。這是「有明確原因的失敗」，
                        // 絕對不能當成正常結束帶過去。
                        var why = DescribeMoveError(result);
                        if (result == MoveErrNoSpace)
                        {
                            // 空間不夠是會一路持續下去的狀況：整輪停在這裡，
                            // 剩下的掛單原封不動留著。
                            Fail(job, why);
                            queue.Abort(why);
                            return TickTaskResult.Continue; // Abort 已經清空佇列
                        }

                        // 其他錯誤（不可重複道具、當下不允許）只影響這一件，
                        // 記成失敗之後繼續處理下一件。
                        Fail(job, why);
                        return TickTaskResult.Done;
                    }

                    job.FiredAt = now;
                    job.Phase = DelistPhase.WaitAck;
                    return TickTaskResult.Continue;
                }

                case DelistPhase.WaitAck:
                {
                    var inventoryManager = InventoryManager.Instance();
                    if (inventoryManager == null)
                        return TickTaskResult.Continue;

                    var slot = inventoryManager->GetInventorySlot(InventoryType.RetainerMarket, job.Slot);
                    if (slot == null || slot->ItemId != job.ItemId)
                    {
                        // 那一格已經不是原本那件道具了 = 伺服器收下並執行了。
                        ProcessedSlots++;
                        DelistedCount++;
                        ChatGui.Print("[Marketbuddy] ??: delisted to the retainer's inventory".Loc(job.Name));
                        return TickTaskResult.Done;
                    }

                    if ((now - job.FiredAt).TotalMilliseconds <= MoveAckTimeoutMs)
                        return TickTaskResult.Continue;

                    // 🔴 送出去了但那一格沒動。原因可能是伺服器拒絕、封包掉了、或連線異常；
                    // 我們分不出來，就照實說分不出來，並且停手——同樣的事很可能會一直發生，
                    // 繼續往下丟只會讓使用者更難分辨到底成功了幾件。
                    var reason = "the server did not confirm the delist in time".Loc();
                    Fail(job, reason);
                    queue.Abort(reason);
                    return TickTaskResult.Continue;
                }

                default:
                    return TickTaskResult.AbortQueue;
            }
        }

        private void Fail(DelistJob job, string reason)
        {
            ProcessedSlots++;
            FailedCount++;
            Log.Warning($"BatchDelist: slot {job.Slot} ({job.Name}) failed: {reason}");
            ChatGui.PrintError("[Marketbuddy] ??: failed - ??".Loc(job.Name, reason));
        }

        /// <summary>把取回函式的回傳碼翻成一句人看得懂的原因。<see cref="BatchReprice.DelistSlot"/> 共用同一份。</summary>
        internal static string DescribeMoveError(int result) => result switch
        {
            MoveErrNoSpace => "retainer inventory is full, cannot delist".Loc(),
            MoveErrUniqueAlreadyHeld => "the retainer already holds this unique item".Loc(),
            MoveErrNotAllowedNow => "the game refused the delist right now".Loc(),
            MoveErrNoSourceSlot => "the market slot was already empty".Loc(),
            _ => "the delist was rejected (code ??)".Loc(result),
        };

        /// <summary>目前市場容器裡第一個有東西的格子（每次都重新找，見 <see cref="DelistJob"/> 的說明）。</summary>
        private static InventoryItem* FindFirstListedSlot(InventoryManager* inventoryManager, out short slotIndex)
        {
            slotIndex = -1;
            var container = inventoryManager->GetInventoryContainer(InventoryType.RetainerMarket);
            if (container == null || !container->IsLoaded)
                return null;

            var slotCount = Math.Min((int)container->Size, MaxMarketSlots);
            for (var i = 0; i < slotCount; i++)
            {
                var slot = inventoryManager->GetInventorySlot(InventoryType.RetainerMarket, i);
                if (slot == null || slot->ItemId == 0)
                    continue;
                slotIndex = (short)i;
                return slot;
            }

            return null;
        }

        private static int CountListedSlots(InventoryManager* inventoryManager)
        {
            var container = inventoryManager->GetInventoryContainer(InventoryType.RetainerMarket);
            if (container == null || !container->IsLoaded)
                return 0;

            var count = 0;
            var slotCount = Math.Min((int)container->Size, MaxMarketSlots);
            for (var i = 0; i < slotCount; i++)
            {
                var slot = inventoryManager->GetInventorySlot(InventoryType.RetainerMarket, i);
                if (slot != null && slot->ItemId != 0)
                    count++;
            }

            return count;
        }

        /// <summary>僱員自己物品欄（RetainerPage1..7）目前的空格數。只做告知用，見 <see cref="Start"/>。</summary>
        internal static int CountFreeRetainerInventorySlots(InventoryManager* inventoryManager)
        {
            var free = 0;
            for (var type = InventoryType.RetainerPage1; type <= InventoryType.RetainerPage7; type++)
            {
                var container = inventoryManager->GetInventoryContainer(type);
                if (container == null || !container->IsLoaded)
                    continue;
                for (var i = 0; i < container->Size; i++)
                {
                    var slot = inventoryManager->GetInventorySlot(type, i);
                    if (slot != null && slot->ItemId == 0)
                        free++;
                }
            }

            return free;
        }

        private static string DescribeItem(InventoryItem* slot)
        {
            var itemSheet = DataManager.GetExcelSheet<Item>();
            var name = $"#{slot->ItemId}";
            if (itemSheet != null && itemSheet.TryGetRow(slot->ItemId, out var row))
                name = row.Name.ExtractText();
            if ((slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0)
                name += $" {(char)SeIconChar.HighQuality}";
            return name;
        }

        private void OnQueueAborted(string reason)
        {
            ReleaseSuppressionIfHeld();
            CurrentItemName = string.Empty;
            ChatGui.PrintError("[Marketbuddy] Delist stopped: ?? (?? delisted, ?? failed)"
                .Loc(reason, DelistedCount, FailedCount));
            if (AutoRetainerBridge.IsBusy)
            {
                AutoRetainerBridge.ArmAvailabilityNotice();
                ChatGui.Print("[Marketbuddy] Wait for AutoRetainer to finish, then press the button again.".Loc());
            }

            BatchAborted?.Invoke(reason);
        }

        private void OnQueueCompleted()
        {
            ReleaseSuppressionIfHeld();
            CurrentItemName = string.Empty;
            ChatGui.Print("[Marketbuddy] Delist finished: ?? delisted, ?? failed".Loc(DelistedCount, FailedCount));
        }
    }
}
