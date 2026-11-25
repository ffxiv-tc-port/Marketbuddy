using System;
using System.Linq;
using Marketbuddy;

// 情境測試：MultiCharSkipLedger（多角色重掛一輪裡被跳過的角色清單與「只重跑這幾角」的目標）。
// 執行：dotnet run -c Release --project tests/MultiCharSkipLedgerTests/MultiCharSkipLedgerTests.csproj
// 回傳碼：0＝全部通過；1＝有情境失敗。

internal static class Program
{
    private static int failures;
    private static int passes;

    private static int Main()
    {
        EmptyLedgerHasNoRerunAndNoSummary();
        RecordKeepsOrderAndReplacesSameCharacter();
        SummaryIsTakenOncePerRound();
        BeginRoundClearsAndRearmsSummary();
        RerunTargetsAreTheRecordedCharacters();
        VersionChangesOnEveryEdit();
        JoinFormatsEveryEntry();

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

    private static void EmptyLedgerHasNoRerunAndNoSummary()
    {
        var l = new MultiCharSkipLedger();
        Check("empty.rerun", l.RerunTargets() == null);
        Check("empty.summary", !l.TryTakeSummary(out _));
        Check("empty.count", l.Count == 0);
    }

    private static void RecordKeepsOrderAndReplacesSameCharacter()
    {
        var l = new MultiCharSkipLedger();
        l.BeginRound();
        l.Record(1, "A", "no bell");
        l.Record(2, "B", "no bell in area");
        l.Record(1, "A", "timed out");
        Check("replace.count", l.Count == 2, $"count={l.Count}");
        Check("replace.order", l.Entries[0].Cid == 1 && l.Entries[1].Cid == 2);
        Check("replace.reason", l.Entries[0].Reason == "timed out", l.Entries[0].Reason);
    }

    private static void SummaryIsTakenOncePerRound()
    {
        var l = new MultiCharSkipLedger();
        l.BeginRound();
        Check("once.emptyFirst", !l.TryTakeSummary(out _));
        l.Record(1, "A", "x");
        Check("once.first", l.TryTakeSummary(out var s) && s.Count == 1);
        Check("once.second", !l.TryTakeSummary(out _));
        Check("once.listKept", l.Count == 1 && l.RerunTargets()!.Contains(1UL));
    }

    private static void BeginRoundClearsAndRearmsSummary()
    {
        var l = new MultiCharSkipLedger();
        l.BeginRound();
        l.Record(1, "A", "x");
        l.TryTakeSummary(out _);
        l.BeginRound();
        Check("begin.cleared", l.Count == 0 && l.RerunTargets() == null);
        l.Record(2, "B", "y");
        Check("begin.summaryAgain", l.TryTakeSummary(out var s) && s.Single().Cid == 2);
    }

    private static void RerunTargetsAreTheRecordedCharacters()
    {
        var l = new MultiCharSkipLedger();
        l.BeginRound();
        l.Record(10, "A", "x");
        l.Record(20, "B", "y");
        l.Record(10, "A", "z");
        var t = l.RerunTargets()!;
        Check("rerun.set", t.Count == 2 && t.Contains(10UL) && t.Contains(20UL));
        t.Clear();
        Check("rerun.copy", l.Count == 2, "caller mutating the target set must not touch the ledger");
    }

    private static void VersionChangesOnEveryEdit()
    {
        var l = new MultiCharSkipLedger();
        var v0 = l.Version;
        l.BeginRound();
        var v1 = l.Version;
        l.Record(1, "A", "x");
        var v2 = l.Version;
        l.Record(1, "A", "y");
        var v3 = l.Version;
        Check("version.bumps", v0 != v1 && v1 != v2 && v2 != v3);
        l.TryTakeSummary(out _);
        Check("version.summaryNoBump", l.Version == v3);
    }

    private static void JoinFormatsEveryEntry()
    {
        var l = new MultiCharSkipLedger();
        l.BeginRound();
        Check("join.empty", l.Join(", ", static (n, r) => n + r) == string.Empty);
        l.Record(1, "A", "x");
        l.Record(2, "B", "y");
        var s = l.Join(", ", static (n, r) => $"{n} ({r})");
        Check("join.text", s == "A (x), B (y)", s);
    }
}
