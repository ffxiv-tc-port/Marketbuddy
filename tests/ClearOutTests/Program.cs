using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Marketbuddy;

// 情境測試：低價下架紀錄本（記帳、合併、修剪、CSV 來回）、「常被低價下架」清單、AutoRetainer 剪貼簿格式、背包分揀分類。
// 執行：dotnet run -c Release --project tests/ClearOutTests/ClearOutTests.csproj
// 回傳碼：0＝全部通過；1＝有情境失敗。

internal static class Program
{
    private static readonly VendorSellKey Ore = new(5111, false);
    private static readonly VendorSellKey OreHq = new(5111, true);
    private static readonly VendorSellKey Cloth = new(5330, false);

    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private static int failures;
    private static int passes;

    private static int Main()
    {
        BookNoteCountsAndKeepsLastSale();
        BookPruneByAge();
        BookPruneByCapDropsOldest();
        BookCsvRoundTrip();
        BookCsvRejectsBrokenLines();
        BookMergeSumsCounts();

        JunkNeedsTwoDelistsAndLowSale();
        JunkCombinesQualities();
        JunkNoThresholdIsEmpty();
        ClipboardIsJsonListOfUint();

        SortVendorOnlyWhenDelistedAndStillLow();
        SortProtectedWins();
        SortNotMarketableLeftAlone();
        SortNoUsablePriceNeedsCheck();
        SortThresholdEdges();
        SortCannotVendorNeverTicked();
        SlotEqualityCatchesChanges();

        Console.WriteLine($"{passes} passed, {failures} failed");
        Console.WriteLine(failures == 0 ? "CLEAROUT_TESTS_OK" : "CLEAROUT_TESTS_FAIL");
        return failures == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ 紀錄本

    private static void BookNoteCountsAndKeepsLastSale()
    {
        var book = new LowPriceDelistBook(100, 180);
        book.Note(Ore, T0, 300, T0.AddHours(-1), 200);
        book.Note(Ore, T0.AddDays(1), -1, DateTime.MinValue, 100);
        book.TryGet(Ore, out var r);
        Check("note: count 2", r.Count == 2);
        Check("note: no sale this time keeps the previous sale", r.LastSalePrice == 300 && r.LastSaleAtUtc == T0.AddHours(-1));
        Check("note: last delist time and target move on", r.LastDelistUtc == T0.AddDays(1) && r.LastTargetPrice == 100);
        book.Note(Ore, T0.AddDays(2), 900, T0.AddDays(2), 0);
        book.TryGet(Ore, out r);
        Check("note: new sale replaces, unknown target keeps old", r.LastSalePrice == 900 && r.LastTargetPrice == 100 && r.Count == 3);
        book.Note(new VendorSellKey(0, false), T0, 1, T0, 1);
        Check("note: item id 0 ignored", book.Count == 1);
        Check("note: HQ is a separate key", !book.TryGet(OreHq, out _));
    }

    private static void BookPruneByAge()
    {
        var book = new LowPriceDelistBook(100, 30);
        book.Note(Ore, T0, 1, T0, 1);
        book.Note(Cloth, T0.AddDays(20), 1, T0, 1);
        var removed = book.Prune(T0.AddDays(31));
        Check("prune age: only the old one goes", removed == 1 && !book.TryGet(Ore, out _) && book.TryGet(Cloth, out _));
    }

    private static void BookPruneByCapDropsOldest()
    {
        var book = new LowPriceDelistBook(2, 180);
        book.Note(Ore, T0.AddHours(2), 1, T0, 1);
        book.Note(Cloth, T0, 1, T0, 1);
        book.Note(OreHq, T0.AddHours(1), 1, T0, 1);
        var removed = book.Prune(T0.AddHours(3));
        Check("prune cap: oldest dropped, count at cap", removed == 1 && book.Count == 2 && !book.TryGet(Cloth, out _));
    }

    private static void BookCsvRoundTrip()
    {
        var book = new LowPriceDelistBook(100, 180);
        book.Note(Ore, T0, 300, T0.AddMinutes(-5), 200);
        book.Note(OreHq, T0, -1, DateTime.MinValue, 0);
        var csv = book.ToCsv();
        var back = new LowPriceDelistBook(100, 180);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Check("csv: header first", lines[0] == LowPriceDelistBook.CsvHeader);
        foreach (var line in lines)
        {
            if (LowPriceDelistBook.TryParseCsvLine(line, out var rec))
                back.Merge(rec);
        }

        var same = back.Count == 2 && book.Records.All(r => back.TryGet(r.Key, out var b) && b == r);
        Check("csv: round trip keeps every field (incl. unknown sale / time)", same);
    }

    private static void BookCsvRejectsBrokenLines()
    {
        string[] bad =
        [
            LowPriceDelistBook.CsvHeader, "", "5111,0,2,2026-10-01 12:00:00,300,", "0,0,2,2026-10-01 12:00:00,300,,0",
            "5111,2,2,2026-10-01 12:00:00,300,,0", "5111,0,0,2026-10-01 12:00:00,300,,0", "5111,0,2,,300,,0",
            "5111,0,2,2026/10/01,300,,0",
        ];
        var rejected = bad.All(l => !LowPriceDelistBook.TryParseCsvLine(l, out _));
        var good = LowPriceDelistBook.TryParseCsvLine("5111,1,2,2026-10-01 12:00:00,-1,,0", out var r) &&
                   r.Key == OreHq && r.LastSalePrice == -1 && r.LastSaleAtUtc == DateTime.MinValue &&
                   r.LastDelistUtc == T0 && r.LastDelistUtc.Kind == DateTimeKind.Utc;
        Check("csv: broken lines rejected, valid line parsed as UTC", rejected && good);
    }

    private static void BookMergeSumsCounts()
    {
        var book = new LowPriceDelistBook(100, 180);
        book.Note(Ore, T0.AddDays(1), -1, DateTime.MinValue, 50);
        book.Merge(new LowPriceDelistRecord(Ore, 3, T0, 400, T0, 300));
        book.TryGet(Ore, out var r);
        Check("merge: counts add, newer delist wins, missing sale filled from older",
            r.Count == 4 && r.LastDelistUtc == T0.AddDays(1) && r.LastTargetPrice == 50 && r.LastSalePrice == 400);
    }

    // ------------------------------------------------------------------ 常被低價下架

    private static LowPriceDelistRecord R(VendorSellKey key, int count, long sale, int day = 0)
        => new(key, count, T0.AddDays(day), sale, sale < 0 ? DateTime.MinValue : T0.AddDays(day), 0);

    private static void JunkNeedsTwoDelistsAndLowSale()
    {
        var list = ClearOutRules.JunkCandidates(
        [
            R(Ore, 2, 200), R(Cloth, 1, 100), R(new VendorSellKey(1, false), 5, 1000), R(new VendorSellKey(2, false), 9, -1),
            R(new VendorSellKey(3, false), 4, 999),
        ], 1000);
        Check("junk: only >=2 delists with a known sale below the minimum",
            list.Select(c => c.ItemId).SequenceEqual(new uint[] { 3, 5111 }));
        Check("junk: sorted by count, most first", list.Count == 2 && list[0].Count == 4);
    }

    private static void JunkCombinesQualities()
    {
        var list = ClearOutRules.JunkCandidates([R(Ore, 1, 5000, 0), R(OreHq, 1, 300, 1)], 1000);
        Check("junk: HQ+NQ counted together, latest sale decides",
            list.Count == 1 && list[0].Count == 2 && list[0].LastSalePrice == 300 && list[0].HasHq && list[0].HasNq);
        var list2 = ClearOutRules.JunkCandidates([R(Ore, 1, 300, 1), R(OreHq, 1, 5000, 0)], 1000);
        Check("junk: older low sale does not count when the latest is high (reversed order)",
            ClearOutRules.JunkCandidates([R(OreHq, 1, 300, 0), R(Ore, 1, 5000, 1)], 1000).Count == 0 && list2.Count == 1);
    }

    private static void JunkNoThresholdIsEmpty()
        => Check("junk: no minimum set -> nothing", ClearOutRules.JunkCandidates([R(Ore, 9, 1)], 0).Count == 0);

    private static void ClipboardIsJsonListOfUint()
    {
        var text = ClearOutRules.ToAutoRetainerClipboard([10, 5, 10, 0, 1]);
        var parsed = JsonSerializer.Deserialize<List<uint>>(text);
        Check("clipboard: JSON List<uint>, deduped and sorted, no zero",
            text == "[1,5,10]" && parsed != null && parsed.SequenceEqual(new uint[] { 1, 5, 10 }));
        Check("clipboard: empty is []", ClearOutRules.ToAutoRetainerClipboard([]) == "[]");
    }

    // ------------------------------------------------------------------ 背包分揀

    private static BagSortItem Item(bool cannotVendor = false, bool marketable = true, bool isProtected = false)
        => new(Ore, 99, cannotVendor, marketable, isProtected);

    private static BagSortSale Sale(long price, bool stale = false) => new(BagSortSaleState.Ready, price, stale);

    private static void SortVendorOnlyWhenDelistedAndStillLow()
    {
        var v = BagSortClassifier.Classify(Item(), Sale(200), true, 1000);
        Check("sort: delisted before + sale below minimum -> sell to shop, pre-ticked",
            v is { Pile: BagSortPile.Vendor, PreTicked: true, CanTick: true });
        var neg = BagSortClassifier.Classify(Item(), Sale(200), false, 1000);
        Check("sort (negative): same price but never delisted -> market, not ticked",
            neg is { Pile: BagSortPile.Market, PreTicked: false, Reason: BagSortReason.BelowThresholdNeverDelisted });
        var neg2 = BagSortClassifier.Classify(Item(), Sale(5000), true, 1000);
        Check("sort (negative): delisted before but sale now above minimum -> market, not ticked",
            neg2 is { Pile: BagSortPile.Market, PreTicked: false });
    }

    private static void SortProtectedWins()
    {
        var v = BagSortClassifier.Classify(Item(isProtected: true), Sale(1), true, 1000);
        Check("sort: protected beats a vendor match", v is { Pile: BagSortPile.Excluded, Reason: BagSortReason.Protected, PreTicked: false, CanTick: false });
    }

    private static void SortNotMarketableLeftAlone()
    {
        var v = BagSortClassifier.Classify(Item(marketable: false), Sale(1), true, 1000);
        Check("sort: not marketable left alone", v is { Pile: BagSortPile.Excluded, CanTick: false });
    }

    private static void SortNoUsablePriceNeedsCheck()
    {
        var cases = new (BagSortSale Sale, BagSortReason Want)[]
        {
            (new BagSortSale(BagSortSaleState.Off, -1, false), BagSortReason.PriceLookupOff),
            (new BagSortSale(BagSortSaleState.Loading, -1, false), BagSortReason.PriceLoading),
            (new BagSortSale(BagSortSaleState.Failed, -1, false), BagSortReason.PriceFailed),
            (new BagSortSale(BagSortSaleState.NoData, -1, false), BagSortReason.NoSaleData),
            (new BagSortSale(BagSortSaleState.Ready, -1, false), BagSortReason.NoSaleData),
            (Sale(1, stale: true), BagSortReason.SaleStale),
        };
        var ok = cases.All(c =>
            BagSortClassifier.Classify(Item(), c.Sale, true, 1000) is { Pile: BagSortPile.NeedsPricing, PreTicked: false } v &&
            v.Reason == c.Want);
        Check("sort: no usable sale (off/loading/failed/none/stale) -> needs a price check, never pre-ticked", ok);
    }

    private static void SortThresholdEdges()
    {
        Check("sort: sale equal to minimum is not below it",
            BagSortClassifier.Classify(Item(), Sale(1000), true, 1000).Pile == BagSortPile.Market);
        Check("sort: one below the minimum is",
            BagSortClassifier.Classify(Item(), Sale(999), true, 1000).Pile == BagSortPile.Vendor);
        Check("sort: no minimum set -> market",
            BagSortClassifier.Classify(Item(), Sale(1), true, 0) is { Pile: BagSortPile.Market, Reason: BagSortReason.NoThreshold });
    }

    private static void SortCannotVendorNeverTicked()
    {
        var v = BagSortClassifier.Classify(Item(cannotVendor: true), Sale(1), true, 1000);
        Check("sort: shop-unsellable stays in the pile but cannot be ticked",
            v is { Pile: BagSortPile.Vendor, PreTicked: false, CanTick: false });
    }

    /// <summary>賣背包原有堆疊前的核對靠格子記錄的值相等：道具、品質、數量、指紋任一不同都要判不同。</summary>
    private static void SlotEqualityCatchesChanges()
    {
        var p = new VendorSellSlot(0, 3, Ore, 20, 7, false, false);
        var ok = p == new VendorSellSlot(0, 3, Ore, 20, 7, false, false) &&
                 p != new VendorSellSlot(0, 3, Ore, 19, 7, false, false) &&
                 p != new VendorSellSlot(0, 3, OreHq, 20, 7, false, false) &&
                 p != new VendorSellSlot(0, 3, Cloth, 20, 7, false, false) &&
                 p != new VendorSellSlot(0, 3, Ore, 20, 8, false, false);
        Check("slot check: item, quality, amount and fingerprint all compared", ok);
    }

    private static void Check(string name, bool ok)
    {
        if (ok) passes++;
        else failures++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
    }
}
