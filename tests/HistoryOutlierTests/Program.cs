using System;
using Marketbuddy;

// 情境測試：和近期成交共識價差太多倍的參考價（轉錢交易、打錯字）怎麼判；共識價怎麼算；
// 下一位賣家是特殊掛單時怎麼排除；新上架板上沒人賣、或唯一參考價被判離群時的退路。
// 情境取自實機 log 的定價紀錄與 Universalis 資料中心成交（倍數 5、基準下限 1000）。
// 負對照是改版前的判準：下一位賣家 >= 10 萬且 >= 參考價 5 倍才擋；沒有退路（NoData／Hold 照舊）。
// 執行：dotnet run -c Release --project tests/HistoryOutlierTests/HistoryOutlierTests.csproj
// 回傳碼：0＝全部通過；1＝有情境失敗。

internal static class Program
{
    private const int Ratio = 5;
    private const long Floor = 1000;

    private static int failures;
    private static int passes;

    private static int Main()
    {
        TransferListingOnBoardIsIgnored();
        NormalSaleBehindTransferListingIsKept();
        CheapListingAgainstNormalSalesIsIgnored();
        RelayedOwnSaleIsIgnored();
        MarketWideCrashIsFollowed();
        MarketWideRiseIsFollowed();
        CheapItemsAreNotJudged();
        BoundariesAreInclusive();
        InvalidSettingsJudgeNothing();
        DeviationIgnoresWitnesses();
        ConsensusDropsSpecialTrades();
        CheapestOrdinaryListing();
        SpecialPeerListingIsExcluded();
        HeldSaleFallsBackToConsensus();
        NobodySellingFallsBack();

        Console.WriteLine($"HISTORYOUTLIER_DONE pass={passes} fail={failures}");
        return failures == 0 ? 0 : 1;
    }

    private static HistoryVerdict Judge(long reference, long median, long[] low, long[] high)
        => HistoryOutlier.Judge(reference, median, Ratio, Floor, low, high);

    /// <summary>改版前的判準：只有下一位賣家（>= 10 萬、>= 5 倍）能把一個價判成異常，而且只判偏低。</summary>
    private static bool LegacyFlags(long reference, long peer)
        => peer >= 100000 && peer >= reference * Ratio;

    /// <summary>遠征長靴：板上 10,000,000（轉錢），近期成交中位數 24,500，最近成交 15,000。</summary>
    private static void TransferListingOnBoardIsIgnored()
    {
        Check("transfer listing L=10M", Judge(10_000_000, 24_500, [-1, 15_000], [15_000]), HistoryVerdict.High);
    }

    /// <summary>同上那一格的成交 15,000：舊判準拿板上 10M 當同業基準，把正常成交判成打錯字。</summary>
    private static void NormalSaleBehindTransferListingIsKept()
    {
        Check("sale S=15000 under transfer listing", Judge(15_000, 24_500, [10_000_000, 10_000_000],
            [10_000_000, 10_000_000]), HistoryVerdict.Normal);
        CheckBool("legacy wrongly flags S=15000", LegacyFlags(15_000, 10_000_000), true);
    }

    /// <summary>精梳毛布地毯：有人掛 4,000（中位數 60,000），最近成交 60,000，下一位賣家 35,000。</summary>
    private static void CheapListingAgainstNormalSalesIsIgnored()
    {
        Check("listing L=4000 vs median 60000", Judge(4_000, 60_000, [35_000, 60_000], [60_000]), HistoryVerdict.Low);
        CheckBool("legacy misses L=4000 (peer under 100k)", LegacyFlags(4_000, 35_000), false);
    }

    /// <summary>隔天那筆 4,000 的成交（自己跟價賣掉的）：板上已回到 35,000。</summary>
    private static void RelayedOwnSaleIsIgnored()
    {
        Check("relayed sale S=4000", Judge(4_000, 60_000, [35_000, 35_000], [35_000, 35_000]), HistoryVerdict.Low);
    }

    /// <summary>整個市場一起崩：中位數還是舊價，但板上與成交都落在新價位 ⇒ 跟。</summary>
    private static void MarketWideCrashIsFollowed()
    {
        Check("crash: S=20000 with board 21000", Judge(20_000, 100_000, [21_000, 21_000], [21_000, 21_000]),
            HistoryVerdict.Normal);
        Check("crash: L=19000 with sale 20000", Judge(19_000, 100_000, [19_500, 20_000], [20_000]),
            HistoryVerdict.Normal);
    }

