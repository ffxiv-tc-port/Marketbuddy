using System;
using System.Collections.Generic;

namespace Marketbuddy
{
    // 本檔契約：不引用 Dalamud（離線測試直接編它）；不做 I/O、不寫 log，只讀兩份記憶體索引。

    /// <summary>
    /// 遊戲內市場查詢被拒絕／逾時之後，「家世界板上別人的最低價」的補位來源：
    /// 先用巡檢記錄索引（<see cref="PriceSurveySnapshot"/>，照舊），沒有才用觀測索引裡同世界、同道具最新的一筆。
    /// <para>🔴 觀測那一筆比巡檢記錄那一列舊（或同秒）時不用：巡檢記錄那一列已經判過（例如最低價是自己的），
    /// 拿更舊的觀測蓋過它等於拿過期行情定價。</para>
    /// </summary>
    internal static class SurveyPriceFallback
    {
        internal const string FromSnapshot = "snapshot";
        internal const string FromObservation = "observation";

        /// <summary>觀測索引的查詢（<see cref="SurveyLink.TryGetLatestObservation"/>）；索引還沒讀完回 false。</summary>
        internal delegate bool ObservationLookup(uint worldId, uint itemId, out MarketObservation latest);

        /// <param name="Source"><see cref="FromSnapshot"/>／<see cref="FromObservation"/>；空＝沒補到。</param>
        /// <param name="Lowest">別人的最低單價；沒補到時 -1。</param>
        /// <param name="Miss">沒補到的原因（只進 log）；補到時空字串。</param>
        /// <param name="Observation">用了（或看過但沒用）的那筆觀測；<paramref name="HasObservation"/>＝false 時無意義。</param>
        internal readonly record struct Pick(
            string Source, long Lowest, DateTime AtUtc, string Miss, MarketObservation Observation, bool HasObservation)
        {
            public bool Found => Source.Length > 0;

            internal static Pick None(string miss) => new(string.Empty, -1, DateTime.MinValue, miss, default, false);
        }

        /// <param name="snapshotMaxAgeHours">巡檢記錄的時限；<b>0＝整個補位關閉</b>（觀測也不用）。</param>
        /// <param name="observationMaxAgeHours">觀測的時限；0＝只用巡檢記錄。</param>
        /// <param name="ownRetainers">自家僱員；空＝分不出最低價是不是自己的 ⇒ 觀測不用。</param>
        internal static Pick Resolve(uint itemId, bool hq, uint homeWorldId, DateTime nowUtc,
            int snapshotMaxAgeHours, int observationMaxAgeHours, IReadOnlySet<ulong> ownRetainers,
            ObservationLookup? lookup)
        {
            if (snapshotMaxAgeHours <= 0)
                return Pick.None("off");
            if (itemId == 0 || homeWorldId == 0)
                return Pick.None("no-world");

            if (PriceSurveySnapshot.TryGetLowest(itemId, hq, homeWorldId, nowUtc, snapshotMaxAgeHours,
                    out var lowest, out var atUtc))
                return new Pick(FromSnapshot, lowest, atUtc, string.Empty, default, false);

            if (observationMaxAgeHours <= 0)
                return Pick.None("obs-off");
            if (lookup == null || !lookup(homeWorldId, itemId, out var obs))
                return Pick.None("obs-none");

            var pick = FromOneObservation(obs, hq, nowUtc, observationMaxAgeHours, ownRetainers);
            if (!pick.Found)
                return pick;

            var snapshotAt = PriceSurveySnapshot.LatestAt(itemId, hq, homeWorldId);
            return snapshotAt != DateTime.MinValue
                   && SurveyShare.TruncateToSecond(snapshotAt) >= SurveyShare.TruncateToSecond(obs.AtUtc)
                ? pick with { Source = string.Empty, Lowest = -1, AtUtc = DateTime.MinValue, Miss = "obs-not-newer" }
                : pick;
        }

        /// <summary>一筆觀測能不能當「家世界板上別人的最低價」：問到有人賣、這個品質有價、最低那筆確定不是自家的、在時限內。</summary>
        internal static Pick FromOneObservation(in MarketObservation obs, bool hq, DateTime nowUtc, int maxAgeHours,
            IReadOnlySet<ulong> ownRetainers)
        {
            Pick Miss(string why, in MarketObservation o) => Pick.None(why) with { Observation = o, HasObservation = true };

            if (maxAgeHours <= 0)
                return Pick.None("obs-off");
            if (obs.Verdict != "ok")
                return Miss("obs-verdict-" + obs.Verdict, obs);

            var price = hq ? obs.LowestHq : obs.LowestNq;
            if (price <= 0)
                return Miss("obs-no-quality", obs);

            // 賣家不知道（0）或自家名單是空的＝分不出是不是自己 ⇒ 不用，免得自己壓自己。
            var seller = hq ? obs.LowestHqRetainer : obs.LowestNqRetainer;
            if (seller == 0 || ownRetainers.Count == 0)
                return Miss("obs-owner-unknown", obs);
            if (ownRetainers.Contains(seller))
                return Miss("obs-own", obs);

            if (obs.AtUtc == DateTime.MinValue || obs.AtUtc < nowUtc.AddHours(-maxAgeHours))
                return Miss("obs-stale", obs);

            return new Pick(FromObservation, price, obs.AtUtc, string.Empty, obs, true);
        }
    }
}
