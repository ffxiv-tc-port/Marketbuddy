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
        private const int RepriceQueueTimeoutMs = 30000; // engine must pick the slot up within this

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
        }

        private sealed class PendingReprice
        {
            public required short Slot;
            public required string Name;
            public required DateTime Deadline;
        }

        private readonly Hook<OpenForItemSlotDelegate> hook;
        private readonly MarketGuiEventHandler gui;
        private readonly BatchReprice engine;
        private readonly MultiRetainerReprice tour;
        private readonly string putUpForSaleText; // Addon sheet row 99

        // One quick-list in flight at a time: set when the menu entry is
        // selected, consumed when RetainerSell opens (or expires).
        private uint pendingItemId;
        private string pendingName = string.Empty;
        private string pendingBaseName = string.Empty; // sheet name only, for window identity check
        private DateTime pendingUntil = DateTime.MinValue;

        // Listings confirmed at the cap that we are waiting to see land in a
        // market slot, plus slots waiting for their single-slot engine run.
        private readonly List<ListingWatch> watches = [];
        private readonly List<PendingReprice> repriceQueue = [];
        private readonly HashSet<short> claimedSlots = [];

        private bool suppressionHeld;

        private Configuration conf => Configuration.GetOrLoad();

        /// <summary>True while a quick-list initiated RetainerSell open is expected.</summary>
        public bool IsQuickListPending => DateTime.UtcNow < pendingUntil;

        /// <summary>True while any stage of a quick listing is still in flight.</summary>
        private bool HasWorkInFlight => IsQuickListPending || watches.Count > 0 || repriceQueue.Count > 0;

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

        public QuickLister(MarketGuiEventHandler gui, BatchReprice engine, MultiRetainerReprice tour)
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
            Framework.Update += OnFrameworkUpdate;
        }

        public void Dispose()
        {
            Framework.Update -= OnFrameworkUpdate;
            AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "RetainerSell", OnRetainerSellSetup);
            hook.Dispose();
            ReleaseSuppressionIfHeld();
        }

        private void OpenForItemSlotDetour(AgentInventoryContext* agent, InventoryType inventoryType, int slot, int a4, uint addonId)
        {
            hook.Original(agent, inventoryType, slot, a4, addonId);
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
            if (CSFramework.Instance()->WindowInactive)
                return;
            if (!Keys[(VirtualKey)conf.QuickListKeyCode])
                return;
            if (IPCManager.IsLocked || engine.IsRunning || tour.IsRunning)
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
            var addon = AtkStage.Instance()->RaptureAtkUnitManager->GetAddonById((ushort)contextAddonId);
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

                pendingItemId = item->ItemId;
                pendingName = ResolveItemName(item, out var baseName);
                pendingBaseName = baseName;
                pendingUntil = DateTime.UtcNow.AddMilliseconds(PendingTimeoutMs);
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
            if (!IsQuickListPending)
                return;

            // Identity guard: only take over the window that shows the item we
            // just quick-listed. A mismatched window (extreme timing with a
            // manual open) is left alone and the pending state simply expires.
            var itemNameNode = ((AddonRetainerSell*)(IntPtr)args.Addon)->ItemName;
            if (pendingBaseName.Length > 0 && itemNameNode != null)
            {
                var shownName = Commons.Utf8StringToString(itemNameNode->NodeText);
                if (!shownName.Contains(pendingBaseName, StringComparison.Ordinal))
                {
                    Log.Debug($"QuickLister: RetainerSell shows '{shownName}', expected '{pendingBaseName}', not taking over");
                    return;
                }
            }

            var itemId = pendingItemId;
            var name = pendingName;
            pendingUntil = DateTime.MinValue; // consume

            // Snapshot the occupied market slots BEFORE confirming so the new
            // listing can be identified when the server ack lands.
            var preSlots = GetOccupiedMarketSlots();

            if (!gui.QuickListFillAndConfirm(args.Addon, Configuration.MAX_PRICE))
            {
                ChatGui.PrintError("[Marketbuddy] ??: quick listing failed - set the price manually".Loc(name));
                return;
            }

            watches.Add(new ListingWatch
            {
                ItemId = itemId,
                Name = name,
                PreListingSlots = preSlots,
                Deadline = DateTime.UtcNow.AddMilliseconds(ListingAckTimeoutMs),
            });
        }

        private void OnFrameworkUpdate(IFramework framework)
        {
            if (watches.Count > 0)
                PumpListingWatches();
            if (repriceQueue.Count > 0)
                PumpRepriceQueue();
            UpdateSuppression();
        }

        private void PumpListingWatches()
        {
            var inventoryManager = InventoryManager.Instance();
            var container = inventoryManager == null
                ? null
                : inventoryManager->GetInventoryContainer(InventoryType.RetainerMarket);

            for (var w = watches.Count - 1; w >= 0; w--)
            {
                var watch = watches[w];

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

        private void PumpRepriceQueue()
        {
            // Expire first so a stuck head never blocks the rest.
            for (var i = repriceQueue.Count - 1; i >= 0; i--)
            {
                if (DateTime.UtcNow <= repriceQueue[i].Deadline)
                    continue;
                var expired = repriceQueue[i];
                repriceQueue.RemoveAt(i);
                claimedSlots.Remove(expired.Slot);
                ChatGui.PrintError("[Marketbuddy] ??: still listed at the price cap (??) - reprice it manually!".Loc(expired.Name, Configuration.MAX_PRICE));
            }

            if (repriceQueue.Count == 0)
                return;
            if (engine.IsRunning || tour.IsRunning || IPCManager.IsLocked || !gui.IsRetainerSellListOpen)
                return;

            var next = repriceQueue[0];
            if (engine.StartQuickReprice(next.Slot))
            {
                repriceQueue.RemoveAt(0);
                claimedSlots.Remove(next.Slot);
            }
            // On false: the engine cannot start right now; retry until the deadline.
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
