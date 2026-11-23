using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    /// <summary>
    /// 即時掛單面板「看盤」欄位的資料：自己世界現在的掛單（單價＋件數）與最近 7 天的每日成交件數。
    /// 🔴 只在使用者按下面板上的「查日銷」時才排進佇列；與最近成交價共用同一個 HttpClient、同一條單一飛行的查詢迴圈
    /// 與同一份請求間隔／退避，最近成交價的佇列優先。純唯讀 HTTP，不送任何遊戲內市場查詢。
    /// </summary>
    internal static partial class LastSoldPriceSource
    {
        /// <summary>v2 多件查詢一次最多帶幾件（端點上限 100）。</summary>
        private const int WatchBatchSize = 50;

        /// <summary>每件最多取幾筆掛單（由便宜到貴）；比我便宜的件數只看得到這幾筆。</summary>
        private const int WatchListingsPerItem = 100;

        /// <summary>
        /// 成交筆數上限。Universalis 的每日成交件數是拿<b>回傳的</b>成交算的（實測 entries=0 時一律回 0），
        /// 所以窗內筆數超過這個值時算出來偏低＝預估天數偏長（保守的那一邊）。
        /// </summary>
        private const int WatchEntriesPerItem = 999;

        private const int WatchWindowSeconds = 7 * 86400;

        /// <summary>一件道具一個品質在自己世界的掛單（由便宜到貴）。</summary>
        internal sealed record WatchListings(long[] Prices, int[] Quantities);

        /// <summary>看盤資料的一份快照。<see cref="State"/> 不是 Ready 時其他欄位沒有意義。</summary>
        internal readonly record struct WatchView(
            LastSoldState State,
            DateTime FetchedUtc,
            DateTime UploadedUtc,
            double UnitsPerDay,
            WatchListings? Listings,
            long CapPrice);

        private sealed class WatchEntry
        {
            public required LastSoldState State;
            public required uint WorldId;
            public required DateTime StampUtc;
            public DateTime UploadedUtc = DateTime.MinValue;
            public double NqPerDay = double.NaN;
            public double HqPerDay = double.NaN;
            public WatchListings? Nq;
            public WatchListings? Hq;

            /// <summary>回傳筆數碰到上限時那一批裡最貴的單價；沒碰到是 <see cref="long.MaxValue"/>。</summary>
            public long CapPrice = long.MaxValue;
        }

        /// <summary>道具 id → 看盤資料。發布之後不再修改，換值一律整個換掉。</summary>
        private static readonly ConcurrentDictionary<uint, WatchEntry> WatchCache = new();

        /// <summary>🔴 只在持有 <see cref="Gate"/> 時碰。</summary>
        private static readonly HashSet<uint> WatchQueue = [];

        private static uint watchQueueWorldId;

        private static int watchFailureLogged;

        private static int watchSuccessLogged;

        /// <summary>
        /// 把這些道具排進看盤查詢（已經有夠新的答案就跳過）。只從 framework 執行緒呼叫。
        /// </summary>
        /// <param name="worldId">掛售所在的世界；0＝還沒登入，不做事。</param>
        internal static void RequestWatch(uint worldId, IReadOnlyCollection<uint> itemIds)
        {
            if (http == null || worldId == 0 || itemIds.Count == 0)
                return;

            var now = DateTime.UtcNow;
            if (UnknownWorlds.ContainsKey(worldId))
            {
                foreach (var itemId in itemIds)
                    WatchCache[itemId] = new WatchEntry { State = LastSoldState.NoData, WorldId = worldId, StampUtc = now };
                return;
            }

            var added = false;
            lock (Gate)
            {
                if (worldId != watchQueueWorldId)
                {
                    WatchQueue.Clear();
                    watchQueueWorldId = worldId;
                }

                foreach (var itemId in itemIds)
                {
                    if (itemId == 0)
                        continue;
                    if (WatchCache.TryGetValue(itemId, out var existing) && existing.WorldId == worldId)
                    {
                        if (existing.State == LastSoldState.Loading)
                            continue;
                        var ttlMinutes = existing.State switch
                        {
                            LastSoldState.Ready => FreshMinutes,
                            LastSoldState.NoData => NoDataMinutes,
                            _ => FailedMinutes,
                        };
                        if (now - existing.StampUtc < TimeSpan.FromMinutes(ttlMinutes))
                            continue;
                    }

                    if (!WatchQueue.Add(itemId))
                        continue;
                    WatchCache[itemId] = new WatchEntry { State = LastSoldState.Loading, WorldId = worldId, StampUtc = now };
                    added = true;
                }
            }

            if (added)
                Kick();
        }

        /// <summary>
        /// 這件道具這個品質在 <paramref name="worldId"/> 的看盤資料。任何執行緒都可以呼叫。
        /// 從沒按過查詢、或資料屬於別的世界時 <see cref="WatchView.State"/> 是 Unknown。
        /// </summary>
        internal static WatchView WatchOf(uint itemId, uint worldId, bool hq)
        {
            if (!WatchCache.TryGetValue(itemId, out var e) || e.WorldId != worldId)
                return new WatchView(LastSoldState.Unknown, DateTime.MinValue, DateTime.MinValue, double.NaN, null, long.MaxValue);
            return new WatchView(e.State, e.StampUtc, e.UploadedUtc, hq ? e.HqPerDay : e.NqPerDay, hq ? e.Hq : e.Nq,
                e.CapPrice);
        }

        /// <summary>從佇列取下一批（呼叫端持有 <see cref="Gate"/>）；佇列空回 null。</summary>
        private static List<uint>? TakeWatchBatchLocked(out uint worldId)
        {
            worldId = watchQueueWorldId;
            if (WatchQueue.Count == 0)
                return null;
            var batch = WatchQueue.Take(WatchBatchSize).ToList();
            foreach (var itemId in batch)
                WatchQueue.Remove(itemId);
            return batch;
        }

        private static void MarkWatch(IEnumerable<uint> itemIds, uint worldId, LastSoldState state)
        {
            var now = DateTime.UtcNow;
            foreach (var itemId in itemIds)
                WatchCache[itemId] = new WatchEntry { State = state, WorldId = worldId, StampUtc = now };
        }

        /// <summary>🔴 保證不擲例外，而且 <paramref name="batch"/> 裡每一件都會被標上一個最終狀態。</summary>
        private static async Task FetchWatchAsync(uint worldId, List<uint> batch, CancellationToken token)
        {
            var client = http;
            if (client == null)
            {
                MarkWatch(batch, worldId, LastSoldState.Failed);
                return;
            }

            Interlocked.Exchange(ref lastRequestTicks, DateTime.UtcNow.Ticks);
            try
            {
                // 多件與單件的回應形狀不同（單件沒有 items 那一層），fields 兩種都列。
                const string fields =
                    "items.lastUploadTime,items.nqSaleVelocity,items.hqSaleVelocity," +
                    "items.listings.pricePerUnit,items.listings.quantity,items.listings.hq," +
                    "lastUploadTime,nqSaleVelocity,hqSaleVelocity," +
                    "listings.pricePerUnit,listings.quantity,listings.hq,unresolvedItems";
                var url = "https://universalis.app/api/v2/" + worldId + "/" + string.Join(",", batch) +
                          "?listings=" + WatchListingsPerItem + "&entries=" + WatchEntriesPerItem +
                          "&entriesWithin=" + WatchWindowSeconds + "&fields=" + fields;
                using var response = await SendWithRetryAsync(client, url, token).ConfigureAwait(false);

                // 404：多件＝不認得這個世界；單件也可能只是不認得這件道具，所以單件不記世界。
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    if (batch.Count > 1 && UnknownWorlds.TryAdd(worldId, 0))
                        Log.Information(
                            $"[Marketbuddy] 看盤：Universalis 不認得世界 id {worldId}，這一次遊戲期間不再向它查詢。");
                    MarkWatch(batch, worldId, LastSoldState.NoData);
                    return;
                }

                if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    MarkWatch(batch, worldId, LastSoldState.NoData);
                    return;
                }

                if (!response.IsSuccessStatusCode)
                {
                    NoteWatchFailure($"HTTP {(int)response.StatusCode}");
                    MarkWatch(batch, worldId, LastSoldState.Failed);
                    return;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                var json = await JsonSerializer.DeserializeAsync<WatchDto>(stream, JsonOptions, token)
                    .ConfigureAwait(false);

                var stamp = DateTime.UtcNow;
                var handled = new HashSet<uint>();
                if (json?.items != null)
                {
                    foreach (var (key, item) in json.items)
                    {
                        if (uint.TryParse(key, out var itemId))
                        {
                            WatchCache[itemId] = BuildWatch(item, worldId, stamp);
                            handled.Add(itemId);
                        }
                    }
                }
                else if (json != null && batch.Count == 1)
                {
                    WatchCache[batch[0]] = BuildWatch(json, worldId, stamp);
                    handled.Add(batch[0]);
                }

                // 回應裡沒提到的（含 unresolvedItems＝不能上市）一律算「沒有資料」，不能卡在查詢中。
                foreach (var itemId in batch)
                {
                    if (!handled.Contains(itemId))
                        WatchCache[itemId] = new WatchEntry { State = LastSoldState.NoData, WorldId = worldId, StampUtc = stamp };
                }

                Volatile.Write(ref watchFailureLogged, 0);
                if (Interlocked.Exchange(ref watchSuccessLogged, 1) == 0)
                    Log.Information(
                        $"[Marketbuddy] 看盤：Universalis 查詢正常運作。第一次成功查了 {batch.Count} 件，" +
                        $"其中 {handled.Count} 件有資料。成功之後不再重複回報，只有失敗才會再寫一行。");
            }
            catch (OperationCanceledException)
            {
                MarkWatch(batch, worldId, LastSoldState.Failed);
            }
            catch (Exception e)
            {
                NoteWatchFailure(e.Message);
                MarkWatch(batch, worldId, LastSoldState.Failed);
            }
        }

        /// <summary>從沒有人上傳過（上傳時間 0）＝沒有資料：那種回應的掛單與成交都是空的，不是「0 件」。</summary>
        private static WatchEntry BuildWatch(WatchDto dto, uint worldId, DateTime stamp)
        {
            var uploaded = ToUtc(dto.lastUploadTime);
            if (uploaded == DateTime.MinValue)
                return new WatchEntry { State = LastSoldState.NoData, WorldId = worldId, StampUtc = stamp };

            var listings = dto.listings ?? [];
            var capPrice = long.MaxValue;
            if (listings.Count >= WatchListingsPerItem)
            {
                capPrice = 0;
                foreach (var l in listings)
                    capPrice = Math.Max(capPrice, l.pricePerUnit ?? 0);
            }

            return new WatchEntry
            {
                State = LastSoldState.Ready,
                WorldId = worldId,
                StampUtc = stamp,
                UploadedUtc = uploaded,
                NqPerDay = Velocity(dto.nqSaleVelocity),
                HqPerDay = Velocity(dto.hqSaleVelocity),
                Nq = ListingsOf(listings, false),
                Hq = ListingsOf(listings, true),
                CapPrice = capPrice,
            };
        }

        private static double Velocity(double? value)
            => value is { } v && double.IsFinite(v) && v >= 0 ? v : double.NaN;

        private static WatchListings ListingsOf(List<WatchListingDto> listings, bool hq)
        {
            var picked = listings.Where(l => l.hq == hq && l.pricePerUnit is > 0 && l.quantity is > 0)
                .OrderBy(l => l.pricePerUnit!.Value)
                .ToList();
            return new WatchListings(
                picked.Select(l => l.pricePerUnit!.Value).ToArray(),
                picked.Select(l => l.quantity!.Value).ToArray());
        }

        private static void NoteWatchFailure(string reason)
        {
            Interlocked.Exchange(ref backoffUntilTicks, DateTime.UtcNow.AddMilliseconds(FailureBackoffMs).Ticks);
            if (Interlocked.Exchange(ref watchFailureLogged, 1) != 0)
                return;
            Log.Information(
                $"[Marketbuddy] 看盤：向 Universalis 查詢失敗（{reason}）。即時掛單面板的看盤欄位會顯示「!」，" +
                "重掛與其他功能不受影響；成功一次之後會再回報下一次失敗。");
        }

        // ReSharper disable all
        /// <summary>v2 現在掛單＋成交速度：多件時是 <c>items</c>，只問一件時欄位直接在最外層。</summary>
        internal sealed class WatchDto
        {
            public long? lastUploadTime { get; set; }
            public double? nqSaleVelocity { get; set; }
            public double? hqSaleVelocity { get; set; }
            public List<WatchListingDto>? listings { get; set; }
            public Dictionary<string, WatchDto>? items { get; set; }
        }

        internal sealed class WatchListingDto
        {
            /// <summary>未稅單價。</summary>
            public long? pricePerUnit { get; set; }

            public int? quantity { get; set; }
            public bool hq { get; set; }
        }
        // ReSharper restore all
    }
}
