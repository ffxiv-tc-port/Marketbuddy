using System;
using System.Collections.Generic;
using Marketbuddy;

// 情境測試：快速上架認領市場格與逾時分類。情境取自實機 log 的形狀（引擎邊跑邊下架、同一格背包連按兩次）。
// 每個情境同時跑「改前規則」（Legacy*，照改前的 QuickLister 逐字搬）當負對照：改前要失敗、改後要通過。
// 執行：dotnet run -c Release --project tests/QuickListMatchTests/QuickListMatchTests.csproj
// 回傳碼：0＝全部通過；1＝有情境失敗。

internal static class Program
{
    private const ulong RetainerA = 1001, RetainerB = 1002;
    private const int Inv1 = 0, Inv2 = 1;
    private static readonly DateTime T0 = new(2026, 10, 21, 10, 28, 38, DateTimeKind.Utc);

    private static int failures;
    private static int passes;

    private static int Main()
    {
        SlotFreedByEngineAfterSnapshot();
        SameItemListedBeforeIsNotNew();
        EngineStartDoesNotReleaseSlotForSecondWatch();
        ReservationDroppedWhenItemLeaves();
        ReservationDroppedOnRetainerChange();
        ReservationKeptWhileContainerUnloaded();
        TwoStacksOfSameItemGetTwoSlots();
        SiblingFromSameBagSlot();

        Console.WriteLine($"QUICKLISTMATCH_DONE pass={passes} fail={failures}");
        return failures == 0 ? 0 : 1;
    }

    // 星藍石：確認時第 2 格是別的道具，引擎 1.3 秒後下架空出第 2 格，新上架補進第 2 格。
    private static void SlotFreedByEngineAfterSnapshot()
    {
        var pre = new Dictionary<short, uint> { [0] = 9, [1] = 38560, [2] = 5721, [3] = 5585 };
        uint[] now = [9, 38560, 12545, 5585, 0, 0];
        var got = QuickListMatch.FindNewListingSlot(now, 12545, pre, new HashSet<short>(), new Dictionary<short, MatchedListing>());
        var legacy = LegacyFind(now, 12545, [0, 1, 2, 3], new HashSet<short>());
        Check("引擎下架空出的格補進新上架：改後認得", got == 2, $"got={got}");
        Check("引擎下架空出的格補進新上架：改前認不得（負對照）", legacy == -1, $"legacy={legacy}");
    }

    private static void SameItemListedBeforeIsNotNew()
    {
        var pre = new Dictionary<short, uint> { [4] = 12545 };
        uint[] now = [0, 0, 0, 0, 12545];
        var got = QuickListMatch.FindNewListingSlot(now, 12545, pre, new HashSet<short>(), new Dictionary<short, MatchedListing>());
        Check("確認前就掛著的同一件不算新上架", got == -1, $"got={got}");
    }

    // 克拉洛胡桃木木材：第一筆認領第 2 格，引擎接手（佇列放掉認領）的下一幀，另一筆同道具的等待又認到第 2 格 → 同一格定價、下架兩次。
    private static void EngineStartDoesNotReleaseSlotForSecondWatch()
    {
        var preOfSecond = new Dictionary<short, uint> { [0] = 9 };
        uint[] now = [9, 0, 44023, 0];
        var claimedAfterEngineStart = new HashSet<short>();
        var reserved = new Dictionary<short, MatchedListing> { [2] = new(44023, RetainerA) };
        var got = QuickListMatch.FindNewListingSlot(now, 44023, preOfSecond, claimedAfterEngineStart, reserved);
        var legacy = LegacyFind(now, 44023, [0], claimedAfterEngineStart);
        Check("引擎接手後同一格不會被第二筆再認領：改後", got == -1, $"got={got}");
        Check("引擎接手後同一格不會被第二筆再認領：改前會再認領（負對照）", legacy == 2, $"legacy={legacy}");
    }

    private static void ReservationDroppedWhenItemLeaves()
    {
        var reserved = new Dictionary<short, MatchedListing> { [2] = new(44023, RetainerA), [5] = new(777, RetainerA) };
        uint[] now = [9, 0, 0, 0, 0, 777];
        var drop = QuickListMatch.StaleReservations(reserved, RetainerA, now, now.Length);
        Check("下架後那一格的認領放掉、還在的留著", drop is { Count: 1 } && drop[0] == 2, Show(drop));

        reserved.Remove(2);
        uint[] relisted = [9, 0, 44023, 0, 0, 777];
        var got = QuickListMatch.FindNewListingSlot(relisted, 44023, new Dictionary<short, uint> { [0] = 9, [5] = 777 },
            new HashSet<short>(), reserved);
        Check("放掉之後同一格再上架同一件可以再認領", got == 2, $"got={got}");
    }

