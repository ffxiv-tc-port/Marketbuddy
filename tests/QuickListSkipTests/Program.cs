using System;
using System.Collections.Generic;
using Marketbuddy;

// 情境測試：快速上架／批次上架要不要跳過「被低價下架過」的道具。
// 負對照是改版前的行為（永遠不跳過）：「三天前才被低價下架」那一格在舊行為下照樣再試掛一次。
// 執行：dotnet run -c Release --project tests/QuickListSkipTests/QuickListSkipTests.csproj
// 回傳碼：0＝全部通過；1＝有情境失敗。

internal static class Program
{
    private static readonly DateTime Now = new(2026, 11, 19, 0, 0, 0, DateTimeKind.Utc);

    private static int failures;
    private static int passes;

    private static int Main()
    {
        var recent = Now.AddDays(-3);
        Check("recently delisted is skipped", QuickListSkipRule.ShouldSkip(recent, null, Now, 30), true);
        Check("legacy retries it", Legacy(), false);
        Check("no record", QuickListSkipRule.ShouldSkip(DateTime.MinValue, null, Now, 30), false);
        Check("outside the window", QuickListSkipRule.ShouldSkip(Now.AddDays(-31), null, Now, 30), false);
        Check("edge of the window", QuickListSkipRule.ShouldSkip(Now.AddDays(-30), null, Now, 30), true);
        Check("window 0 = any time", QuickListSkipRule.ShouldSkip(Now.AddDays(-170), null, Now, 0), true);
        Check("removed after the delist", QuickListSkipRule.ShouldSkip(recent, recent.AddHours(1), Now, 30), false);
        Check("delisted again after removal", QuickListSkipRule.ShouldSkip(recent, recent.AddHours(-1), Now, 30), true);
        Check("removed at the same instant", QuickListSkipRule.ShouldSkip(recent, recent, Now, 30), false);

        var dismissals = new List<QuickListSkipDismissal>
        {
            new() { ItemId = 1, DismissedUtcTicks = Now.AddDays(-200).Ticks },
            new() { ItemId = 2, DismissedUtcTicks = Now.AddDays(-10).Ticks },
            null!,
            new() { ItemId = 3, DismissedUtcTicks = -5 },
        };
        CheckInt("prune old, null and invalid", QuickListSkipRule.PruneDismissals(dismissals, Now, 180), 3);
        CheckInt("one kept", dismissals.Count, 1);
        Check("invalid ticks map to MinValue", QuickListSkipRule.TicksToUtc(long.MaxValue) == DateTime.MinValue, true);

        Console.WriteLine($"QUICKLISTSKIP_DONE pass={passes} fail={failures}");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>改版前：不看紀錄，每一次都試掛。</summary>
    private static bool Legacy() => false;

    private static void Check(string name, bool got, bool want) => Report(name, got == want, $"{got} (want {want})");

    private static void CheckInt(string name, int got, int want) => Report(name, got == want, $"{got} (want {want})");

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
