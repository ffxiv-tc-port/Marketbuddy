using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Marketbuddy.Common;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    /// <summary>
    /// 持久化的「批次上架查價低於門檻」記憶，寫在設定目錄的 <see cref="FileName"/>；規則見 <see cref="BelowThresholdRule"/>。
    /// 形狀與 <see cref="LowPriceDelistLedger"/> 相同：寫入在 framework 執行緒；讀檔、寫檔在執行緒池；查詢任何執行緒皆可。
    /// 存檔整份重寫，每次變動踢一次、合併連發，失敗只記一次。
    /// </summary>
    internal static class BelowThresholdLedger
    {
        internal const string FileName = "batch_below_threshold.csv";

        private const int MaxRecords = 3000;

        /// <summary>🔴 只在持有 <see cref="Gate"/> 時碰它。</summary>
        private static readonly BelowThresholdBook Book = new(MaxRecords, BelowThresholdRule.MaxDays);

        /// <summary>🔴 鎖內不做 I/O。</summary>
        private static readonly object Gate = new();

        private static long revision;
        private static long savedRevision;
        private static int saving;
        private static int loadStarted;
        private static int failureLogged;
        private static volatile bool loaded;

        private static string? cachedFilePath;

        private static string FilePath
            => cachedFilePath ??= Path.Combine(PluginInterface.ConfigDirectory.FullName, FileName);

        /// <summary>檔案讀完了沒；沒讀完之前一律不跳過（＝舊行為）。</summary>
        internal static bool Loaded => loaded;

        /// <summary>內容每變一次 +1（UI 判斷要不要重算用）。</summary>
        internal static long Revision => Interlocked.Read(ref revision);

        internal static int Count
        {
            get
            {
                lock (Gate)
                {
                    return Book.Count;
                }
            }
        }

        internal static void BeginLoad()
        {
            if (Interlocked.CompareExchange(ref loadStarted, 1, 0) != 0)
                return;
            Task.Run(LoadCore);
        }

        internal static void Note(VendorSellKey key, uint price, uint vendorUnitPrice)
        {
            if (key.ItemId == 0 || price == 0)
                return;
            var now = DateTime.UtcNow;
            lock (Gate)
            {
                Book.Note(key, now, price, vendorUnitPrice);
                Book.Prune(now);
                revision++;
            }

            if (loaded)
                KickSave();
        }

        /// <summary>回 true＝原本有、已拿掉。</summary>
        internal static bool Forget(VendorSellKey key)
        {
            bool removed;
            lock (Gate)
            {
                removed = Book.Forget(key);
                if (removed)
                    revision++;
            }

            if (removed && loaded)
                KickSave();
            return removed;
        }

        internal static void Clear()
        {
            lock (Gate)
            {
                Book.Clear();
                revision++;
            }

            if (loaded)
                KickSave();
        }

        internal static bool TryGet(VendorSellKey key, out BelowThresholdRecord record)
        {
            lock (Gate)
            {
                return Book.TryGet(key, out record);
            }
        }

        private static void LoadCore()
        {
            var fromFile = new List<BelowThresholdRecord>();
            try
            {
                var path = FilePath;
                if (File.Exists(path))
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    while (reader.ReadLine() is { } line)
                    {
                        if (BelowThresholdBook.TryParseCsvLine(line, out var record))
                            fromFile.Add(record);
                    }
                }
            }
            catch (Exception e)
            {
                // 讀不到就當沒有記憶；之後的存檔會整份重寫。
                ReportFailure(e, "讀取");
            }

            bool dirty;
            lock (Gate)
            {
                foreach (var record in fromFile)
                    Book.Merge(record);
                Book.Prune(DateTime.UtcNow);
                revision++;
                // 讀檔前就記下的那幾筆要寫回去；純讀檔不必重寫。
                dirty = revision - 1 != savedRevision;
                if (!dirty)
                    savedRevision = revision;
                loaded = true;
            }

            if (dirty)
                KickSave();
        }

        private static void KickSave()
        {
            if (Interlocked.CompareExchange(ref saving, 1, 0) != 0)
                return;
            Task.Run(SaveLoop);
        }

        private static void SaveLoop()
        {
            try
            {
                while (true)
                {
                    string csv;
                    long at;
                    lock (Gate)
                    {
                        if (revision == savedRevision)
                            break;
                        at = revision;
                        csv = Book.ToCsv();
                    }

                    CsvRewriteFile.Write(FilePath, csv);
                    lock (Gate)
                    {
                        savedRevision = at;
                    }
                }
            }
            catch (Exception e)
            {
                ReportFailure(e, "寫入");
                // 放棄這一代、不立刻重試；下一次變動會再試。
                lock (Gate)
                {
                    savedRevision = revision;
                }
            }
            finally
            {
                Volatile.Write(ref saving, 0);
                bool again;
                lock (Gate)
                {
                    again = revision != savedRevision;
                }

                if (again)
                    KickSave();
            }
        }

        private static void ReportFailure(Exception e, string what)
        {
            if (Interlocked.Exchange(ref failureLogged, 1) != 0)
                return;
            try
            {
                Log.Warning(e, $"[Marketbuddy] 低於門檻記憶{what}失敗（{FileName}）；本工作階段不再重複回報。");
            }
            catch
            {
                // 記錄失敗絕不能中斷任何流程。
            }
        }
    }
}
