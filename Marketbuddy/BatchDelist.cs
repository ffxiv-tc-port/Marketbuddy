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

        /// <summary>
        /// 這一輪是不是因為**目的地容器滿了**而停的（而不是出錯）。
        /// 巡迴用它決定結束語要講「正常收工但沒跑完」還是「出事了」。
        /// </summary>
        bool StoppedForSpace { get; }
    }

    /// <summary>
    /// 「把這名僱員上架中的道具全部下架」批次引擎。
    ///
    /// 🔑 **目的地由設定 <see cref="Configuration.DelistToRetainerInventory"/> 決定，
    /// 預設是玩家背包，而那個預設是有理由的、不是隨便挑的。**
    /// 使用者的用途是「收回來、把同款道具併成堆疊、重新上架」：同一款道具散在不同僱員
    /// 身上時，只有全部回到**同一個**容器才會併成一個堆疊。收到僱員自己的物品欄就合併
    /// 不了，因為每個僱員的物品欄是各自獨立的。所以預設走
    /// <c>MoveFromRetainerMarketToPlayerInventory()</c>；設定改成僱員時走
    /// <c>MoveFromRetainerMarketToRetainerInventory()</c>。
    ///
    /// ⚠️ 這個設定同時管改價流程裡的自動下架（<see cref="BatchReprice.DelistSlot"/>）
    /// 與兩顆手動下架按鈕，**兩邊必須讀同一個欄位**，否則同一句「下架收回至」會有兩種
    /// 意思。而且目的地一換，**跟著換的不只是呼叫哪支函式**：錯誤訊息的主詞、空間檢查
    /// 要數哪個容器、「滿了停手」的說法、以及收工時「佔掉幾格」的意義全部都得跟著走。
    ///
    /// 🔑 **目的地在 <see cref="Start"/> 當下抄成快照（<see cref="toRetainerInventory"/>），
    /// 之後整輪都用那份快照。** 使用者中途改設定不該讓同一批東西一半進背包一半進僱員，
    /// 更不該讓開跑訊息、空間量測與結束語各自講不同的容器。
    ///
    /// ——**不是** <c>MoveItemSlot</c>。這個差別很重要：<c>MoveItemSlot</c> 對僱員這種
    /// 伺服器權威容器只會更新本機、假裝成功；而這兩支函式（離線反編譯 TC 7.20 客戶端證實）
    /// 會寫一筆待處理交易記錄再送封包給伺服器，也就是真的來回。因此這裡**不**用
    /// 「送出即算成功」，每一件都等到那一格真的空掉才算數。
    ///
    /// 🔑 **「目的地滿了」是預期中的正常結束，不是錯誤。** 收回玩家背包時尤其如此：
    /// 背包 140 格，而九個僱員最多可以掛 180 件，所以跑不完是常態（會併堆疊，所以實際
    /// 能收多少無法事先算準）。撞到滿時整輪乾淨停手、照實回報進度與剩餘量，而且
    /// **不計入失敗數**——那一件根本沒被動到，還好好掛在市場上。
    ///
    /// 續跑不需要任何狀態：巡迴每次都重新挑「還有掛單的僱員」，這裡每一件也都重新找
    /// 「目前第一個有東西的格子」，所以清出空間再按一次，自然就從剩下的地方接著跑。
    ///
    /// 🔑 **單價門檻（<see cref="Configuration.DelistAboveUnitPrice"/>）預設 0 ＝ 停用 ＝
    /// 全部下架，也就是這個引擎一直以來的行為。** 設成 &gt;0 時只下架**單價**高於它的掛單，
    /// 其餘原封不動留在市場上。停用時 <see cref="PassesPriceFilter"/> 第一行就短路，
    /// 連查價都不查，所以預設路徑的原生呼叫次數與加這個功能之前完全相同。
    /// ⚠️ 門檻比的是每一件的掛售價不是整堆總價（論證見設定欄位的說明）。
    /// ⚠️ 查不到價時該格**留著不動**——不確定的時候少做比多做安全。
    ///
    /// 🔴 嚴格手動：只有使用者按下按鈕（且通過二次確認）才會跑。
    /// 沒有任何事件驅動的接手鏈，關掉視窗／按 ESC／IPC 鎖定／AutoRetainer 開始運作
    /// 都會立刻停手。停手原因一律照實回報，不會把「撞到限制」講成「正常結束」，
    /// 也不會反過來把「正常收工」講得像出錯。
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
        ///
        /// 🔎 2026-08-03 目的地換成玩家背包之後**重新評估過，結論是維持 20 秒**。
        /// 反編譯比對兩支函式：它們寫的是**同一張**待處理交易表（同樣 rdi 起算、
        /// 0x3C 一格、上限 0x80 筆）、共用**同一個**序號欄位（[rdi+0x1E00]），
        /// 只有交易型別差一號（僱員 0xC8 / 玩家 0xC9）與送出函式不同。
        /// 而我們判定完成的方式兩邊完全一樣——盯**來源**那一格（RetainerMarket）
        /// 有沒有空掉，跟目的地是誰無關。所以沒有理由預期來回時間有系統性差異。
        /// ⚠️ 但要說清楚：3.9～10.6 秒那組數字是僱員容器量的，**這一支沒有實機量過**。
        /// 保險絲設寬本來就是為了涵蓋沒量到的情況，量到之後再收窄。
        /// </summary>
        private const int MoveAckTimeoutMs = 20000;

        /// <summary>連續兩次取回之間的最小間隔，避免一口氣把 20 筆交易全丟給伺服器。</summary>
        private const int MoveThrottleMs = 250;

        /// <summary>每一格的看門狗（佇列層的最後保險，必須寬於 MoveAckTimeoutMs）。</summary>
        private const int SlotWatchdogSeconds = 30;

        // ------------------------------------------------------------------
        // 兩支取回函式的回傳值（→玩家背包／→僱員物品欄）。
        //
        // 🔑 **數值兩支相同，所以下面這組常數兩支共用**；但 0x19／0x1C 的**主詞不同**，
        // 所以翻成人話時一定要知道目的地是誰——見 DescribeMoveError 的 toRetainerInventory
        // 參數。以下的反編譯記錄是針對「→玩家背包」那一支做的（僱員那一支在更早一輪
        // 就驗過，rva 0x83D470，這一輪重掃結果一致）。
        //
        // ⚠️ **這是重新驗過的表，不是沿用「→僱員物品欄」那一支的。** 那是另一支函式，
        // 錯誤碼不保證相同，所以 2026-08-03 對 TC 7.20 客戶端重新離線反編譯了一次。
        // 方法：先拿 Marketbuddy 自己在實機用得好好的 7 條特徵碼當校準組
        // （GetInventoryContainer / GetInventorySlot / GetEmptySlotsInBag /
        //  Get+SetRetainerMarketPrice / MoveItemSlot / MoveToRetainerMarket）——
        // 全部在 .text 內唯一命中，證明掃描器本身是準的；而且「→僱員物品欄」那支
        // 解出來的 rva 與上一輪完全一致（0x83D470），等於連結論也對得上。
        //
        // 本函式：特徵碼 `E8 ?? ?? ?? ?? EB 49 84 C0` **唯一命中**，
        // 解到 **rva 0x83D630**；.text 內有 3 個 call 指向它（0xAD6290 / 0xADC05F /
        // 0xC8B19A），其中 0xADC05F 就緊鄰「→僱員物品欄」的 call 0xADC0AA——
        // 也就是遊戲自己那個「取回到背包／取回到僱員」選單，所以**不是內聯後的死碼**。
        //
        // 控制流：
        //   • GetInventorySlot 取來源格，取不到 → 回 4
        //   • 條件閘門（cond 0x88）不過 → 顯示遊戲訊息後回 10
        //   • 呼叫玩家背包側的檢查子函式 rva 0x8317F0（**與僱員側的 0x83D280 是不同函式**，
        //     它是全遊戲共用的「這件道具塞不塞得進玩家背包」檢查，有 25 個 call）。
        //     它只會回 0 / 6 / 0x19 / 0x1C 四種，wrapper 原封不動往外傳。
        //   • 通過 → 寫入待處理交易（type 0xC9）並送出封包，回 0
        //
        // 🔑 0x19 那顆檢查是**會算堆疊**的：它把每個背包容器對這件道具還能吃下多少
        // 加總起來（含併進既有堆疊）再跟要取回的數量比。所以它比我們自己數空格準，
        // 我們數的空格數只是一個**會低估**的下界——這正是空間檢查不能拿來當閘門的原因。
        //
        // 🔑 錯誤碼的**數值**跟「→僱員物品欄」那支相同，但 0x19/0x1C 的**主詞不同**，
        // 這一點由遊戲自己印的 LogMessage 行號證明（查 TC 7.20 EXD 實資料）：
        //   0x19 → 訊息 1240「背包已滿。」               ← 兩支共用，這裡指玩家背包
        //   0x1C → 訊息 1241「已持有相同物品，帶有珍稀屬性的物品只能持有一個。」
        //          （僱員那支用的是 4585「**僱員的**裝備、物品或出售列表中已持有…」）
        // 所以錯誤字串必須跟著目的地走，不能共用同一句——見 DescribeMoveError。
        // ------------------------------------------------------------------
        internal const int MoveOk = 0;
        private const int MoveErrNoSourceSlot = 4;

        /// <summary>檢查子函式查不到這件道具的資料列（rva 0x831B00）。上一輪的表漏了這個碼。</summary>
        private const int MoveErrUnknownItem = 6;

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

        private Configuration conf => Configuration.GetOrLoad();

        private DateTime lastMove = DateTime.MinValue;
        private bool suppressionHeld;

        /// <summary>
        /// 這一輪的目的地：true = 僱員自己的物品欄，false = 玩家背包。
        ///
        /// 🔑 這是 <see cref="Start"/> 當下對 <see cref="Configuration.DelistToRetainerInventory"/>
        /// 取的**快照**，不是每次用的時候現讀。理由是整輪的一致性：開跑訊息、空間量測、
        /// 呼叫哪支函式、錯誤訊息主詞、結束語全都要講同一個容器，而使用者隨時可以在
        /// 設定視窗裡把開關撥過去。現讀會讓一批東西一半進背包一半進僱員，而且訊息會
        /// 說謊（例如開跑說「收回背包」、結束說「佔用僱員 N 格」）。
        /// </summary>
        private bool toRetainerInventory;

        /// <summary>
        /// 這一輪的單價門檻快照：只下架**單價 &gt; 這個值**的掛單；0 = 全部都下架。
        ///
        /// 🔑 跟 <see cref="toRetainerInventory"/> 同樣的理由做成快照：佇列長度、開跑訊息、
        /// 「保留了幾件」的統計與每一格的判定必須是同一套規則。使用者中途把門檻拉高，
        /// 不該讓同一批東西前半段照舊門檻、後半段照新門檻。
        ///
        /// ⚠️ 0 是**停用**不是「門檻等於 0」：掛售價最低就是 1 gil，門檻 0 若照
        /// 「&gt; 0」解讀其實也等於全部通過，兩種解讀在這裡剛好一致；但仍然刻意在
        /// <see cref="PassesPriceFilter"/> 一開頭就短路掉，這樣停用時**連查價都不查**，
        /// 呼叫次數與現行行為完全相同。
        /// </summary>
        private int aboveUnitPrice;

        /// <summary>
        /// 開跑當下**目的地容器**的空格數，用來在收工時算出「這一輪佔掉幾格」。
        /// 目的地是背包就數背包、是僱員就數僱員的物品欄頁。
        /// </summary>
        private int freeDestinationSlotsAtStart;

        public bool IsRunning => queue.IsRunning;
        public int TotalSlots { get; private set; }
        public int ProcessedSlots { get; private set; }

        /// <summary>
        /// 這一輪是不是因為**目的地容器滿了**而停的（背包或僱員物品欄，看設定）。
        /// 🔑 這不是錯誤旗標，是「正常收工但沒跑完」——巡迴靠它決定要講哪一種結束語。
        /// </summary>
        public bool StoppedForSpace { get; private set; }

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

            var listed = CountListedSlots(inventoryManager, 0);
            if (listed == 0)
            {
                ChatGui.PrintError("[Marketbuddy] Cannot start: ??".Loc("this retainer has nothing listed".Loc()));
                return;
            }

            // 🔑 門檻在這裡定案，整輪不再改（理由見 aboveUnitPrice 的說明）。
            // 門檻 0（預設）時 eligible 直接沿用 listed，**連第二次掃描都不做**——
            // 停用時的執行路徑與加這個功能之前逐字相同。
            aboveUnitPrice = Math.Max(0, conf.DelistAboveUnitPrice);
            var eligible = aboveUnitPrice <= 0 ? listed : CountListedSlots(inventoryManager, aboveUnitPrice);
            if (eligible == 0)
            {
                // 🔑 有掛單、但沒有一件超過門檻。這**不是錯誤**，是門檻正常運作，
                // 所以用一般訊息不用紅字——巡迴會照樣往下一名僱員走，紅字會讓
                // 「設了門檻」這件正常的事每經過一名僱員就叫一次。
                ChatGui.Print(
                    "[Marketbuddy] Nothing above ?? gil per unit here - ?? listing(s) left alone."
                        .Loc(aboveUnitPrice.ToString("N0"), listed));
                return;
            }

            TotalSlots = eligible;
            ProcessedSlots = 0;
            // 因門檻而保留的件數。門檻 0 時 eligible == listed，所以這裡是 0，
            // 與加這個功能之前（永遠 0）相同。
            SkippedCount = listed - eligible;
            DelistedCount = 0;
            FailedCount = 0;
            StoppedForSpace = false;
            CurrentItemName = string.Empty;
            lastMove = DateTime.MinValue;

            // 🔑 目的地在這裡定案，整輪不再改（理由見 toRetainerInventory 的說明）。
            toRetainerInventory = conf.DelistToRetainerInventory;

            // 🔑 開始前的空間資訊刻意是**告知而不是攔阻**，而且刻意**不做預估**。
            // 我們數得到的只有「空格數」，但道具會併進已有的同款堆疊（收回玩家背包時，
            // 合併正是這個功能的目的），所以空格數是一個會低估的下界——拿它當閘門會
            // 產生「其實放得下卻拒絕開始」的假拒絕，拿它去換算「大概可以下架幾件」則會
            // 算出一個騙人的數字。真正會算堆疊的檢查是遊戲自己那顆（見上面 0x19 的說明），
            // **權威永遠是回傳碼，不是我們數的這個數字。**
            //
            // ⚠️ 而且收回玩家背包時「空間不足」是常態不是邊緣情況（140 格 vs 最多 180 件），
            // 所以這裡**不能**印警告——每次都跳的警告就是狼來了。改成把實際數字併進
            // 開跑訊息裡，讓它變成有用的資訊。
            //
            // ⚠️ 數哪個容器要跟著目的地走：數錯容器的「剩 N 格」比不講還糟。
            freeDestinationSlotsAtStart = CountFreeDestinationSlots(inventoryManager);

            AutoRetainerBridge.AcquireSuppression("batch delist");
            suppressionHeld = true;

            for (var i = 0; i < eligible; i++)
            {
                var job = new DelistJob();
                queue.Enqueue($"delist #{i + 1}", TimeSpan.FromSeconds(SlotWatchdogSeconds), () => TickJob(job));
            }

            var active = retainerManager->GetActiveRetainer();
            var retainerName = active == null ? string.Empty : active->NameString;
            ChatGui.Print((toRetainerInventory
                    ? "[Marketbuddy] Delisting ?? item(s) into the retainer's own inventory (retainer: ??, ?? retainer slot(s) free)..."
                    : "[Marketbuddy] Delisting ?? item(s) into your bags (retainer: ??, ?? bag slot(s) free)...")
                .Loc(eligible, retainerName, freeDestinationSlotsAtStart));

            // ⚠️ 門檻擋下東西時**一定要當場說**，而且要說單價門檻是多少。
            // 少了這一句，使用者按了「全部下架」卻發現東西還在，只會以為外掛壞了。
            // 門檻停用時 SkippedCount 是 0，這一行不會出現。
            if (SkippedCount > 0)
                ChatGui.Print("[Marketbuddy] ?? listing(s) kept: unit price not above ?? gil."
                    .Loc(SkippedCount, aboveUnitPrice.ToString("N0")));
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

                    var slot = FindFirstListedSlot(inventoryManager, aboveUnitPrice, out var slotIndex);
                    if (slot == null)
                    {
                        // 已經沒有**該下架**的掛單了：比開始時預期的少（別人買走、或本來就估多了；
                        // 有門檻時也可能是剩下的都在門檻以下）。這不是失敗，直接收工。
                        return TickTaskResult.Done;
                    }

                    if ((now - lastMove).TotalMilliseconds < MoveThrottleMs)
                        return TickTaskResult.Continue;

                    job.Slot = slotIndex;
                    job.ItemId = slot->ItemId;
                    job.Name = DescribeItem(slot);
                    CurrentItemName = job.Name;

                    var quantity = (uint)Math.Max(1, slot->Quantity);

                    // ⚠️ 兩支函式**不能**互相退回：目的地是使用者的設定，撞牆時如實
                    // 停手／回報，絕不靜默改送到另一個容器（那會把東西放到使用者沒
                    // 預期的地方，而且事後很難查）。
                    var result = toRetainerInventory
                        ? inventoryManager->MoveFromRetainerMarketToRetainerInventory(
                            InventoryType.RetainerMarket, (ushort)slotIndex, quantity)
                        : inventoryManager->MoveFromRetainerMarketToPlayerInventory(
                            InventoryType.RetainerMarket, (ushort)slotIndex, quantity);
                    lastMove = now;
                    Log.Information(
                        $"[Marketbuddy] BatchDelist: slot {slotIndex} ({job.Name}) qty {quantity} -> " +
                        $"{(toRetainerInventory ? "retainer inventory" : "player inventory")}, move returned {result}");

                    if (result == MoveErrNoSpace)
                    {
                        // 🔑 目的地滿了**不是失敗**，是這個功能預期中的正常結束
                        // （收回背包時尤其如此：140 格裝不下最多 180 件，跑不完是常態）。
                        // 刻意**不**走 Fail()：那一件根本沒被動到，還好好掛在市場上，
                        // 把它算成「失敗」會讓使用者以為東西出事了。
                        // 整輪停在這裡，剩下的掛單原封不動留著等下一次。
                        StoppedForSpace = true;
                        queue.Abort(DestinationFullReason(toRetainerInventory));
                        return TickTaskResult.Continue; // Abort 已經清空佇列
                    }

                    if (result != MoveOk)
                    {
                        // 🔴 送出被遊戲當場擋下來，而且不是空間問題。
                        // 這是「有明確原因的失敗」，絕對不能當成正常結束帶過去。
                        // 只影響這一件，記成失敗之後繼續處理下一件。
                        // ⚠️ 目的地要傳進去：0x19／0x1C 兩支的主詞不同（見常數區）。
                        Fail(job, DescribeMoveError(result, toRetainerInventory));
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
                        ChatGui.Print((toRetainerInventory
                                ? "[Marketbuddy] ??: delisted into the retainer's inventory"
                                : "[Marketbuddy] ??: delisted into your bags")
                            .Loc(job.Name));
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

        /// <summary>
        /// 「目的地滿了」的說法。
        /// 🔑 刻意直接轉呼 <see cref="DescribeMoveError"/> 的 0x19 分支：停手原因與錯誤
        /// 字串共用**同一份來源**，措辭才不可能分岔成兩套。
        /// </summary>
        internal static string DestinationFullReason(bool toRetainerInventory)
            => DescribeMoveError(MoveErrNoSpace, toRetainerInventory);

        /// <summary>
        /// 把取回函式的回傳碼翻成一句人看得懂的原因。<see cref="BatchReprice.DelistSlot"/> 共用同一份。
        ///
        /// ⚠️ **必須知道目的地**：兩支取回函式的回傳碼數值相同，但 0x19／0x1C 的**主詞不同**
        /// （遊戲自己印的 LogMessage 就分成兩套，見上面常數區的說明）。
        /// 共用同一句話會讓「僱員身上已有這件珍稀道具」被講成「你身上已有」，反之亦然。
        /// </summary>
        /// <param name="toRetainerInventory">true = 收到僱員物品欄；false = 收到玩家背包。</param>
        internal static string DescribeMoveError(int result, bool toRetainerInventory) => result switch
        {
            MoveErrNoSpace => toRetainerInventory
                ? "retainer inventory is full, cannot delist".Loc()
                : "your inventory is full, cannot delist".Loc(),
            MoveErrUniqueAlreadyHeld => toRetainerInventory
                ? "the retainer already holds this unique item".Loc()
                : "you already hold this unique item".Loc(),
            MoveErrNotAllowedNow => "the game refused the delist right now".Loc(),
            MoveErrNoSourceSlot => "the market slot was already empty".Loc(),
            MoveErrUnknownItem => "the game did not recognise this item".Loc(),
            _ => "the delist was rejected (code ??)".Loc(result),
        };

        /// <summary>
        /// 這名僱員目前還剩幾件**這一輪會處理的**掛單（每次都重新數，不留狀態）。
        ///
        /// ⚠️ <paramref name="aboveUnitPrice"/> 要跟著門檻走，不能永遠數全部：
        /// 這個數字出現在「清出空間再按一次就接著跑」那句話裡，所以它必須是「還剩幾件
        /// **會被下架**」而不是「還剩幾件掛著」。被門檻留下來的那些再按幾次都不會動，
        /// 把它們算進去等於叫使用者去追一個永遠追不完的數字。門檻 0 時兩者相同。
        /// </summary>
        internal static int CountRemainingListed(int aboveUnitPrice)
        {
            var inventoryManager = InventoryManager.Instance();
            return inventoryManager == null ? 0 : CountListedSlots(inventoryManager, aboveUnitPrice);
        }

        /// <summary>
        /// 這一輪佔掉了**目的地容器**幾格；量不到就回 null，**不編數字**。
        /// 開跑與收工兩次都用 <see cref="CountFreeDestinationSlots"/> 數同一個容器，
        /// 所以兩端一定對得起來。
        /// </summary>
        private int? DestinationSlotsUsed()
        {
            var inventoryManager = InventoryManager.Instance();
            if (inventoryManager == null)
                return null;
            var used = freeDestinationSlotsAtStart - CountFreeDestinationSlots(inventoryManager);
            return used < 0 ? null : used;
        }

        /// <summary>目的地容器目前的空格數：背包用遊戲自己的計數，僱員則逐頁數。</summary>
        private int CountFreeDestinationSlots(InventoryManager* inventoryManager)
            => toRetainerInventory
                ? CountFreeRetainerInventorySlots(inventoryManager)
                : (int)inventoryManager->GetEmptySlotsInBag();

        /// <summary>
        /// 目前這名僱員物品欄的空格數。
        ///
        /// ⚠️ 只數 <c>IsLoaded</c> 的頁：沒載入的頁我們**看不到內容**，把它當成「全空」
        /// 會憑空多報幾十格。⚠️ 這個數字跟背包那個一樣**只拿來顯示，不當閘門**——
        /// 它不會算「併進既有堆疊」，所以是個會低估的下界；權威永遠是取回函式的回傳碼。
        /// </summary>
        private static int CountFreeRetainerInventorySlots(InventoryManager* inventoryManager)
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

        /// <summary>
        /// 這一格的掛單過不過得了單價門檻。
        ///
        /// 🔑 <paramref name="aboveUnitPrice"/> &lt;= 0（預設）時**立刻回 true 且完全不查價**，
        /// 所以停用門檻時連原生呼叫次數都跟以前一樣。
        ///
        /// 🔑 門檻比的是 <c>GetRetainerMarketPrice()</c>，那是**每一件的掛售價**不是整堆總價
        /// （論證見 <see cref="Configuration.DelistAboveUnitPrice"/>）。嚴格大於，
        /// 剛好等於門檻的留著——UI 上寫的是「高於」。
        ///
        /// ⚠️ 查不到價（回 0）時回 false ＝ **留著不動**。這是刻意挑的失敗方向：
        /// 少下架一件，使用者再按一次就好；錯下架一件，他得重新上架，而且中間那段時間
        /// 東西不在市場上賣。「不確定就不要動」在這裡是唯一安全的預設。
        /// </summary>
        private static bool PassesPriceFilter(InventoryManager* inventoryManager, int slotIndex, int aboveUnitPrice)
        {
            if (aboveUnitPrice <= 0)
                return true;
            return inventoryManager->GetRetainerMarketPrice((short)slotIndex) > (ulong)aboveUnitPrice;
        }

        /// <summary>
        /// 目前市場容器裡第一個**該下架**的格子（每次都重新找，見 <see cref="DelistJob"/> 的說明）。
        ///
        /// 🔴 有門檻時**必須跳過**不合格的格子繼續往後找，不能只看第一個有東西的格子：
        /// 被門檻留下來的那一格會一直待在那裡，只看第一格的話整輪會卡在它身上重試到看門狗。
        /// </summary>
        private static InventoryItem* FindFirstListedSlot(InventoryManager* inventoryManager, int aboveUnitPrice,
            out short slotIndex)
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
                if (!PassesPriceFilter(inventoryManager, i, aboveUnitPrice))
                    continue;
                slotIndex = (short)i;
                return slot;
            }

            return null;
        }

        /// <summary>
        /// 市場容器裡有幾件掛單。<paramref name="aboveUnitPrice"/> 傳 0 = 全部都數
        /// （＝這個函式一直以來的行為），傳 &gt;0 則只數單價高於它的。
        /// </summary>
        private static int CountListedSlots(InventoryManager* inventoryManager, int aboveUnitPrice)
        {
            var container = inventoryManager->GetInventoryContainer(InventoryType.RetainerMarket);
            if (container == null || !container->IsLoaded)
                return 0;

            var count = 0;
            var slotCount = Math.Min((int)container->Size, MaxMarketSlots);
            for (var i = 0; i < slotCount; i++)
            {
                var slot = inventoryManager->GetInventorySlot(InventoryType.RetainerMarket, i);
                if (slot != null && slot->ItemId != 0 && PassesPriceFilter(inventoryManager, i, aboveUnitPrice))
                    count++;
            }

            return count;
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

            // 🔑 「目的地滿了」與「出錯了」**必須長得不一樣**。
            // 前者是預期中的正常結束：用一般訊息（不是紅字錯誤）、講清楚進度與剩餘量、
            // 並且明說再按一次就會接著跑。把它印成錯誤會讓使用者以為東西掉了。
            //
            // ⚠️ 但**兩條路徑都要發 BatchAborted**：巡迴是靠這個事件才知道要整個停下來。
            // 少發的話，巡迴會把這名僱員當成正常跑完、接著去下一個——而背包還是滿的，
            // 於是每個僱員都白跑一趟。單僱員直接跑時沒有訂閱者，發了也不會有副作用。
            if (StoppedForSpace)
            {
                // ⚠️ 講對是哪個容器滿了：使用者要去清的地方不一樣（背包 vs 這名僱員的
                // 物品欄），講錯等於叫他去整理一個根本沒滿的容器。
                ChatGui.Print((toRetainerInventory
                        ? "[Marketbuddy] This retainer's inventory is full - stopped here, this is not an error. ?? delisted, ?? still listed on this retainer. Make room in that retainer's inventory and press the button again to carry on."
                        : "[Marketbuddy] Bags are full - stopped here, this is not an error. ?? delisted, ?? still listed on this retainer. Free up space and press the button again to carry on.")
                    .Loc(DelistedCount, CountRemainingListed(aboveUnitPrice)));
            }
            else
            {
                ChatGui.PrintError("[Marketbuddy] Delist stopped: ?? (?? delisted, ?? failed)"
                    .Loc(reason, DelistedCount, FailedCount));
                if (AutoRetainerBridge.IsBusy)
                {
                    AutoRetainerBridge.ArmAvailabilityNotice();
                    ChatGui.Print("[Marketbuddy] Wait for AutoRetainer to finish, then press the button again.".Loc());
                }
            }

            BatchAborted?.Invoke(reason);
        }

        private void OnQueueCompleted()
        {
            ReleaseSuppressionIfHeld();
            CurrentItemName = string.Empty;

            // 🔑 「佔掉幾格」在收回**玩家背包**時不只是副作用，而是這個功能的**產出**：
            // 使用者要的就是把散在各個僱員身上的同款道具併成堆疊，而「下架 N 件只佔掉
            // M 格」正是合併真的發生了的證據。
            //
            // ⚠️ 收回**僱員自己的物品欄**時這個意義不成立：每名僱員的物品欄各自獨立，
            // 跨僱員合併根本不可能發生。所以那邊只講一句中性的「佔用 N 格」，
            // 不沿用任何跟合併有關的說法，免得暗示了一件不會發生的事。
            var used = DestinationSlotsUsed();
            ChatGui.Print(used is null
                ? "[Marketbuddy] Delist finished: ?? delisted, ?? failed".Loc(DelistedCount, FailedCount)
                : (toRetainerInventory
                        ? "[Marketbuddy] Delist finished: ?? delisted, ?? failed (?? retainer inventory slot(s) used)"
                        : "[Marketbuddy] Delist finished: ?? delisted, ?? failed (?? bag slot(s) used)")
                    .Loc(DelistedCount, FailedCount, used.Value));

            // ⚠️ 收工時再講一次「留了幾件」：使用者回頭看僱員身上還有東西時，
            // 要能在同一段訊息裡看到那是門檻擋的、不是漏掉的。門檻停用時不會出現。
            if (SkippedCount > 0)
                ChatGui.Print("[Marketbuddy] ?? listing(s) kept: unit price not above ?? gil."
                    .Loc(SkippedCount, aboveUnitPrice.ToString("N0")));
        }
    }
}
