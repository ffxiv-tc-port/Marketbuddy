using System;
using Marketbuddy;

// 情境測試：批次上架把定價決策換成「上架／改賣商店／跳過」的結論，以及連續被遊戲拒絕的停止條件。
// 負對照照快速上架的舊規則（查不到價也掛上限價、不看門檻）寫：改前要失敗、改後要通過。
// 執行：dotnet run -c Release --project tests/BatchListTests/BatchListTests.csproj
// 回傳碼：0＝全部通過；1＝有情境失敗。

internal static class Program
{
    private const int MaxPrice = 999999999;
    private static int failures;
    private static int passes;

    private static int Main()
    {
        ListsAtDecisionPrice();
        BelowVendorIsNotListed();
        BelowMinIsNotListed();
        NoDataIsSkippedNotCapped();
        HoldWins();
        OwnLowestMatchesWhenCheaper();
        OwnLowestUsedWhenNoOtherPrice();
        ClampsToCap();
        RefusalStreak();
        Listable();

        Console.WriteLine($"BATCHLIST_DONE pass={passes} fail={failures}");
        return failures == 0 ? 0 : 1;
    }

    private static PriceProbeOutcome Classify(long decision, bool held = false, bool ownIsLowest = false,
        long ownLowest = -1, uint vendor = 0, uint tax = 5, bool belowVendor = false, int minPrice = 0)
        => BatchListRules.Classify(decision, held, ownIsLowest, ownLowest, vendor, tax, belowVendor, minPrice, MaxPrice,
            out _);

    private static void ListsAtDecisionPrice()
    {
        var o = BatchListRules.Classify(1234, false, false, -1, 0, 5, true, 0, MaxPrice, out var price);
        Check("有目標價就照價上架", o == PriceProbeOutcome.List && price == 1234, $"o={o} price={price}");
    }

    // 扣稅後 95 < NPC 收購 100：不上架。快速上架的舊規則是先掛上限價、再由引擎下架（使用者要的正是「根本不要上市場」）。
    private static void BelowVendorIsNotListed()
    {
        var o = Classify(100, vendor: 100, tax: 5, belowVendor: true);
        Check("扣稅後低於 NPC 收購價：改賣商店", o == PriceProbeOutcome.BelowVendor, $"o={o}");
        Check("扣稅後低於 NPC 收購價：舊規則照樣上架（負對照）", LegacyQuickList(100) == PriceProbeOutcome.List, "");
        var off = Classify(100, vendor: 100, tax: 5, belowVendor: false);
        Check("門檻關著時照樣上架", off == PriceProbeOutcome.List, $"o={off}");
        var noVendor = Classify(100, vendor: 0, tax: 5, belowVendor: true);
        Check("商店不收（0）不比", noVendor == PriceProbeOutcome.List, $"o={noVendor}");
    }

    private static void BelowMinIsNotListed()
    {
        Check("低於最低價：改賣商店", Classify(499, minPrice: 500) == PriceProbeOutcome.BelowMin, "");
        Check("等於最低價：上架", Classify(500, minPrice: 500) == PriceProbeOutcome.List, "");
    }

    private static void NoDataIsSkippedNotCapped()
    {
        Check("沒人在賣：跳過", Classify(-1) == PriceProbeOutcome.NoData, "");
        Check("沒人在賣：舊規則掛上限價（負對照）", LegacyQuickList(-1) == PriceProbeOutcome.List, "");
    }

    private static void HoldWins()
    {
        Check("異常低價保護擋下：跳過", Classify(-1, held: true) == PriceProbeOutcome.Hold, "");
        Check("擋下優先於自家最低價", Classify(-1, held: true, ownIsLowest: true, ownLowest: 300) == PriceProbeOutcome.Hold, "");
    }

    private static void OwnLowestMatchesWhenCheaper()
    {
        BatchListRules.Classify(1000, false, true, 800, 0, 5, false, 0, MaxPrice, out var price);
        Check("自家掛單最低而且比目標價便宜：齊平", price == 800, $"price={price}");
        BatchListRules.Classify(700, false, true, 800, 0, 5, false, 0, MaxPrice, out price);
        Check("成交價比自家最低更低：用成交價", price == 700, $"price={price}");
    }

    private static void OwnLowestUsedWhenNoOtherPrice()
    {
        var o = BatchListRules.Classify(-1, false, true, 800, 0, 5, false, 0, MaxPrice, out var price);
        Check("只有自家掛單：與它齊平", o == PriceProbeOutcome.List && price == 800, $"o={o} price={price}");
    }

    private static void ClampsToCap()
    {
        BatchListRules.Classify(5_000_000_000, false, false, -1, 0, 5, false, 0, MaxPrice, out var price);
        Check("超過上限夾到上限", price == MaxPrice, $"price={price}");
    }

    private static void RefusalStreak()
    {
        var streak = 0;
        var stop1 = BatchListRules.NoteWindowOutcome(ref streak, false);
        var stop2 = BatchListRules.NoteWindowOutcome(ref streak, false);
        Check("連續兩件沒開窗：停", !stop1 && stop2, $"stop1={stop1} stop2={stop2}");

        streak = 0;
        BatchListRules.NoteWindowOutcome(ref streak, false);
        BatchListRules.NoteWindowOutcome(ref streak, true);
        var stop3 = BatchListRules.NoteWindowOutcome(ref streak, false);
        Check("中間開過一次就重算：不停", !stop3, $"streak={streak}");
    }

    private static void Listable()
    {
        Check("能上市場", BatchListRules.IsListable(false, 44, false, false), "");
        Check("不可交易：不列", !BatchListRules.IsListable(true, 44, false, false), "");
        Check("市場分類 0：不列", !BatchListRules.IsListable(false, 0, false, false), "");
        Check("收藏品：不列", !BatchListRules.IsListable(false, 44, true, false), "");
    }

    // 改前的快速上架：先掛上限價、再交給引擎；這一步本身不看門檻、查不到價也照掛。
    private static PriceProbeOutcome LegacyQuickList(long decision) => PriceProbeOutcome.List;

    private static void Check(string name, bool ok, string detail)
    {
        if (ok)
        {
            passes++;
            return;
        }

        failures++;
        Console.WriteLine($"FAIL {name} {detail}");
    }
}
