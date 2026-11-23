using System;
using Marketbuddy;

// 情境測試：即時掛單看盤欄位（扣稅後實拿、比我便宜的件數、預估幾天賣完、資料新不新）。
// 實拿向量取自僱員錢包實際進帳；掛單與每日成交取自 Universalis 台服一個世界的實際回應。
// 負對照：舊的「先乘 (100-稅) 再捨去」算法在 2,399 gil 那一筆會差 1 gil。
// 執行：dotnet run -c Release --project tests/MarketWatchTests/MarketWatchTests.csproj（0＝全過、1＝有失敗）

internal static class Program
{
    private static int failures;
    private static int passes;

    private static int Main()
    {
        NetMatchesWallet();
        LegacyNetIsOffByOne();
        NetUnknownIsNotZero();
        CheaperCountsStrictlyLower();
        CheaperLowerBoundOnlyWhenCapCouldHideCheaper();
        DaysToSellFromRealVelocity();
        DaysToSellEdgeCases();
        StaleRules();

        Console.WriteLine($"MARKETWATCH_DONE pass={passes} fail={failures}");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>錢包實際進帳（單價, 數量, 稅率%, 進帳）。</summary>
    private static readonly (long Unit, long Qty, uint Tax, long Received)[] WalletVectors =
    [
        (5500, 1, 5, 5225), (3000, 1, 5, 2850), (2399, 1, 5, 2280), (2500, 1, 5, 2375),
        (6800, 1, 5, 6460), (1400, 1, 5, 1330), (3800, 1, 5, 3610), (1300, 1, 5, 1235),
        (2000, 1, 5, 1900), (50000, 1, 5, 47500), (25000, 1, 5, 23750),
    ];

    private static void NetMatchesWallet()
    {
        foreach (var v in WalletVectors)
            Check($"net {v.Unit}x{v.Qty}@{v.Tax}%", MarketWatchMath.NetTotal(v.Unit, v.Qty, v.Tax) == v.Received);
        Check("net stack 450x5@5%", MarketWatchMath.NetTotal(450, 5, 5) == 2250 - 112);
        Check("net tax clamp 30%->25%", MarketWatchMath.NetTotal(100, 1, 30) == 75);
    }

    private static void LegacyNetIsOffByOne()
    {
        static long Legacy(long unit, long qty, uint tax) => unit * qty * (100 - tax) / 100;
        Check("legacy formula disagrees with wallet on 2399", Legacy(2399, 1, 5) != 2280);
    }

    private static void NetUnknownIsNotZero()
    {
        Check("net unknown price", MarketWatchMath.NetTotal(0, 5, 5) == -1);
        Check("net unknown qty", MarketWatchMath.NetTotal(100, 0, 5) == -1);
    }

    // Universalis 台服一個世界、一件道具的普通品掛單：449×6、450×1、450×1、450×1。
    private static readonly long[] Prices = [449, 450, 450, 450];
    private static readonly int[] Quantities = [6, 1, 1, 1];

    private static void CheaperCountsStrictlyLower()
    {
        Check("cheaper than 450", MarketWatchMath.CheaperQuantity(Prices, Quantities, 450, long.MaxValue, out var lb1) == 6 && !lb1);
        Check("cheaper than 449 (I am the cheapest)", MarketWatchMath.CheaperQuantity(Prices, Quantities, 449, long.MaxValue, out _) == 0);
        Check("cheaper than 451", MarketWatchMath.CheaperQuantity(Prices, Quantities, 451, long.MaxValue, out _) == 9);
        Check("cheaper with unknown own price", MarketWatchMath.CheaperQuantity(Prices, Quantities, 0, long.MaxValue, out _) == -1);
        Check("cheaper on empty board", MarketWatchMath.CheaperQuantity([], [], 100, long.MaxValue, out _) == 0);
    }

    private static void CheaperLowerBoundOnlyWhenCapCouldHideCheaper()
    {
        // 回傳碰到上限、最貴的那筆 450：我掛 451 時被截掉的那些（≥450）可能也比我便宜。
        MarketWatchMath.CheaperQuantity(Prices, Quantities, 451, 450, out var lbAbove);
        Check("capped and all returned are cheaper -> lower bound", lbAbove);
        MarketWatchMath.CheaperQuantity(Prices, Quantities, 450, 450, out var lbEqual);
        Check("capped but cap price not below mine -> exact", !lbEqual);
    }

    private static void DaysToSellFromRealVelocity()
    {
        // 同一件道具普通品 7 天成交 155 件 ⇒ 每日 22.142857。
        const double perDay = 155 / 7.0;
        var days = MarketWatchMath.DaysToSell(6, 5, perDay);
        Check("days (6+5)/22.14", Math.Abs(days - 11 / perDay) < 1e-9 && days < 1);
        Check("days 300 ahead", Math.Abs(MarketWatchMath.DaysToSell(300, 10, 10) - 31) < 1e-9);
    }

    private static void DaysToSellEdgeCases()
    {
        Check("no sales -> infinity, not 0", double.IsPositiveInfinity(MarketWatchMath.DaysToSell(3, 1, 0)));
        Check("unknown velocity -> NaN", double.IsNaN(MarketWatchMath.DaysToSell(3, 1, double.NaN)));
        Check("unknown cheaper -> NaN", double.IsNaN(MarketWatchMath.DaysToSell(-1, 1, 5)));
        Check("unknown qty -> NaN", double.IsNaN(MarketWatchMath.DaysToSell(3, 0, 5)));
    }

    private static void StaleRules()
    {
        var now = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
        Check("never uploaded is stale", MarketWatchMath.IsStale(DateTime.MinValue, now, MarketWatchMath.StaleAfter));
        Check("23h is fresh", !MarketWatchMath.IsStale(now.AddHours(-23), now, MarketWatchMath.StaleAfter));
        Check("25h is stale", MarketWatchMath.IsStale(now.AddHours(-25), now, MarketWatchMath.StaleAfter));
    }

    private static void Check(string name, bool ok)
    {
        if (ok)
        {
            passes++;
            return;
        }

        failures++;
        Console.WriteLine($"FAIL {name}");
    }
}
