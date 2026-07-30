using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Network.Structures;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;
using Marketbuddy.Common;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    /// <summary>
    /// One-click "relist every listed slot of the current retainer at
    /// (lowest market price - undercut)" batch engine.
    ///
    /// Headless: market data is requested via InfoProxyItemSearch.RequestData()
    /// and captured through the IMarketBoard.OfferingsReceived event; prices are
    /// applied via InventoryManager.SetRetainerMarketPrice(). RetainerSell /
    /// ItemSearchResult windows are never opened and no native UI is touched.
    /// No hooks, no packet forgery, no memory patches; a stuck market query is
    /// handled by timeout + one backoff retry, then the slot is skipped.
    ///
    /// Strictly manual: runs only when the user clicks the button while a
    /// retainer's sell list (RetainerSellList) is open. Cancellable at any time
    /// via the cancel button or ESC; closing the sell list also aborts.
    /// </summary>
    internal sealed unsafe class BatchReprice : IDisposable
    {
        // Pacing / safety constants. Requests are throttled and retried with
        // backoff instead of patching the client's "please wait" throttle path.
        private const int ThrottleMs = 500;          // min gap between two market data requests
        private const int OfferingsTimeoutMs = 5000; // max wait for market data per attempt
        private const int RetryBackoffMs = 2000;     // extra wait before the retry attempt
        private const int MaxAttempts = 2;           // 1 initial attempt + 1 retry per slot
        private const int SlotWatchdogSeconds = 30;  // hard per-slot watchdog (queue-level safety net)

        private enum SlotPhase
        {
            Throttle,
            Request,
            WaitOfferings,
            Apply
        }

        private sealed class SlotJob
        {
            public required short Slot;
            public required uint ItemId;
            public required bool IsHq;
            public required string Name;
            public int Attempt;
            public SlotPhase Phase = SlotPhase.Throttle;
            public DateTime NotBefore = DateTime.MinValue;
            public DateTime WaitStart;
        }

        private readonly MarketGuiEventHandler gui;
        private readonly TickTaskQueue queue = new();

        // Market data capture state. Only one request is ever in flight.
        private readonly List<(uint Price, bool IsHq, ulong RetainerId)> captured = new();
        private bool offeringsPending;
        private bool offeringsReceived;
        private uint pendingItemId;
        private int lastAcceptedRequestId = int.MinValue;
        private DateTime lastRequestAt = DateTime.MinValue;

        // Batch state (for UI / summary).
        private HashSet<ulong> ownRetainerIds = new();

        public bool IsRunning => queue.IsRunning;
        public int TotalSlots { get; private set; }
        public int ProcessedSlots { get; private set; }
        public int RepricedCount { get; private set; }
        public int SkippedCount { get; private set; }
        public int FailedCount { get; private set; }
        public string CurrentItemName { get; private set; } = string.Empty;

        private Configuration conf => Configuration.GetOrLoad();

        public BatchReprice(MarketGuiEventHandler gui)
        {
            this.gui = gui;
            queue.Aborted += OnQueueAborted;
            queue.Completed += OnQueueCompleted;
            MarketBoard.OfferingsReceived += OnOfferingsReceived;
            Framework.Update += OnFrameworkUpdate;
        }

        public void Dispose()
        {
            Framework.Update -= OnFrameworkUpdate;
            MarketBoard.OfferingsReceived -= OnOfferingsReceived;
            // Detach handlers first so an unload-time abort stays silent.
            queue.Aborted -= OnQueueAborted;
            queue.Completed -= OnQueueCompleted;
            if (queue.IsRunning)
                queue.Abort("plugin unloading");
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

            if (!gui.IsRetainerSellListOpen)
            {
                reason = "retainer sell list is not open".Loc();
                return false;
            }

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
                if (reason.Length > 0)
                    ChatGui.PrintError("[Marketbuddy] Cannot start: ??".Loc(reason));
                return;
            }

            var inventoryManager = InventoryManager.Instance();
            var retainerManager = RetainerManager.Instance();
            if (inventoryManager == null || retainerManager == null)
                return;
            var container = inventoryManager->GetInventoryContainer(InventoryType.RetainerMarket);
            if (container == null)
                return;

            // Snapshot every listed slot of the active retainer. Pointers are
            // null-checked before any dereference.
            var itemSheet = DataManager.GetExcelSheet<Item>();
            var jobs = new List<SlotJob>();
            for (var i = 0; i < container->Size; i++)
            {
                var slot = inventoryManager->GetInventorySlot(InventoryType.RetainerMarket, i);
                if (slot == null || slot->ItemId == 0)
                    continue;

                var name = $"#{slot->ItemId}";
                if (itemSheet != null && itemSheet.TryGetRow(slot->ItemId, out var row))
                    name = row.Name.ExtractText();
                var isHq = (slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
                if (isHq)
                    name += $" {(char)SeIconChar.HighQuality}";

                jobs.Add(new SlotJob { Slot = slot->Slot, ItemId = slot->ItemId, IsHq = isHq, Name = name });
            }

            if (jobs.Count == 0)
            {
                ChatGui.PrintError("[Marketbuddy] Cannot start: ??".Loc("this retainer has nothing listed".Loc()));
                return;
            }

            // All of our own retainers: if the lowest listing is ours (any
            // retainer), never undercut ourselves.
            ownRetainerIds = new HashSet<ulong>();
            foreach (var retainer in retainerManager->Retainers)
            {
                if (retainer.RetainerId != 0)
                    ownRetainerIds.Add(retainer.RetainerId);
            }

            TotalSlots = jobs.Count;
            ProcessedSlots = 0;
            RepricedCount = 0;
            SkippedCount = 0;
            FailedCount = 0;
            CurrentItemName = string.Empty;
            lastAcceptedRequestId = int.MinValue;
            offeringsPending = false;
            offeringsReceived = false;

            foreach (var job in jobs)
                queue.Enqueue(job.Name, TimeSpan.FromSeconds(SlotWatchdogSeconds), () => TickSlot(job));

            var active = retainerManager->GetActiveRetainer();
            var retainerName = active == null ? string.Empty : active->NameString;
            ChatGui.Print("[Marketbuddy] Relisting ?? item(s) (retainer: ??)...".Loc(jobs.Count, retainerName));
        }

        public void CancelByButton() => Cancel("cancelled by user".Loc());

        private void Cancel(string reason) => queue.Abort(reason);

        private void OnFrameworkUpdate(IFramework framework)
        {
            if (!queue.IsRunning)
                return;

            // Global guard rails, evaluated every tick while the batch runs.
            if (!gui.IsRetainerSellListOpen)
            {
                Cancel("the retainer sell list was closed".Loc());
                return;
            }

            if (Keys[VirtualKey.ESCAPE])
            {
                Cancel("ESC pressed".Loc());
                return;
            }

            if (IPCManager.IsLocked)
            {
                Cancel("locked via IPC by another plugin".Loc());
                return;
            }

            if (Commons.GetUnitBase("RetainerSell") != null)
            {
                Cancel("manual price adjustment detected".Loc());
                return;
            }

            queue.Update();
        }

        private TickTaskResult TickSlot(SlotJob job)
        {
            CurrentItemName = job.Name;
            var now = DateTime.UtcNow;

            switch (job.Phase)
            {
                case SlotPhase.Throttle:
                    if (now < job.NotBefore)
                        return TickTaskResult.Continue;
                    if ((now - lastRequestAt).TotalMilliseconds < ThrottleMs)
                        return TickTaskResult.Continue;
                    job.Phase = SlotPhase.Request;
                    return TickTaskResult.Continue;

                case SlotPhase.Request:
                {
                    var slot = GetMarketSlot(job.Slot);
                    if (slot == null || slot->ItemId != job.ItemId)
                    {
                        Skip(job, "[Marketbuddy] ??: slot changed, skipped".Loc(job.Name));
                        return TickTaskResult.Done;
                    }

                    var proxy = GetItemSearchProxy();
                    if (proxy == null)
                    {
                        Fail(job, "InfoProxyItemSearch unavailable");
                        return TickTaskResult.Done;
                    }

                    job.Attempt++;
                    proxy->EndRequest(); // reset any dangling request state
                    proxy->SearchItemId = job.ItemId;
                    captured.Clear();
                    offeringsReceived = false;
                    pendingItemId = job.ItemId;
                    offeringsPending = true;
                    lastRequestAt = now;
                    job.WaitStart = now;

                    if (!proxy->RequestData())
                    {
                        offeringsPending = false;
                        if (job.Attempt >= MaxAttempts)
                        {
                            Fail(job, "market data request could not be sent".Loc());
                            return TickTaskResult.Done;
                        }

                        job.NotBefore = now.AddMilliseconds(RetryBackoffMs);
                        job.Phase = SlotPhase.Throttle;
                        return TickTaskResult.Continue;
                    }

                    job.Phase = SlotPhase.WaitOfferings;
                    return TickTaskResult.Continue;
                }

                case SlotPhase.WaitOfferings:
                    if (offeringsReceived)
                    {
                        job.Phase = SlotPhase.Apply;
                        return TickTaskResult.Continue;
                    }

                    if ((now - job.WaitStart).TotalMilliseconds > OfferingsTimeoutMs)
                    {
                        offeringsPending = false;
                        if (job.Attempt >= MaxAttempts)
                        {
                            Fail(job, "no market data received (timed out)".Loc());
                            return TickTaskResult.Done;
                        }

                        job.NotBefore = now.AddMilliseconds(RetryBackoffMs);
                        job.Phase = SlotPhase.Throttle;
                        return TickTaskResult.Continue;
                    }

                    return TickTaskResult.Continue;

                case SlotPhase.Apply:
                    ApplySlot(job);
                    return TickTaskResult.Done;

                default:
                    return TickTaskResult.AbortQueue;
            }
        }

        private void ApplySlot(SlotJob job)
        {
            if (captured.Count == 0)
            {
                Skip(job, "[Marketbuddy] ??: no listings found, price left unchanged".Loc(job.Name));
                return;
            }

            IEnumerable<(uint Price, bool IsHq, ulong RetainerId)> eligible = captured;
            if (job.IsHq && conf.BatchCompareHqOnly && captured.Any(l => l.IsHq))
                eligible = captured.Where(l => l.IsHq);

            var lowest = eligible.MinBy(l => l.Price);
            if (ownRetainerIds.Contains(lowest.RetainerId))
            {
                Skip(job, "[Marketbuddy] ??: your own listing is already the lowest (?? gil)".Loc(job.Name, lowest.Price));
                return;
            }

            // Same undercut maths as the manual flow: float multiplication for
            // percent mode (never integer division), then clamp.
            var target = conf.UndercutUsePercent
                ? (long)(lowest.Price * (1f - conf.UndercutPercent / 100f))
                : lowest.Price - (long)conf.UndercutPrice;
            var newPrice = (uint)Math.Clamp(target, Configuration.MIN_PRICE, Configuration.MAX_PRICE);

            var inventoryManager = InventoryManager.Instance();
            var slot = GetMarketSlot(job.Slot);
            if (inventoryManager == null || slot == null || slot->ItemId != job.ItemId)
            {
                Skip(job, "[Marketbuddy] ??: slot changed, skipped".Loc(job.Name));
                return;
            }

            var current = inventoryManager->GetRetainerMarketPrice(job.Slot);
            if (current == newPrice)
            {
                Skip(job, "[Marketbuddy] ??: already at ?? gil".Loc(job.Name, newPrice));
                return;
            }

            inventoryManager->SetRetainerMarketPrice(job.Slot, newPrice);
            ProcessedSlots++;
            RepricedCount++;
            ChatGui.Print("[Marketbuddy] ??: ?? → ?? gil".Loc(job.Name, current, newPrice));
        }

        private void OnOfferingsReceived(IMarketBoardCurrentOfferings offerings)
        {
            if (!offeringsPending)
                return;

            // Later pages of a batch we already consumed share its RequestId.
            if (offerings.RequestId == lastAcceptedRequestId)
                return;

            var listings = offerings.ItemListings;
            // Stale response for a previously requested item: ignore. An empty
            // page cannot be attributed, accept it as "no results" (we only ever
            // have a single request in flight).
            if (listings.Count > 0 && listings[0].ItemId != pendingItemId)
                return;

            lastAcceptedRequestId = offerings.RequestId;
            captured.Clear();
            foreach (var listing in listings)
                captured.Add((listing.PricePerUnit, listing.IsHq, listing.RetainerId));

            offeringsPending = false;
            offeringsReceived = true;
        }

        private void OnQueueAborted(string reason)
        {
            ResetRequestState();
            ChatGui.PrintError("[Marketbuddy] Relist cancelled: ?? (?? repriced, ?? skipped, ?? failed)"
                .Loc(reason, RepricedCount, SkippedCount, FailedCount));
        }

        private void OnQueueCompleted()
        {
            ResetRequestState();
            ChatGui.Print("[Marketbuddy] Relist finished: ?? repriced, ?? skipped, ?? failed"
                .Loc(RepricedCount, SkippedCount, FailedCount));
        }

        private void ResetRequestState()
        {
            offeringsPending = false;
            offeringsReceived = false;
            CurrentItemName = string.Empty;
            var proxy = GetItemSearchProxy();
            if (proxy != null)
                proxy->EndRequest();
        }

        private void Skip(SlotJob job, string chatMessage)
        {
            ProcessedSlots++;
            SkippedCount++;
            ChatGui.Print(chatMessage);
        }

        private void Fail(SlotJob job, string reason)
        {
            ProcessedSlots++;
            FailedCount++;
            Log.Warning($"BatchReprice: slot {job.Slot} ({job.Name}) failed: {reason}");
            ChatGui.PrintError("[Marketbuddy] ??: failed - ??".Loc(job.Name, reason));
        }

        private static InventoryItem* GetMarketSlot(short slotIndex)
        {
            var inventoryManager = InventoryManager.Instance();
            return inventoryManager == null
                ? null
                : inventoryManager->GetInventorySlot(InventoryType.RetainerMarket, slotIndex);
        }

        private static InfoProxyItemSearch* GetItemSearchProxy()
        {
            var infoModule = InfoModule.Instance();
            return infoModule == null
                ? null
                : (InfoProxyItemSearch*)infoModule->GetInfoProxyById(InfoProxyId.ItemSearch);
        }
    }
}