    /// <summary>金屬紫染劑：中位數 1,000，板上 5,750，最近成交 28,000——兩個現況觀測都在高處。</summary>
    private static void MarketWideRiseIsFollowed()
    {
        Check("rise: L=5750 backed by sale 28000", Judge(5_750, 1_000, [-1, 28_000], [28_000]), HistoryVerdict.Normal);
        Check("rise: S=28000 backed by board 5750", Judge(28_000, 1_000, [5_750, 5_750], [5_750, 5_750]),
            HistoryVerdict.Normal);
        Check("rise: lone L=50000 with sale 5000", Judge(50_000, 1_000, [-1, 5_000], [5_000]), HistoryVerdict.High);
    }

    private static void CheapItemsAreNotJudged()
    {
        Check("median 999 under floor", Judge(4, 999, [], []), HistoryVerdict.NoData);
        Check("median floor 0 judges all", HistoryOutlier.Judge(4, 999, Ratio, 0, [], []), HistoryVerdict.Low);
    }

    private static void BoundariesAreInclusive()
    {
        Check("exactly 5x below", Judge(2_000, 10_000, [], []), HistoryVerdict.Low);
        Check("just inside below", Judge(2_001, 10_000, [], []), HistoryVerdict.Normal);
        Check("exactly 5x above", Judge(50_000, 10_000, [], []), HistoryVerdict.High);
        Check("just inside above", Judge(49_999, 10_000, [], []), HistoryVerdict.Normal);
        Check("low witness at the edge does not back", Judge(2_000, 10_000, [10_000], []), HistoryVerdict.Low);
        Check("high witness at the edge does not back", Judge(50_000, 10_000, [], [10_000]), HistoryVerdict.High);
    }

    private static void InvalidSettingsJudgeNothing()
    {
        Check("ratio 1", HistoryOutlier.Judge(1, 10_000, 1, Floor, [], []), HistoryVerdict.NoData);
        Check("ratio 1001", HistoryOutlier.Judge(1, 10_000, 1001, Floor, [], []), HistoryVerdict.NoData);
        Check("no median", Judge(1, -1, [], []), HistoryVerdict.NoData);
        Check("no reference", Judge(0, 10_000, [], []), HistoryVerdict.NoData);
    }

    /// <summary>診斷 log 用的偏離方向：佐證救回的情境仍要看得出偏離（log 才寫得出「有佐證，照常採用」）。</summary>
    private static void DeviationIgnoresWitnesses()
    {
        Check("deviation: crash S backed by board", HistoryOutlier.Deviation(20_000, 100_000, Ratio, Floor), HistoryVerdict.Low);
        Check("deviation: rise L backed by sale", HistoryOutlier.Deviation(5_750, 1_000, Ratio, Floor), HistoryVerdict.High);
        Check("deviation: inside ratio", HistoryOutlier.Deviation(2_001, 10_000, Ratio, Floor), HistoryVerdict.Normal);
        Check("deviation: under floor", HistoryOutlier.Deviation(4, 999, Ratio, Floor), HistoryVerdict.NoData);
    }

    private static readonly DateTime Now = new(2026, 11, 22, 7, 0, 0, DateTimeKind.Utc);

    // 使用者的設定：降價 0、不捨；異常價保護開、門檻 10 萬、倍數 5、用成交紀錄判、基準下限 1000。
    private static readonly RelistPricing.UndercutRule Undercut = new(false, 0, 0, 1, 1000);
    private static readonly RelistPricing.AnomalyRule Guard = new(true, 100_000, Ratio, true, 1000);

    private static void ConsensusDropsSpecialTrades()
    {
        CheckLong("2 entries: none", PriceConsensus.Compute([3_000, 2_000], 10, Ratio).Price, -1);
        CheckLong("median of 5", PriceConsensus.Compute([9, 1, 5, 3, 7], 10, 1).Price, 5);
        CheckLong("even count averages", PriceConsensus.Compute([60_000, 4_000, 60_000, 55_000, 61_000, 59_000], 10, Ratio).Price,
            60_000);

        // 海盜手套：11-21 3500、11-08 3200、11-05 5096、11-02 2500、11-02 500、11-01 959、10-29 96（由新到舊）。
        var gloves = PriceConsensus.Compute([3_500, 3_200, 5_096, 2_500, 500, 959, 96], 10, Ratio);
        CheckLong("gloves consensus", gloves.Price, 3_200);
        CheckLong("gloves used", gloves.Used, 5);
        CheckBool("gloves dropped 96 and 500", gloves.DroppedText == "96/500", true);

        // 軟銀錠普通品（Universalis 實際紀錄，由新到舊）：最新一筆 450，前面一批 1,750～2,300 是同一分鐘一口氣成交的。
        long[] ingotNq = [450, 3_000, 3_000, 2_500, 2_300, 2_200, 2_000, 2_000, 2_000, 1_750,
            3_000, 3_000, 2_900, 2_800, 2_800, 2_800, 2_788, 2_700, 2_500, 2_199];
        var ingot10 = PriceConsensus.Compute(ingotNq, 10, Ratio);
        CheckLong("ingot N=10: burst pulls consensus to 2100", ingot10.Price, 2_100);
        CheckBool("ingot N=10: 450 not dropped", ingot10.Dropped.Length == 0, true);
        var ingot = PriceConsensus.Compute(ingotNq, 20, Ratio);
        CheckLong("ingot N=20 consensus", ingot.Price, 2_700);
        CheckBool("ingot N=20 dropped 450", ingot.DroppedText == "450", true);

        CheckLong("only the newest N count", PriceConsensus.Compute([100, 200, 300, 9_000, 9_000, 9_000], 3, Ratio).Price, 200);
        CheckLong("transfer sale left out",
            PriceConsensus.Compute([160_000, 165_000, 158_000, 99_999_999, 162_000], 10, Ratio).Price, 161_000);
        var tooFew = PriceConsensus.Compute([100, 100_000, 100_000_000], 10, Ratio);
        CheckLong("too few left after dropping", tooFew.Price, -1);
        CheckLong("invalid ratio drops nothing", PriceConsensus.Compute([100, 100_000, 100_000_000], 10, 1).Price, 100_000);
    }

