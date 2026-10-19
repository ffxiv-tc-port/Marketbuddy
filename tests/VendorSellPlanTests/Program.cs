using System;
using System.Collections.Generic;
using System.Linq;
using Marketbuddy;

// 情境測試：每個情境造一份「下架前」與「現在」的背包快照，比對 VendorSellPlanner 的判定。
// 執行：dotnet run -c Release --project tests/VendorSellPlanTests/VendorSellPlanTests.csproj
// 回傳碼：0＝全部通過；1＝有情境失敗。

internal static class Program
{
    private const uint Inv1 = 0, Inv2 = 1, Inv4 = 3, ArmoryHead = 3201;

    private static readonly VendorSellKey Ruby = new(5104, false);
    private static readonly VendorSellKey RubyHq = new(5104, true);
    private static readonly VendorSellKey Dye = new(5729, false);
    private static readonly VendorSellKey Gear = new(40000, false);
    private static readonly VendorSellKey Other = new(4, false);

    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(20);

    private static int failures;
    private static int passes;

    private static int Main()
    {
        NewSlotIsSellable();
        MergedIntoExistingStackIsManual();
        MoreThanDelistedIsManual();
        LessThanDelistedWaitsThenGoesManual();
        NothingInBagsWaitsThenDrops();
        NotArrivedWhileOwnStockUnchanged();
        MergedStackSoldByHandDrops();
        ExistingStackMovedIsManual();
        TwoDelistsIntoOneNewSlotIsSellable();
        OverflowIntoExistingStackIsManual();
        FullExistingStackUntouchedNewSlotIsSellable();
        SoldQuantityIsSubtracted();
        HqAndNqAreSeparate();
        FreedSlotReusedIsSellable();
        UnknownContainerIsManual();
        ConfirmationItemsAreManual();
        NoVendorPriceIsManual();
        ArmoryIsManual();
        SwappedGearIsManual();
        UnrelatedChangesDoNotMatter();
        SplitStackIsSellable();
        ExtraUnitsAfterAllSoldIsDone();
        OrderFollowsFirstDelist();
        MixDetailSeparates();
        RefilledSnapshotMatchesFresh();

        QuickListedAfterFirstDelistIsSellable();
        QuickListBurstAllSellable();
        OtherStackOfSameItemIsNeverSold();
        SameQuantityOwnStackNotArrivedIsNotSold();
        SameItemDelistedTwiceKeepsFirstBaseline();
        SameStackRelistedIsManual();
        OwnStacksInSeveralSlots();
        PartlyBoughtThenReturned();
        RejectedDelistLeavesNoBaseline();
        ReturnedIntoAnotherBagOrFreedSlot();
        HqAndNqHaveTheirOwnBaselines();
        NoBaselineIsNeverSellable();
        CommitOfAnotherItemIsRefused();
        SummaryCountsAndTopReason();

        Console.WriteLine($"PASS {passes} / FAIL {failures}");
        return failures == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- scenarios

    private static void NewSlotIsSellable()
    {
        var before = Snap();
        var now = Snap(S(Inv1, 5, Ruby, 28));
        var v = One(before, now, [D(Ruby, 28, VendorSellCause.BelowVendor, 20)]);
        Expect("新格子：可賣", v.State == VendorSellState.Sellable && v.NewSlots.Count == 1 &&
                              v.NewSlots[0].Slot == 5 && v.EstimatedGil == 28 * 20, v);
    }

    private static void MergedIntoExistingStackIsManual()
    {
        var before = Snap(S(Inv1, 3, Ruby, 50));
        var now = Snap(S(Inv1, 3, Ruby, 78));
        var v = One(before, now, [D(Ruby, 28)]);
        Expect("併入既有堆疊：需要手動", v.State == VendorSellState.Manual &&
                                         v.Reason == VendorSellManualReason.MergedIntoExistingStack &&
                                         v.ExistingBefore == 50 && v.ExistingNow == 78, v);
    }

    private static void MoreThanDelistedIsManual()
    {
        var before = Snap();
        var now = Snap(S(Inv1, 5, Ruby, 30));
        var v = One(before, now, [D(Ruby, 28)]);
        Expect("數量多於下架量：需要手動", v.State == VendorSellState.Manual &&
                                           v.Reason == VendorSellManualReason.QuantityMismatch, v);
    }

    private static void LessThanDelistedWaitsThenGoesManual()
    {
        var before = Snap();
        var now = Snap(S(Inv1, 5, Ruby, 20));
        var early = One(before, now, [D(Ruby, 28)], T0.AddSeconds(5));
        Expect("數量不足、還在等待時間內：等待", early.State == VendorSellState.Waiting, early);
        var late = One(before, now, [D(Ruby, 28)], T0.AddSeconds(25));
        Expect("數量不足、超過等待時間：數量不符", late.State == VendorSellState.Manual &&
                                                  late.Reason == VendorSellManualReason.QuantityMismatch, late);
    }

    private static void NothingInBagsWaitsThenDrops()
    {
        var early = One(Snap(), Snap(), [D(Ruby, 28)], T0.AddSeconds(5));
        Expect("背包裡一件都沒有、還在等待時間內：等待", early.State == VendorSellState.Waiting, early);
        var late = One(Snap(), Snap(), [D(Ruby, 28)], T0.AddSeconds(25));
        Expect("背包裡一件都沒有、超過等待時間：不再列出", late.State == VendorSellState.Done, late);
    }

    private static void NotArrivedWhileOwnStockUnchanged()
    {
        var before = Snap(S(Inv1, 3, Ruby, 50));
        var v = One(before, Snap(S(Inv1, 3, Ruby, 50)), [D(Ruby, 28)], T0.AddSeconds(25));
        Expect("原有堆疊沒動、下架品一直沒出現：沒看到回來", v.State == VendorSellState.Manual &&
                                                         v.Reason == VendorSellManualReason.NotArrived, v);
    }

    private static void MergedStackSoldByHandDrops()
    {
        var before = Snap(S(Inv1, 3, Ruby, 50));
        var v = One(before, Snap(), [D(Ruby, 28)], T0.AddSeconds(60));
        Expect("併入的堆疊被使用者自己賣掉：不再列出", v.State == VendorSellState.Done, v);
    }

    private static void ExistingStackMovedIsManual()
    {
        var before = Snap(S(Inv1, 3, Ruby, 50));
        var now = Snap(S(Inv2, 0, Ruby, 50), S(Inv1, 7, Ruby, 28));
        var v = One(before, now, [D(Ruby, 28)]);
        Expect("原有堆疊被移動：格子變動", v.State == VendorSellState.Manual &&
                                           v.Reason == VendorSellManualReason.ExistingStackChanged, v);
    }

    private static void TwoDelistsIntoOneNewSlotIsSellable()
    {
        var before = Snap();
        var now = Snap(S(Inv1, 5, Ruby, 57));
        var v = One(before, now, [D(Ruby, 28), D(Ruby, 29, VendorSellCause.BelowMinPrice)]);
        Expect("同款兩次下架併成一個新格子：可賣", v.State == VendorSellState.Sellable &&
                                                   v.Arrived == 57 && v.Causes.Count == 2, v);
    }

    private static void OverflowIntoExistingStackIsManual()
    {
        var before = Snap(S(Inv1, 3, Ruby, 990));
        var now = Snap(S(Inv1, 3, Ruby, 999), S(Inv1, 4, Ruby, 11));
        var v = One(before, now, [D(Ruby, 20)]);
        Expect("部分併入舊堆疊、溢出到新格子：需要手動", v.State == VendorSellState.Manual &&
                                                         v.Reason == VendorSellManualReason.MergedIntoExistingStack, v);
    }

    private static void FullExistingStackUntouchedNewSlotIsSellable()
    {
        var before = Snap(S(Inv1, 3, Ruby, 999));
        var now = Snap(S(Inv1, 3, Ruby, 999), S(Inv1, 4, Ruby, 20));
        var v = One(before, now, [D(Ruby, 20)]);
        Expect("舊堆疊已滿沒動、下架品落在新格子：可賣（只賣新格子）",
            v.State == VendorSellState.Sellable && v.NewSlots.Count == 1 && v.NewSlots[0].Slot == 4, v);
    }

    private static void SoldQuantityIsSubtracted()
    {
        var before = Snap();
        var delists = new[] { D(Ruby, 28), D(Ruby, 29) };
        var afterFirstSale = Snap(S(Inv1, 6, Ruby, 29));
        var v = One(before, afterFirstSale, delists, sold: new() { [Ruby] = 28 });
        Expect("賣掉一格後剩下那一格仍可賣", v.State == VendorSellState.Sellable && v.Remaining == 29, v);
        var done = One(before, Snap(), delists, sold: new() { [Ruby] = 57 });
        Expect("全部賣完：完成", done.State == VendorSellState.Done, done);
    }

    private static void HqAndNqAreSeparate()
    {
        var before = Snap(S(Inv1, 3, RubyHq, 5));
        var now = Snap(S(Inv1, 3, RubyHq, 5), S(Inv1, 9, Ruby, 10));
        var all = VendorSellPlanner.Evaluate(before, now, [D(Ruby, 10)], new Dictionary<VendorSellKey, int>(), T0, Settle);
        Expect("優質與普通分開：普通可賣、優質不列", all.Count == 1 && all[0].Key == Ruby &&
                                                     all[0].State == VendorSellState.Sellable, all[0]);
    }

    private static void FreedSlotReusedIsSellable()
    {
        var before = Snap(S(Inv1, 5, Other, 3));
        var now = Snap(S(Inv1, 5, Ruby, 10));
        var v = One(before, now, [D(Ruby, 10)]);
        Expect("別的東西空出來的格子被下架品填上：可賣", v.State == VendorSellState.Sellable, v);
    }

    private static void UnknownContainerIsManual()
    {
        var before = new VendorSellSnapshot([Inv1, Inv2], []);
        var now = Snap(S(Inv4, 0, Ruby, 10));
        var v = One(before, now, [D(Ruby, 10)]);
        Expect("出現在當時讀不到的容器：需要手動", v.State == VendorSellState.Manual &&
                                                   v.Reason == VendorSellManualReason.UnknownContainer, v);
    }

    private static void ConfirmationItemsAreManual()
    {
        var withMateria = One(Snap(), Snap(S(Inv1, 5, Gear, 1, needsConfirmation: true)), [D(Gear, 1)]);
        Expect("鑲有魔晶石：需要手動", withMateria.Reason == VendorSellManualReason.NeedsConfirmation, withMateria);
        var unique = One(Snap(), Snap(S(Inv1, 5, Gear, 1)), [D(Gear, 1) with { Unique = true }]);
        Expect("獨佔道具：需要手動", unique.Reason == VendorSellManualReason.NeedsConfirmation, unique);
    }

    private static void NoVendorPriceIsManual()
    {
        var v = One(Snap(), Snap(S(Inv1, 5, Dye, 10)), [D(Dye, 10, VendorSellCause.BelowMinPrice, 0)]);
        Expect("NPC 不收購：需要手動", v.State == VendorSellState.Manual &&
                                       v.Reason == VendorSellManualReason.NoVendorPrice, v);
    }

    private static void ArmoryIsManual()
    {
        var v = One(Snap(), Snap(S(ArmoryHead, 2, Gear, 1, armory: true)), [D(Gear, 1)]);
        Expect("落在兵裝庫：需要手動", v.State == VendorSellState.Manual &&
                                       v.Reason == VendorSellManualReason.InArmory, v);
    }

    private static void SwappedGearIsManual()
    {
        var before = Snap(S(Inv1, 3, Gear, 1, detail: 111));
        var now = Snap(S(Inv1, 3, Gear, 1, detail: 222), S(Inv1, 8, Gear, 1, detail: 111));
        var v = One(before, now, [D(Gear, 1)]);
        Expect("同款裝備兩件互換位置：格子變動", v.State == VendorSellState.Manual &&
                                                 v.Reason == VendorSellManualReason.ExistingStackChanged, v);
    }

    private static void UnrelatedChangesDoNotMatter()
    {
        var before = Snap(S(Inv1, 1, Other, 5));
        var now = Snap(S(Inv1, 9, Ruby, 12));
        var v = One(before, now, [D(Ruby, 12)]);
        Expect("別款道具的變動不影響判定", v.State == VendorSellState.Sellable, v);
    }

    private static void SplitStackIsSellable()
    {
        var now = Snap(S(Inv1, 5, Ruby, 10), S(Inv2, 0, Ruby, 18));
        var v = One(Snap(), now, [D(Ruby, 28)]);
        Expect("下架品被拆成兩格：兩格都可賣", v.State == VendorSellState.Sellable && v.NewSlots.Count == 2 &&
                                               v.NewSlots[0].Container == Inv1 && v.NewSlots[1].Container == Inv2, v);
    }

    private static void ExtraUnitsAfterAllSoldIsDone()
    {
        var v = One(Snap(), Snap(S(Inv1, 5, Ruby, 3)), [D(Ruby, 28)], sold: new() { [Ruby] = 28 });
        Expect("這一輪的量都賣完後又多出同款：不再列出", v.State == VendorSellState.Done, v);
    }

    private static void OrderFollowsFirstDelist()
    {
        var now = Snap(S(Inv1, 1, Dye, 5), S(Inv1, 2, Ruby, 7));
        var all = VendorSellPlanner.Evaluate(Snap(), now,
            [D(Dye, 5), D(Ruby, 7), D(Dye, 0)], new Dictionary<VendorSellKey, int>(), T0, Settle);
        Expect("清單順序照第一次下架的先後", all.Count == 2 && all[0].Key == Dye && all[1].Key == Ruby, all[0]);
    }

    private static void MixDetailSeparates()
    {
        var a = VendorSellPlanner.MixDetail(VendorSellPlanner.DetailSeed, 1);
        var b = VendorSellPlanner.MixDetail(VendorSellPlanner.DetailSeed, 2);
        var a2 = VendorSellPlanner.MixDetail(VendorSellPlanner.DetailSeed, 1);
        passes += a != b && a == a2 ? 1 : 0;
        failures += a != b && a == a2 ? 0 : 1;
        Console.WriteLine($"{(a != b && a == a2 ? "PASS" : "FAIL")}  指紋：不同內容不同值、相同內容相同值");
    }

    // 重複使用的那一份先裝過更多、別的容器的東西，再 Refill 成現在的內容：要和新建的一份完全一樣。
    private static void RefilledSnapshotMatchesFresh()
    {
        var before = Snap(S(Inv1, 0, Other, 1), S(Inv1, 3, Ruby, 99));
        List<uint> loaded = [Inv1, Inv2, ArmoryHead];
        List<VendorSellSlot> now = [S(Inv2, 4, Ruby, 10), S(Inv1, 3, Ruby, 99), S(Inv1, 7, Ruby, 18)];
        var reused = new VendorSellSnapshot();
        reused.Refill([Inv1, Inv2, 2, Inv4], [S(Inv4, 9, Dye, 5), S(Inv2, 4, Gear, 1), S(Inv1, 0, Other, 2), S(2, 1, RubyHq, 3)]);
        reused.Refill(loaded, now);
        var fresh = new VendorSellSnapshot(loaded, now);

        var same = reused.Slots.SequenceEqual(fresh.Slots)
                   && new uint[] { Inv1, Inv2, 2, Inv4, ArmoryHead }.All(c => reused.IsLoaded(c) == fresh.IsLoaded(c))
                   && reused.Get(Inv4, 9) == null && reused.Get(Inv2, 4) == fresh.Get(Inv2, 4);
        var a = One(before, reused, [D(Ruby, 28)]);
        var b = One(before, fresh, [D(Ruby, 28)]);
        var ok = same && a.State == b.State && a.Reason == b.Reason && a.Arrived == b.Arrived
                 && a.NewSlots.SequenceEqual(b.NewSlots);
        Expect("重複使用的快照 Refill 後與新建的一份相同", ok, a);
    }

    // ---------------------------------------------------------------- per-item baselines (same calls as VendorSeller)

    // The user's flow: quick-list one item, it bounces back (below the minimum) about a second later, next item.
    private static void QuickListedAfterFirstDelistIsSellable()
    {
        foreach (var (label, back) in new[] { ("同一格", (Inv2, (short)8)), ("別格", (Inv2, (short)20)) })
        {
            var t = new Timeline(S(Inv1, 0, Other, 5), S(Inv2, 8, Ruby, 28));
            t.Delist(Dye, 5);
            t.Put(S(Inv1, 1, Dye, 5));
            t.Remove(Inv2, 8);
            t.Delist(Ruby, 28);
            t.Put(S(back.Item1, back.Item2, Ruby, 28));
            var all = t.Evaluate(T0.AddSeconds(25));
            var ruby = all.First(x => x.Key == Ruby);
            Expect($"第一次下架之後才上架的道具退回{label}：可賣", ruby.State == VendorSellState.Sellable &&
                ruby.NewSlots.Count == 1 && ruby.NewSlots[0].Slot == back.Item2 &&
                all.First(x => x.Key == Dye).State == VendorSellState.Sellable, ruby);
        }
    }

    private static void QuickListBurstAllSellable()
    {
        var keys = new[] { Ruby, Dye, Gear, RubyHq, new VendorSellKey(6000, false) };
        var t = new Timeline(keys.Select((k, i) => S(Inv1, (short)i, k, 3 + i)).ToArray());
        for (var i = 0; i < keys.Length; i++)
        {
            t.Remove(Inv1, (short)i);
            t.Delist(keys[i], 3 + i, T0.AddSeconds(i));
            t.Put(S(Inv2, (short)(10 + i), keys[i], 3 + i));
        }

        var all = t.Evaluate(T0.AddSeconds(30));
        var ok = all.Count == keys.Length && all.All(v => v.State == VendorSellState.Sellable && v.NewSlots.Count == 1);
        Expect($"連續快速上架 {keys.Length} 種、逐一退回：每一種都可賣", ok, all.FirstOrDefault(v => v.State != VendorSellState.Sellable) ?? all[0]);
    }

    private static void OtherStackOfSameItemIsNeverSold()
    {
        var t = new Timeline(S(Inv1, 3, Ruby, 50), S(Inv1, 4, Ruby, 28));
        t.Remove(Inv1, 4);
        t.Delist(Ruby, 28);
        t.Put(S(Inv2, 0, Ruby, 28));
        var v = t.One(T0.AddSeconds(25));
        Expect("同款另有一疊沒上架：只賣退回的那格", v.State == VendorSellState.Sellable && v.NewSlots.Count == 1 &&
            v.NewSlots[0].Container == Inv2 && v.ExistingBefore == 50, v);

        var m = new Timeline(S(Inv1, 3, Ruby, 50), S(Inv1, 4, Ruby, 28));
        m.Remove(Inv1, 4);
        m.Delist(Ruby, 28);
        m.Put(S(Inv1, 3, Ruby, 78));
        var merged = m.One(T0.AddSeconds(25));
        Expect("同款另有一疊、退回時併進那一疊：需要手動", merged.State == VendorSellState.Manual &&
            merged.Reason == VendorSellManualReason.MergedIntoExistingStack && merged.NewSlots.Count == 0, merged);
    }

    // The quantity gate alone cannot protect this one: the user's own stack holds exactly what was delisted.
    private static void SameQuantityOwnStackNotArrivedIsNotSold()
    {
        var t = new Timeline(S(Inv1, 3, Ruby, 28), S(Inv1, 4, Ruby, 28));
        t.Remove(Inv1, 4);
        t.Delist(Ruby, 28);
        var v = t.One(T0.AddSeconds(25));
        Expect("同款另有一疊數量剛好相同、下架品沒回來：不賣那一疊", v.State == VendorSellState.Manual &&
            v.Reason == VendorSellManualReason.NotArrived && v.NewSlots.Count == 0, v);
    }

    private static void SameItemDelistedTwiceKeepsFirstBaseline()
    {
        var t = new Timeline(S(Inv1, 4, Ruby, 28));
        t.Remove(Inv1, 4);
        t.Delist(Ruby, 28);
        t.Put(S(Inv1, 4, Ruby, 28));
        t.Delist(Ruby, 10, T0.AddSeconds(5)); // another listing of the same item, already on the market
        t.Put(S(Inv1, 9, Ruby, 10));
        var v = t.One(T0.AddSeconds(30));
        Expect("同款同一輪下架兩次：沿用第一次的下架前背包，兩格都可賣", v.State == VendorSellState.Sellable &&
            v.Arrived == 38 && v.NewSlots.Count == 2, v);
    }

    // The same 28 listed again from where they came back: only 28 exist, 56 were delisted.
    private static void SameStackRelistedIsManual()
    {
        var t = new Timeline(S(Inv1, 4, Ruby, 28));
        t.Remove(Inv1, 4);
        t.Delist(Ruby, 28);
        t.Put(S(Inv1, 4, Ruby, 28));
        t.Remove(Inv1, 4);
        t.Delist(Ruby, 28, T0.AddSeconds(3));
        t.Put(S(Inv1, 4, Ruby, 28));
        var v = t.One(T0.AddSeconds(30));
        Expect("退回的那一疊又被上架再退回：數量不符，需要手動", v.State == VendorSellState.Manual &&
            v.Reason == VendorSellManualReason.QuantityMismatch, v);
    }

    private static void OwnStacksInSeveralSlots()
    {
        var t = new Timeline(S(Inv1, 3, Ruby, 10), S(Inv2, 7, Ruby, 15), S(Inv1, 4, Ruby, 20));
        t.Remove(Inv1, 4);
        t.Delist(Ruby, 20);
        t.Put(S(Inv4, 0, Ruby, 20));
        var v = t.One(T0.AddSeconds(25));
        Expect("同款分在不同背包格：只賣退回的那格", v.State == VendorSellState.Sellable && v.NewSlots.Count == 1 &&
            v.NewSlots[0].Container == Inv4 && v.ExistingBefore == 25, v);

        var u = new Timeline(S(Inv1, 3, Ruby, 10), S(Inv2, 7, Ruby, 15), S(Inv1, 4, Ruby, 20));
        u.Remove(Inv1, 4);
        u.Delist(Ruby, 20);
        u.Put(S(Inv2, 7, Ruby, 5)); // the user used some of one of their own stacks
        u.Put(S(Inv4, 0, Ruby, 20));
        var changed = u.One(T0.AddSeconds(25));
        Expect("同款分在不同背包格、其中一疊變了：需要手動", changed.State == VendorSellState.Manual &&
            changed.Reason == VendorSellManualReason.ExistingStackChanged, changed);
    }

    // 20 of a 35 stack listed, 5 bought on the market, 15 come back; 15 stayed in the bag the whole time.
    private static void PartlyBoughtThenReturned()
    {
        Timeline Start()
        {
            var t = new Timeline(S(Inv1, 3, Ruby, 35));
            t.Put(S(Inv1, 3, Ruby, 15));
            t.Delist(Ruby, 15);
            return t;
        }

        var pending = Start().One(T0.AddSeconds(25));
        Expect("上架後被買走一部分、退回的還沒到：不賣留在背包的那一疊", pending.State == VendorSellState.Manual &&
            pending.Reason == VendorSellManualReason.NotArrived && pending.NewSlots.Count == 0, pending);

        var mergedT = Start();
        mergedT.Put(S(Inv1, 3, Ruby, 30));
        var merged = mergedT.One(T0.AddSeconds(25));
        Expect("上架後被買走一部分、退回時併回原來那一疊：需要手動",
            merged.Reason == VendorSellManualReason.MergedIntoExistingStack, merged);

        var newT = Start();
        newT.Put(S(Inv1, 9, Ruby, 15));
        var fresh = newT.One(T0.AddSeconds(25));
        Expect("上架後被買走一部分、退回到新格子：只賣新格子", fresh.State == VendorSellState.Sellable &&
            fresh.NewSlots.Count == 1 && fresh.NewSlots[0].Slot == 9 && fresh.Arrived == 15, fresh);
    }

    // A move the game refused must not leave a baseline behind: the next one is read again, right before it.
    private static void RejectedDelistLeavesNoBaseline()
    {
        var t = new Timeline();
        t.Delist(Ruby, 10, accepted: false);
        t.Put(S(Inv1, 9, Ruby, 10)); // the user buys 10 elsewhere
        t.Delist(Ruby, 10, T0.AddSeconds(2));
        var v = t.One(T0.AddSeconds(30));
        Expect("被拒的下架不留下架前背包：之後才拿到的同款不賣", v.State != VendorSellState.Sellable &&
            v.NewSlots.Count == 0 && v.ExistingBefore == 10, v);
    }

    private static void ReturnedIntoAnotherBagOrFreedSlot()
    {
        var t = new Timeline(S(Inv1, 4, Ruby, 20), S(Inv4, 2, Other, 1));
        t.Remove(Inv1, 4);
        t.Remove(Inv4, 2); // the user discarded something meanwhile
        t.Delist(Ruby, 20);
        t.Put(S(Inv4, 2, Ruby, 20));
        var v = t.One(T0.AddSeconds(25));
        Expect("退回到別的背包、原本裝別的東西的格子：可賣", v.State == VendorSellState.Sellable &&
            v.NewSlots.Count == 1 && v.NewSlots[0].Container == Inv4, v);
    }

    private static void HqAndNqHaveTheirOwnBaselines()
    {
        // NQ goes first while the HQ one still sits in Inv1#5; the HQ one is listed and bounced later.
        var t = new Timeline(S(Inv1, 3, Ruby, 10), S(Inv1, 5, RubyHq, 5), S(Inv1, 6, Ruby, 7));
        t.Remove(Inv1, 6);
        t.Delist(Ruby, 7);
        t.Put(S(Inv2, 0, Ruby, 7));
        t.Remove(Inv1, 5);
        t.Delist(RubyHq, 5, T0.AddSeconds(2));
        t.Put(S(Inv1, 5, RubyHq, 5));
        var all = t.Evaluate(T0.AddSeconds(30));
        var nq = all.First(x => x.Key == Ruby);
        var hq = all.First(x => x.Key == RubyHq);
        Expect("優質與普通各自一份下架前背包：普通只賣新格子", nq.State == VendorSellState.Sellable &&
            nq.NewSlots.Count == 1 && nq.NewSlots[0].Container == Inv2 && nq.ExistingBefore == 10, nq);
        Expect("優質與普通各自一份下架前背包：優質可賣", hq.State == VendorSellState.Sellable &&
            hq.NewSlots.Count == 1 && hq.NewSlots[0].Slot == 5 && hq.ExistingBefore == 0, hq);
    }

    private static void NoBaselineIsNeverSellable()
    {
        var baselines = new VendorSellBaselines();
        var all = VendorSellPlanner.Evaluate(baselines.BeforeOf, Snap(S(Inv1, 5, Ruby, 28)), [D(Ruby, 28)],
            new Dictionary<VendorSellKey, int>(), T0.AddSeconds(25), Settle);
        Expect("沒有下架前背包的道具：需要手動", all[0].State == VendorSellState.Manual &&
            all[0].Reason == VendorSellManualReason.UnknownContainer, all[0]);
    }

    private static void CommitOfAnotherItemIsRefused()
    {
        var b = new VendorSellBaselines();
        b.Stage(Dye, [Inv1], []);
        var refused = !b.Commit(Ruby) && !b.Has(Ruby) && !b.Has(Dye) && !b.Commit(Dye);
        Check("暫存的是別種道具：不收下，暫存也一併作廢", refused);
    }

    private static void SummaryCountsAndTopReason()
    {
        var now = Snap(S(Inv1, 1, Ruby, 5), S(Inv1, 2, Dye, 3), S(Inv1, 3, Gear, 1, armory: false, needsConfirmation: true),
            S(ArmoryHead, 0, new VendorSellKey(7000, false), 1, armory: true));
        var plan = VendorSellPlanner.Evaluate(Snap(), now,
            [D(Ruby, 5), D(Dye, 4), D(Gear, 1), D(new VendorSellKey(7000, false), 1), D(Other, 2)],
            new Dictionary<VendorSellKey, int>(), T0.AddSeconds(25), Settle);
        var s = VendorSellPlanner.Summarize(plan);
        // Dye: mismatch; Gear: confirmation; 7000: armory; Other: nothing in bags after the window = Done.
        Check($"摘要：可賣 1 種 1 格、需要手動 3 種、平手取列舉值小的原因（{s}）",
            s.Sellable == 1 && s.Stacks == 1 && s.Gil == 100 && s.Manual == 3 && s.Waiting == 0 &&
            s.TopReason == VendorSellManualReason.QuantityMismatch && s.TopReasonCount == 1 &&
            VendorSellPlanner.Summarize(plan) == s);
    }

    /// <summary>背包加上 VendorSeller 的記帳順序：每次下架前 Stage、遊戲收下才 Commit 並記帳。</summary>
    private sealed class Timeline
    {
        private static readonly uint[] Loaded = [Inv1, Inv2, 2, Inv4, ArmoryHead];
        private readonly Dictionary<(uint, short), VendorSellSlot> bag = new();
        private readonly VendorSellBaselines baselines = new();
        private readonly List<VendorSellDelist> delists = new();

        public Timeline(params VendorSellSlot[] slots)
        {
            foreach (var s in slots)
                Put(s);
        }

        public void Put(VendorSellSlot s) => bag[(s.Container, s.Slot)] = s;

        public void Remove(uint container, short slot) => bag.Remove((container, slot));

        public void Delist(VendorSellKey key, int qty, DateTime? at = null, bool accepted = true)
        {
            baselines.Stage(key, Loaded, bag.Values);
            if (accepted && baselines.Commit(key))
                delists.Add(D(key, qty) with { AtUtc = at ?? T0 });
        }

        public List<VendorSellVerdict> Evaluate(DateTime at)
            => VendorSellPlanner.Evaluate(baselines.BeforeOf, new VendorSellSnapshot(Loaded, bag.Values), delists,
                new Dictionary<VendorSellKey, int>(), at, Settle);

        public VendorSellVerdict One(DateTime at)
        {
            var all = Evaluate(at);
            if (all.Count != 1)
                throw new InvalidOperationException($"expected exactly one verdict, got {all.Count}");
            return all[0];
        }
    }

    // ---------------------------------------------------------------- helpers

    private static void Check(string name, bool ok)
    {
        if (ok) passes++;
        else failures++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
    }

    private static VendorSellSnapshot Snap(params VendorSellSlot[] slots)
        => new([Inv1, Inv2, 2, Inv4, ArmoryHead], slots);

    private static VendorSellSlot S(uint container, short slot, VendorSellKey key, int qty,
        ulong detail = 7, bool armory = false, bool needsConfirmation = false)
        => new(container, slot, key, qty, detail, armory, needsConfirmation);

    private static VendorSellDelist D(VendorSellKey key, int qty,
        VendorSellCause cause = VendorSellCause.BelowVendor, uint vendorUnit = 20)
        => new(key, qty, cause, vendorUnit, false, $"item{key.ItemId}{(key.Hq ? "HQ" : "")}", T0);

    private static VendorSellVerdict One(VendorSellSnapshot before, VendorSellSnapshot now,
        VendorSellDelist[] delists, DateTime? at = null, Dictionary<VendorSellKey, int>? sold = null)
    {
        var all = VendorSellPlanner.Evaluate(before, now, delists, sold ?? new Dictionary<VendorSellKey, int>(),
            at ?? T0.AddSeconds(1), Settle);
        if (all.Count != 1)
            throw new InvalidOperationException($"expected exactly one verdict, got {all.Count}");
        return all[0];
    }

    private static void Expect(string name, bool ok, VendorSellVerdict v)
    {
        if (ok) passes++;
        else failures++;
        var slots = string.Join(",", v.NewSlots.Select(s => $"{s.Container}#{s.Slot}x{s.Quantity}"));
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  -> {v.State}/{v.Reason} " +
                          $"delisted={v.Delisted} sold={v.Sold} arrived={v.Arrived} " +
                          $"existing={v.ExistingBefore}->{v.ExistingNow} new=[{slots}] gil~{v.EstimatedGil}");
    }
}
