using System;
using System.Collections.Generic;
using Marketbuddy;

// 情境測試：和近期成交中位數差太多倍的參考價（轉錢交易、打錯字）怎麼判。
// 情境取自實機 log 的定價紀錄與 Universalis 資料中心成交中位數（倍數 5、中位數下限 1000）。
// 負對照是改版前唯一的判準（下一位賣家 >= 10 萬且 >= 參考價 5 倍才擋）：前兩個情境它都判錯。
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
        MedianNeedsEnoughEntries();

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

    private static void MedianNeedsEnoughEntries()
    {
        CheckLong("4 entries", HistoryOutlier.Median(new List<long> { 1, 2, 3, 4 }), -1);
        CheckLong("5 entries", HistoryOutlier.Median(new List<long> { 9, 1, 5, 3, 7 }), 5);
        CheckLong("6 entries", HistoryOutlier.Median(new List<long> { 60_000, 4_000, 60_000, 55_000, 61_000, 59_000 }), 59_500);
        CheckLong("one transfer sale barely moves it",
            HistoryOutlier.Median(new List<long> { 160_000, 165_000, 158_000, 9_999_999, 162_000 }), 162_000);
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
