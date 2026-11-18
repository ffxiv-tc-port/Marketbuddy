using System;
using Marketbuddy;

// 情境測試：快速上架被選單守衛擋下後的補送判斷。
// 負對照是改版前的行為（擋下就丟，永遠不補）：「守衛在期限內解除、其餘都沒變」那一格在舊行為下沒有補送。
// 執行：dotnet run -c Release --project tests/QuickListReplayTests/QuickListReplayTests.csproj
// 回傳碼：0＝全部通過；1＝有情境失敗。

internal static class Program
{
    private static int failures;
    private static int passes;

    private static readonly ReplaySlot Recorded = new(48, 7, 5086, 3, false);

    private static int Main()
    {
        FiresWhenGuardReleasesInTime();
        WaitsWhileGuardHolds();
        TimeoutWins();
        BlockedStateAbandons();
        UserInputAbandons();
        ClosedMenuAbandons();
        MenuRetargetedAbandons();
        SlotChangedAbandons();
        LegacyNeverReplays();

        Console.WriteLine($"QUICKLISTREPLAY_DONE pass={passes} fail={failures}");
        return failures == 0 ? 0 : 1;
    }

    private static ReplayVerdict Run(double elapsed = 500, string? blockedBy = null, bool input = false, bool menuOpen = true,
        int container = 48, int slot = 7, ReplaySlot? current = null, bool useCurrent = false, bool guard = true)
        => QuickListReplay.Decide(Recorded, elapsed, blockedBy, input, menuOpen, container, slot,
            useCurrent ? current : Recorded, guard, out _);

    /// <summary>改版前：守衛擋下＝這一次不做，沒有任何補送。</summary>
    private static ReplayVerdict Legacy() => ReplayVerdict.Abandon;

    private static void FiresWhenGuardReleasesInTime()
    {
        Check("期限內守衛解除＝補送", Run() == ReplayVerdict.Fire);
        Check("剛好到期限仍可補送", Run(elapsed: QuickListReplay.WindowMs) == ReplayVerdict.Fire);
    }

    private static void WaitsWhileGuardHolds() => Check("守衛還擋著＝等", Run(guard: false) == ReplayVerdict.Wait);

    private static void TimeoutWins()
    {
        Check("過期限＝放棄（即使守衛放行）", Run(elapsed: QuickListReplay.WindowMs + 1) == ReplayVerdict.Abandon);
        Check("過期限＝放棄（守衛仍擋）", Run(elapsed: QuickListReplay.WindowMs + 1, guard: false) == ReplayVerdict.Abandon);
    }

    private static void BlockedStateAbandons()
    {
        QuickListReplay.Decide(Recorded, 100, "出售清單關了", false, true, 48, 7, Recorded, true, out var reason);
        Check("別的狀態擋著＝放棄並帶原因", Run(blockedBy: "出售清單關了") == ReplayVerdict.Abandon && reason == "出售清單關了");
    }

    private static void UserInputAbandons()
    {
        Check("等待期間有輸入＝放棄", Run(input: true) == ReplayVerdict.Abandon);
        Check("有輸入時守衛擋著也放棄（不等）", Run(input: true, guard: false) == ReplayVerdict.Abandon);
    }

    private static void ClosedMenuAbandons()
    {
        Check("選單關了＝放棄", Run(menuOpen: false) == ReplayVerdict.Abandon);
        Check("選單關了時守衛擋著也放棄", Run(menuOpen: false, guard: false) == ReplayVerdict.Abandon);
    }

    private static void MenuRetargetedAbandons()
    {
        Check("選單換成別格＝放棄", Run(slot: 8) == ReplayVerdict.Abandon);
        Check("選單換成別的容器＝放棄", Run(container: 49) == ReplayVerdict.Abandon);
    }

    private static void SlotChangedAbandons()
    {
        Check("格子空了＝放棄", Run(useCurrent: true, current: null) == ReplayVerdict.Abandon);
        Check("換了道具＝放棄", Run(useCurrent: true, current: Recorded with { ItemId = 5087 }) == ReplayVerdict.Abandon);
        Check("數量變了＝放棄", Run(useCurrent: true, current: Recorded with { Quantity = 2 }) == ReplayVerdict.Abandon);
        Check("品質變了＝放棄", Run(useCurrent: true, current: Recorded with { Hq = true }) == ReplayVerdict.Abandon);
        Check("同一件同數量＝補送", Run(useCurrent: true, current: Recorded with { }) == ReplayVerdict.Fire);
    }

    private static void LegacyNeverReplays()
        => Check("負對照：舊行為在可補送的情境下不補", Legacy() != Run());

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
