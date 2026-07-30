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
        private const int PriceCacheTtlMinutes = 30; // reuse market data for the same item within this window

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
            public bool FromCache;
            public bool QuickListed;
            public SlotPhase Phase = SlotPhase.Throttle;
            public DateTime NotBefore = DateTime.MinValue;
            public DateTime WaitStart;
        }

        private sealed class CachedOfferings
        {
            public required DateTime At;
            public required List<(uint Price, bool IsHq, ulong RetainerId)> Listings;
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
        private bool suppressionHeld;

        // Live market tax rates, cached opportunistically from the
        // TaxRatesReceived event; conf.MarketTaxPercent is the fallback.
        private IMarketTaxRates? taxRates;

        // In-memory market data cache keyed by item id (shared across batches
        // and retainers, never persisted). Stores the full first-page listings
        // so NQ/HQ eligibility is still computed per slot.
        private readonly Dictionary<uint, CachedOfferings> priceCache = new();

        // Batch state (for UI / summary).
        private HashSet<ulong> ownRetainerIds = new();

        /// <summary>Fired when a batch runs to completion (all slots processed).</summary>
        public event System.Action? BatchFinished;

        /// <summary>Fired when a batch is cancelled or aborted, with the reason.</summary>
        public event System.Action<string>? BatchAborted;

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
            var container = inventoryManager->GetInventoryContainer(InventoryType.RetainerMarket);
            if (container == null)
                return;

            // Snapshot every listed slot of the active retainer. Pointers are
            // null-checked before any dereference.
            var jobs = new List<SlotJob>();
            for (var i = 0; i < container->Size; i++)
            {
                var slot = inventoryManager->GetInventorySlot(InventoryType.RetainerMarket, i);
                if (slot == null || slot->ItemId == 0)
                    continue;
                jobs.Add(CreateSlotJob(slot));
            }

            if (jobs.Count == 0)
            {
                ChatGui.PrintError("[Marketbuddy] Cannot start: ??".Loc("this retainer has nothing listed".Loc()));
                return;
            }

            BeginBatch(jobs);

            var active = retainerManager->GetActiveRetainer();
            var retainerName = active == null ? string.Empty : active->NameString;
            ChatGui.Print("[Marketbuddy] Relisting ?? item(s) (retainer: ??)...".Loc(jobs.Count, retainerName));
        }

        /// <summary>
        /// Runs the full pipeline (compare via cache/request, undercut,
        /// thresholds, delist) for one just-quick-listed slot that is parked at
        /// the price cap. Returns false when the engine cannot start right now.
        /// </summary>
        public bool StartQuickReprice(short slotIndex)
        {
            if (!CanStart(out _))
                return false;

            var inventoryManager = InventoryManager.Instance();
            var slot = inventoryManager == null
                ? null
                : inventoryManager->GetInventorySlot(InventoryType.RetainerMarket, slotIndex);
            if (slot == null || slot->ItemId == 0)
                return false;

            var job = CreateSlotJob(slot);
            job.QuickListed = true;
            BeginBatch([job]);
            return true;
        }

        private static SlotJob CreateSlotJob(InventoryItem* slot)
        {
            var itemSheet = DataManager.GetExcelSheet<Item>();
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

            return new SlotJob
            {
                Slot = slot->Slot, ItemId = slot->ItemId, IsHq = isHq, Name = name,
                VendorUnitPrice = vendorUnitPrice
            };
        }

        private void BeginBatch(List<SlotJob> jobs)
        {
            // All of our own retainers: if the lowest listing is ours (any
            // retainer), never undercut ourselves.
            ownRetainerIds = new HashSet<ulong>();
            var retainerManager = RetainerManager.Instance();
            if (retainerManager != null)
            {
                foreach (var retainer in retainerManager->Retainers)
                {
                    if (retainer.RetainerId != 0)
                        ownRetainerIds.Add(retainer.RetainerId);
                }
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

            // Hold AutoRetainer off for the duration of this user-triggered
            // run; every exit path (completion, cancel, abort, dispose) goes
            // through ResetRequestState/ForceRelease so it is always restored.
            AutoRetainerBridge.AcquireSuppression("batch reprice");
            suppressionHeld = true;

            foreach (var job in jobs)
                queue.Enqueue(job.Name, TimeSpan.FromSeconds(SlotWatchdogSeconds), () => TickSlot(job));
        }

        private void ReleaseSuppressionIfHeld()
        {
            if (!suppressionHeld)
                return;
            suppressionHeld = false;
            AutoRetainerBridge.ReleaseSuppression();
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

            // AutoRetainer mutual exclusion (cached at 1 Hz by the bridge): if
            // it starts driving retainers mid-batch, we stand down immediately.
            if (AutoRetainerBridge.IsBusy)
            {
                Cancel("AutoRetainer became busy".Loc());
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
                    // Fresh cached market data for this item skips the whole
                    // request/wait pipeline (and the request throttle).
                    if (TryGetCachedOfferings(job.ItemId, out var cachedListings))
                    {
                        captured.Clear();
                        captured.AddRange(cachedListings);
                        job.FromCache = true;
                        job.Phase = SlotPhase.Apply;
                        return TickTaskResult.Continue;
                    }

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
                        StoreCachedOfferings(job.ItemId, captured);
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
                        StoreCachedOfferings(job.ItemId, captured);
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
            var cacheTag = job.FromCache ? " " + "(cached price)".Loc() : string.Empty;

            if (captured.Count == 0)
            {
                HandleNoListings(job, cacheTag);
                return;
            }

            IEnumerable<(uint Price, bool IsHq, ulong RetainerId)> eligible = captured;
            if (job.IsHq && conf.BatchCompareHqOnly && captured.Any(l => l.IsHq))
                eligible = captured.Where(l => l.IsHq);

            var lowest = eligible.MinBy(l => l.Price);
            if (ownRetainerIds.Contains(lowest.RetainerId))
            {
                if (!job.QuickListed)
                {
                    Skip(job, "[Marketbuddy] ??: your own listing is already the lowest (?? gil)".Loc(job.Name, lowest.Price) + cacheTag);
                    return;
                }

                // The quick-listed slot is parked at the price cap and must
                // never stay there: match our own lowest listing instead of
                // undercutting ourselves.
                FinishPricing(job, lowest.Price, cacheTag,
                    "[Marketbuddy] ??: matched your own lowest listing at ?? gil".Loc(job.Name, lowest.Price) + cacheTag);
                return;
            }

            // Same undercut maths as the manual flow: float multiplication for
            // percent mode (never integer division), then clamp.
            var target = conf.UndercutUsePercent
                ? (long)(lowest.Price * (1f - conf.UndercutPercent / 100f))
                : lowest.Price - (long)conf.UndercutPrice;
            var newPrice = (uint)Math.Clamp(target, Configuration.MIN_PRICE, Configuration.MAX_PRICE);
            FinishPricing(job, newPrice, cacheTag, null);
        }

        /// <summary>
        /// No market listings for this item. Normal batch slots just keep their
        /// price; quick-listed slots are parked at the price cap and must never
        /// be left there silently - fall back to the user's minimum price when
        /// one is configured, otherwise warn loudly and count as failed.
        /// </summary>
        private void HandleNoListings(SlotJob job, string cacheTag)
        {
            if (!job.QuickListed)
            {
                // Nothing on sale is a normal situation, not a failure: quiet
                // debug log, counted as skipped.
                Log.Debug($"BatchReprice: slot {job.Slot} ({job.Name}) has no market listings, skipped");
                Skip(job, "[Marketbuddy] ??: no one is selling this item, skipped".Loc(job.Name) + cacheTag);
                return;
            }

            if (conf.BatchMinPrice > 0)
            {
                ChatGui.Print("[Marketbuddy] ??: no one is selling this item, using your minimum price ?? gil".Loc(job.Name, conf.BatchMinPrice) + cacheTag);
                FinishPricing(job, (uint)conf.BatchMinPrice, cacheTag, null);
                return;
            }

            ProcessedSlots++;
            FailedCount++;
            Log.Warning($"BatchReprice: quick-listed slot {job.Slot} ({job.Name}) has no market data; still listed at the price cap");
            ChatGui.PrintError("[Marketbuddy] ??: no market data - still listed at the price cap (??), set a price manually!".Loc(job.Name, Configuration.MAX_PRICE));
        }

        /// <summary>Slot re-validation, delist thresholds, then the actual price update.</summary>
        private void FinishPricing(SlotJob job, uint newPrice, string cacheTag, string? successMessage)
        {
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
                        "[Marketbuddy] ??: market net ?? < NPC ?? gil, delisted".Loc(job.Name, netUnit, job.VendorUnitPrice) + cacheTag);
                    return;
                }
            }

            if (conf.BatchMinPrice > 0 && newPrice < (uint)conf.BatchMinPrice)
            {
                DelistSlot(job, inventoryManager, slot,
                    "[Marketbuddy] ??: target price ?? below your minimum ??, delisted".Loc(job.Name, newPrice, conf.BatchMinPrice) + cacheTag);
                return;
            }

            var current = inventoryManager->GetRetainerMarketPrice(job.Slot);
            if (current == newPrice)
            {
                Skip(job, "[Marketbuddy] ??: already at ?? gil".Loc(job.Name, newPrice) + cacheTag);
                return;
            }

            inventoryManager->SetRetainerMarketPrice(job.Slot, newPrice);
            ProcessedSlots++;
            RepricedCount++;
            ChatGui.Print(successMessage ?? "[Marketbuddy] ??: ?? → ?? gil".Loc(job.Name, current, newPrice) + cacheTag);
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

            // Destination is a user setting (player inventory by default).
            // Deliberately NO silent fallback to the other container: a full
            // destination is a reported failure and the batch moves on.
            int result;
            string destinationTag;
            if (conf.DelistToRetainerInventory)
            {
                if (!HasFreeRetainerInventorySlot(inventoryManager))
                {
                    Fail(job, "retainer inventory is full, cannot delist".Loc());
                    return;
                }

                result = inventoryManager->MoveFromRetainerMarketToRetainerInventory(
                    InventoryType.RetainerMarket, (ushort)job.Slot, quantity);
                destinationTag = " " + "(moved to retainer inventory)".Loc();
            }
            else
            {
                if (inventoryManager->GetEmptySlotsInBag() == 0)
                {
                    Fail(job, "your inventory is full, cannot delist".Loc());
                    return;
                }

                result = inventoryManager->MoveFromRetainerMarketToPlayerInventory(
                    InventoryType.RetainerMarket, (ushort)job.Slot, quantity);
                destinationTag = " " + "(moved to your inventory)".Loc();
            }

            Log.Debug($"BatchReprice: delist slot {job.Slot} ({job.Name}) qty {quantity}, move returned {result}");
            ProcessedSlots++;
            DelistedCount++;
            ChatGui.Print(chatMessage + destinationTag);
        }

        /// <summary>
        /// Shared guard for interactive listings (manual flow and
        /// AutoRetainer's quick "put up for sale"): same thresholds as the
        /// batch delist logic, applied to the price about to be entered.
        /// Gated by the same opt-in settings; itemId 0 (unknown) skips the
        /// vendor comparison, the minimum-price check is item-independent.
        /// </summary>
        public bool ShouldBlockListing(uint price, uint itemId, bool isHq, out string reason)
        {
            reason = string.Empty;

            if (conf.BatchDelistBelowVendor && itemId != 0)
            {
                var itemSheet = DataManager.GetExcelSheet<Item>();
                if (itemSheet != null && itemSheet.TryGetRow(itemId, out var row) && row.PriceLow > 0)
                {
                    var vendorUnit = isHq ? row.PriceLow + 1u : row.PriceLow;
                    var netUnit = price * (100L - CurrentTaxPercent()) / 100L;
                    if (netUnit < vendorUnit)
                    {
                        reason = "market net ?? < NPC ?? gil".Loc(netUnit, vendorUnit);
                        return true;
                    }
                }
            }

            if (conf.BatchMinPrice > 0 && price < (uint)conf.BatchMinPrice)
            {
                reason = "price ?? below your minimum ??".Loc(price, conf.BatchMinPrice);
                return true;
            }

            return false;
        }

        /// <summary>Number of unexpired entries in the market data cache.</summary>
        public int PriceCacheCount
        {
            get
            {
                var cutoff = DateTime.UtcNow.AddMinutes(-PriceCacheTtlMinutes);
                return priceCache.Count(kv => kv.Value.At > cutoff);
            }
        }

        /// <summary>Drops all cached market data so the next batch queries fresh prices.</summary>
        public void ClearPriceCache() => priceCache.Clear();

        private bool TryGetCachedOfferings(uint itemId, out List<(uint Price, bool IsHq, ulong RetainerId)> listings)
        {
            listings = [];
            if (!priceCache.TryGetValue(itemId, out var entry))
                return false;
            if (DateTime.UtcNow - entry.At > TimeSpan.FromMinutes(PriceCacheTtlMinutes))
            {
                priceCache.Remove(itemId);
                return false;
            }

            listings = entry.Listings;
            return true;
        }

        private void StoreCachedOfferings(uint itemId, List<(uint Price, bool IsHq, ulong RetainerId)> listings)
        {
            priceCache[itemId] = new CachedOfferings { At = DateTime.UtcNow, Listings = new(listings) };
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
            if (AutoRetainerBridge.IsBusy)
            {
                AutoRetainerBridge.ArmAvailabilityNotice();
                ChatGui.Print("[Marketbuddy] Wait for AutoRetainer to finish, then press the button again.".Loc());
            }

            BatchAborted?.Invoke(reason);
        }

        private void OnQueueCompleted()
        {
            ResetRequestState();
            ChatGui.Print("[Marketbuddy] Relist finished: ?? repriced, ?? skipped, ?? delisted, ?? failed"
                .Loc(RepricedCount, SkippedCount, DelistedCount, FailedCount));
            BatchFinished?.Invoke();
        }

        private void ResetRequestState()
        {
            ReleaseSuppressionIfHeld();
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
