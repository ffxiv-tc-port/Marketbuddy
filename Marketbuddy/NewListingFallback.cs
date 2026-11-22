using System;

namespace Marketbuddy
{
    /// <summary>
    /// 新上架退路與「下一位賣家是不是特殊掛單」要用的參照資料：從 Universalis 快取
    /// （<see cref="LastSoldPriceSource"/>）與巡檢記錄索引（<see cref="PriceSurveySnapshot"/>）收集。
    /// 執行緒：兩個來源都是並行安全的快取，任何執行緒都可以呼叫；不配置、不做 I/O、不寫 log、不送任何查詢。
    /// </summary>
    internal static class NewListingFallback
    {
        private const int MaxPool = 64;

        /// <summary>Universalis 那一半最多佔幾格，留位置給巡檢記錄（每個世界一筆）。</summary>
        private const int MaxUniversalis = 48;

        /// <param name="lastSale">最近一筆成交（過期也算）；-1＝沒有。</param>
        internal static RelistPricing.FallbackInputs Gather(uint itemId, bool hq, bool ignoreQuality, int ratio,
            int windowDays, long lastSale, DateTime nowUtc)
        {
            var consensus = LastSoldPriceSource.FallbackConsensusOf(itemId, hq, ignoreQuality, out var mixed);
            Span<long> pool = stackalloc long[MaxPool];
            var n = FillPool(itemId, hq, windowDays, nowUtc, pool);
            var cheapest = PriceConsensus.CheapestOrdinary(pool[..n], ratio, out var reference);
            return new RelistPricing.FallbackInputs(consensus, mixed, cheapest, reference, n, lastSale);
        }

        /// <summary>
        /// 判「下一位賣家是不是特殊掛單」用的參照價：這個品質的共識價；沒有就用別的世界掛單的參照價；都沒有 -1。
        /// </summary>
        internal static long PeerReference(uint itemId, bool hq, int ratio, int windowDays, DateTime nowUtc)
        {
            var consensus = LastSoldPriceSource.ConsensusOf(itemId, hq);
            if (consensus.IsKnown)
                return consensus.Price;
            Span<long> pool = stackalloc long[MaxPool];
            var n = FillPool(itemId, hq, windowDays, nowUtc, pool);
            PriceConsensus.CheapestOrdinary(pool[..n], ratio, out var reference);
            return reference;
        }

        /// <summary>
        /// 別的世界的掛單：Universalis 現在掛單（已扣掉自己的世界與排除清單）＋巡檢記錄裡同資料中心別的世界的最低價（窗內）。
        /// 同一個世界、同一個價的兩筆只算一次（巡檢與 Universalis 常是同一筆掛單）。
        /// </summary>
        private static int FillPool(uint itemId, bool hq, int windowDays, DateTime nowUtc, Span<long> pool)
        {
            Span<uint> worlds = stackalloc uint[MaxPool];
            var n = LastSoldPriceSource.CopyDcListings(itemId, hq, pool[..MaxUniversalis], worlds[..MaxUniversalis]);

            var selling = LastSoldPriceSource.SellingWorldId;
            if (selling == 0)
                return n;

            foreach (var world in LastSoldPriceSource.DataCenterWorlds)
            {
                if (n >= pool.Length)
                    break;
                if (world == selling || LastSoldPriceSource.IsWorldExcluded(world))
                    continue;
                if (!PriceSurveySnapshot.TryGetLowest(itemId, hq, world, nowUtc, windowDays * 24, out var lowest, out _))
                    continue;
                if (Contains(pool[..n], worlds[..n], world, lowest))
                    continue;
                pool[n] = lowest;
                worlds[n] = world;
                n++;
            }

            return n;
        }

        private static bool Contains(ReadOnlySpan<long> prices, ReadOnlySpan<uint> worlds, uint world, long price)
        {
            for (var i = 0; i < prices.Length; i++)
            {
                if (worlds[i] == world && prices[i] == price)
                    return true;
            }

            return false;
        }
    }
}
