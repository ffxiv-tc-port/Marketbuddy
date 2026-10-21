using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    /// <summary>
    /// 持久化的「低價下架」紀錄（重掛時低於 NPC 收購價／低於自訂最低價而下架），寫在設定目錄的
    /// <see cref="FileName"/>。與 <see cref="RetainerDelistLedger"/> 不同：那本只留 30 分鐘、只放記憶體。
    /// 執行緒：<see cref="Note"/> 在 framework 執行緒；讀檔、寫檔在執行緒池；查詢任何執行緒皆可。
    /// 存檔整份重寫（暫存檔→<see cref="File.Move(string,string,bool)"/>），每次下架踢一次、合併連發，失敗只記一次。
    /// </summary>
    internal static class LowPriceDelistLedger
    {
        internal const string FileName = "low_price_delists.csv";

        private const int MaxRecords = 3000;
        private const int MaxAgeDays = 180;

        private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

        /// <summary>🔴 只在持有 <see cref="Gate"/> 時碰它。</summary>
        private static readonly LowPriceDelistBook Book = new(MaxRecords, MaxAgeDays);

        /// <summary>🔴 鎖內不做 I/O。</summary>
        private static readonly object Gate = new();

        private static long revision;
        private static long savedRevision;
        private static int saving;
        private static int loadStarted;
        private static int writeFailureLogged;
        private static volatile bool loaded;

        private static string? cachedFilePath;

        private static string FilePath
            => cachedFilePath ??= Path.Combine(PluginInterface.ConfigDirectory.FullName, FileName);

        /// <summary>檔案讀完了沒；沒讀完之前清單只會有這個工作階段新記的。</summary>
        internal static bool Loaded => loaded;

        /// <summary>內容每變一次 +1（UI 判斷要不要重算用）。</summary>
        internal static long Revision => Interlocked.Read(ref revision);

        internal static void BeginLoad()
        {
            if (Interlocked.CompareExchange(ref loadStarted, 1, 0) != 0)
                return;
            Task.Run(LoadCore);
        }

        /// <summary>記一次低價下架。</summary>
        /// <param name="salePrice">當時查到的成交單價；-1＝沒有。</param>
        /// <param name="targetPrice">當時算出、低於門檻的價；0＝不知道。</param>
        internal static void Note(uint itemId, bool hq, long salePrice, DateTime saleAtUtc, uint targetPrice)
        {
            if (itemId == 0)
                return;
            var now = DateTime.UtcNow;
            lock (Gate)
            {
                Book.Note(new VendorSellKey(itemId, hq), now, salePrice, saleAtUtc, targetPrice);
                Book.Prune(now);
                revision++;
            }

            if (loaded)
                KickSave();
        }

        internal static bool TryGet(VendorSellKey key, out LowPriceDelistRecord record)
        {
            lock (Gate)
            {
                return Book.TryGet(key, out record);
            }
        }

        /// <summary>整份拷一份出來（鎖內只拷貝）。</summary>
        internal static List<LowPriceDelistRecord> Snapshot()
        {
            lock (Gate)
            {
                return new List<LowPriceDelistRecord>(Book.Records);
            }
        }

        private static void LoadCore()
        {
            var fromFile = new List<LowPriceDelistRecord>();
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
                        if (LowPriceDelistBook.TryParseCsvLine(line, out var record))
                            fromFile.Add(record);
                    }
                }
            }
            catch (Exception e)
            {
                // 讀不到就當沒有紀錄；之後的存檔會整份重寫。
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

                    WriteAll(csv);
                    lock (Gate)
                    {
                        savedRevision = at;
                    }
                }
            }
            catch (Exception e)
            {
                ReportFailure(e, "寫入");
                // 放棄這一代、不立刻重試（寫不進去的原因通常不會馬上消失）；下一次下架會再試。
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

        private static void WriteAll(string csv)
        {
            var path = FilePath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var bytes = new UTF8Encoding(false).GetBytes(csv);
            var temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(Utf8Bom, 0, Utf8Bom.Length);
                stream.Write(bytes, 0, bytes.Length);
            }

            File.Move(temp, path, true);
        }

        private static void ReportFailure(Exception e, string what)
        {
            if (Interlocked.Exchange(ref writeFailureLogged, 1) != 0)
                return;
            try
            {
                Log.Warning(e, $"[Marketbuddy] 低價下架紀錄{what}失敗（{FileName}）；本工作階段不再重複回報。");
            }
            catch
            {
                // 記錄失敗絕不能中斷任何流程。
            }
        }
    }
}
