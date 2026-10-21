using System;
using System.Collections.Generic;
using Marketbuddy;

// 情境測試：BellAbsenceGate（多角色重掛接手後「這一區沒有傳喚鈴」的提早跳過）。
// 執行：dotnet run -c Release --project tests/BellAbsenceGateTests/BellAbsenceGateTests.csproj
// 回傳碼：0＝全部通過；1＝有情境失敗。

internal static class Program
{
    private static readonly TimeSpan Settled = TimeSpan.MaxValue;
    private static readonly TimeSpan Normal = TimeSpan.FromSeconds(3);
    private const double OpenBellTimeoutSeconds = 25;

    private static int failures;
    private static int passes;

    private static int Main()
    {
        NoBellSkipsOnThirdScan();
        BellOutOfReachNeverSkips();
        BellInteractedNeverSkips();
        OneSightingResetsTheCount();
        UnknownResetsTheCount();
        RecentZoneChangeDoesNotCount();
        CountStartsOverAfterSettle();
        RescanIntervalOnlyShortWhileAbsent();
        CadenceNoBellSkipsAroundTwoSeconds();
        CadenceBellOutOfReachWaitsFullTimeout();

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

    private static List<bool> Feed(BellAbsenceGate gate, TimeSpan sinceZone, params BellScan[] scans)
    {
        var results = new List<bool>();
        foreach (var s in scans)
            results.Add(gate.Observe(s, sinceZone));
        return results;
    }

    private static void NoBellSkipsOnThirdScan()
    {
        var r = Feed(new BellAbsenceGate(), Settled, BellScan.NoneInTable, BellScan.NoneInTable, BellScan.NoneInTable);
        Check(nameof(NoBellSkipsOnThirdScan), !r[0] && !r[1] && r[2], string.Join(",", r));
    }

    private static void BellOutOfReachNeverSkips()
    {
        var gate = new BellAbsenceGate();
        var any = false;
        for (var i = 0; i < 50; i++)
            any |= gate.Observe(BellScan.OutOfReach, Settled);
        Check(nameof(BellOutOfReachNeverSkips), !any && gate.ConsecutiveAbsent == 0);
    }

    private static void BellInteractedNeverSkips()
    {
        var gate = new BellAbsenceGate();
        var any = false;
        for (var i = 0; i < 50; i++)
            any |= gate.Observe(BellScan.Interacted, Settled);
        Check(nameof(BellInteractedNeverSkips), !any);
    }

    private static void OneSightingResetsTheCount()
    {
        var r = Feed(new BellAbsenceGate(), Settled,
            BellScan.NoneInTable, BellScan.NoneInTable, BellScan.OutOfReach, BellScan.NoneInTable, BellScan.NoneInTable);
        Check(nameof(OneSightingResetsTheCount), !r.Contains(true), string.Join(",", r));
    }

    private static void UnknownResetsTheCount()
    {
        var r = Feed(new BellAbsenceGate(), Settled,
            BellScan.NoneInTable, BellScan.NoneInTable, BellScan.Unknown, BellScan.NoneInTable, BellScan.NoneInTable);
        Check(nameof(UnknownResetsTheCount), !r.Contains(true), string.Join(",", r));
    }

    private static void RecentZoneChangeDoesNotCount()
    {
        var gate = new BellAbsenceGate();
        var any = false;
        for (var i = 0; i < 9; i++)
            any |= gate.Observe(BellScan.NoneInTable, TimeSpan.FromSeconds(i));
        Check(nameof(RecentZoneChangeDoesNotCount), !any && gate.ConsecutiveAbsent == 0);
    }

    private static void CountStartsOverAfterSettle()
    {
        var gate = new BellAbsenceGate();
        gate.Observe(BellScan.NoneInTable, TimeSpan.FromSeconds(8));
        gate.Observe(BellScan.NoneInTable, TimeSpan.FromSeconds(9));
        var a = gate.Observe(BellScan.NoneInTable, TimeSpan.FromSeconds(10));
        var b = gate.Observe(BellScan.NoneInTable, TimeSpan.FromSeconds(11));
        var c = gate.Observe(BellScan.NoneInTable, TimeSpan.FromSeconds(12));
        Check(nameof(CountStartsOverAfterSettle), !a && !b && c, $"{a},{b},{c}");
    }

    private static void RescanIntervalOnlyShortWhileAbsent()
    {
        var gate = new BellAbsenceGate();
        var before = gate.NextScanInterval(Normal);
        gate.Observe(BellScan.NoneInTable, Settled);
        var during = gate.NextScanInterval(Normal);
        gate.Observe(BellScan.OutOfReach, Settled);
        var after = gate.NextScanInterval(Normal);
        Check(nameof(RescanIntervalOnlyShortWhileAbsent),
            before == Normal && during == BellAbsenceGate.AbsentRescanInterval && after == Normal,
            $"{before},{during},{after}");
    }

    // 照 MultiCharacterTour.TickOpeningBell 的節奏模擬（每 0.1 秒一幀），回傳跳過的時刻；沒跳過回 null。
    private static double? SimulateSkipAt(Func<double, BellScan> world)
    {
        var gate = new BellAbsenceGate();
        var lastScan = double.NegativeInfinity;
        for (var t = 0.0; t <= OpenBellTimeoutSeconds; t = Math.Round(t + 0.1, 1))
        {
            if (t - lastScan < gate.NextScanInterval(Normal).TotalSeconds)
                continue;
            lastScan = t;
            if (gate.Observe(world(t), Settled))
                return t;
        }

        return null;
    }

    private static void CadenceNoBellSkipsAroundTwoSeconds()
    {
        var at = SimulateSkipAt(_ => BellScan.NoneInTable);
        Check(nameof(CadenceNoBellSkipsAroundTwoSeconds), at is >= 1.9 and <= 2.1, $"{at}");
    }

    private static void CadenceBellOutOfReachWaitsFullTimeout()
    {
        var at = SimulateSkipAt(_ => BellScan.OutOfReach);
        Check(nameof(CadenceBellOutOfReachWaitsFullTimeout), at == null, $"{at}");
    }
}
