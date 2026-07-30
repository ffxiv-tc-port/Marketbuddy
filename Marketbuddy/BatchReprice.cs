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
        private const int EmptyResultGraceMs = 1000; // history seen + this long with no offerings => nothing on sale

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
            public required uint VendorUnitPrice;
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
        private bool historySeen;
        private DateTime historySeenAt;
        private uint pendingItemId;
        private int lastAcceptedRequestId = int.MinValue;
        private DateTime lastRequestAt = DateTime.MinValue;

        // Live market tax rates, cached opportunistically from the
        // TaxRatesReceived event; conf.MarketTaxPercent is the fallback.
        private IMarketTaxRates? taxRates;

        // Batch state (for UI / summary).
        private HashSet<ulong> ownRetainerIds = new();

        public bool IsRunning => queue.IsRunning;
        public int TotalSlots { get; private set; }
        public int ProcessedSlots { get; private set; }
        public int RepricedCount { get; private set; }
        public int SkippedCount { get; private set; }
        public int DelistedCount { get; private set; }
        public int FailedCount { get; private set; }
        public string CurrentItemName { get; private set; } = string.Empty;

        private Configuration conf => Configuration.GetOrLoad();

        public BatchReprice(MarketGuiEventHandler gui)
        {
            this.gui = gui;
            queue.Aborted += OnQueueAborted;
            queue.Completed += OnQueueCompleted;
            MarketBoard.OfferingsReceived += OnOfferingsReceived;
            MarketBoard.HistoryReceived += OnHistoryReceived;
            MarketBoard.TaxRatesReceived += OnTaxRatesReceived;
            Framework.Update += OnFrameworkUpdate;
        }

        public void Dispose()
        {
            Framework.Update -= OnFrameworkUpdate;
            MarketBoard.TaxRatesReceived -= OnTaxRatesReceived;
            MarketBoard.HistoryReceived -= OnHistoryReceived;
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
                var priceLow = 0u;
                if (itemSheet != null && itemSheet.TryGetRow(slot->ItemId, out var row))
                {
                    name = row.Name.ExtractText();
                    priceLow = row.PriceLow;
                }

                var isHq = (slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
                if (isHq)
                    name += $" {(char)SeIconChar.HighQuality}";

                // NPC vendors pay PriceLow for NQ and PriceLow+1 for HQ (verified
                // against CriticalCommonLib's production SellToVendorPrice; the
                // old "HQ = +10%" rule is long gone). PriceLow 0 = unsellable.
                var vendorUnitPrice = priceLow == 0 ? 0u : isHq ? priceLow + 1 : priceLow;

                jobs.Add(new SlotJob
                {
                    Slot = slot->Slot, ItemId = slot->ItemId, IsHq = isHq, Name = name,
                    VendorUnitPrice = vendorUnitPrice
                });
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
            DelistedCount = 0;
            FailedCount = 0;
            CurrentItemName = string.Empty;
            lastAcceptedRequestId = int.MinValue;
            offeringsPending = false;
            offeringsReceived = false;
            historySeen = false;

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
                    historySeen = false;
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

                    // The server sends NO offerings packet at all for an item with
                    // zero listings (verified against Dalamud's NetworkHandlers:
                    // zero pages expected when AmountToArrive == 0), but the sale
                    // history packet of the same request still arrives. History
                    // seen + a grace period with no offerings page is therefore
                    // the definitive "nothing on sale" answer: skip immediately,
                    // no retry, no full timeout.
                    if (historySeen && (now - historySeenAt).TotalMilliseconds >= EmptyResultGraceMs)
                    {
                        offeringsPending = false;
                        captured.Clear();
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
                // Nothing on sale is a normal situation, not a failure: quiet
                // debug log, counted as skipped.
                Log.Debug($"BatchReprice: slot {job.Slot} ({job.Name}) has no market listings, skipped");
                Skip(job, "[Marketbuddy] ??: no one is selling this item, skipped".Loc(job.Name));
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

            // Delist guards: if the price we are about to set is not worth
            // keeping on the market, take the item off the board instead.
            // Both features are opt-in and default to off.
            if (conf.BatchDelistBelowVendor && job.VendorUnitPrice > 0)
            {
                var taxPercent = CurrentTaxPercent();
                var netUnit = newPrice * (100L - taxPercent) / 100L;
                if (netUnit < job.VendorUnitPrice)
                {
                    DelistSlot(job, inventoryManager, slot,
                        "[Marketbuddy] ??: market net ?? < NPC ?? gil, delisted".Loc(job.Name, netUnit, job.VendorUnitPrice));
                    return;
                }
            }

            if (conf.BatchMinPrice > 0 && newPrice < (uint)conf.BatchMinPrice)
            {
                DelistSlot(job, inventoryManager, slot,
                    "[Marketbuddy] ??: target price ?? below your minimum ??, delisted".Loc(job.Name, newPrice, conf.BatchMinPrice));
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

            var listings = offerings.ItemListings;
            if (listings.Count > 0)
            {
                // Later pages of a batch we already consumed share its RequestId.
                if (offerings.RequestId == lastAcceptedRequestId)
                    return;

                // Stale response for a previously requested item: ignore.
                if (listings[0].ItemId != pendingItemId)
                    return;
            }

            // An empty response is a definitive "nothing on sale" and is always
            // accepted while a request is pending: it cannot be a later page of
            // an earlier batch (those always carry entries), it cannot be
            // attributed by ItemId, and we only ever have a single request in
            // flight. Deliberately no RequestId check for it either, so a
            // non-incrementing RequestId can never make us drop it.

            lastAcceptedRequestId = offerings.RequestId;
            captured.Clear();
            foreach (var listing in listings)
                captured.Add((listing.PricePerUnit, listing.IsHq, listing.RetainerId));

            offeringsPending = false;
            offeringsReceived = true;
        }

        private void OnHistoryReceived(IMarketBoardHistory history)
        {
            if (!offeringsPending || historySeen)
                return;
            if (history.ItemId != pendingItemId)
                return;

            historySeen = true;
            historySeenAt = DateTime.UtcNow;
        }

        private void OnTaxRatesReceived(IMarketTaxRates rates)
        {
            // The packet is a generic "result dialog"; sanity-check the values
            // before trusting it (rates are single-digit percentages).
            if (rates.ValidUntil <= DateTime.UtcNow)
                return;
            if (rates.LimsaLominsaTax > 25 || rates.GridaniaTax > 25 || rates.UldahTax > 25)
                return;
            taxRates = rates;
            Log.Debug($"BatchReprice: cached market tax rates (valid until {rates.ValidUntil:u})");
        }

        /// <summary>Market tax percent for the active retainer's city; falls back to the configured constant.</summary>
        private uint CurrentTaxPercent()
        {
            var fallback = (uint)Math.Clamp(conf.MarketTaxPercent, 0, 25);
            if (taxRates == null || taxRates.ValidUntil <= DateTime.UtcNow)
                return fallback;

            var retainerManager = RetainerManager.Instance();
            var active = retainerManager == null ? null : retainerManager->GetActiveRetainer();
            if (active == null)
                return fallback;

            uint? rate = active->Town switch
            {
                RetainerManager.RetainerTown.LimsaLominsa => taxRates.LimsaLominsaTax,
                RetainerManager.RetainerTown.Gridania => taxRates.GridaniaTax,
                RetainerManager.RetainerTown.Uldah => taxRates.UldahTax,
                RetainerManager.RetainerTown.Ishgard => taxRates.IshgardTax,
                RetainerManager.RetainerTown.Kugane => taxRates.KuganeTax,
                RetainerManager.RetainerTown.Crystarium => taxRates.CrystariumTax,
                RetainerManager.RetainerTown.OldSharlayan => taxRates.SharlayanTax,
                _ => null,
            };
            return rate is null or > 25 ? fallback : rate.Value;
        }

        private void DelistSlot(SlotJob job, InventoryManager* inventoryManager, InventoryItem* slot, string chatMessage)
        {
            var quantity = (uint)Math.Max(1, slot->Quantity);

            // Moving the item needs a free destination slot; check before
            // firing so a full inventory becomes a reported failure, not a
            // silent no-op. Retainer inventory first, then the player's bags.
            int result;
            if (HasFreeRetainerInventorySlot(inventoryManager))
            {
                result = inventoryManager->MoveFromRetainerMarketToRetainerInventory(
                    InventoryType.RetainerMarket, (ushort)job.Slot, quantity);
            }
            else if (inventoryManager->GetEmptySlotsInBag() > 0)
            {
                result = inventoryManager->MoveFromRetainerMarketToPlayerInventory(
                    InventoryType.RetainerMarket, (ushort)job.Slot, quantity);
            }
            else
            {
                Fail(job, "no free inventory space to delist".Loc());
                return;
            }

            Log.Debug($"BatchReprice: delist slot {job.Slot} ({job.Name}) qty {quantity}, move returned {result}");
            ProcessedSlots++;
            DelistedCount++;
            ChatGui.Print(chatMessage);
        }

        private static bool HasFreeRetainerInventorySlot(InventoryManager* inventoryManager)
        {
            for (var type = InventoryType.RetainerPage1; type <= InventoryType.RetainerPage7; type++)
            {
                var container = inventoryManager->GetInventoryContainer(type);
                if (container == null || !container->IsLoaded)
                    continue;
                for (var i = 0; i < container->Size; i++)
                {
                    var slot = inventoryManager->GetInventorySlot(type, i);
                    if (slot != null && slot->ItemId == 0)
                        return true;
                }
            }

            return false;
        }

        private void OnQueueAborted(string reason)
        {
            ResetRequestState();
            ChatGui.PrintError("[Marketbuddy] Relist cancelled: ?? (?? repriced, ?? skipped, ?? delisted, ?? failed)"
                .Loc(reason, RepricedCount, SkippedCount, DelistedCount, FailedCount));
        }

        private void OnQueueCompleted()
        {
            ResetRequestState();
            ChatGui.Print("[Marketbuddy] Relist finished: ?? repriced, ?? skipped, ?? delisted, ?? failed"
                .Loc(RepricedCount, SkippedCount, DelistedCount, FailedCount));
        }

        private void ResetRequestState()
        {
            offeringsPending = false;
            offeringsReceived = false;
            historySeen = false;
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