    private static void ReservationDroppedOnRetainerChange()
    {
        var reserved = new Dictionary<short, MatchedListing> { [1] = new(5, RetainerA) };
        uint[] now = [0, 5];
        var other = QuickListMatch.StaleReservations(reserved, RetainerB, now, now.Length);
        var away = QuickListMatch.StaleReservations(reserved, 0, null, 0);
        Check("換僱員就放掉認領", other is { Count: 1 }, Show(other));
        Check("不在僱員身上就放掉認領", away is { Count: 1 }, Show(away));
    }

    private static void ReservationKeptWhileContainerUnloaded()
    {
        var reserved = new Dictionary<short, MatchedListing> { [1] = new(5, RetainerA) };
        var drop = QuickListMatch.StaleReservations(reserved, RetainerA, null, 0);
        Check("容器沒載入時同一名僱員的認領不動", drop == null, Show(drop));
    }

    private static void TwoStacksOfSameItemGetTwoSlots()
    {
        var pre = new Dictionary<short, uint> { [0] = 9 };
        uint[] now = [9, 5187, 0, 5187];
        var claimed = new HashSet<short>();
        var reserved = new Dictionary<short, MatchedListing>();
        var first = QuickListMatch.FindNewListingSlot(now, 5187, pre, claimed, reserved);
        reserved[first] = new MatchedListing(5187, RetainerA);
        var second = QuickListMatch.FindNewListingSlot(now, 5187, pre, claimed, reserved);
        Check("兩疊同道具各認一格", first == 1 && second == 3, $"first={first} second={second}");
    }

    // 楓糖：第一次按鍵 2.2 秒後同一格背包又按一次，第二次的那筆認領成功；第一次那筆 6 秒到期。
    private static void SiblingFromSameBagSlot()
    {
        var source = new ListingSource(Inv1, 12, 2);
        var recent = new List<SourceMatch> { new(RetainerA, 4848, source, 11, T0.AddSeconds(2.2)) };
        var hit = QuickListMatch.FindSiblingMatch(recent, RetainerA, 4848, source, T0, 10000);
        Check("同一格背包後一次按鍵認領成功＝重按", hit is { Slot: 11 }, hit?.ToString() ?? "null");

        var otherBag = QuickListMatch.FindSiblingMatch(recent, RetainerA, 4848, new ListingSource(Inv2, 12, 2), T0, 10000);
        Check("另一格背包的同道具不算重按", otherBag == null, otherBag?.ToString() ?? "null");

        var otherRetainer = QuickListMatch.FindSiblingMatch(recent, RetainerB, 4848, source, T0, 10000);
        Check("另一名僱員不算重按", otherRetainer == null, otherRetainer?.ToString() ?? "null");

        var before9 = new List<SourceMatch> { new(RetainerA, 4848, source, 11, T0.AddSeconds(-9)) };
        var before11 = new List<SourceMatch> { new(RetainerA, 4848, source, 11, T0.AddSeconds(-11)) };
        Check("按鍵前 9 秒內同一格已上架成功＝重按",
            QuickListMatch.FindSiblingMatch(before9, RetainerA, 4848, source, T0, 10000) != null, "");
        Check("按鍵前超過 10 秒的不算",
            QuickListMatch.FindSiblingMatch(before11, RetainerA, 4848, source, T0, 10000) == null, "");
    }

    // 改前 QuickLister.PumpListingWatches 的比對：確認前有東西的格一律排除、只看佇列認領。
    private static short LegacyFind(uint[] slotItems, uint itemId, HashSet<short> preListingSlots, HashSet<short> claimedSlots)
    {
        for (short i = 0; i < slotItems.Length; i++)
        {
            if (preListingSlots.Contains(i) || claimedSlots.Contains(i))
                continue;
            if (slotItems[i] != itemId)
                continue;
            return i;
        }

        return -1;
    }

    private static string Show(List<short>? list) => list == null ? "null" : "[" + string.Join(",", list) + "]";

    private static void Check(string name, bool ok, string detail)
    {
        if (ok)
        {
            passes++;
            Console.WriteLine($"PASS {name}");
            return;
        }

        failures++;
        Console.WriteLine($"FAIL {name} {detail}");
    }
}
