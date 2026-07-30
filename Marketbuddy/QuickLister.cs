using System;
using System.Linq;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
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
    /// Native quick listing: while a configurable key is held and a retainer
    /// selling context is open, right-clicking a sellable item automatically
    /// picks the "Put up for sale" context menu entry (Addon sheet row 99 -
    /// no hardcoded strings). The game then opens RetainerSell for that item
    /// and Marketbuddy's existing flow takes over: auto compare, price fill,
    /// the opt-in listing thresholds, and auto confirm.
    ///
    /// Same recipe as AutoRetainer's QuickSellItems, but hooked through the
    /// bundled ClientStructs member AgentInventoryContext.OpenForItemSlot
    /// (resolved address, no manual signature scan). Strictly manual: one
    /// item per key-held right-click, nothing scans the inventory.
    /// </summary>
    internal sealed unsafe class QuickLister : IDisposable
    {
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

        private static readonly string[] RetainerSellContextAddons =
        [
            "RetainerSellList",
            "RetainerGrid0",
            "RetainerGrid1",
            "RetainerGrid2",
            "RetainerGrid3",
            "RetainerGrid4",
            "RetainerCrystalGrid",
        ];

        private delegate void OpenForItemSlotDelegate(AgentInventoryContext* agent, InventoryType inventoryType, int slot, int a4, uint addonId);

        private readonly Hook<OpenForItemSlotDelegate> hook;
        private readonly BatchReprice engine;
        private readonly MultiRetainerReprice tour;
        private readonly string putUpForSaleText; // Addon sheet row 99

        private Configuration conf => Configuration.GetOrLoad();

        public QuickLister(BatchReprice engine, MultiRetainerReprice tour)
        {
            this.engine = engine;
            this.tour = tour;

            var addonSheet = DataManager.GetExcelSheet<Addon>();
            putUpForSaleText = addonSheet != null && addonSheet.TryGetRow(99, out var row)
                ? row.Text.ExtractText()
                : string.Empty;

            hook = Hook.HookFromAddress<OpenForItemSlotDelegate>(
                AgentInventoryContext.Addresses.OpenForItemSlot.Value, OpenForItemSlotDetour);
            hook.Enable();
        }

        public void Dispose()
        {
            hook.Dispose();
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
            if (!CanSellFrom.Contains(inventoryType))
                return;
            if (!IsRetainerSellContextOpen())
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

                AddonHelpers.FireContextMenuSelect(addon, i);
                agent->AgentInterface.Hide();
                addon->Close(true);
                Log.Debug($"QuickLister: selected '{putUpForSaleText}' ({i}) for {inventoryType}#{slot}");
                return;
            }

            // No "Put up for sale" entry (item cannot be listed): do nothing.
        }

        private static bool IsRetainerSellContextOpen()
        {
            foreach (var name in RetainerSellContextAddons)
            {
                if (AddonHelpers.GetReadyAddon(name) != null)
                    return true;
            }

            return false;
        }
    }
}
