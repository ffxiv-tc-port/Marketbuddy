using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Marketbuddy
{
    // 本檔契約：不引用 Dalamud（離線測試直接編它）。全部是純計算；Universalis 的回應由呼叫端抓好交進來。

    /// <summary>Universalis 上各 (世界, 道具) 最後一次有人上傳的時間（一輪開始時批次抓一次，這一輪內不再問）。</summary>
    internal sealed class UploadTimes
    {
        private readonly Dictionary<uint, Dictionary<uint, DateTime>> byWorld = new();

        internal int Count { get; private set; }

        /// <summary>這一輪向 Universalis 問了幾批（顯示與 log 用）。</summary>
        internal int Requests { get; set; }

        internal void Set(uint worldId, uint itemId, DateTime utc)
        {
            if (!byWorld.TryGetValue(worldId, out var items))
                byWorld[worldId] = items = new Dictionary<uint, DateTime>();
            if (!items.TryGetValue(itemId, out var known))
                Count++;
            else if (known >= utc)
                return;
            items[itemId] = utc;
        }

        internal bool TryGet(uint worldId, uint itemId, out DateTime utc)
        {
            utc = DateTime.MinValue;
            return byWorld.TryGetValue(worldId, out var items) && items.TryGetValue(itemId, out utc);
        }

        /// <summary>
        /// 解析 <c>/api/v2/aggregated/{資料中心}/{道具…}</c> 的回應：<c>results[].itemId</c> 與
        /// <c>results[].worldUploadTimes[].{worldId,timestamp}</c>（毫秒）。<c>failedItems</c>＝Universalis 不認得，不記。
        /// </summary>
        /// <returns>這份回應裡有幾件道具。格式不對擲 <see cref="JsonException"/>（呼叫端當這一輪抓失敗）。</returns>
        internal int AddAggregated(ReadOnlySpan<byte> json)
        {
            using var doc = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("not an object");
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                return 0;
            var n = 0;
            foreach (var result in results.EnumerateArray())
            {
                if (result.ValueKind != JsonValueKind.Object
                    || !result.TryGetProperty("itemId", out var idElement) || idElement.ValueKind != JsonValueKind.Number
                    || !idElement.TryGetUInt32(out var itemId))
                    continue;
                n++;
                if (!result.TryGetProperty("worldUploadTimes", out var times) || times.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var t in times.EnumerateArray())
                {
                    if (t.ValueKind == JsonValueKind.Object
                        && t.TryGetProperty("worldId", out var w) && w.ValueKind == JsonValueKind.Number && w.TryGetUInt32(out var worldId)
                        && t.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.Number && ts.TryGetInt64(out var ms)
                        && ms > 0 && ms < 4_102_444_800_000)
                        Set(worldId, itemId, DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime);
                }
            }

            return n;
        }
    }

    /// <summary>一個世界這一輪要掃的清單（舊的在前）與跳過的件數。</summary>
    internal sealed record WorldPlan(uint WorldId, List<uint> Queue, int SkippedFresh, int SkippedNoData);

    internal static class FullMarketPlanner
    {
        /// <summary>
        /// 在每一個世界都「問到了、沒人賣、這一頁也沒有成交」的道具。
        /// 🔑 只要有一個世界沒問過（或成交不知道）就不算——第一輪一律全掃。
        /// </summary>
        internal static HashSet<uint> NothingAnywhere(IReadOnlyList<IReadOnlyDictionary<uint, ItemBrief>> perWorld)
        {
            var result = new HashSet<uint>();
            if (perWorld.Count == 0)
                return result;
            foreach (var (itemId, brief) in perWorld[0])
            {
                if (brief.NothingAtAll)
                    result.Add(itemId);
            }

            for (var i = 1; i < perWorld.Count && result.Count > 0; i++)
                result.RemoveWhere(id => !perWorld[i].TryGetValue(id, out var b) || !b.NothingAtAll);
            return result;
        }

        /// <summary>
        /// 一個世界的待掃清單。先後＝「最後一次有人更新」由舊到新：Universalis 的上傳時間與兩台合起來最後一次問到答案的時間取較新的，
        /// 兩者都沒有＝最舊。<paramref name="uploads"/> 為 null（Universalis 失敗或逾時）就只看本機時間。
        /// </summary>
        /// <param name="freshHours">這麼多小時內有人更新過就跳過；0＝不跳。</param>
        /// <param name="noDataDays">每個世界都沒人賣也沒成交的，這個世界上次問到答案在這麼多天內就跳過；0＝不降頻。</param>
        internal static WorldPlan PlanWorld(uint worldId, IReadOnlyList<uint> items, IReadOnlyDictionary<uint, ItemBrief> local,
            IReadOnlySet<uint> nothingAnywhere, UploadTimes? uploads, DateTime nowUtc, int freshHours, int noDataDays)
        {
            var freshCutoff = freshHours > 0 ? nowUtc.AddHours(-freshHours) : DateTime.MaxValue;
            var noDataCutoff = noDataDays > 0 ? nowUtc.AddDays(-noDataDays) : DateTime.MaxValue;
            var keyed = new List<(DateTime Key, uint ItemId)>(items.Count);
            int fresh = 0, noData = 0;
            foreach (var itemId in items)
            {
                var answered = local.TryGetValue(itemId, out var brief) ? brief.AnsweredUtc : DateTime.MinValue;
                if (nothingAnywhere.Contains(itemId) && answered > noDataCutoff)
                {
                    noData++;
                    continue;
                }

                var key = answered;
                if (uploads != null && uploads.TryGet(worldId, itemId, out var uploaded) && uploaded > key)
                    key = uploaded;
                if (key > freshCutoff)
                {
                    fresh++;
                    continue;
                }

                keyed.Add((key, itemId));
            }

            keyed.Sort((a, b) =>
            {
                var c = a.Key.CompareTo(b.Key);
                return c != 0 ? c : a.ItemId.CompareTo(b.ItemId);
            });
            var queue = new List<uint>(keyed.Count);
            foreach (var (_, itemId) in keyed)
                queue.Add(itemId);
            return new WorldPlan(worldId, queue, fresh, noData);
        }

        /// <summary>待掃最多的世界；一樣多時挑上次掃描最舊的（沒掃過＝最舊），再一樣挑 id 小的。沒有任何待掃回 0。</summary>
        internal static uint ChooseWorld(IEnumerable<(uint WorldId, int Pending, DateTime LastUtc)> candidates)
        {
            uint best = 0;
            var bestPending = 0;
            var bestLast = DateTime.MaxValue;
            foreach (var (worldId, pending, last) in candidates)
            {
                if (pending <= 0)
                    continue;
                var better = best == 0
                             || pending > bestPending
                             || (pending == bestPending && (last < bestLast || (last == bestLast && worldId < best)));
                if (!better)
                    continue;
                best = worldId;
                bestPending = pending;
                bestLast = last;
            }

            return best;
        }

        /// <summary>預估秒數＝件數 × 查價間隔（被拒重問與換世界不算）。</summary>
        internal static double EstimateSeconds(long items, int gateIntervalMs) => items * Math.Max(0, gateIntervalMs) / 1000.0;
    }

    /// <summary>
    /// 請 Lifestream 換世界之後，判斷「傳送根本沒開始」：已經過了寬限、Lifestream 不忙、不在讀取畫面、人還沒到目的地，
    /// 而且這個狀態連續維持一段時間。🔑 不等長逾時：Lifestream 自己放棄之後這裡幾秒內就看得出來。
    /// <para>契約：純判斷，遊戲狀態由呼叫端在 framework 執行緒讀好帶進來。</para>
    /// </summary>
    internal sealed class TravelWatch
    {
        internal static TimeSpan Grace = TimeSpan.FromSeconds(8);
        internal static TimeSpan IdleConfirm = TimeSpan.FromSeconds(5);

        private DateTime startedAt;
        private DateTime idleSince = DateTime.MinValue;
        private uint target;

        internal enum Result
        {
            Waiting = 0,
            Arrived = 1,
            NotStarted = 2,
        }

        internal void Start(DateTime nowUtc, uint targetWorldId)
        {
            startedAt = nowUtc;
            target = targetWorldId;
            idleSince = DateTime.MinValue;
        }

        internal Result Tick(DateTime nowUtc, bool loggedIn, uint currentWorldId, bool lifestreamBusy, bool betweenAreas)
        {
            if (loggedIn && currentWorldId == target)
                return Result.Arrived;
            if (lifestreamBusy || betweenAreas || !loggedIn)
            {
                idleSince = DateTime.MinValue;
                return Result.Waiting;
            }

            if (idleSince == DateTime.MinValue)
                idleSince = nowUtc;
            return nowUtc - startedAt >= Grace && nowUtc - idleSince >= IdleConfirm
                ? Result.NotStarted
                : Result.Waiting;
        }
    }
}
