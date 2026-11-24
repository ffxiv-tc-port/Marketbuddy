using System;
using System.Collections.Generic;
using System.Linq;
using Marketbuddy;

// 情境測試：最近成交價的等待上限與斷路器（LastSoldWaitPolicy／LastSoldHealth）。
// 負對照是改版前的行為：一律等 25 秒、沒有斷路器、不預取。後半段的小模擬只供估算，不是斷言。
// 執行：dotnet run -c Release --project tests/LastSoldWaitTests/LastSoldWaitTests.csproj
// 回傳碼：0＝全部通過；1＝有情境失敗。

internal static class Program
{
    private static readonly DateTime T0 = new(2026, 11, 24, 8, 0, 0, DateTimeKind.Utc);

    private static int failures;
    private static int passes;

    private static int Main()
    {
        Check("healthy batch waits 25 s", LastSoldWaitPolicy.LimitMs(false, false), 25000);
        Check("healthy single item waits 8 s", LastSoldWaitPolicy.LimitMs(true, false), 8000);
        Check("struggling batch does not wait", LastSoldWaitPolicy.LimitMs(false, true), 0);
        Check("struggling single item does not wait", LastSoldWaitPolicy.LimitMs(true, true), 0);
        Check("legacy single item waited 25 s (negative control)", LegacyLimitMs(true) < 25000, false);

        var h = new LastSoldHealth();
        Check("fresh: not struggling", h.IsStruggling(T0), false);
        Check("first trouble trips", h.NoteTrouble(T0), true);
        Check("struggling right after", h.IsStruggling(T0.AddSeconds(1)), true);
        Check("second trouble does not re-trip", h.NoteTrouble(T0.AddSeconds(10)), false);
        Check("window counts from the latest trouble", h.IsStruggling(T0.AddSeconds(10).AddMilliseconds(LastSoldHealth.WindowMs - 1)), true);
        Check("window expires", h.IsStruggling(T0.AddSeconds(10).AddMilliseconds(LastSoldHealth.WindowMs)), false);
        Check("success recovers", h.NoteSuccess(T0.AddSeconds(20)), true);
        Check("healthy after success", h.IsStruggling(T0.AddSeconds(21)), false);
        Check("success when healthy is not a recovery", h.NoteSuccess(T0.AddSeconds(22)), false);
        Check("trouble after success trips again", h.NoteTrouble(T0.AddSeconds(30)), true);
        Check("trouble after expiry trips again (log once per episode)",
            new Func<bool>(() =>
            {
                var x = new LastSoldHealth();
                x.NoteTrouble(T0);
                return x.NoteTrouble(T0.AddMilliseconds(LastSoldHealth.WindowMs + 1));
            })(), true);
        Check("success at the same instant as trouble clears it",
            new Func<bool>(() =>
            {
                var x = new LastSoldHealth();
                x.NoteTrouble(T0);
                x.NoteSuccess(T0);
                return x.IsStruggling(T0.AddSeconds(1));
            })(), false);

        // 預取：答案在輪到之前就回來了，輪到時一格都不等。
        var pre = Simulate(Mode.New, Flow.BatchList, latencySec: 1.5, ok: true, items: 9);
        Check("prefetched batch: items after the first wait 0", pre.Skip(1).All(w => w.WaitSec == 0), true);
        var legacyPre = Simulate(Mode.Legacy, Flow.BatchList, latencySec: 1.5, ok: true, items: 9);
        Check("legacy batch listing asks per item (negative control)", legacyPre.Skip(1).All(w => w.WaitSec == 0), false);

        var slowNew = Simulate(Mode.New, Flow.BatchList, latencySec: 30, ok: false, items: 9);
        Check("Universalis down: no item waits more than 8 s", slowNew.Max(w => w.WaitSec) <= 8.0, true);
        var slowOld = Simulate(Mode.Legacy, Flow.BatchList, latencySec: 30, ok: false, items: 9);
        Check("legacy Universalis down: items wait 25 s (negative control)", slowOld.Max(w => w.WaitSec) <= 8.0, false);

        Console.WriteLine();
        Console.WriteLine("估算（9 件；Universalis 每個請求的耗時／結果；每件市場板比價另計約 2 秒）：");
        foreach (var flow in new[] { Flow.BatchList, Flow.QuickList })
        foreach (var (latency, ok, label) in new[]
                 {
                     (1.5, true, "正常 1.5 s 成功"),
                     (9.0, true, "慢 9 s 成功"),
                     (18.0, true, "很慢 18 s 成功"),
                     (30.0, false, "掛掉 30 s 逾時失敗"),
                 })
        {
            var oldW = Simulate(Mode.Legacy, flow, latency, ok, 9);
            var newW = Simulate(Mode.New, flow, latency, ok, 9);
            Console.WriteLine(
                $"  {flow,-9} {label,-14} 改前 總等 {oldW.Sum(w => w.WaitSec),6:F1} s [{string.Join(",", oldW.Select(w => w.WaitSec.ToString("F0")))}]" +
                $"  改後 總等 {newW.Sum(w => w.WaitSec),6:F1} s [{string.Join(",", newW.Select(w => w.WaitSec.ToString("F0")))}]" +
                $"  改後用到成交價 {newW.Count(w => w.GotPrice)}/9（改前 {oldW.Count(w => w.GotPrice)}/9）");
        }

        Console.WriteLine();
        Console.WriteLine($"通過 {passes}，失敗 {failures}");
        return failures == 0 ? 0 : 1;
    }

