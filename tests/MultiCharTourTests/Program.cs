using System;
using Marketbuddy;

// 情境測試：多角色巡迴的模式（只重掛／背包上架＋重掛）與 AutoRetainer 接手期間的起跑閘門、
// 背包段結束後的去向、武裝前置條件，以及背包段准上架的道具（PlayerTickBook.LegMayList）。
// 執行：dotnet run -c Release --project tests/MultiCharTourTests/MultiCharTourTests.csproj
// 回傳碼：0＝全部通過；1＝有情境失敗。

internal static class Program
{
    private static int failures;
    private static int passes;

    private static int Main()
    {
        DefaultModeIsRelistOnly();
        HandoverGateOnlyOpensForTheLegsBagStage();
        AfterBagStageRelistsOnlyWhenCompletedAndArmed();
        ArmBlockOrder();
        LegMayListTruthTable();
        LegListIsNeverWiderThanThePanel();

        Console.WriteLine($"passed {passes}, failed {failures}");
        return failures == 0 ? 0 : 1;
    }

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok)
        {
            passes++;
            return;
        }

        failures++;
        Console.WriteLine($"FAIL {name} {detail}");
    }

    private static void DefaultModeIsRelistOnly()
    {
        Check("mode.defaultBool", MultiCharTourRules.ModeOf(false) == MultiCharTourMode.RelistOnly);
        Check("mode.default", default(MultiCharTourMode) == MultiCharTourMode.RelistOnly);
        Check("mode.bag", MultiCharTourRules.ModeOf(true) == MultiCharTourMode.BagListThenRelist);
        Check("stage.relistOnly", !MultiCharTourRules.HasBagStage(MultiCharTourMode.RelistOnly));
        Check("stage.bag", MultiCharTourRules.HasBagStage(MultiCharTourMode.BagListThenRelist));
    }

    private static void HandoverGateOnlyOpensForTheLegsBagStage()
    {
        Check("gate.bagFromLeg", MultiCharTourRules.AllowedDuringHandover(TourMode.BagList, fromMultiCharLeg: true));
        Check("gate.bagOtherEntry", !MultiCharTourRules.AllowedDuringHandover(TourMode.BagList, fromMultiCharLeg: false));
        Check("gate.replenish", !MultiCharTourRules.AllowedDuringHandover(TourMode.Replenish, false) &&
                                !MultiCharTourRules.AllowedDuringHandover(TourMode.Replenish, true));
        Check("gate.tidy", !MultiCharTourRules.AllowedDuringHandover(TourMode.Tidy, false) &&
                           !MultiCharTourRules.AllowedDuringHandover(TourMode.Tidy, true));
        Check("gate.reprice", MultiCharTourRules.AllowedDuringHandover(TourMode.Reprice, false));
        Check("gate.delist", MultiCharTourRules.AllowedDuringHandover(TourMode.Delist, false));
    }

    private static void AfterBagStageRelistsOnlyWhenCompletedAndArmed()
    {
        Check("after.done", MultiCharTourRules.AfterBagStage(true, true) == LegAfterBag.Relist);
        Check("after.disarmed", MultiCharTourRules.AfterBagStage(true, false) == LegAfterBag.Close);
        Check("after.aborted", MultiCharTourRules.AfterBagStage(false, true) == LegAfterBag.Close);
        Check("after.both", MultiCharTourRules.AfterBagStage(false, false) == LegAfterBag.Close);
    }

    private static void ArmBlockOrder()
    {
        Check("arm.relistIgnoresAll",
            MultiCharTourRules.ArmBlock(MultiCharTourMode.RelistOnly, false, false, false) == BagModeArmBlock.None);
        var bag = MultiCharTourMode.BagListThenRelist;
        Check("arm.tourOff", MultiCharTourRules.ArmBlock(bag, false, false, false) == BagModeArmBlock.BagListTourOff);
        Check("arm.preTickOff", MultiCharTourRules.ArmBlock(bag, true, false, true) == BagModeArmBlock.PreTickOff);
        Check("arm.hook", MultiCharTourRules.ArmBlock(bag, true, true, false) == BagModeArmBlock.MenuHookUnavailable);
        Check("arm.ok", MultiCharTourRules.ArmBlock(bag, true, true, true) == BagModeArmBlock.None);
    }

    private static void LegMayListTruthTable()
    {
        for (var i = 0; i < 8; i++)
        {
            bool ticked = (i & 1) != 0, eligible = (i & 2) != 0, optedOut = (i & 4) != 0;
            var expect = ticked && eligible && !optedOut;
            Check($"leg.{i}", PlayerTickBook.LegMayList(ticked, eligible, optedOut) == expect);
        }
    }

    /// <summary>照面板同一份勾選走一遍：准上架的永遠是面板上勾著的子集。</summary>
    private static void LegListIsNeverWiderThanThePanel()
    {
        const ulong cid = 7;
        var a = new VendorSellKey(1, false);
        var b = new VendorSellKey(2, false);
        var craft = new VendorSellKey(3, false);
        var book = new PlayerTickBook();
        book.SyncCharacter(cid);
        book.TryPreTick(true, cid, [(a, true), (b, true), (craft, false)]);
        book.Set(craft, true);
        book.Set(b, false);

        bool May(VendorSellKey k, bool eligible) => PlayerTickBook.LegMayList(book.Contains(k), eligible, book.OptedOut(cid));
        Check("panel.preTicked", May(a, true));
        Check("panel.declinedStays", !May(b, true) && !book.Contains(b));
        Check("panel.manualIneligibleNotListed", book.Contains(craft) && !May(craft, false));

        book.TryPreTick(true, cid, [(b, true)]);
        Check("panel.declinedNotReTicked", !book.Contains(b));

        book.Clear();
        book.Set(a, true);
        Check("panel.optedOutManualTickNotListed", book.Contains(a) && book.OptedOut(cid) && !May(a, true));

        book.SyncCharacter(8);
        Check("panel.otherCharNotOptedOut", !book.OptedOut(8) && book.Count == 0);
    }
}
