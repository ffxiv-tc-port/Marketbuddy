using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Text;
using Dalamud.Hooking;
using Dalamud.Memory;
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
    /// <summary><see cref="QuickLister.OpenAndSelectVendorSell"/> 的結果。只有 <see cref="Fired"/> 代表真的選了一項。</summary>
    internal enum VendorSellMenuResult
    {
        /// <summary>選單開了，但本外掛的 detour 沒有被呼叫到：什麼都沒選。</summary>
        NotCalled,
        Fired,
        SlotMismatch,
        MenuUnavailable,
        EntryMissing,
        EntryDisabled,
        NotSent,
    }

    /// <summary>
    /// Native quick listing: while a configurable key is held and the retainer
    /// sell list is open, right-clicking a sellable item automatically picks
    /// the "Put up for sale" context menu entry (Addon sheet row 99 - no
    /// hardcoded strings).
    /// Strictly manual: one item per key-held right-click,
    /// nothing scans the inventory.
    /// </summary>
    internal sealed unsafe class QuickLister : IDisposable
    {
        private const int PendingTimeoutMs = 5000;       // menu jump -> RetainerSell must open within this
        private const int ListingAckTimeoutMs = 6000;    // confirm -> item appears in a market slot

        /// <summary>
        /// 引擎必須在這麼久之內接手這一格。
        /// 🔑 這段時間<b>只在真的有機會推進時才倒數</b>：被我們自己合法佔用引擎而擋住的時間不算，不然道具會被靜默丟掉、永久留在 999999999。
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
            public required uint ItemId;

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
            /// <summary>道具表的 Name／Singular 正規化後的候選；空陣列＝查不到表，任何上架視窗都配。</summary>
            public required string[] NameCandidates;
            public required DateTime Deadline;
        }

        private readonly Hook<OpenForItemSlotDelegate> hook;
        private readonly MarketGuiEventHandler gui;
        private readonly BatchReprice engine;
        private readonly MultiRetainerTour tour;
        private readonly string putUpForSaleText; // Addon sheet row 99
        private readonly string retainerSellText; // Addon sheet row 5480

        private sealed class VendorSellArm
        {
            public required InventoryType Type { get; init; }
            public required int Slot { get; init; }
            public required VendorSellKey Key { get; init; }
            public required int Quantity { get; init; }
            public VendorSellMenuResult Result = VendorSellMenuResult.NotCalled;
            public string MenuDump = string.Empty;
        }

        /// <summary>
        /// 只在 <see cref="OpenAndSelectVendorSell"/> 同步呼叫開選單的那一小段期間不為 null。
        /// 不為 null 時 detour 一律走賣給商店那條路，快速上架整條不碰。
        /// </summary>
        private VendorSellArm? vendorSellArm;

        // Every quick-list still waiting for its menu-jump to open a RetainerSell
        // window. A list (not a single pending slot) so a fast burst of right-clicks
        // never has one selection clobber another before its window has opened;
        // each entry is matched to whichever RetainerSell instance shows its item
        // name and expires on its own if that never happens.
        private readonly List<PendingMenuSelect> pendingMenuSelects = [];

        // True while a RetainerSell window is expected to be open because of our own
        // automation (just filled + confirmed, waiting for the native close) - as
        // opposed to a genuine manual price adjustment. BatchReprice reads
        // IsCapListingInFlight to tell the two apart so listing item #2 never gets
        // mistaken for the user manually overriding item #1's in-flight reprice.
        private bool awaitingRetainerSellClose;

        // Listings confirmed at the cap that we are waiting to see land in a
        // market slot, plus slots waiting for their single-slot engine run.
        private readonly List<ListingWatch> watches = [];
        private readonly List<PendingReprice> repriceQueue = [];
        private readonly HashSet<short> claimedSlots = [];

        private bool suppressionHeld;

        // 最近一次 RetainerSell PostSetup 的樣子，只給選單逾時那行診斷用。
        private DateTime lastSellSetupAt = DateTime.MinValue;
        private string lastSellSetupShown = string.Empty;
        private bool lastSellSetupMatched;

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

        /// <summary>false＝ClientStructs 沒解出右鍵選單函式的位址：不可建構本類別（建構子會在掛 hook 時擲）。</summary>
        internal static bool HookTargetResolved => AgentInventoryContext.Addresses.OpenForItemSlot.Value != 0;

        public QuickLister(MarketGuiEventHandler gui, BatchReprice engine, MultiRetainerTour tour)
        {
            this.gui = gui;
            this.engine = engine;
            this.tour = tour;

            var addonSheet = DataManager.GetExcelSheet<Addon>();
            putUpForSaleText = addonSheet != null && addonSheet.TryGetRow(99, out var row)
                ? row.Text.ExtractText()
                : string.Empty;
            retainerSellText = addonSheet != null && addonSheet.TryGetRow(5480, out var sellRow)
                ? sellRow.Text.ExtractText()
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
            if (vendorSellArm is { } arm)
            {
                HandleVendorSellMenu(arm, agent, inventoryType, slot);
                return;
            }

            if (conf.QuickListKeyCode == 0 || putUpForSaleText.Length == 0)
                return;
            // 🔴 CSFramework.Instance() 是 [StaticAddress(..., isPointer: true)]:產生器讀
            //    「指標的位址」再解參考一層,所以它會回 null,特徵碼失配時則會擲。
            //    裸解參考 null 原生指標是 AccessViolationException,try/catch 攔不到 ⇒ 只能事前判空。
            //    這裡是 hook detour 內的處理常式,取不到就當成「不快速上架」(fail-closed),
            //    使用者按鍵時無事發生而不是崩潰。
            var csFramework = CSFramework.Instance();
            if (csFramework == null || csFramework->WindowInactive)
                return;
            if (!Keys[(VirtualKey)conf.QuickListKeyCode])
                return;
            // Deliberately NOT gated on engine.IsRunning: listing item #2 must not wait
            // for item #1's headless reprice to finish. BatchReprice's own abort checks
            // read IsCapListingInFlight to avoid mistaking this window for a manual price
            // adjustment. The full-retainer tour is a different, exclusive automation and
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
            //    真正的爆點在 GetAddonById 之前就已經發生了(AVE,try/catch 攔不到)
            //    ⇒ 只能事前逐層判空。
            var stage = AtkStage.Instance();
            if (stage == null || stage->RaptureAtkUnitManager == null)
                return;
            var addon = stage->RaptureAtkUnitManager->GetAddonById((ushort)contextAddonId);
            if (addon == null)
                return;

            var sawDisabledEntry = false;
            for (var i = 0; i < agent->ContextItemCount; i++)
            {
                var param = agent->EventParams[agent->ContexItemStartIndex + i];
                if (param.Type != ValueType.String)
                    continue;
                // GetValueAsString() decodes the string pointer as raw UTF-8, so
                // an entry that carries SeString payloads never compares equal to
                // the plain sheet text. Decode through SeString and compare
                // TextValue, which puts this side on the same basis as
                // putUpForSaleText (Addon row 99, already ExtractText()ed).
                if (param.String.Value == null)
                    continue;
                var entryText = MemoryHelper
                    .ReadSeStringNullTerminated((nint)param.String.Value).TextValue;
                if (entryText != putUpForSaleText)
                    continue;
                if (agent->IsContextItemDisabled(i))
                {
                    Log.Debug($"QuickLister: found '{putUpForSaleText}' at {i} but it is disabled");
                    sawDisabledEntry = true;
                    continue;
                }

                // 守衛要在建立呼叫端狀態**之前**問，因為底下那發 callback 可能同步
                // 開出 RetainerSell，待處理項必須先排好。CanPress 只問不記，
                // 同一幀同一執行緒接著呼叫的 TryPress 一定得到相同答案。
                var contextMenuName = AddonHelpers.ReadAddonName(addon);
                if (contextMenuName.Length == 0)
                    contextMenuName = "ContextMenu";
                if (!AddonPressGuard.CanPress(contextMenuName, (nint)addon, PressKind.Terminal))
                {
                    // 上一次的選單還沒走完生命週期（位址還在）。這一輪就不做 ——
                    // 刻意**不**排重試：使用者再按一次右鍵就是了。
                    Log.Information(
                        "QuickLister: 上一個右鍵選單還沒關乾淨，這一次的快速上架跳過（不對可能正在關閉的選單再按一次）");
                    WriteDiag("選單守衛擋下", item->ItemId, ResolveItemName(item, out _), "原因=上一個右鍵選單還沒關乾淨");
                    return;
                }

                var name = ResolveItemName(item, out var nameCandidates);
                var pending = new PendingMenuSelect
                {
                    ItemId = item->ItemId,
                    Name = name,
                    NameCandidates = nameCandidates,
                    Deadline = DateTime.UtcNow.AddMilliseconds(PendingTimeoutMs),
                };
                pendingMenuSelects.Add(pending);
                // Hold AutoRetainer off from the menu jump until the whole
                // list-then-reprice flow has drained (see UpdateSuppression).
                AcquireSuppression();

                // 🔴🔴 送出這一發之後，**不准在同一個呼叫堆疊裡再碰這扇窗**。
                //    ⇒ 緊接在後面再 Hide()+Close(true) 就是「對正在關閉／已經關掉的窗
                //      再操作一次」，也就是原生 AccessViolationException。AVE 在 .NET Core 屬
                //      corrupted-state exception，try/catch（包含外層那個 detour 的 catch）
                //      完全攔不到 ⇒ 唯一的防護是不要送第二次。
                if (!AddonHelpers.FireContextMenuSelect(addon, i, contextMenuName, out var closedByCallback))
                {
                    // 守衛在 CanPress 之後才擋下（同一幀同一執行緒理論上不會發生，但回傳值
                    // 不能吞掉）：把剛排進去的待處理項收回來，否則它會一直掛到逾時，期間
                    // IsCapListingInFlight 為真，引擎會誤判有上限掛單正在飛。
                    // 抑制旗標不必手動放：UpdateSuppression 下一幀看到沒有在飛的工作就會放掉。
                    pendingMenuSelects.Remove(pending);
                    Log.Information(
                        "QuickLister: 守衛擋下了這一次的選單選取，本次快速上架取消（待處理項已收回）");
                    WriteDiag("選單守衛擋下", pending.ItemId, pending.Name, "原因=送出選單選取時被守衛擋下");
                    return;
                }

                if (closedByCallback)
                {
                    // 原生端已經在回到這裡之前跑完 Hide+Close 了 —— 這裡什麼都不做才是對的。
                    MarketDiag.Trace(
                        $"QuickLister: 已選取『{putUpForSaleText}』({i}) 給 {inventoryType}#{slot}；選單已由原生端關閉");
                    return;
                }

                // 回 false ＝原生端走的是不關窗那條。剩下唯一沒被排除的情形是「處理常式自己把窗關掉了」——
                // 那件事離線證不出來，所以碰它之前用 addon id 重查一次，當作額外一道閘門（加分，不是證明；
                // 真正把這個站移出危險窗口的是上面那個 closedByCallback 分歧）。
                // 🔴 這裡對存下來的位址**只做等值比較**：存的是 id，解參考的是遊戲這一幀自己
                //    交回來的指標。同 id 換成另一扇窗時比較會不相等，一樣不碰。
                var afterStage = AtkStage.Instance();
                var afterManager = afterStage == null ? null : afterStage->RaptureAtkUnitManager;
                var stillOpen = afterManager == null
                    ? null
                    : afterManager->GetAddonById((ushort)contextAddonId);
                if (stillOpen != addon)
                {
                    // 走到這裡＝這扇窗已經不在 AllLoadedUnitsList 裡了（被 Finalize／釋放，
                    // 或同一個 id 換成別的實例）。離線證不出來這會不會真的發生，
                    // 所以這一行是要使用者回報的診斷。
                    Log.Information(
                        $"QuickLister: 已選取『{putUpForSaleText}』({i}) 給 {inventoryType}#{slot}；callback 說沒關窗，但選單已經不在載入清單裡（已釋放或換了實例），不再動它");
                    return;
                }

                agent->AgentInterface.Hide();
                stillOpen->Close(true);
                Log.Debug(
                    $"QuickLister: 已選取『{putUpForSaleText}』({i}) 給 {inventoryType}#{slot}；選單由我們自己關掉");
                return;
            }

            // No "Put up for sale" entry (item cannot be listed): do nothing.
            WriteDiag(sawDisabledEntry ? "選單出售停用" : "選單沒有出售", item->ItemId, ResolveItemName(item, out _),
                sawDisabledEntry ? "原因=右鍵選單的出售那一項是停用的" : "原因=右鍵選單裡沒有出售那一項");
        }

        /// <summary>Addon 表第 5480 列（「委託僱員出售物品」）讀得到，<see cref="VendorSeller"/> 才能用。</summary>
        internal bool CanVendorSell => retainerSellText.Length > 0;

        /// <summary>
        /// 替 <see cref="VendorSeller"/> 打開某一格的右鍵選單並選「委託僱員出售物品」。
        /// 🔑 選取發生在同一個呼叫堆疊裡的 detour：原生端剛開好選單，走的是與快速上架相同的守衛與送出路徑。
        /// </summary>
        /// <param name="menuDump">沒選成（找不到／停用）時的選單內容，給呼叫端寫進 log。</param>
        internal VendorSellMenuResult OpenAndSelectVendorSell(InventoryType type, short slot, uint ownerAddonId,
            VendorSellKey key, int quantity, out string menuDump)
        {
            menuDump = string.Empty;
            if (vendorSellArm != null || retainerSellText.Length == 0)
                return VendorSellMenuResult.NotSent;
            var agent = AgentInventoryContext.Instance();
            if (agent == null)
                return VendorSellMenuResult.MenuUnavailable;

            var arm = new VendorSellArm { Type = type, Slot = slot, Key = key, Quantity = quantity };
            vendorSellArm = arm;
            try
            {
                agent->OpenForItemSlot(type, slot, 0, ownerAddonId);
            }
            finally
            {
                vendorSellArm = null;
            }

            menuDump = arm.MenuDump;
            return arm.Result;
        }

        private void HandleVendorSellMenu(VendorSellArm arm, AgentInventoryContext* agent, InventoryType inventoryType, int slot)
        {
            // 只認第一次、而且是我們指名的那一格；其餘一律不選任何一項。
            if (arm.Result != VendorSellMenuResult.NotCalled)
                return;
            if (inventoryType != arm.Type || slot != arm.Slot)
            {
                arm.Result = VendorSellMenuResult.SlotMismatch;
                return;
            }

            var inventoryManager = InventoryManager.Instance();
            var item = inventoryManager == null ? null : inventoryManager->GetInventorySlot(inventoryType, slot);
            if (item == null || item->ItemId != arm.Key.ItemId || item->Quantity != arm.Quantity ||
                ((item->Flags & InventoryItem.ItemFlags.HighQuality) != 0) != arm.Key.Hq)
            {
                arm.Result = VendorSellMenuResult.SlotMismatch;
                return;
            }

            arm.Result = VendorSellMenuResult.MenuUnavailable;
            var contextAddonId = agent->AgentInterface.GetAddonId();
            if (contextAddonId == 0)
                return;
            // 與快速上架同一套逐層判空：AtkStage.Instance() 與 RaptureAtkUnitManager 都可能是 null。
            var stage = AtkStage.Instance();
            if (stage == null || stage->RaptureAtkUnitManager == null)
                return;
            var addon = stage->RaptureAtkUnitManager->GetAddonById((ushort)contextAddonId);
            if (addon == null)
                return;

            var start = Math.Clamp(agent->ContexItemStartIndex, 0, agent->EventParams.Length);
            var count = Math.Clamp(agent->ContextItemCount, 0, agent->EventParams.Length - start);
            var labels = new List<string>(count);
            var sawDisabled = false;
            for (var i = 0; i < count; i++)
            {
                var param = agent->EventParams[start + i];
                var entryText = param.Type == ValueType.String && param.String.Value != null
                    ? MemoryHelper.ReadSeStringNullTerminated((nint)param.String.Value).TextValue
                    : string.Empty;
                labels.Add($"{i}:{entryText}");
                if (entryText != retainerSellText)
                    continue;
                if (agent->IsContextItemDisabled(i))
                {
                    sawDisabled = true;
                    continue;
                }

                var contextMenuName = AddonHelpers.ReadAddonName(addon);
                if (contextMenuName.Length == 0)
                    contextMenuName = "ContextMenu";
                if (!AddonPressGuard.CanPress(contextMenuName, (nint)addon, PressKind.Terminal) ||
                    !AddonHelpers.FireContextMenuSelect(addon, i, contextMenuName, out var closedByCallback))
                {
                    arm.Result = VendorSellMenuResult.NotSent;
                    return;
                }

                arm.Result = VendorSellMenuResult.Fired;
                // 🔴 原生端說它關了窗，就一個字都不再碰（理由同上方快速上架那一段）。
                if (closedByCallback)
                    return;

                var afterStage = AtkStage.Instance();
                var afterManager = afterStage == null ? null : afterStage->RaptureAtkUnitManager;
                var stillOpen = afterManager == null ? null : afterManager->GetAddonById((ushort)contextAddonId);
                if (stillOpen != addon)
                {
                    Log.Information(
                        $"QuickLister: 已選取『{retainerSellText}』({i}) 給 {inventoryType}#{slot}；callback 說沒關窗，但選單已經不在載入清單裡，不再動它");
                    return;
                }

                agent->AgentInterface.Hide();
                stillOpen->Close(true);
                Log.Information(
                    $"QuickLister: 已選取『{retainerSellText}』({i}) 給 {inventoryType}#{slot}；原生端沒關窗，選單由我們自己關掉");
                return;
            }

            arm.Result = sawDisabled ? VendorSellMenuResult.EntryDisabled : VendorSellMenuResult.EntryMissing;
            arm.MenuDump = string.Join(" | ", labels);
        }

        private void OnRetainerSellSetup(AddonEvent type, AddonArgs args)
        {
            if (pendingMenuSelects.Count == 0)
                return;

            // Identity guard: take over the oldest pending entry whose sheet name equals the
            // window's item name (ItemNameMatch, normalized). A Singular hit beats a Name-only hit:
            // 鮮奶油's Name is 奶油, the same as another item. The window exposes no item id.
            // No match (a genuine manual open) is left alone; pending entries expire on their own.
            var itemNameNode = ((AddonRetainerSell*)(IntPtr)args.Addon)->ItemName;
            var shownName = itemNameNode != null ? Commons.Utf8StringToString(itemNameNode->NodeText) : string.Empty;

            var matchIndex = -1;
            var bestRank = int.MaxValue;
            for (var i = 0; i < pendingMenuSelects.Count; i++)
            {
                var candidates = pendingMenuSelects[i].NameCandidates;
                var rank = candidates.Length == 0 ? 0 : ItemNameMatch.MatchRank(candidates, shownName);
                if (rank < 0 || rank >= bestRank)
                    continue;
                matchIndex = i;
                bestRank = rank;
                if (rank == 0)
                    break;
            }

            lastSellSetupAt = DateTime.UtcNow;
            lastSellSetupShown = shownName;
            lastSellSetupMatched = matchIndex >= 0;

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

            if (!gui.QuickListFillAndConfirm(args.Addon, Configuration.MAX_PRICE, out var fillFailReason))
            {
                ChatGui.PrintError("[Marketbuddy] ??: quick listing failed - set the price manually".Loc(pending.Name));
                WriteDiag("填價失敗", pending.ItemId, pending.Name, $"原因={fillFailReason}");
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

                var createdAt = expired.Deadline.AddMilliseconds(-PendingTimeoutMs);
                var sellSeen = lastSellSetupAt >= createdAt
                    ? $"等待期間上架視窗開過（{(DateTime.UtcNow - lastSellSetupAt).TotalSeconds:0.0} 秒前，顯示『{lastSellSetupShown}』，" +
                      (lastSellSetupMatched ? "配給了某一筆待辦）" : "沒配上任何待辦）")
                    : "等待期間沒有上架視窗開啟";
                WriteDiag("選單逾時", expired.ItemId, expired.Name, $"原因=選了出售之後 {PendingTimeoutMs / 1000} 秒內沒等到對應的上架視窗；{sellSeen}");
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
                    WriteDiag("上架後換僱員", watch.ItemId, watch.Name,
                        $"原因=上架確認前換到別的僱員；{SameItemSlotsText(watch.ItemId, watch.PreListingSlots)}（以下格數是新僱員的）");
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
                        ItemId = watch.ItemId,
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
                    var retainerState = activeRetainer == 0 ? "目前不在僱員身上" : "還在同一名僱員";
                    WriteDiag("上架確認逾時", watch.ItemId, watch.Name,
                        $"原因=按下確認後 {ListingAckTimeoutMs / 1000} 秒內市場格沒出現這件道具；{retainerState}；{SameItemSlotsText(watch.ItemId, watch.PreListingSlots)}");
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
            // 診斷開關關著時不寫；把「詳細市場診斷」打開才寫 Information。
            MarketDiag.Trace($"[Marketbuddy] [MBDIAG] QUICKLIST-BLOCKED {reason}");
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
            // 被靜默丟掉、永久停在 999999999」。
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
                    WriteDiag("定價佇列換僱員", entry.ItemId, entry.Name,
                        $"原因=輪到定價前換到別的僱員（原市場格 {entry.Slot}）；最後擋住的原因={OrUnknown(entry.LastBlock)}（以下格數是新僱員的）");
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
                // 🔑 放棄時**要講為什麼**：只說「請手動定價」使用者看不出是撞到什麼。
                ChatGui.PrintError(entry.LastBlock.Length > 0
                    ? "[Marketbuddy] ??: still listed at the price cap (??) - gave up after being blocked by: ?? - reprice it manually!"
                        .Loc(entry.Name, Configuration.MAX_PRICE, entry.LastBlock)
                    : "[Marketbuddy] ??: still listed at the price cap (??) - reprice it manually!"
                        .Loc(entry.Name, Configuration.MAX_PRICE));
                var whichDeadline = now > entry.HardDeadline ? $"{RepriceQueueHardCapMs / 60000} 分鐘硬上限" : $"{RepriceQueueTimeoutMs / 1000} 秒期限";
                WriteDiag("定價佇列放棄", entry.ItemId, entry.Name,
                    $"原因=引擎沒在{whichDeadline}內接手（市場格 {entry.Slot}）；最後擋住的原因={OrUnknown(entry.LastBlock)}；引擎上次拒絕={OrUnknown(engine.LastStartRefusalReason)}");
            }

            if (repriceQueue.Count == 0)
            {
                lastBlockReason = string.Empty;
                return;
            }

            // 🔴 佇列非空卻動不了時把原因記下來（否則「排進佇列卻沒人接手」在 log 裡完全沒有線索）——
            // 只在原因「改變」時記一次，不會洗版。
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

        private static string ResolveItemName(InventoryItem* item, out string[] nameCandidates)
        {
            var baseName = string.Empty;
            nameCandidates = [];
            var itemSheet = DataManager.GetExcelSheet<Item>();
            if (itemSheet != null && itemSheet.TryGetRow(item->ItemId, out var row))
            {
                baseName = row.Name.ExtractText();
                nameCandidates = ItemNameMatch.Candidates(baseName, row.Singular.ExtractText());
            }

            var name = baseName.Length > 0 ? baseName : $"#{item->ItemId}";
            if ((item->Flags & InventoryItem.ItemFlags.HighQuality) != 0)
                name += $" {(char)SeIconChar.HighQuality}";
            return name;
        }

        private static string OrUnknown(string s) => s.Length > 0 ? s : "不知道";

        /// <summary>
        /// 快速上架失敗出口的診斷，前綴 <c>[QuickListDiag]</c>。只在失敗事件當下呼叫一次（不是每幀）；
        /// 不掛詳細市場診斷開關。讀不到的欄位寫「不知道」，不省略。原生物件只在這一幀讀、不保存。
        /// </summary>
        private void WriteDiag(string stage, uint itemId, string name, string detail)
        {
            try
            {
                var engineText = engine.IsRunning ? $"跑中（佇列 {engine.QueuedJobCount}）" : "閒置";
                Log.Information(
                    $"[QuickListDiag] 階段={stage} 道具={OrUnknown(name)}(#{itemId}) {detail} " +
                    $"市場格={MarketSlotsText()} 上架視窗={RetainerSellStateText()} " +
                    $"出售清單={(gui.IsRetainerSellListOpen ? "開著" : "關著")} 引擎={engineText} " +
                    $"快速上架待辦=選單{pendingMenuSelects.Count}/等確認{watches.Count}/等定價{repriceQueue.Count}");
            }
            catch (Exception e)
            {
                Log.Information($"[QuickListDiag] 階段={stage} 道具={OrUnknown(name)}(#{itemId}) 診斷本身失敗：{e.Message}");
            }
        }

        /// <summary>「已佔 N/上限 M（僱員資料報 K）」；容器沒載入時整段寫不知道。</summary>
        private static string MarketSlotsText()
        {
            var retainerManager = RetainerManager.Instance();
            var active = retainerManager == null ? null : retainerManager->GetActiveRetainer();
            var retainerCount = active == null ? "不知道" : active->MarketItemCount.ToString();

            var inventoryManager = InventoryManager.Instance();
            var container = inventoryManager == null
                ? null
                : inventoryManager->GetInventoryContainer(InventoryType.RetainerMarket);
            if (container == null || !container->IsLoaded || container->Size <= 0)
                return $"不知道（僱員資料報 {retainerCount}）";

            var occupied = 0;
            for (short i = 0; i < container->Size; i++)
            {
                var slot = inventoryManager->GetInventorySlot(InventoryType.RetainerMarket, i);
                if (slot != null && slot->ItemId != 0)
                    occupied++;
            }

            return $"已佔 {occupied}/上限 {container->Size}（僱員資料報 {retainerCount}）";
        }

        /// <summary>以名稱查當幀的 RetainerSell；不保存位址。</summary>
        private static string RetainerSellStateText()
        {
            var addon = Commons.GetUnitBase("RetainerSell");
            if (addon == null)
                return "沒開";
            return addon->IsVisible ? "看得到" : "已載入但隱藏";
        }

        /// <summary>市場裡同一個道具 id 現在佔了幾格、其中幾格是上架前就有的、幾格已被別的快速上架認領。</summary>
        private string SameItemSlotsText(uint itemId, HashSet<short> preListingSlots)
        {
            var inventoryManager = InventoryManager.Instance();
            var container = inventoryManager == null
                ? null
                : inventoryManager->GetInventoryContainer(InventoryType.RetainerMarket);
            if (container == null || !container->IsLoaded || container->Size <= 0)
                return "同道具在市場的格數=不知道";

            int total = 0, pre = 0, claimed = 0;
            for (short i = 0; i < container->Size; i++)
            {
                var slot = inventoryManager->GetInventorySlot(InventoryType.RetainerMarket, i);
                if (slot == null || slot->ItemId != itemId)
                    continue;
                total++;
                if (preListingSlots.Contains(i))
                    pre++;
                else if (claimedSlots.Contains(i))
                    claimed++;
            }

            return $"同道具在市場 {total} 格（上架前已有 {pre}、已被別的快速上架認領 {claimed}）";
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
