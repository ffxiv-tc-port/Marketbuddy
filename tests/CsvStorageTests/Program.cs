using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Marketbuddy.Common;

// 逐位元組比對：CsvAppendLog／CsvRewriteFile 對上抽出前各紀錄檔自己的寫檔碼（下面 Legacy*，逐字保留）。
// 執行：dotnet run -c Release --project tests/CsvStorageTests/CsvStorageTests.csproj
// 回傳碼：0＝全部通過；1＝有情境失敗。

internal static class Program
{
    private const string Header = "timestampUtc,worldId,worldName,itemId";

    private static readonly string[] Rows =
    [
        "2026-11-17 01:02:03,4028,伊弗利特,5106",
        "2026-11-17 01:02:04,4029,\"逗號,在裡面\",5107",
        "2026-11-17 01:02:05,4030,\"引號\"\"在裡面\",5108",
        "2026-11-17 01:02:06,4031,\"換行\r\n在裡面\",5109",
        "",
    ];

    private static int failures;
    private static int passes;
    private static string root = string.Empty;

    private static int Main()
    {
        root = Path.Combine(Path.GetTempPath(), "mb-csvstorage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            AppendToMissingFileInMissingDirectory();
            AppendToEmptyFile();
            AppendToExistingFile();
            AppendInManyBatches();
            AppendFailureReportedOnce();
            RewriteNewFile();
            RewriteShrinksExistingFile();
            RewriteEmptyText();
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* 暫存目錄清不掉不影響結論 */ }
        }

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

    private static string Fresh(string name, string? subdir = null)
    {
        var dir = subdir == null ? root : Path.Combine(root, subdir);
        return Path.Combine(dir, name);
    }

    // ---- 抽出前的寫檔碼（PriceSurveyLog／ShoppingSurveyLog／RetainerSalesLog.WriteBatch 三份相同） ----
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private static void LegacyWriteBatch(string path, List<string> batch)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var needHeader = !File.Exists(path) || new FileInfo(path).Length == 0;

        var text = new StringBuilder();
        if (needHeader)
            text.Append(Header).Append("\r\n");
        foreach (var row in batch)
            text.Append(row).Append("\r\n");

        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        if (needHeader)
            stream.Write(Utf8Bom, 0, Utf8Bom.Length);
        var bytes = new UTF8Encoding(false).GetBytes(text.ToString());
        stream.Write(bytes, 0, bytes.Length);
    }

    // ---- 抽出前的整份重寫尾段（PendingActions／PriceSurveyWorldLog／RetainerMarketWatcher／LowPriceDelistLedger／RetainerListingAge 五份相同） ----
    private static void LegacyWriteAll(string path, string text)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var bytes = new UTF8Encoding(false).GetBytes(text);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(Utf8Bom, 0, Utf8Bom.Length);
            stream.Write(bytes, 0, bytes.Length);
        }

        File.Move(temp, path, true);
    }

    private static CsvAppendLog<string> NewLog(string path, Action<Exception>? onFailure = null)
        => new(new object(), () => path, Header, row => row, onFailure ?? (e => throw new Exception("unexpected", e)));

    /// <summary>等 flush 把檔案寫成預期內容；逾時就回當下的內容讓比對失敗。</summary>
    private static byte[] WaitFor(string path, byte[] expected, CsvAppendLog<string> log)
    {
        var sw = Stopwatch.StartNew();
        byte[] actual = [];
        while (sw.ElapsedMilliseconds < 5000)
        {
            if (log.PendingCount == 0 && File.Exists(path))
            {
                try
                {
                    actual = File.ReadAllBytes(path);
                }
                catch (IOException)
                {
                    Thread.Sleep(10);
                    continue;
                }

                if (actual.AsSpan().SequenceEqual(expected))
                    return actual;
            }

            Thread.Sleep(10);
        }

        return actual;
    }

    private static void CompareAppend(string name, string? seed, List<List<string>> batches)
    {
        var legacyPath = Fresh(name + ".legacy.csv", name + "-legacy");
        var newPath = Fresh(name + ".new.csv", name + "-new");
        if (seed != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
            File.WriteAllBytes(legacyPath, Encoding.UTF8.GetBytes(seed));
            File.WriteAllBytes(newPath, Encoding.UTF8.GetBytes(seed));
        }

        foreach (var batch in batches)
            LegacyWriteBatch(legacyPath, batch);
        var expected = File.ReadAllBytes(legacyPath);

        var log = NewLog(newPath);
        foreach (var batch in batches)
        {
            foreach (var row in batch)
                log.Enqueue(row);
            log.Kick();
        }

        var actual = WaitFor(newPath, expected, log);
        Check(name, actual.AsSpan().SequenceEqual(expected),
            $"expected {expected.Length} bytes, got {actual.Length}");
        Check(name + ".path", log.FilePath == newPath);
    }

    private static void AppendToMissingFileInMissingDirectory()
        => CompareAppend("append-missing", null, [Rows.ToList()]);

    private static void AppendToEmptyFile()
        => CompareAppend("append-empty", string.Empty, [Rows.ToList()]);

    private static void AppendToExistingFile()
        => CompareAppend("append-existing", (char)0xFEFF + Header + "\r\nold,1,舊,2\r\n", [Rows.ToList()]);

    private static void AppendInManyBatches()
        => CompareAppend("append-batches", null,
            Enumerable.Range(0, 40).Select(i => new List<string> { $"row{i},{i},名{i},{i * 7}" }).ToList());

    private static void AppendFailureReportedOnce()
    {
        // 路徑本身是目錄 ⇒ FileStream 一定開不了。
        var path = Fresh("is-a-directory");
        Directory.CreateDirectory(path);
        var reports = 0;
        var log = NewLog(path, _ =>
        {
            Interlocked.Increment(ref reports);
            throw new InvalidOperationException("reporter throws; must be swallowed");
        });

        for (var i = 0; i < 3; i++)
        {
            log.Enqueue("x");
            log.Kick();
            var sw = Stopwatch.StartNew();
            while (log.PendingCount > 0 && sw.ElapsedMilliseconds < 2000)
                Thread.Sleep(10);
            Thread.Sleep(50);
        }

        Check("append-failure-once", Volatile.Read(ref reports) == 1, $"reports={reports}");
    }

    private static void CompareRewrite(string name, string? seed, string text)
    {
        var legacyPath = Fresh(name + ".legacy.csv", name + "-legacy");
        var newPath = Fresh(name + ".new.csv", name + "-new");
        if (seed != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
            File.WriteAllText(legacyPath, seed);
            File.WriteAllText(newPath, seed);
        }

        LegacyWriteAll(legacyPath, text);
        CsvRewriteFile.Write(newPath, text);

        var expected = File.ReadAllBytes(legacyPath);
        var actual = File.ReadAllBytes(newPath);
        Check(name, actual.AsSpan().SequenceEqual(expected), $"expected {expected.Length} bytes, got {actual.Length}");
        Check(name + ".no-temp", !File.Exists(newPath + ".tmp"));
    }

    private static string RewriteText()
    {
        var text = new StringBuilder();
        text.Append(Header).Append("\r\n");
        foreach (var row in Rows)
            text.Append(row).Append("\r\n");
        return text.ToString();
    }

    private static void RewriteNewFile() => CompareRewrite("rewrite-new", null, RewriteText());

    private static void RewriteShrinksExistingFile()
        => CompareRewrite("rewrite-shrink", new string('x', 10000), RewriteText());

    private static void RewriteEmptyText() => CompareRewrite("rewrite-empty", "old", string.Empty);
}