    private static void CheapestOrdinaryListing()
    {
        CheckLong("troll-low skipped", PriceConsensus.CheapestOrdinary(new long[] { 1, 900, 1_000, 1_100 }, Ratio, out var r1), 900);
        CheckLong("low median reference", r1, 900);
        // 一低一天價：平均會被天價拉走，下中位數不會。
        CheckLong("two listings, one absurd", PriceConsensus.CheapestOrdinary(new long[] { 100_000_000, 500 }, Ratio, out _), 500);
        CheckLong("empty pool", PriceConsensus.CheapestOrdinary(Span<long>.Empty, Ratio, out var r2), -1);
        CheckLong("empty pool reference", r2, -1);
    }

    /// <summary>海盜長靴：家世界板上 2,600 與 100,000,000，別的世界 333／400／400／1000／3000／3000，沒有共識價。</summary>
    private static void SpecialPeerListingIsExcluded()
    {
        var reference = -1L;
        PriceConsensus.CheapestOrdinary(new long[] { 333, 400, 400, 1_000, 3_000, 3_000 }, Ratio, out reference);
        CheckLong("boots other-world reference", reference, 400);
        CheckLong("boots peer dropped", PriceConsensus.SanitizePeer(2_600, 100_000_000, reference, Ratio), -1);

        var listing = RelistPricing.FromListing(2_600, 100_000_000, -1, PriceSourceTag.Live, Now, Undercut, Guard, -1, -1,
            reference);
        CheckLong("boots listed at 2600", listing.Target, 2_600);
        var legacy = RelistPricing.FromListing(2_600, 100_000_000, -1, PriceSourceTag.Live, Now, Undercut, Guard, -1, -1, -1);
        CheckBool("legacy holds boots (anomaly-peer)", legacy.HeldByAnomaly && legacy.Anomaly.Baseline == AnomalyBaseline.Peer,
            true);
        CheckBool("legacy decision is Hold", RelistPricing.Decide(legacy, RelistPricing.FromSale(750, Now.AddDays(-11), -1, -1,
            Now, 7, Guard, -1, 2_600, -1), false).Outcome == RelistOutcome.Hold, true);

        // 最低價本身也偏離參照價：分不出誰才特殊，照舊讓同業判定去擋。
        CheckLong("both deviate: peer kept", PriceConsensus.SanitizePeer(26, 150_000, 400, Ratio), 150_000);
        CheckLong("peer near reference kept", PriceConsensus.SanitizePeer(2_600, 1_500, 400, Ratio), 1_500);
        CheckLong("no reference: peer kept", PriceConsensus.SanitizePeer(2_600, 100_000_000, -1, Ratio), 100_000_000);
        var noHistory = Guard with { UseHistory = false };
        CheckBool("history off: legacy peer test", RelistPricing.FromListing(2_600, 100_000_000, -1, PriceSourceTag.Live, Now,
            Undercut, noHistory, -1, -1, reference).HeldByAnomaly, true);
    }

