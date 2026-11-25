using System;
using System.Threading.Tasks;
using Marketbuddy;

// 情境測試：每小時 Universalis 請求彙總（UniversalisRequestStats）。
// 口徑比照 PriceInsight 與 TCToolbox：滿一小時寫一行、整小時沒有動靜不寫、卸載補寫「近 N 分」、寫完歸零。
// 負對照：改前沒有 UniversalisRequestStats.cs，這個專案建置失敗。
// 執行：dotnet run -c Release --project tests/UniversalisStatsTests/UniversalisStatsTests.csproj（0＝全過、1＝有失敗）

internal static class Program
{
    private const long Hour = UniversalisRequestStats.IntervalMs;
    private static int failures;
    private static int passes;

    private static int Main()
    {
        NotBeforeAnHour();
        QuietHourWritesNothing();
        FullHourLine();
        ForcedFlushOnUnload();
        FlushResetsCounters();
        ConcurrentCountsAreExact();
        ResetClears();

        Console.WriteLine($"UNISTATS_DONE pass={passes} fail={failures}");
        return failures == 0 ? 0 : 1;
    }

    private static void NotBeforeAnHour()
    {
        var s = new UniversalisRequestStats("[Marketbuddy]", 0);
        s.Query([1u, 2u]);
        s.Attempt(0);
        s.Succeeded();
        Check("未滿一小時不寫", s.TryFlush(Hour - 1) == null);
        Check("滿一小時才寫", s.TryFlush(Hour) != null);
    }

    private static void QuietHourWritesNothing()
    {
        var s = new UniversalisRequestStats("[Marketbuddy]", 0);
        Check("安靜的一小時不寫", s.TryFlush(Hour) == null);
        s.Attempt(0);
        Check("安靜那一小時之後重新計時", s.TryFlush(Hour + Hour - 1) == null);
        Check("下一個整小時寫", s.TryFlush(Hour + Hour) != null);
    }

    private static void FullHourLine()
    {
        var s = new UniversalisRequestStats("[Marketbuddy]", 1000);
        s.Query([10u, 11u, 12u]);
        s.Query([11u, 13u]);
        s.Query([10u]);
        s.Attempt(0);
        s.Attempt(1);
        s.Attempt(0);
        s.Attempt(0);
        s.Succeeded();
        s.Succeeded();
        s.Failed();
        var line = s.TryFlush(1000 + Hour);
        const string expected =
            "[Marketbuddy] Universalis 本小時：查價 3 次（道具 4 件）、HTTP 請求 4 次（其中重試 1 次）、成功 2、失敗 1";
        Check("本小時整行逐字", line == expected, line);
    }

    private static void ForcedFlushOnUnload()
    {
        var s = new UniversalisRequestStats("[Marketbuddy]", 0);
        s.Query([7u]);
        s.Attempt(0);
        s.Failed();
        var line = s.TryFlush(10 * 60_000 + 30_000, force: true);
        Check("卸載補寫近 10 分", line != null && line.Contains("Universalis 近 10 分：查價 1 次（道具 1 件）"), line);

        var t = new UniversalisRequestStats("[Marketbuddy]", 0);
        t.Attempt(0);
        var shortLine = t.TryFlush(5_000, force: true);
        Check("不到一分鐘也至少寫近 1 分", shortLine != null && shortLine.Contains("近 1 分"), shortLine);

        var quiet = new UniversalisRequestStats("[Marketbuddy]", 0);
        Check("卸載時沒動靜不寫", quiet.TryFlush(10 * 60_000, force: true) == null);
    }

    private static void FlushResetsCounters()
    {
        var s = new UniversalisRequestStats("[Marketbuddy]", 0);
        s.Query([1u]);
        s.Attempt(0);
        s.Succeeded();
        Check("第一次寫", s.TryFlush(Hour) != null);
        Check("寫完歸零", s.TryFlush(Hour + Hour) == null);
        s.Query([2u]);
        var line = s.TryFlush(Hour * 3);
        Check("歸零後只數新的", line != null && line.Contains("查價 1 次（道具 1 件）、HTTP 請求 0 次"), line);
    }

    private static void ConcurrentCountsAreExact()
    {
        var s = new UniversalisRequestStats("[Marketbuddy]", 0);
        Parallel.For(0, 8, t =>
        {
            for (var i = 0; i < 20_000; i++)
            {
                s.Attempt(i % 4 == 0 ? 1 : 0);
                s.Query([(uint)(i % 100)]);
                if (i % 2 == 0)
                    s.Succeeded();
                else
                    s.Failed();
            }
        });
        var line = s.TryFlush(Hour);
        const string expected =
            "[Marketbuddy] Universalis 本小時：查價 160000 次（道具 100 件）、HTTP 請求 160000 次（其中重試 40000 次）、成功 80000、失敗 80000";
        Check("多執行緒計數精確", line == expected, line);
    }

    private static void ResetClears()
    {
        var s = new UniversalisRequestStats("[Marketbuddy]", 0);
        s.Query([1u]);
        s.Attempt(0);
        s.Reset(Hour / 2);
        Check("Reset 後清空", s.TryFlush(Hour / 2 + Hour) == null);
        s.Attempt(0);
        Check("Reset 重新計時", s.TryFlush(Hour / 2 + Hour + Hour - 1) == null);
    }

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok)
        {
            passes++;
            Console.WriteLine($"PASS {name}");
        }
        else
        {
            failures++;
            Console.WriteLine($"FAIL {name}{(detail == null ? "" : " :: " + detail)}");
        }
    }
}