    private static int LegacyLimitMs(bool singleItem) => 25000;

    private enum Mode { Legacy, New }

    private enum Flow
    {
        /// <summary>按一次開始、整批一起排（批次上架）。</summary>
        BatchList,

        /// <summary>使用者每 30 秒右鍵一件，3 秒後輪到引擎定價（快速上架）。</summary>
        QuickList,
    }

    private readonly record struct ItemWait(double WaitSec, bool GotPrice);

    private enum S { Unknown, Loading, Ready, Failed }

    /// <summary>10 ms 一步。Universalis 端照 LastSoldPriceSource：逐一送、間隔 1.2 秒、失敗後退避 30 秒（改後含 HttpClient 逾時）。</summary>
    private static List<ItemWait> Simulate(Mode mode, Flow flow, double latencySec, bool ok, int items)
    {
        const double step = 0.01, marketSec = 2.0, gapSec = 1.2, backoffSec = 30, failedTtlSec = 120;
        var state = new S[items];
        var stamp = new double[items];
        var queue = new List<int>();
        List<int>? inflight = null;
        double inflightDone = 0, nextSend = 0;
        var health = new LastSoldHealth();
        var waits = new List<ItemWait>();

        void Enqueue(int i, double t)
        {
            if (state[i] == S.Loading || (state[i] == S.Failed && t - stamp[i] < failedTtlSec) || state[i] == S.Ready)
                return;
            state[i] = S.Loading;
            stamp[i] = t;
            queue.Add(i);
        }

        double Arrival(int i) => flow == Flow.BatchList ? 0 : i * 30.0;
        double HeadStart() => flow == Flow.BatchList ? 0 : 3.0;

        var current = 0;
        double turnStart = -1, marketUntil = -1;
        var prefetched = new bool[items];
        for (var t = 0.0; current < items && t < 3600; t += step)
        {
            var now = T0.AddSeconds(t);

            if (mode == Mode.New)
            {
                for (var i = 0; i < items; i++)
                {
                    if (!prefetched[i] && t >= Arrival(i))
                    {
                        prefetched[i] = true;
                        Enqueue(i, t);
                    }
                }
            }

            if (inflight != null && t >= inflightDone)
            {
                foreach (var i in inflight)
                {
                    state[i] = ok ? S.Ready : S.Failed;
                    stamp[i] = t;
                }

                if (ok)
                    health.NoteSuccess(now);
                else
                    health.NoteTrouble(now);
                nextSend = t + gapSec + (!ok && mode == Mode.New ? backoffSec : 0);
                inflight = null;
            }

            if (inflight == null && queue.Count > 0 && t >= nextSend)
            {
                inflight = queue.Take(60).ToList();
                queue.RemoveRange(0, inflight.Count);
                inflightDone = t + latencySec;
            }

            if (marketUntil >= 0)
            {
                if (t >= marketUntil)
                {
                    marketUntil = -1;
                    turnStart = -1;
                    current++;
                }

                continue;
            }

            if (turnStart < 0)
            {
                var ready = Arrival(current) + HeadStart();
                if (t < ready)
                    continue;
                turnStart = t;
                Enqueue(current, t);
            }

            var s = state[current];
            var waited = t - turnStart;
            if (s is S.Ready or S.Failed)
            {
                waits.Add(new ItemWait(Math.Round(waited, 1), s == S.Ready));
                marketUntil = t + marketSec;
                continue;
            }

            var limitMs = mode == Mode.Legacy
                ? LegacyLimitMs(true)
                : LastSoldWaitPolicy.LimitMs(true, health.IsStruggling(now));
            if (waited * 1000 >= limitMs)
            {
                if (mode == Mode.New && limitMs > 0)
                    health.NoteTrouble(now);
                waits.Add(new ItemWait(Math.Round(waited, 1), false));
                marketUntil = t + marketSec;
            }
        }

        return waits;
    }

    private static void Check<T>(string name, T actual, T expected)
    {
        if (EqualityComparer<T>.Default.Equals(actual, expected))
        {
            passes++;
            return;
        }

        failures++;
        Console.WriteLine($"FAIL {name}: expected {expected}, got {actual}");
    }
}