    /// <summary>軟銀錠優質格：板上沒人，最近成交是普通品 450（11-22），普通品共識價 2,700（最新 20 筆）。</summary>
    private static void HeldSaleFallsBackToConsensus()
    {
        var sale = RelistPricing.FromSale(450, Now.AddHours(-4), -1, -1, Now, 7, Guard, 2_700, -1, 2_700);
        CheckBool("ingot sale followed with the N=10 consensus",
            RelistPricing.FromSale(450, Now.AddHours(-4), -1, -1, Now, 7, Guard, 2_100, -1, 2_100).IsUsable, true);
        CheckBool("ingot sale held (anomaly-hist)", sale.HeldByAnomaly && sale.Anomaly.Baseline == AnomalyBaseline.History, true);
        var decision = RelistPricing.Decide(PriceCandidate.None, sale, false);
        CheckBool("relist keeps Hold", decision.Outcome == RelistOutcome.Hold, true);

        var inputs = new RelistPricing.FallbackInputs(PriceConsensus.Compute([2_800, 3_000, 2_500, 1_750, 2_000, 2_800], 10, Ratio),
            false, -1, -1, 0, 450);
        var fallback = RelistPricing.WithNewListingFallback(decision, false, RelistPricing.FallbackCandidate(inputs, Undercut, Ratio));
        CheckBool("new listing uses consensus", fallback.Outcome == RelistOutcome.Price && fallback.Price == 2_600
                                                && fallback.Winner.Source == PriceSourceTag.Consensus, true);

        var dcOnly = new RelistPricing.FallbackInputs(ConsensusResult.None, false, 1_500, 2_000, 4, 450);
        CheckBool("held without consensus stays Hold",
            RelistPricing.WithNewListingFallback(decision, false, RelistPricing.FallbackCandidate(dcOnly, Undercut, Ratio)).Outcome
            == RelistOutcome.Hold, true);
    }

    /// <summary>海盜手套：板上沒人，最近成交 2,500 是 20 天前（過期）。</summary>
    private static void NobodySellingFallsBack()
    {
        var sale = RelistPricing.FromSale(2_500, Now.AddDays(-20), -1, -1, Now, 7, Guard, -1, -1, -1);
        var decision = RelistPricing.Decide(PriceCandidate.None, sale, false);
        CheckBool("legacy NoData", decision.Outcome == RelistOutcome.NoData, true);

        var consensus = new RelistPricing.FallbackInputs(
            PriceConsensus.Compute([3_500, 3_200, 5_096, 2_500, 500, 959, 96], 10, Ratio), false, 500, 950, 7, 2_500);
        var withConsensus = RelistPricing.WithNewListingFallback(decision, false,
            RelistPricing.FallbackCandidate(consensus, Undercut, Ratio));
        CheckBool("gloves listed at consensus 3200", withConsensus.Outcome == RelistOutcome.Price && withConsensus.Price == 3_200,
            true);

        var pool = new long[] { 500, 500, 949, 950, 987, 2_000, 2_000 };
        var cheapest = PriceConsensus.CheapestOrdinary(pool, Ratio, out var reference);
        var dc = new RelistPricing.FallbackInputs(ConsensusResult.None, false, cheapest, reference, pool.Length, 2_500);
        var withDc = RelistPricing.WithNewListingFallback(decision, false, RelistPricing.FallbackCandidate(dc, Undercut, Ratio));
        CheckBool("no consensus: cheapest other-world listing", withDc.Outcome == RelistOutcome.Price && withDc.Price == 500
                                                                && withDc.Winner.Source == PriceSourceTag.DcListing, true);

        var undercutOne = Undercut with { Amount = 1 };
        CheckLong("other-world listing takes the undercut",
            RelistPricing.FallbackCandidate(dc, undercutOne, Ratio).Target, 499);

        var troll = new RelistPricing.FallbackInputs(ConsensusResult.None, false, 100_000_000, 100_000_000, 1, 2_500);
        CheckBool("lone absurd listing rejected", RelistPricing.FallbackCandidate(troll, Undercut, Ratio).IsUsable, false);

        CheckBool("own listing lowest: no fallback",
            RelistPricing.WithNewListingFallback(decision, true, RelistPricing.FallbackCandidate(consensus, Undercut, Ratio)).Outcome
            == RelistOutcome.NoData, true);
        var nothing = new RelistPricing.FallbackInputs(ConsensusResult.None, false, -1, -1, 0, 2_500);
        CheckBool("nothing to fall back on: NoData",
            RelistPricing.WithNewListingFallback(decision, false, RelistPricing.FallbackCandidate(nothing, Undercut, Ratio)).Outcome
            == RelistOutcome.NoData, true);
    }

    private static void Check(string name, HistoryVerdict got, HistoryVerdict want)
        => Report(name, got == want, $"{got} (want {want})");

    private static void CheckBool(string name, bool got, bool want) => Report(name, got == want, $"{got} (want {want})");

    private static void CheckLong(string name, long got, long want) => Report(name, got == want, $"{got} (want {want})");

    private static void Report(string name, bool ok, string detail)
    {
        if (ok)
        {
            passes++;
            return;
        }

        failures++;
        Console.WriteLine($"FAIL {name}: {detail}");
    }
}
