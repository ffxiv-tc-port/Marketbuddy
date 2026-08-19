using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Text;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Marketbuddy.Common;
using static Marketbuddy.Common.Dalamud;
using CSFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace Marketbuddy
{
    /// <summary>
    /// Native quick listing: while a configurable key is held and the retainer
    /// sell list is open, right-clicking a sellable item automatically picks
    /// the "Put up for sale" context menu entry (Addon sheet row 99 - no
    /// hardcoded strings). The RetainerSell window that opens is then taken
    /// over directly: the item is listed immediately at the price cap
    /// (999,999,999) and the new market slot is handed to the BatchReprice
    /// engine as a single-slot run - compare (30 min cache), undercut,
    /// thresholds and delist all reuse the existing pipeline. Listing first
    /// and repricing after turns "stuck at the price input" into "listed,
    /// then priced".
    ///
    /// Hooked through the bundled ClientStructs member
    /// AgentInventoryContext.OpenForItemSlot (resolved address, no manual
    /// signature scan). Strictly manual: one item per key-held right-click,
    /// nothing scans the inventory.
    /// </summary>
    internal sealed unsafe class QuickLister : IDisposable
    {
        private const int PendingTimeoutMs = 5000;       // menu jump -> RetainerSell must open within this
        private const int ListingAckTimeoutMs = 6000;    // confirm -> item appears in a market slot

        /// <summary>
        /// 引擎必須在這麼久之內接手這一格。
        ///
        /// 🔑 這段時間**只在真的有機會推進時才倒數**。原本它是絕對期限，於是「使用者
        /// 一邊跑全僱員巡迴、一邊快速上架」時會這樣：
        ///   03:26:42 RetainerSellList not open → 03:26:46 multi-retainer tour running
        ///   → 03:26:47 engine already running → 03:27:02 multi-retainer tour running
        ///   → 03:27:06「仍掛在上限價，請手動定價！」
        /// 那 30 秒裡它被擋住的原因**全部是我們自己合法佔用引擎**，不是卡住，
        /// 卻照樣把道具靜默丟掉、永久留在 999999999。
        /// </summary>
        private const int RepriceQueueTimeoutMs = 30000;

        /// <summary>
        /// 不管被什麼擋住都算數的硬上限。軟期限會被暫停，所以需要一個逃生口，
        /// 免得「真的永遠不會好」的情況變成無限等待。設得比軟期限寬很多，
        /// 因為正常情況下根本碰不到它（巡迴自己的看門狗是 600 秒）。
        /// </summary>
        private const int RepriceQueueHardCapMs = 15 * 60 * 1000;

        /// <summary>Key codes offered in the settings combo (VirtualKey values; 0 = disabled).</summary>
        public static readonly int[] SelectableKeyCodes = [0, 0x10 /* SHIFT */, 0x11 /* CTRL */, 0x12 /* ALT */];

        private static readonly InventoryType[] CanSellFrom =
        [
            InventoryType.Inventory1,
            InventoryType.Inventory2,
            InventoryType.Inventory3,
            InventoryType.Inventory4,
            InventoryType.ArmoryMainHand,
            InventoryType.ArmoryHead,
            InventoryType.ArmoryBody,
            InventoryType.ArmoryHands,
            InventoryType.ArmoryLegs,
            InventoryType.ArmoryFeets,
            InventoryType.ArmoryEar,
            InventoryType.ArmoryNeck,
            InventoryType.ArmoryWrist,
            InventoryType.ArmoryRings,
            InventoryType.ArmoryOffHand,
            InventoryType.RetainerPage1,
            InventoryType.RetainerPage2,
            InventoryType.RetainerPage3,
            InventoryType.RetainerPage4,
            InventoryType.RetainerPage5,
            InventoryType.RetainerPage6,
            InventoryType.RetainerPage7,
        ];

        private delegate void OpenForItemSlotDelegate(AgentInventoryContext* agent, InventoryType inventoryType, int slot, int a4, uint addonId);

        private sealed class ListingWatch
        {
            public required uint ItemId;
            public required string Name;
            public required HashSet<short> PreListingSlots;
            public required DateTime Deadline;

            /// <summary>掛在哪一名僱員身上。市場容器是「目前這名僱員的」，換人之後同一個索引指的是別的東西。</summary>
            public required ulong RetainerId;
        }

        private sealed class PendingReprice
        {
            public required short Slot;
            public required string Name;

            /// <summary>
            /// 軟期限：**只在真的有機會推進時才倒數**（被我們自己的巡迴／引擎佔用時會往後推）。
            /// </summary>
            public required DateTime Deadline;

            /// <summary>
            /// 硬期限：不管被什麼擋住都算數的逃生口，避免軟期限被無限延後變成永遠等待。
            /// </summary>
            public required DateTime HardDeadline;

            /// <summary>
            /// 這名僱員的 ID。🔴 沒有它的話，使用者離開後換到另一名僱員時，
            /// 這個 Slot 索引指的會是**別人的**市場格子，於是去改到不相干的道具。
            /// </summary>
            public required ulong RetainerId;

            /// <summary>最後一次被擋住的原因，放棄時要講給使用者聽。</summary>
            public string LastBlock = string.Empty;
        }

        private sealed class PendingMenuSelect
        {
            public required uint ItemId;
            public required string Name;
            public required string BaseName; // sheet name only, for window identity check
            public required DateTime Deadline;
        }

        private readonly Hook<OpenForItemSlotDelegate> hook;
        private readonly MarketGuiEventHandler gui;
        private readonly BatchReprice engine;
        private readonly MultiRetainerTour tour;
        private readonly string putUpForSaleText; // Addon sheet row 99

        // Every quick-list still waiting for its menu-jump to open a
        // RetainerSell window. A list (not a single pending slot) so a fast
        // burst of right-clicks - the whole point of queueing - never has one
        // selection clobber another before its window has had a chance to
        // open; each entry is matched to whichever RetainerSell instance
        // shows its item name and expires on its own if that never happens.
        private readonly List<PendingMenuSelect> pendingMenuSelects = [];

        // True while a RetainerSell window is expected to be open because of
        // our own automation (just filled + confirmed, waiting for the native
        // close) - as opposed to a genuine manual price adjustment. BatchReprice
        // reads IsCapListingInFlight to tell the two apart so listing item #2
        // never gets mistaken for the user manually overriding item #1's
        // in-flight headless reprice.
        private bool awaitingRetainerSellClose;

        // Listings confirmed at the cap that we are waiting to see land in a
        // market slot, plus slots waiting for their single-slot engine run.
        private readonly List<ListingWatch> watches = [];
        private readonly List<PendingReprice> repriceQueue = [];
        private readonly HashSet<short> claimedSlots = [];

        private bool suppressionHeld;

        private Configuration conf => Configuration.GetOrLoad();

        /// <summary>True while at least one quick-list menu-jump is waiting for its RetainerSell window to open.</summary>
        public bool IsQuickListPending => pendingMenuSelects.Count > 0;

        /// <summary>
        /// True while a RetainerSell window open right now is our own doing
        /// (menu-jump pending, or filled+confirmed and not yet closed) rather
        /// than the player manually adjusting a price. See <see cref="awaitingRetainerSellClose"/>.
        /// </summary>
        public bool IsCapListingInFlight => IsQuickListPending || awaitingRetainerSellClose;

        /// <summary>Total items anywhere in the quick-list pipeline right now - surfaced in the overlay UI.</summary>
        public int PendingCount => pendingMenuSelects.Count + watches.Count + repriceQueue.Count;

        /// <summary>True while any stage of a quick listing is still in flight.</summary>
        private bool HasWorkInFlight => IsQuickListPending || awaitingRetainerSellClose || watches.Count > 0 || repriceQueue.Count > 0;

        private void AcquireSuppression()
        {
            if (suppressionHeld)
                return;
            suppressionHeld = true;
            AutoRetainerBridge.AcquireSuppression("quick listing");
        }

        private void ReleaseSuppressionIfHeld()
        {
            if (!suppressionHeld)
                return;
            suppressionHeld = false;
            AutoRetainerBridge.ReleaseSuppression();
        }

        /// <summary>
        /// Releases our suppression as soon as nothing is in flight any more.
        /// The engine holds its own reference while it reprices, so the
        /// hand-off never leaves a gap.
        /// </summary>
        private void UpdateSuppression()
        {
            if (suppressionHeld && !HasWorkInFlight && !engine.IsRunning)
                ReleaseSuppressionIfHeld();
        }

        public QuickLister(MarketGuiEventHandler gui, BatchReprice engine, MultiRetainerTour tour)
        {
            this.gui = gui;
            this.engine = engine;
            this.tour = tour;

            var addonSheet = DataManager.GetExcelSheet<Addon>();
            putUpForSaleText = addonSheet != null && addonSheet.TryGetRow(99, out var row)
                ? row.Text.ExtractText()
                : string.Empty;

            hook = Hook.HookFromAddress<OpenForItemSlotDelegate>(
                AgentInventoryContext.Addresses.OpenForItemSlot.Value, OpenForItemSlotDetour);
            hook.Enable();

            AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "RetainerSell", OnRetainerSellSetup);
            AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "RetainerSell", OnRetainerSellFinalize);
            Framework.Update += OnFrameworkUpdate;
        }

        public void Dispose()
        {
            Framework.Update -= OnFrameworkUpdate;
            AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "RetainerSell", OnRetainerSellFinalize);
            AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "RetainerSell", OnRetainerSellSetup);
            hook.Dispose();
            ReleaseSuppressionIfHeld();
        }

        private void OpenForItemSlotDetour(AgentInventoryContext* agent, InventoryType inventoryType, int slot, int a4, uint addonId)
        {
            hook.OriginalDisposeSafe(agent, inventoryType, slot, a4, addonId);
            try
            {
                HandleContextMenuOpened(agent, inventoryType, slot);
            }
            catch (Exception e)
            {
                Log.Error(e, "QuickLister: error while handling inventory context menu");
            }
        }

        private void HandleContextMenuOpened(AgentInventoryContext* agent, InventoryType inventoryType, int slot)
        {
            if (conf.QuickListKeyCode == 0 || putUpForSaleText.Length == 0)
                return;
            // 🔴 CSFramework.Instance() 是 [StaticAddress(..., isPointer: true)]:產生器讀
            //    「指標的位址」再解參考一層,所以它會回 null(不帶 isPointer 的那種才保證
            //    非 null),特徵碼失配時則會擲。裸解參考 null 原生指標是
            //    AccessViolationException,在 .NET Core 屬 corrupted-state exception,
            //    try/catch 攔不到 ⇒ 只能事前判空。這裡是 hook detour 內的處理常式,
            //    取不到就當成「不快速上架」(fail-closed),使用者按鍵時無事發生而不是崩潰。
            var csFramework = CSFramework.Instance();
            if (csFramework == null || csFramework->WindowInactive)
                return;
            if (!Keys[(VirtualKey)conf.QuickListKeyCode])
                return;
            // Deliberately NOT gated on engine.IsRunning: the point of this
            // queue is that listing item #2 must not wait for item #1's
            // headless reprice to finish. BatchReprice's own abort checks
            // read IsCapListingInFlight to avoid mistaking this window for a
            // manual price adjustment while that reprice is in flight. The
            // full-retainer tour is a different, exclusive automation and
            // still blocks quick listing outright.
            if (IPCManager.IsLocked || tour.IsRunning)
                return;
            if (AutoRetainerBridge.IsBusy)
            {
                AutoRetainerBridge.ArmAvailabilityNotice();
                ChatGui.PrintError("[Marketbuddy] AutoRetainer is running - this action was skipped. You will be told when it finishes.".Loc());
                return;
            }

            if (!CanSellFrom.Contains(inventoryType))
                return;
            // The whole flow (cap listing + engine reprice) needs the sell list.
            if (!gui.IsRetainerSellListOpen)
                return;

            var inventoryManager = InventoryManager.Instance();
            var item = inventoryManager == null ? null : inventoryManager->GetInventorySlot(inventoryType, slot);
            if (item == null || item->ItemId == 0)
                return;

            var contextAddonId = agent->AgentInterface.GetAddonId();
            if (contextAddonId == 0)
                return;
            // 🔴 底下的 addon == null 是半套判空:AtkStage.Instance() 是
            //    [StaticAddress(..., isPointer: true)],會回 null;RaptureAtkUnitManager
            //    是 AtkStage +0x20 的裸指標欄位,也可能是 null。兩層都沒檢查的話,
            //    真正的爆點在 GetAddonById 之前就已經發生了。
            //    裸解參考 null 原生指標是 AccessViolationException,在 .NET Core 屬
            //    corrupted-state exception,try/catch 攔不到 ⇒ 只能事前逐層判空。
            var stage = AtkStage.Instance();
            if (stage == null || stage->RaptureAtkUnitManager == null)
                return;
            var addon = stage->RaptureAtkUnitManager->GetAddonById((ushort)contextAddonId);
            if (addon == null)
                return;

            for (var i = 0; i < agent->ContextItemCount; i++)
            {
                var param = agent->EventParams[agent->ContexItemStartIndex + i];
                if (param.Type != ValueType.String)
                    continue;
                if (param.GetValueAsString() != putUpForSaleText)
                    continue;
                if (agent->IsContextItemDisabled(i))
                {
                    Log.Debug($"QuickLister: found '{putUpForSaleText}' at {i} but it is disabled");
                    continue;
                }

                var name = ResolveItemName(item, out var baseName);
                pendingMenuSelects.Add(new PendingMenuSelect
                {
                    ItemId = item->ItemId,
                    Name = name,
                    BaseName = baseName,
                    Deadline = DateTime.UtcNow.AddMilliseconds(PendingTimeoutMs),
                });
                // Hold AutoRetainer off from the menu jump until the whole
                // list-then-reprice flow has drained (see UpdateSuppression).
                AcquireSuppression();

                AddonHelpers.FireContextMenuSelect(addon, i);
                agent->AgentInterface.Hide();
                addon->Close(true);
                Log.Debug($"QuickLister: selected '{putUpForSaleText}' ({i}) for {inventoryType}#{slot}");
                return;
            }

            // No "Put up for sale" entry (item cannot be listed): do nothing.
        }

        private void OnRetainerSellSetup(AddonEvent type, AddonArgs args)
        {
            if (pendingMenuSelects.Count == 0)
                return;

            // Identity guard: take over the oldest pending entry whose sheet
            // name shows up in the window. With several quick-lists queued,
            // RetainerSell is reused sequentially for each one in turn, so
            // matching by name (not just "something is pending") keeps a
            // burst of right-clicks from ever being attributed to the wrong
            // item. No match (extreme timing with a genuine manual open) is
            // left alone; every still-pending entry simply expires on its own.
            var itemNameNode = ((AddonRetainerSell*)(IntPtr)args.Addon)->ItemName;
            var shownName = itemNameNode != null ? Commons.Utf8StringToString(itemNameNode->NodeText) : string.Empty;

            var matchIndex = -1;
            for (var i = 0; i < pendingMenuSelects.Count; i++)
            {
                if (pendingMenuSelects[i].BaseName.Length == 0 ||
                    (shownName.Length > 0 && shownName.Contains(pendingMenuSelects[i].BaseName, StringComparison.Ordinal)))
                {
                    matchIndex = i;
                    break;
                }
            }

            if (matchIndex < 0)
            {
                Log.Debug($"QuickLister: RetainerSell shows '{shownName}', no pending quick-list matches, not taking over");
                return;
            }

            var pending = pendingMenuSelects[matchIndex];
            pendingMenuSelects.RemoveAt(matchIndex);

            // Snapshot the occupied market slots BEFORE confirming so the new
            // listing can be identified when the server ack lands.
            var preSlots = GetOccupiedMarketSlots();

            if (!gui.QuickListFillAndConfirm(args.Addon, Configuration.MAX_PRICE))
            {
                ChatGui.PrintError("[Marketbuddy] ??: quick listing failed - set the price manually".Loc(pending.Name));
                return;
            }

            // The window is still open pending the server's confirmation ack;
            // BatchReprice must not mistake it for a manual price adjustment
            // if a previous quick-list's headless reprice is running right now.
            awaitingRetainerSellClose = true;

            watches.Add(new ListingWatch
            {
                ItemId = pending.ItemId,
                Name = pending.Name,
                PreListingSlots = preSlots,
                Deadline = DateTime.UtcNow.AddMilliseconds(ListingAckTimeoutMs),
                RetainerId = CurrentRetainerId(),
            });
        }

        private void OnRetainerSellFinalize(AddonEvent type, AddonArgs args)
        {
            awaitingRetainerSellClose = false;
        }

        private void OnFrameworkUpdate(IFramework framework)
        {
            if (pendingMenuSelects.Count > 0)
                ExpirePendingMenuSelects();
            if (watches.Count > 0)
                PumpListingWatches();
            if (repriceQueue.Count > 0)
                PumpRepriceQueue();
            UpdateSuppression();
        }

        private void ExpirePendingMenuSelects()
        {
            for (var i = pendingMenuSelects.Count - 1; i >= 0; i--)
            {
                if (DateTime.UtcNow <= pendingMenuSelects[i].Deadline)
                    continue;
                var expired = pendingMenuSelects[i];
                pendingMenuSelects.RemoveAt(i);
                Log.Debug($"QuickLister: pending menu-select for '{expired.Name}' expired without a matching RetainerSell window");
            }
        }

        private void PumpListingWatches()
        {
            var inventoryManager = InventoryManager.Instance();
            var container = inventoryManager == null
                ? null
                : inventoryManager->GetInventoryContainer(InventoryType.RetainerMarket);

            var activeRetainer = CurrentRetainerId();

            for (var w = watches.Count - 1; w >= 0; w--)
            {
                var watch = watches[w];

                // 🔴 換僱員之後這個容器是**另一個人的**，同一個索引指的是別的東西。
                // 比對名稱／ItemId 也不夠：同款道具很可能同時掛在好幾名僱員身上。
                if (activeRetainer != 0 && watch.RetainerId != activeRetainer)
                {
                    watches.RemoveAt(w);
                    ChatGui.PrintError("[Marketbuddy] ??: you left that retainer before the listing was confirmed - check its price!".Loc(watch.Name));
                    continue;
                }

                var matchedSlot = (short)-1;
                if (container != null)
                {
                    for (short i = 0; i < container->Size; i++)
                    {
                        if (watch.PreListingSlots.Contains(i) || claimedSlots.Contains(i))
                            continue;
                        var slot = inventoryManager->GetInventorySlot(InventoryType.RetainerMarket, i);
                        if (slot == null || slot->ItemId != watch.ItemId)
                            continue;
                        matchedSlot = i;
                        break;
                    }
                }

                if (matchedSlot >= 0)
                {
                    watches.RemoveAt(w);
                    claimedSlots.Add(matchedSlot);
                    ChatGui.Print("[Marketbuddy] ??: listed at the price cap, now finding the right price...".Loc(watch.Name));
                    repriceQueue.Add(new PendingReprice
                    {
                        Slot = matchedSlot,
                        Name = watch.Name,
                        Deadline = DateTime.UtcNow.AddMilliseconds(RepriceQueueTimeoutMs),
                        HardDeadline = DateTime.UtcNow.AddMilliseconds(RepriceQueueHardCapMs),
                        RetainerId = watch.RetainerId,
                    });
                    continue;
                }

                if (DateTime.UtcNow > watch.Deadline)
                {
                    watches.RemoveAt(w);
                    ChatGui.PrintError("[Marketbuddy] ??: could not confirm the listing; if it is up, its price is the cap - check it!".Loc(watch.Name));
                }
            }
        }

        /// <summary>
        /// 目前這名僱員的 ID；不在僱員身上時回 0。
        /// 佇列裡的市場格索引只在**同一名僱員**身上有意義，所以每個項目都要綁著它。
        /// </summary>
        private static ulong CurrentRetainerId()
        {
            var retainerManager = RetainerManager.Instance();
            var active = retainerManager == null ? null : retainerManager->GetActiveRetainer();
            return active == null ? 0 : active->RetainerId;
        }

        /// <summary>上一次記錄過的阻塞原因；只在原因改變時才寫 log，避免每幀洗版。</summary>
        private string lastBlockReason = string.Empty;

        private void NoteBlockReason(string reason)
        {
            if (reason == lastBlockReason)
                return;
            lastBlockReason = reason;
            // Information：使用者的記錄等級會濾掉 Debug/Verbose。
            Log.Information($"[Marketbuddy] [MBDIAG] QUICKLIST-BLOCKED {reason}");
        }

        /// <summary>上一次跑 <see cref="PumpRepriceQueue"/> 的時刻，用來算出要把軟期限往後推多久。</summary>
        private DateTime lastQueuePump = DateTime.MinValue;

        private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

        private void PumpRepriceQueue()
        {
            var now = DateTime.UtcNow;
            // ⚠️ 這個函式只在佇列非空時才被呼叫，所以 lastQueuePump 可能是「上一批處理完」
            // 的很久以前——直接拿差值去延後期限，會在第一幀就把軟期限推掉好幾分鐘，
            // 等於期限形同虛設。夾在一幀的合理上限（遊戲卡頓／alt-tab 也一樣安全）。
            var elapsed = lastQueuePump == DateTime.MinValue
                ? TimeSpan.Zero
                : Min(now - lastQueuePump, TimeSpan.FromSeconds(1));
            lastQueuePump = now;

            // 🔴 為什麼要先判斷「被誰擋住」再決定期限要不要倒數：
            // 被**我們自己的**巡迴／引擎佔用時，這一格並沒有卡住，只是還沒輪到它。
            // 讓期限在那段時間繼續倒數，等於「使用者一邊巡迴一邊快速上架就會有道具
            // 被靜默丟掉、永久停在 999999999」——實機 log 抓到的正是這條路徑。
            var blocked =
                engine.IsRunning ? "engine already running"
                : tour.IsRunning ? "multi-retainer tour running"
                : IPCManager.IsLocked ? "IPC locked by another plugin"
                : !gui.IsRetainerSellListOpen ? "RetainerSellList not open"
                : string.Empty;

            // 前三個是「我們（或別的外掛）合法佔著引擎，等一下就會輪到」——暫停倒數。
            // 「出售品視窗沒開」不算：那代表使用者已經走開，這一格在這個僱員身上，
            // 不會自己好起來，該讓它照常到期並告訴使用者。
            var pauseCountdown = blocked.Length > 0 && blocked != "RetainerSellList not open";

            var activeRetainer = CurrentRetainerId();

            for (var i = repriceQueue.Count - 1; i >= 0; i--)
            {
                var entry = repriceQueue[i];
                if (blocked.Length > 0)
                    entry.LastBlock = blocked;

                // 🔴 使用者換到別的僱員了：這個 Slot 索引在新僱員的市場容器裡指的是
                // 不相干的道具，繼續留著遲早會改到別人的東西。立刻放棄並說清楚。
                if (activeRetainer != 0 && entry.RetainerId != activeRetainer)
                {
                    repriceQueue.RemoveAt(i);
                    claimedSlots.Remove(entry.Slot);
                    ChatGui.PrintError("[Marketbuddy] ??: still listed at the price cap (??) - you moved to another retainer, so reprice it manually!"
                        .Loc(entry.Name, Configuration.MAX_PRICE));
                    continue;
                }

                if (pauseCountdown)
                {
                    // 還沒輪到它，不算它的時間。硬期限照走。
                    entry.Deadline += elapsed;
                }

                if (now <= entry.Deadline && now <= entry.HardDeadline)
                    continue;

                repriceQueue.RemoveAt(i);
                claimedSlots.Remove(entry.Slot);
                // 🔑 放棄時**要講為什麼**。原本只說「請手動定價」，使用者看不出是
                // 撞到什麼——而那個原因我們其實一直都知道。
                ChatGui.PrintError(entry.LastBlock.Length > 0
                    ? "[Marketbuddy] ??: still listed at the price cap (??) - gave up after being blocked by: ?? - reprice it manually!"
                        .Loc(entry.Name, Configuration.MAX_PRICE, entry.LastBlock)
                    : "[Marketbuddy] ??: still listed at the price cap (??) - reprice it manually!"
                        .Loc(entry.Name, Configuration.MAX_PRICE));
            }

            if (repriceQueue.Count == 0)
            {
                lastBlockReason = string.Empty;
                return;
            }

            // 🔴 這五個條件原本全部靜默 return，所以「排進佇列卻沒人接手、30 秒後噴
            // 『請手動定價』」在 log 裡完全沒有線索（2026-08-02 實機遇到，只能靠推理）。
            // 佇列非空卻動不了時就把原因記下來——只在原因「改變」時記一次，不會洗版。
            if (blocked.Length > 0)
            {
                NoteBlockReason($"{blocked} (queued={repriceQueue.Count}, head='{repriceQueue[0].Name}'" +
                                (pauseCountdown ? ", countdown paused" : "") + ")");
                return;
            }

            var next = repriceQueue[0];
            if (engine.StartQuickReprice(next.Slot))
            {
                lastBlockReason = string.Empty;
                repriceQueue.RemoveAt(0);
                claimedSlots.Remove(next.Slot);
                return;
            }

            // On false: the engine cannot start right now; retry until the deadline.
            // StartQuickReprice 內部自己有更細的原因（CanStart 的 reason），這裡把它撈出來。
            NoteBlockReason($"engine refused: {engine.LastStartRefusalReason} (queued={repriceQueue.Count}, head='{next.Name}')");
        }

        private static string ResolveItemName(InventoryItem* item, out string baseName)
        {
            baseName = string.Empty;
            var itemSheet = DataManager.GetExcelSheet<Item>();
            if (itemSheet != null && itemSheet.TryGetRow(item->ItemId, out var row))
                baseName = row.Name.ExtractText();

            var name = baseName.Length > 0 ? baseName : $"#{item->ItemId}";
            if ((item->Flags & InventoryItem.ItemFlags.HighQuality) != 0)
                name += $" {(char)SeIconChar.HighQuality}";
            return name;
        }

        private static HashSet<short> GetOccupiedMarketSlots()
        {
            var occupied = new HashSet<short>();
            var inventoryManager = InventoryManager.Instance();
            var container = inventoryManager == null
                ? null
                : inventoryManager->GetInventoryContainer(InventoryType.RetainerMarket);
            if (container == null)
                return occupied;

            for (short i = 0; i < container->Size; i++)
            {
                var slot = inventoryManager->GetInventorySlot(InventoryType.RetainerMarket, i);
                if (slot != null && slot->ItemId != 0)
                    occupied.Add(i);
            }

            return occupied;
        }
    }
}
