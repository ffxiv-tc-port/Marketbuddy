using System;
using System.Collections.Generic;
using Marketbuddy;

// 情境測試：遊戲內查價被拒／逾時之後的補價來源（巡檢記錄優先、觀測索引次之）與行情查詢視窗的純函式部分。
// 負對照是改版前的判準：只看巡檢記錄（LegacySnapshotOnly），觀測索引裡有資料也補不到。
// 執行：dotnet run -c Release --project tests/MarketLookupTests/MarketLookupTests.csproj
// 回傳碼：0＝全部通過；1＝有情境失敗。

internal static class Program
{
    private const uint Home = 4030;
    private const uint OtherWorld = 4031;
    private const ulong Mine = 9001;
    private const ulong Stranger = 7777;

    private static readonly DateTime Now = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
    private static readonly HashSet<ulong> Own = [Mine, 9002];

    private static int failures;
    private static int passes;

    private static int Main()
    {
        FreshObservationFillsWhenSurveyLogHasNothing();
        StaleObservationIsNotUsed();
        QualityMustMatch();
        NoDataMeansNoFallback();
        SurveyLogWinsOverObservation();
        OlderObservationDoesNotOverrideSurveyRow();
        OwnOrUnknownSellerIsNotUsed();
        SwitchesTurnItOff();
        EmptyOrFailedAnswerIsNotUsed();
        FilledPriceStillGoesThroughGuard();
        SearchRanksAndCaches();
        UnknownIsQuestionMarkNeverZero();
        CheapestPrefersFreshData();
        OwnListingIsMarked();

        Console.WriteLine($"MARKETLOOKUP_DONE pass={passes} fail={failures}");
        return failures == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------------
    //  補價選擇
    // ---------------------------------------------------------------------

    private static MarketObservation Obs(uint world, uint item, DateTime at, long nq, ulong nqSeller, long hq, ulong hqSeller,
        string verdict = "ok", SaleSummary sales = default, int count = 3)
        => new(at, world, world == Home ? "家" : "別", item, nq, nqSeller, hq, hqSeller, count, "live", 2000, verdict, sales);

    private static SurveyPriceFallback.ObservationLookup LookupOf(params MarketObservation[] all)
        => (uint world, uint item, out MarketObservation latest) =>
        {
            foreach (var o in all)
            {
                if (o.WorldId == world && o.ItemId == item)
                {
                    latest = o;
                    return true;
                }
            }

            latest = default;
            return false;
        };

    private static SurveyPriceFallback.Pick Resolve(uint item, bool hq, SurveyPriceFallback.ObservationLookup? lookup,
        int surveyHours = 24, int obsHours = 6, IReadOnlySet<ulong>? own = null)
        => SurveyPriceFallback.Resolve(item, hq, Home, Now, surveyHours, obsHours, own ?? Own, lookup);

    /// <summary>改版前：只有巡檢記錄。</summary>
    private static bool LegacySnapshotOnly(uint item, bool hq, out long lowest)
        => PriceSurveySnapshot.TryGetLowest(item, hq, Home, Now, 24, out lowest, out _);

    private static void NoteSurveyRow(uint item, bool hq, DateTime at, long lowest, int lowestIsOurs, string verdict = "ok")
        => PriceSurveySnapshot.Note(new PriceSurveyRow(at, Home, "家", item, hq, 5000, hq ? -1 : lowest, hq ? lowest : -1,
            lowestIsOurs, 3, "live", 2000, verdict));

    /// <summary>新上架的道具不在巡檢清單上：巡檢記錄沒有，觀測有（全市場掃描 2 小時前）。</summary>
    private static void FreshObservationFillsWhenSurveyLogHasNothing()
    {
        PriceSurveySnapshot.Reset();
        var lookup = LookupOf(Obs(Home, 5001, Now.AddHours(-2), 1_200, Stranger, -1, 0));
        var pick = Resolve(5001, false, lookup);
        Check("fresh observation: source", SurveyPriceFallback.FromObservation, pick.Source);
        Check("fresh observation: price", 1_200L, pick.Lowest);
        Check("fresh observation: time", Now.AddHours(-2), pick.AtUtc);
        CheckBool("negative control: legacy (survey log only) finds nothing", false, LegacySnapshotOnly(5001, false, out _));
    }

    private static void StaleObservationIsNotUsed()
    {
        PriceSurveySnapshot.Reset();
        var lookup = LookupOf(Obs(Home, 5002, Now.AddHours(-7), 1_200, Stranger, -1, 0));
        var pick = Resolve(5002, false, lookup);
        CheckBool("stale observation: not found", false, pick.Found);
        Check("stale observation: reason", "obs-stale", pick.Miss);
        CheckBool("stale observation: observation still reported for the log", true, pick.HasObservation);

        // 時限邊界：剛好 6 小時算在內。
        var edge = Resolve(5002, false, LookupOf(Obs(Home, 5002, Now.AddHours(-6), 1_200, Stranger, -1, 0)));
        CheckBool("observation exactly at the limit: used", true, edge.Found);
    }

    private static void QualityMustMatch()
    {
        PriceSurveySnapshot.Reset();
        var nqOnly = LookupOf(Obs(Home, 5003, Now.AddHours(-1), 1_200, Stranger, -1, 0));
        var hqPick = Resolve(5003, true, nqOnly);
        CheckBool("HQ listing, only NQ on the board: not found", false, hqPick.Found);
        Check("HQ listing, only NQ on the board: reason", "obs-no-quality", hqPick.Miss);

        var both = LookupOf(Obs(Home, 5004, Now.AddHours(-1), 1_200, Stranger, 3_000, Stranger + 1));
        Check("HQ listing uses the HQ lowest", 3_000L, Resolve(5004, true, both).Lowest);
        Check("NQ listing uses the NQ lowest", 1_200L, Resolve(5004, false, both).Lowest);
    }

    private static void NoDataMeansNoFallback()
    {
        PriceSurveySnapshot.Reset();
        var elsewhere = LookupOf(Obs(OtherWorld, 5005, Now.AddHours(-1), 900, Stranger, -1, 0));
        Check("only another world has data: never used", "obs-none", Resolve(5005, false, elsewhere).Miss);
        Check("no observation index (not loaded / no link)", "obs-none", Resolve(5005, false, null).Miss);
        CheckBool("no data at all: not found", false, Resolve(5005, false, LookupOf()).Found);
    }

    private static void SurveyLogWinsOverObservation()
    {
        PriceSurveySnapshot.Reset();
        NoteSurveyRow(5006, false, Now.AddHours(-3), 1_500, lowestIsOurs: 0);
        var newer = LookupOf(Obs(Home, 5006, Now.AddHours(-1), 1_000, Stranger, -1, 0));
        var pick = Resolve(5006, false, newer);
        Check("survey log first: source", SurveyPriceFallback.FromSnapshot, pick.Source);
        Check("survey log first: price", 1_500L, pick.Lowest);
    }

    private static void OlderObservationDoesNotOverrideSurveyRow()
    {
        PriceSurveySnapshot.Reset();
        // 巡檢記錄那一列較新、最低是自己的（不可用）；觀測較舊、說別人 1,000 ⇒ 不用舊觀測。
        NoteSurveyRow(5007, false, Now.AddHours(-1), 1_100, lowestIsOurs: 1);
        var older = LookupOf(Obs(Home, 5007, Now.AddHours(-2), 1_000, Stranger, -1, 0));
        Check("older observation behind a newer survey row: reason", "obs-not-newer", Resolve(5007, false, older).Miss);

        var newer = LookupOf(Obs(Home, 5007, Now.AddMinutes(-10), 1_050, Stranger, -1, 0));
        var pick = Resolve(5007, false, newer);
        Check("newer observation after an unusable survey row: source", SurveyPriceFallback.FromObservation, pick.Source);
        Check("newer observation after an unusable survey row: price", 1_050L, pick.Lowest);
    }

    private static void OwnOrUnknownSellerIsNotUsed()
    {
        PriceSurveySnapshot.Reset();
        Check("lowest is my retainer", "obs-own",
            Resolve(5008, false, LookupOf(Obs(Home, 5008, Now.AddHours(-1), 1_000, Mine, -1, 0))).Miss);
        Check("seller unknown (0)", "obs-owner-unknown",
            Resolve(5008, false, LookupOf(Obs(Home, 5008, Now.AddHours(-1), 1_000, 0, -1, 0))).Miss);
        Check("own retainers not known", "obs-owner-unknown",
            Resolve(5008, false, LookupOf(Obs(Home, 5008, Now.AddHours(-1), 1_000, Stranger, -1, 0)),
                own: new HashSet<ulong>()).Miss);
    }

    private static void SwitchesTurnItOff()
    {
        PriceSurveySnapshot.Reset();
        var lookup = LookupOf(Obs(Home, 5009, Now.AddHours(-1), 1_000, Stranger, -1, 0));
        Check("survey fallback 0 turns everything off", "off", Resolve(5009, false, lookup, surveyHours: 0).Miss);
        Check("observation hours 0 = survey log only", "obs-off", Resolve(5009, false, lookup, obsHours: 0).Miss);
    }

    private static void EmptyOrFailedAnswerIsNotUsed()
    {
        PriceSurveySnapshot.Reset();
        Check("nobody selling: not a price", "obs-verdict-empty",
            Resolve(5010, false, LookupOf(Obs(Home, 5010, Now.AddHours(-1), -1, 0, -1, 0, verdict: "empty"))).Miss);
    }

    /// <summary>補到的價走既有的候選函式：異常低價照樣被擋、正常價照樣套降價。</summary>
    private static void FilledPriceStillGoesThroughGuard()
    {
        PriceSurveySnapshot.Reset();
        var undercut = new RelistPricing.UndercutRule(false, 0, 1, 1, 1000);
        var guard = new RelistPricing.AnomalyRule(true, 100_000, 5, true, 1000);

        var typo = Resolve(5011, false, LookupOf(Obs(Home, 5011, Now.AddHours(-1), 2_000, Stranger, -1, 0)));
        var held = RelistPricing.FromListing(typo.Lowest, -1, -1, PriceSourceTag.Survey, typo.AtUtc, undercut, guard,
            50_000, -1, 50_000);
        CheckBool("filled price 25x below the sales consensus: held", true, held.HeldByAnomaly);

        var normal = Resolve(5012, false, LookupOf(Obs(Home, 5012, Now.AddHours(-1), 48_000, Stranger, -1, 0)));
        var priced = RelistPricing.FromListing(normal.Lowest, -1, -1, PriceSourceTag.Survey, normal.AtUtc, undercut, guard,
            50_000, -1, 50_000);
        Check("normal filled price: undercut applied", 47_999L, priced.Target);
    }

    // ---------------------------------------------------------------------
    //  行情查詢視窗
    // ---------------------------------------------------------------------

    private static void SearchRanksAndCaches()
    {
        var catalog = new List<LookupCatalogEntry>();
        foreach (var (id, name) in new (uint, string)[]
                 {
                     (10, "奶油雞肉寬麵"), (11, "奶油"), (12, "雞肉"), (13, "Fire Shard"), (44, "Wind Shard"), (440, "火之水晶"),
                 })
            catalog.Add(new LookupCatalogEntry(id, name, MarketLookupSearch.Fold(name)));

        List<uint> Ids(string q, int max = 50)
        {
            var hits = MarketLookupSearch.Filter(catalog, q, max, out _);
            var ids = new List<uint>();
            foreach (var i in hits)
                ids.Add(catalog[i].ItemId);
            return ids;
        }

        Check("exact name first, then prefix", "11,10", string.Join(",", Ids("奶油")));
        Check("exact name first, then contains", "12,10", string.Join(",", Ids("雞肉")));
        Check("id match first", "44", string.Join(",", Ids("44")));
        Check("case-insensitive, full-width space folded", "13,44", string.Join(",", Ids("　SHARD ")));
        Check("blank query: nothing", "", string.Join(",", Ids("   ")));
        MarketLookupSearch.Filter(catalog, "Shard", 1, out var total);
        Check("cap keeps the total", 2, total);
        Check("cap trims the list", 1, MarketLookupSearch.Filter(catalog, "Shard", 1, out _).Count);
    }

    private static readonly (uint, string)[] Worlds = [(Home, "家"), (OtherWorld, "乙"), (4032, "丙")];

    private static void UnknownIsQuestionMarkNeverZero()
    {
        var noSales = Obs(Home, 1, Now.AddHours(-1), 1_200, Stranger, -1, 0, count: -1);
        var rows = MarketLookupModel.BuildRows(Worlds, [noSales], Now, MarketLookupModel.StaleHours);
        Check("every data-centre world gets a row", 3, rows.Count);
        var home = rows[0];
        CheckBool("row with data first", true, home.HasData && home.WorldId == Home);
        Check("NQ price shown", "1,200", MarketLookupModel.PriceText(home, false));
        Check("asked, nobody selling HQ", "—", MarketLookupModel.PriceText(home, true));
        Check("listing count unknown", "?", MarketLookupModel.CountText(home));
        Check("sales not recorded: last sale", "?", MarketLookupModel.LastSaleText(home));
        Check("sales not recorded: 7-day count", "?", MarketLookupModel.RecentSalesText(home));

        var blank = rows[1];
        CheckBool("world without data: no data", false, blank.HasData);
        foreach (var hq in new[] { false, true })
            Check($"world without data: price {(hq ? "HQ" : "NQ")}", "?", MarketLookupModel.PriceText(blank, hq));
        Check("world without data: count", "?", MarketLookupModel.CountText(blank));
        Check("world without data: last sale", "?", MarketLookupModel.LastSaleText(blank));
        Check("world without data: 7-day count", "?", MarketLookupModel.RecentSalesText(blank));
        Check("world without data: owner", LookupOwner.Unknown, MarketLookupModel.OwnerOf(blank, false, Own));

        var emptyPage = new SaleSummary(true, -1, DateTime.MinValue, -1, DateTime.MinValue, 0, 0);
        var known = MarketLookupModel.BuildRows(Worlds, [Obs(Home, 1, Now, 900, Stranger, -1, 0, sales: emptyPage)], Now, 24)[0];
        Check("sales known, page empty: last sale", "—", MarketLookupModel.LastSaleText(known));
        Check("sales known, page empty: 7-day count is a real 0", "0", MarketLookupModel.RecentSalesText(known));

        var fullPage = new SaleSummary(true, 800, Now.AddHours(-2), 1_500, Now.AddHours(-1), 20, 20);
        var busy = MarketLookupModel.BuildRows(Worlds, [Obs(Home, 1, Now, 900, Stranger, -1, 0, sales: fullPage)], Now, 24)[0];
        Check("whole page inside 7 days: at least", "20+", MarketLookupModel.RecentSalesText(busy));
        CheckBool("latest sale picks the newer quality", true,
            MarketLookupModel.TryLastSale(busy, out var p, out _, out var saleHq) && p == 1_500 && saleHq);

        foreach (var row in rows)
        {
            foreach (var hq in new[] { false, true })
                CheckBool($"never draws 0 ({row.WorldName}, hq={hq})", false, MarketLookupModel.PriceText(row, hq) == "0");
        }
    }

    private static void CheapestPrefersFreshData()
    {
        var rows = MarketLookupModel.BuildRows(Worlds,
        [
            Obs(Home, 1, Now.AddHours(-30), 500, Stranger, -1, 0),
            Obs(OtherWorld, 1, Now.AddHours(-1), 900, Stranger, 2_000, Stranger),
            Obs(4032, 1, Now.AddHours(-2), 950, Stranger, 1_800, Stranger),
        ], Now, MarketLookupModel.StaleHours);
        CheckBool("30-hour-old row is stale", true, rows.Find(r => r.WorldId == Home).Stale);
        Check("cheapest NQ among fresh rows", OtherWorld, rows[MarketLookupModel.Cheapest(rows, false)].WorldId);
        Check("cheapest HQ", 4032u, rows[MarketLookupModel.Cheapest(rows, true)].WorldId);

        var allStale = MarketLookupModel.BuildRows(Worlds,
        [
            Obs(Home, 1, Now.AddHours(-30), 500, Stranger, -1, 0),
            Obs(OtherWorld, 1, Now.AddHours(-40), 900, Stranger, -1, 0),
        ], Now, MarketLookupModel.StaleHours);
        Check("all stale: cheapest of the old ones", Home, allStale[MarketLookupModel.Cheapest(allStale, false)].WorldId);
        Check("nobody sells HQ: no cheapest", -1, MarketLookupModel.Cheapest(allStale, true));
    }

    private static void OwnListingIsMarked()
    {
        var rows = MarketLookupModel.BuildRows(Worlds,
        [
            Obs(Home, 1, Now, 500, Mine, 900, 0),
            Obs(OtherWorld, 1, Now, 600, Stranger, -1, 0),
        ], Now, MarketLookupModel.StaleHours);
        var home = rows.Find(r => r.WorldId == Home);
        Check("my retainer's lowest", LookupOwner.Own, MarketLookupModel.OwnerOf(home, false, Own));
        Check("seller not recorded", LookupOwner.Unknown, MarketLookupModel.OwnerOf(home, true, Own));
        Check("own retainers not known yet", LookupOwner.Unknown, MarketLookupModel.OwnerOf(home, false, null));
        Check("someone else", LookupOwner.Other, MarketLookupModel.OwnerOf(rows.Find(r => r.WorldId == OtherWorld), false, Own));
        Check("no HQ listing", LookupOwner.NoListing, MarketLookupModel.OwnerOf(rows.Find(r => r.WorldId == OtherWorld), true, Own));
    }

    // ---------------------------------------------------------------------

    private static void Check<T>(string name, T expected, T actual)
    {
        if (EqualityComparer<T>.Default.Equals(expected, actual))
        {
            passes++;
            return;
        }

        failures++;
        Console.WriteLine($"FAIL {name}: expected {expected}, got {actual}");
    }

    private static void CheckBool(string name, bool expected, bool actual) => Check(name, expected, actual);
}
