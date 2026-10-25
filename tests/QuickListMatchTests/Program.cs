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
        GateWaitsForEarlierListing();
        GateCapReached();
        GateIgnoresOtherRetainerUnsentAndLater();
        GateAbandonsOnUserEvent();
        GateAbandonsOnHeldInput();
        ButtonReleaseIsNotAUserClick();
        DeferredConfirmChecksSourceSlot();
        SiblingRequiresSameQuantity();
        HandedOverWindowStaysInFlight();
        SlowAckKeepsWaiting();
        UnsentWatchKeepsShortTimeout();
        SiblingRetentionCoversHardTimeout();

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

    // 楓糖（實機）：引擎下架 → 金屬黃染劑確認 → 0.65 秒後楓糖的上架視窗開出，金屬黃染劑還沒出現在市場格。
    // 逐幀模擬：金屬黃染劑在開窗後 400 ms 出現。改前開窗當幀就按（被吞的那種）；改後等它出現那一幀才按。
    private static void GateWaitsForEarlierListing()
    {
        var setup = T0.AddMilliseconds(800);
        var l1 = new InFlightListing(RetainerA, T0.AddMilliseconds(150), Sent: true);
        var landedAt = setup.AddMilliseconds(400);

        double? FirstConfirmMs(Func<InFlightListing[], DateTime, ConfirmGateVerdict> rule)
        {
            for (var ms = 0; ms <= 4000; ms += 16)
            {
                var now = setup.AddMilliseconds(ms);
                InFlightListing[] inFlight = now < landedAt ? [l1] : [];
                if (rule(inFlight, now) != ConfirmGateVerdict.Wait)
                    return ms;
            }

            return null;
        }

        var after = FirstConfirmMs((f, now) => QuickListConfirmGate.Decide(f, RetainerA, setup, now, out _));
        var legacy = FirstConfirmMs((_, _) => ConfirmGateVerdict.ConfirmNow);
        Check("更早那筆出現的那一幀才按確認", after is >= 400 and < 416, $"after={after}");
        Check("改前開窗當幀就按、那筆還在飛（負對照）", legacy == 0, $"legacy={legacy}");

        var blocked = QuickListConfirmGate.Decide([l1], RetainerA, setup, setup, out var blocking);
        Check("開窗當下回報擋住 1 筆", blocked == ConfirmGateVerdict.Wait && blocking == 1, $"{blocked} blocking={blocking}");
        var none = QuickListConfirmGate.Decide([], RetainerA, setup, setup, out _);
        Check("沒有在飛的上架：開窗當下就按（行為不變）", none == ConfirmGateVerdict.ConfirmNow, $"{none}");
    }

    private static void GateCapReached()
    {
        var l1 = new InFlightListing(RetainerA, T0, Sent: true);
        var setup = T0.AddMilliseconds(500);
        var before = QuickListConfirmGate.Decide([l1], RetainerA, setup, setup.AddMilliseconds(2999), out _);
        var at = QuickListConfirmGate.Decide([l1], RetainerA, setup, setup.AddMilliseconds(3000), out var blocking);
        Check("上限前一刻還在等", before == ConfirmGateVerdict.Wait, $"{before}");
        Check("等到 3 秒上限照送", at == ConfirmGateVerdict.CapReached && blocking == 1, $"{at}");
    }

    private static void GateIgnoresOtherRetainerUnsentAndLater()
    {
        var setup = T0.AddSeconds(1);
        var otherRetainer = QuickListConfirmGate.Decide([new InFlightListing(RetainerB, T0, true)], RetainerA, setup, setup, out _);
        var unsent = QuickListConfirmGate.Decide([new InFlightListing(RetainerA, T0, false)], RetainerA, setup, setup, out _);
        var later = QuickListConfirmGate.Decide([new InFlightListing(RetainerA, setup.AddMilliseconds(1), true)], RetainerA, setup,
            setup.AddMilliseconds(5), out _);
        var noRetainer = QuickListConfirmGate.Decide([new InFlightListing(0, T0, true)], 0, setup, setup, out _);
        Check("別名僱員的上架不擋", otherRetainer == ConfirmGateVerdict.ConfirmNow, $"{otherRetainer}");
        Check("沒按確認的那筆不擋下一筆", unsent == ConfirmGateVerdict.ConfirmNow, $"{unsent}");
        Check("開始等之後才送出的不擋", later == ConfirmGateVerdict.ConfirmNow, $"{later}");
        Check("認不得僱員不等", noRetainer == ConfirmGateVerdict.ConfirmNow, $"{noRetainer}");
    }

    // 數值照本 pin 的 AtkEventType：ButtonClick 25、MouseDown 3；250 是列舉外的未知值。
    private static void GateAbandonsOnUserEvent()
    {
        Check("ButtonClick 放棄", QuickListConfirmGate.ShouldAbandonOnEvent(25), "");
        Check("MouseDown 放棄", QuickListConfirmGate.ShouldAbandonOnEvent(3), "");
        Check("未知值 250 放棄", QuickListConfirmGate.ShouldAbandonOnEvent(250), "");
        Check("0 放棄", QuickListConfirmGate.ShouldAbandonOnEvent(0), "");
        foreach (var hover in new byte[] { 5, 6, 7, 18, 19 })
            Check($"懸停／焦點 {hover} 不放棄", !QuickListConfirmGate.ShouldAbandonOnEvent(hover), "");
    }

    // 手把遮罩照本 pin 的 GamepadButtons：South 0x20、DpadUp 0x1、R1 0x800；鍵碼 Return 13、NumPad0 96、Space 32、W 87。
    private static void GateAbandonsOnHeldInput()
    {
        Check("沒有輸入不放棄", !QuickListConfirmGate.ShouldAbandonOnInput(0, []), "");
        foreach (ushort mask in new ushort[] { 0x20, 0x1, 0x800, 0x8000 })
            Check($"手把 0x{mask:X} 按著放棄", QuickListConfirmGate.ShouldAbandonOnInput(mask, []), "");
        foreach (var key in new[] { 13, 96, 32 })
            Check($"鍵 {key} 按著放棄", QuickListConfirmGate.ShouldAbandonOnInput(0, [key]), "");
        Check("W 按著不放棄", !QuickListConfirmGate.ShouldAbandonOnInput(0, [87]), "");
        Check("W 與 Return 一起按著放棄", QuickListConfirmGate.ShouldAbandonOnInput(0, [87, 13]), "");
    }

    // ButtonRelease 在單純滑鼠移出按鈕時也會送；真的點擊一定先有 ButtonPress 23。
    private static void ButtonReleaseIsNotAUserClick()
    {
        Check("ButtonRelease 24 不放棄", !QuickListConfirmGate.ShouldAbandonOnEvent(24), "");
        Check("ButtonPress 23 放棄", QuickListConfirmGate.ShouldAbandonOnEvent(23), "");
        Check("改前 ButtonRelease 24 會放棄（負對照）", LegacyShouldAbandonOnEvent(24), "");
    }

    // 同一格連按兩次：第二扇窗等第一筆落地才按，此時原格已空。
    private static void DeferredConfirmChecksSourceSlot()
    {
        Check("原格還是這件：按", QuickListConfirmGate.SourceStillHoldsItem(4848, 4848), "");
        Check("原格已空：不按", !QuickListConfirmGate.SourceStillHoldsItem(0, 4848), "");
        Check("原格換成別的道具：不按", !QuickListConfirmGate.SourceStillHoldsItem(5187, 4848), "");
        Check("改前原格已空照按（負對照）", LegacyDeferredConfirms(0, 4848), "");
    }

    private static void SiblingRequiresSameQuantity()
    {
        var recent = new List<SourceMatch> { new(RetainerA, 4848, new ListingSource(Inv1, 12, 99), 11, T0.AddSeconds(1)) };
        var source = new ListingSource(Inv1, 12, 2);
        var hit = QuickListMatch.FindSiblingMatch(recent, RetainerA, 4848, source, T0, 10000);
        var legacy = LegacyFindSibling(recent, RetainerA, 4848, source, T0, 10000);
        Check("同一格不同數量不算重按", hit == null, hit?.ToString() ?? "null");
        Check("改前不同數量也算重按（負對照）", legacy != null, legacy?.ToString() ?? "null");
    }

    // 延後確認放棄、視窗交給使用者：到它自己關掉之前都算快速上架開的窗，引擎不可當成手動改價而取消。
    private static void HandedOverWindowStaysInFlight()
    {
        const nint win = 0x1000, other = 0x2000;
        var own = new SellWindowOwnership();
        own.HandedOver(win);
        Check("放棄後視窗還開著：仍算在飛", own.AnyOpen && own.Owns(win, 0), "");
        Check("改前放棄後就不算在飛（負對照）", !LegacyIsCapListingInFlight(pending: false, awaitingClose: false, deferred: false), "");
        own.OnFinalize(other);
        Check("別扇上架視窗關掉不清", own.AnyOpen, "");
        own.OnFrame(win);
        Check("同一扇還在：每幀檢查不清", own.AnyOpen, "");
        own.OnFinalize(win);
        Check("它自己關掉才清", !own.AnyOpen && !own.Owns(win, 0), "");

        own.HandedOver(win);
        own.OnFrame(other);
        Check("漏收關窗：每幀看到換了一扇就清", !own.AnyOpen, "");
        own.HandedOver(win);
        own.OnFrame(0);
        Check("漏收關窗：每幀看到沒有上架視窗就清", !own.AnyOpen, "");
        own.HandedOver(win);
        own.OnSetup();
        Check("新的上架視窗開了就清（同位址重開是別件）", !own.AnyOpen, "");

        own.Confirmed(win);
        Check("按了確認那扇算自己的", own.AnyOpen && own.Owns(win, 0) && !own.Owns(other, 0), "");
        own.OnFinalize(other);
        Check("按了確認那扇：任何一扇關掉都清（沿用改前）", !own.AnyOpen, "");
        Check("延後中那扇算自己的", own.Owns(other, other) && !own.Owns(0, 0), "");
    }

    // 改前 QuickListConfirmGate.ShouldAbandonOnEvent：只排除 5、6、7、18、19。
    private static bool LegacyShouldAbandonOnEvent(byte t) => t is not (5 or 6 or 7 or 18 or 19);

    // 改前 PumpDeferredConfirm：閘門一開就按，不看原格。
    private static bool LegacyDeferredConfirms(uint currentItemId, uint expectedItemId) => true;

    // 改前 QuickLister.IsCapListingInFlight：放棄後 deferredConfirm 清成 null、視窗不算任何一項。
    private static bool LegacyIsCapListingInFlight(bool pending, bool awaitingClose, bool deferred) => pending || awaitingClose || deferred;

    // 改前 QuickListMatch.FindSiblingMatch：不比數量。
    private static SourceMatch? LegacyFindSibling(List<SourceMatch> recent, ulong retainerId, uint itemId, ListingSource source,
        DateTime createdAt, int windowMs)
    {
        var from = createdAt.AddMilliseconds(-windowMs);
        foreach (var m in recent)
        {
            if (m.RetainerId == retainerId && m.ItemId == itemId && m.At >= from &&
                m.Source.Container == source.Container && m.Source.Slot == source.Slot)
                return m;
        }

        return null;
    }

    // 實機 10-22～10-25：26 筆「上架確認逾時」都在 6 秒之後才出現市場格（上界 9.1 秒中位、21.2 秒 p90、35.3 秒最長）。
    private static void SlowAckKeepsWaiting()
    {
        foreach (var ageMs in new[] { 3000.0, 7000.0, 21200.0, 35300.0 })
        {
            var stage = QuickListAckTiming.Classify(true, ageMs, false);
            var expect = ageMs <= 6000 ? QuickListAckTiming.Stage.Waiting : QuickListAckTiming.Stage.Slow;
            Check($"SlowAck age={ageMs}: not timed out, notice once", stage == expect, $"stage={stage}");
            Check($"SlowAck age={ageMs}: noticed -> waiting", QuickListAckTiming.Classify(true, ageMs, true) == QuickListAckTiming.Stage.Waiting, "");
        }

        Check("SlowAck 45s boundary still waiting", QuickListAckTiming.Classify(true, 45000, true) == QuickListAckTiming.Stage.Waiting, "");
        Check("SlowAck past 45s times out", QuickListAckTiming.Classify(true, 45001, true) == QuickListAckTiming.Stage.TimedOut, "");
        Check("SlowAck 35.3s is not a timeout (legacy: timeout)", QuickListAckTiming.Classify(true, 35300, true) != QuickListAckTiming.Stage.TimedOut, "");
    }

    private static void UnsentWatchKeepsShortTimeout()
    {
        Check("Unsent 6s waiting", QuickListAckTiming.Classify(false, 6000, false) == QuickListAckTiming.Stage.Waiting, "");
        Check("Unsent past 6s times out", QuickListAckTiming.Classify(false, 6001, false) == QuickListAckTiming.Stage.TimedOut, "");
        Check("Unsent never gets the slow notice", QuickListAckTiming.Classify(false, 5000, false) == QuickListAckTiming.Stage.Waiting, "");
        Check("TimeoutMs", QuickListAckTiming.TimeoutMs(true) == 45000 && QuickListAckTiming.TimeoutMs(false) == 6000, "");
    }

    // 逾時當下要找得到 45 秒前後的重按認領：改前的保留長度（6+2*10 秒）不夠。
    private static void SiblingRetentionCoversHardTimeout()
    {
        var created = T0;
        var claimedAt = created.AddSeconds(15);
        var timeoutAt = created.AddSeconds(45.5);
        var recent = new List<SourceMatch> { new(RetainerA, 5, new ListingSource(Inv1, 3, 1), 4, claimedAt) };
        var legacyCutoff = timeoutAt.AddMilliseconds(-(6000 + 2 * 10000));
        var newCutoff = timeoutAt.AddMilliseconds(-QuickListAckTiming.SiblingRetentionMs(10000));
        Check("Retention legacy would have pruned the claim", claimedAt < legacyCutoff, $"legacyCutoff={legacyCutoff:O}");
        Check("Retention new keeps the claim", claimedAt >= newCutoff, $"newCutoff={newCutoff:O}");
        Check("Retention sibling still found", QuickListMatch.FindSiblingMatch(recent, RetainerA, 5, new ListingSource(Inv1, 3, 1), created.AddSeconds(4), 10000) != null, "");
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
