using System;
using System.Collections.Generic;
using Marketbuddy;

// 情境測試：最近成交價 S 比板上最低 L 低、而且正好是自家僱員掛過的價時，要不要照 S 定價。
// 負對照是改版前的行為（saleIsOwnPrice 恆為 false）：三大染劑「被自己的成交拉低」那一格照 S 掛。
// 執行：dotnet run -c Release --project tests/OwnSaleEchoTests/OwnSaleEchoTests.csproj
// 回傳碼：0＝全部通過；1＝有情境失敗。

internal static class Program
{
    private const uint WhiteDye = 13114;
    private const uint OtherItem = 5057;
    private static readonly DateTime Now = new(2026, 11, 19, 0, 0, 0, DateTimeKind.Utc);

    // 降 1 gil、不捨；異常價保護關掉（本測試只看取低規則）。
    private static readonly RelistPricing.UndercutRule Undercut = new(false, 0, 1, 1, 0);
    private static readonly RelistPricing.AnomalyRule NoGuard = new(false, 100000, 5, false, 1000);

    private static int failures;
    private static int passes;

    private static int Main()
    {
        DecideRule();
        IndexContents();
        EndToEnd();

        Console.WriteLine($"OwnSaleEchoTests: {passes} passed, {failures} failed");
        return failures == 0 ? 0 : 1;
    }

    private static PriceCandidate L(long lowest)
        => RelistPricing.FromListing(lowest, -1, -1, PriceSourceTag.Live, Now, Undercut, NoGuard, -1, -1, -1);

    private static PriceCandidate S(long unitPrice, double daysAgo = 0.5, int maxAgeDays = 7)
        => RelistPricing.FromSale(unitPrice, Now.AddDays(-daysAgo), -1, -1, Now, maxAgeDays, NoGuard, -1, -1, -1);

    private static void DecideRule()
    {
        // 板上最低 33,000（目標 32,999），最近成交 28,000＝自家先前掛的價。
        var own = RelistPricing.Decide(L(33_000), S(28_000), true);
        Check("own lower sale is not followed", own.Price == 32_999 && own.Winner.Source == PriceSourceTag.Live, true);
        Check("own lower sale is flagged", own.OwnSaleSkipped, true);

        var legacy = RelistPricing.Decide(L(33_000), S(28_000), false);
        Check("legacy follows the own sale down", legacy.Price == 28_000 && legacy.Winner.Source == PriceSourceTag.Sale, true);
        Check("legacy is not flagged", legacy.OwnSaleSkipped, false);

        var higher = RelistPricing.Decide(L(33_000), S(40_000), true);
        Check("own sale above the board: board as before, not flagged",
            higher.Price == 32_999 && !higher.OwnSaleSkipped, true);

        // 33,050 捨到 33,000 仍高於 32,999：本來就照 L，不算「因為自家而跳過」。
        var roundedAbove = RelistPricing.Decide(L(33_000), S(33_050), true);
        Check("rounded sale not below the board target is not flagged",
            roundedAbove.Price == 32_999 && !roundedAbove.OwnSaleSkipped, true);

        var onlySale = RelistPricing.Decide(PriceCandidate.None, S(28_000), true);
        Check("nobody else listing: own sale still used", onlySale.Price == 28_000 && !onlySale.OwnSaleSkipped, true);

        var stale = RelistPricing.Decide(L(33_000), S(28_000, daysAgo: 10), true);
        Check("stale sale: board, not flagged", stale.Price == 32_999 && !stale.OwnSaleSkipped, true);

        var nothing = RelistPricing.Decide(PriceCandidate.None, PriceCandidate.None, true);
        Check("no data stays no data", nothing.Outcome == RelistOutcome.NoData && !nothing.OwnSaleSkipped, true);
    }

    private static void IndexContents()
    {
        var shelf = new List<(uint, bool, long)>
        {
            (WhiteDye, false, 28_000),
            (WhiteDye, true, 31_000),
            (0, false, 999),
            (OtherItem, false, -1),
        };
        var gone = new List<(uint, bool, long, DateTime)>
        {
            (WhiteDye, false, 26_500, Now.AddDays(-6.9)),
            (WhiteDye, false, 24_000, Now.AddDays(-7.1)),
            (OtherItem, false, 1_200, Now.AddDays(-7)),
        };
        var index = OwnPriceIndex.Build(shelf, gone, Now);

        Check("shelf price", index.Contains(WhiteDye, false, 28_000), true);
        Check("quality is part of the key", index.Contains(WhiteDye, true, 28_000), false);
        Check("hq shelf price", index.Contains(WhiteDye, true, 31_000), true);
        Check("gone inside the window", index.Contains(WhiteDye, false, 26_500), true);
        Check("gone outside the window", index.Contains(WhiteDye, false, 24_000), false);
        Check("edge of the window counts", index.Contains(OtherItem, false, 1_200), true);
        Check("exact price only (no rounding)", index.Contains(WhiteDye, false, 28_050), false);
        Check("item 0 and unknown price are dropped", index.Count, 4);
        Check("non-positive query", index.Contains(WhiteDye, false, 0), false);
        Check("empty index", OwnPriceIndex.Empty.Contains(WhiteDye, false, 28_000), false);
    }

    private static void EndToEnd()
    {
        var index = OwnPriceIndex.Build(
            [(WhiteDye, false, 28_050)],
            Array.Empty<(uint, bool, long, DateTime)>(), Now);

        // S 的原始單價 28,050 對上自家價；它捨到 28,000 才和 L 比。
        var sale = S(28_050);
        var decision = RelistPricing.Decide(L(33_000), sale, index.Contains(WhiteDye, false, sale.Reference));
        Check("raw sale price is matched, rounded target compared", decision.Price == 32_999 && decision.OwnSaleSkipped, true);

        var other = S(27_900);
        var notOwn = RelistPricing.Decide(L(33_000), other, index.Contains(WhiteDye, false, other.Reference));
        Check("someone else's lower sale is followed", notOwn.Price == 27_900 && !notOwn.OwnSaleSkipped, true);
    }

    private static void Check<T>(string name, T actual, T expected)
    {
        if (EqualityComparer<T>.Default.Equals(actual, expected))
        {
            passes++;
            return;
        }

        failures++;
        Console.WriteLine($"FAIL {name}: expected {expected}, got {actual}");
    }
}
