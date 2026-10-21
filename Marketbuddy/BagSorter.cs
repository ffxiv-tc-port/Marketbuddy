using System;
using System.Collections.Generic;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    /// <summary>
    /// 背包分揀：按一次按鈕，把四個背包裡的道具分成「賣商店／上市場／要查價」三堆，勾選的交給 <see cref="VendorSeller"/>。
    /// 🔴 市場紅線：不查市場板、不上架、不下架；Universalis 只讀（沿用 <see cref="LastSoldPriceSource"/>，
    /// 而且照它的契約只在「用最近成交價」開著時才查）。交接只換清單，開始賣要使用者自己在出售品視窗旁按。
    /// 執行緒：按鈕只立旗標，讀背包、呼叫 IPC、分類都在 framework 執行緒；不跨幀保存原生指標（存的是格子的值）。
    /// </summary>
    internal sealed class BagSorter : IDisposable
    {
        /// <summary>分揀結果開著、而且畫面最近有人在看時，多久重算一次。</summary>
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

        /// <summary>畫面這麼久沒畫它就停止重算（分頁切走或視窗關了）。</summary>
        private static readonly TimeSpan ViewTimeout = TimeSpan.FromSeconds(3);

        internal sealed class Row
        {
            public required VendorSellKey Key { get; init; }
            public required string Name { get; init; }
            public required int Quantity { get; init; }
            public required int Stacks { get; init; }
            public required uint VendorUnitPrice { get; init; }
            public required BagSortVerdict Verdict { get; init; }

            /// <summary>這次用來分類的成交單價；-1＝沒有。</summary>
            public required long SalePrice { get; init; }

            public required DateTime SaleAtUtc { get; init; }

            /// <summary>低價下架紀錄裡這一種的次數；0＝沒有紀錄。</summary>
            public required int DelistCount { get; init; }
        }

        private readonly VendorSeller? seller;

        private readonly List<uint> readLoaded = new();
        private readonly List<VendorSellSlot> readSlots = new();
        private readonly Dictionary<VendorSellKey, bool> tickOverrides = new();
        private HashSet<uint> protectedIds = new();

        private bool sortRequested;
        private bool handOverRequested;
        private bool summaryPending;
        private DateTime lastViewedUtc = DateTime.MinValue;
        private DateTime nextRefreshUtc = DateTime.MinValue;
        private ulong contentId;

        private Configuration conf => Configuration.GetOrLoad();

        /// <summary>按過分揀、結果還在（換角色或按「清除」才收掉）。</summary>
        public bool Active { get; private set; }

        public IReadOnlyList<Row> Rows { get; private set; } = Array.Empty<Row>();

        /// <summary><see cref="Rows"/> 每換一份 +1。</summary>
        public int Version { get; private set; }

        /// <summary>保護清單這一層的狀態：null＝沒接上（InventoryTools 沒裝或呼叫失敗，這一層略過）；否則是保護了幾種道具。</summary>
        public int? ProtectedCount { get; private set; }

        /// <summary>上一次交接的結果（給畫面顯示）；-1＝還沒交接過。</summary>
        public int LastHandOverStacks { get; private set; } = -1;

        public BagSorter(VendorSeller? seller)
        {
            this.seller = seller;
            Framework.Update += OnFrameworkUpdate;
        }

        public void Dispose()
        {
            Framework.Update -= OnFrameworkUpdate;
        }

        public void RequestSort() => sortRequested = true;

        public void RequestHandOver() => handOverRequested = true;

        /// <summary>畫面每畫一次就呼叫，沒人看就不重算。</summary>
        public void MarkViewed() => lastViewedUtc = DateTime.UtcNow;

        public void Clear()
        {
            Active = false;
            Rows = Array.Empty<Row>();
            Version++;
            tickOverrides.Clear();
            LastHandOverStacks = -1;
            summaryPending = false;
        }

        public bool IsTicked(Row row)
            => row.Verdict.CanTick && (tickOverrides.TryGetValue(row.Key, out var t) ? t : row.Verdict.PreTicked);

        public void SetTicked(Row row, bool ticked)
        {
            if (!row.Verdict.CanTick)
                return;
            if (ticked == row.Verdict.PreTicked)
                tickOverrides.Remove(row.Key);
            else
                tickOverrides[row.Key] = ticked;
        }

        private void OnFrameworkUpdate(IFramework framework)
        {
            var now = DateTime.UtcNow;
            var cid = PlayerState.ContentId;
            if (Active && cid != contentId)
            {
                Clear();
                return;
            }

            if (sortRequested)
            {
                sortRequested = false;
                if (cid == 0)
                    return;
                contentId = cid;
                tickOverrides.Clear();
                LastHandOverStacks = -1;
                protectedIds = ReadProtectedIds(out var ok);
                ProtectedCount = ok ? protectedIds.Count : null;
                Active = true;
                Refresh(now, logSummary: true);
                return;
            }

            if (!Active)
                return;

            if (handOverRequested)
            {
                handOverRequested = false;
                Refresh(now, logSummary: false);
                HandOver();
                return;
            }

            if (now >= nextRefreshUtc && now - lastViewedUtc < ViewTimeout)
                Refresh(now, logSummary: false);
        }

        private void Refresh(DateTime now, bool logSummary)
        {
            nextRefreshUtc = now + RefreshInterval;
            readLoaded.Clear();
            readSlots.Clear();
            VendorSeller.CaptureBags(readLoaded, readSlots);

            var lookupOn = conf.RelistUseLastSoldPrice;
            if (lookupOn)
            {
                var ids = new HashSet<uint>();
                foreach (var s in readSlots)
                {
                    if (s.Key.ItemId != uint.MaxValue)
                        ids.Add(s.Key.ItemId);
                }

                var worldId = PlayerState.ContentId == 0 ? 0u : PlayerState.CurrentWorld.RowId;
                LastSoldPriceSource.Request(worldId, ids);
            }

            var byKey = new Dictionary<VendorSellKey, (int Qty, int Stacks, bool NeedsConfirmation)>();
            var order = new List<VendorSellKey>();
            foreach (var s in readSlots)
            {
                if (s.Key.ItemId == uint.MaxValue)
                    continue;
                if (!byKey.TryGetValue(s.Key, out var agg))
                    order.Add(s.Key);
                byKey[s.Key] = (agg.Qty + s.Quantity, agg.Stacks + 1, agg.NeedsConfirmation || s.NeedsConfirmation);
            }

            var sheet = DataManager.GetExcelSheet<Item>();
            var threshold = (long)conf.BatchMinPrice;
            var rows = new List<Row>(order.Count);
            foreach (var key in order)
            {
                var agg = byKey[key];
                var name = $"#{key.ItemId}";
                var priceLow = 0u;
                var marketable = false;
                var unique = false;
                if (sheet != null && sheet.TryGetRow(key.ItemId, out var item))
                {
                    name = item.Name.ExtractText();
                    priceLow = item.PriceLow;
                    marketable = !item.IsUntradable && item.ItemSearchCategory.RowId != 0;
                    unique = item.IsUnique;
                }

                if (key.Hq)
                    name += $" {(char)SeIconChar.HighQuality}";
                var vendorUnit = priceLow == 0 ? 0u : key.Hq ? priceLow + 1 : priceLow;

                var sale = ReadSale(key, lookupOn, now, out var salePrice, out var saleAt);
                var delistCount = LowPriceDelistLedger.TryGet(key, out var record) ? record.Count : 0;
                var input = new BagSortItem(key, agg.Qty, vendorUnit == 0 || unique || agg.NeedsConfirmation,
                    marketable, protectedIds.Contains(key.ItemId));
                rows.Add(new Row
                {
                    Key = key, Name = name, Quantity = agg.Qty, Stacks = agg.Stacks, VendorUnitPrice = vendorUnit,
                    Verdict = BagSortClassifier.Classify(input, sale, delistCount > 0, threshold),
                    SalePrice = salePrice, SaleAtUtc = saleAt, DelistCount = delistCount,
                });
            }

            Rows = rows;
            Version++;
            summaryPending |= logSummary;
            // 成交價還在查的時候不寫，查完那一次才寫（一次分揀一行）。
            if (summaryPending && !rows.Exists(r => r.Verdict.Reason == BagSortReason.PriceLoading))
            {
                summaryPending = false;
                LogSummary(rows, lookupOn, threshold);
            }
        }

        private BagSortSale ReadSale(VendorSellKey key, bool lookupOn, DateTime now, out long price, out DateTime at)
        {
            price = -1;
            at = DateTime.MinValue;
            if (!lookupOn)
                return new BagSortSale(BagSortSaleState.Off, -1, false);

            switch (LastSoldPriceSource.StateOf(key.ItemId))
            {
                case LastSoldState.Unknown:
                case LastSoldState.Loading:
                    return new BagSortSale(BagSortSaleState.Loading, -1, false);
                case LastSoldState.Failed:
                    return new BagSortSale(BagSortSaleState.Failed, -1, false);
                case LastSoldState.NoData:
                    return new BagSortSale(BagSortSaleState.NoData, -1, false);
            }

            if (!LastSoldPriceSource.TryGet(key.ItemId, key.Hq, conf.RelistLastSoldIgnoreQuality, out var sold))
                return new BagSortSale(BagSortSaleState.NoData, -1, false);
            price = sold.UnitPrice;
            at = sold.SoldAtUtc;
            return new BagSortSale(BagSortSaleState.Ready, sold.UnitPrice,
                RelistPricing.IsSaleStale(sold.SoldAtUtc, now, conf.RelistLastSoldMaxAgeDays));
        }

        private void HandOver()
        {
            if (seller == null)
            {
                LastHandOverStacks = 0;
                return;
            }

            var ticked = new Dictionary<VendorSellKey, Row>();
            foreach (var row in Rows)
            {
                if (IsTicked(row))
                    ticked[row.Key] = row;
            }

            var picks = new List<VendorSeller.SortPick>();
            foreach (var slot in readSlots)
            {
                if (slot.NeedsConfirmation || !ticked.TryGetValue(slot.Key, out var row))
                    continue;
                picks.Add(new VendorSeller.SortPick(slot, row.Name, row.VendorUnitPrice));
            }

            LastHandOverStacks = seller.SetSortPicks(picks);
        }

        /// <summary>
        /// InventoryTools 製作清單上的道具（每份清單要做的成品，加上目前啟用那份清單還要從僱員取回的材料）。
        /// 對方沒裝、還沒好或擲例外就回空集合、<paramref name="ok"/>＝false，這一層略過、不擋分揀。
        /// </summary>
        internal static HashSet<uint> ReadProtectedIds(out bool ok)
        {
            var ids = new HashSet<uint>();
            ok = false;
            try
            {
                var lists = PluginInterface
                    .GetIpcSubscriber<Dictionary<string, string>>("AllaganTools.GetCraftLists").InvokeFunc();
                var getItems = PluginInterface
                    .GetIpcSubscriber<string, Dictionary<uint, uint>>("AllaganTools.GetCraftItems");
                if (lists != null)
                {
                    foreach (var key in lists.Keys)
                    {
                        var items = getItems.InvokeFunc(key);
                        if (items != null)
                            ids.UnionWith(items.Keys);
                    }
                }

                var retrieval = PluginInterface
                    .GetIpcSubscriber<Dictionary<uint, uint>>("AllaganTools.GetRetrievalItems").InvokeFunc();
                if (retrieval != null)
                    ids.UnionWith(retrieval.Keys);
                ok = true;
            }
            catch (Exception e)
            {
                Log.Information($"[Marketbuddy] 背包分揀：InventoryTools 製作清單讀不到（{e.GetType().Name}），保護清單這一層略過");
            }

            return ids;
        }

        private static void LogSummary(List<Row> rows, bool lookupOn, long threshold)
        {
            int vendor = 0, market = 0, pricing = 0, excluded = 0, preTicked = 0;
            foreach (var r in rows)
            {
                switch (r.Verdict.Pile)
                {
                    case BagSortPile.Vendor: vendor++; break;
                    case BagSortPile.Market: market++; break;
                    case BagSortPile.NeedsPricing: pricing++; break;
                    default: excluded++; break;
                }

                if (r.Verdict.PreTicked)
                    preTicked++;
            }

            Log.Information(
                $"[Marketbuddy] 背包分揀：{rows.Count} 種；賣商店 {vendor}（預勾 {preTicked}）、上市場 {market}、要查價 {pricing}、" +
                $"不處理 {excluded}；成交價查詢={(lookupOn ? "開" : "關")}，門檻 {threshold}");
        }
    }
}
