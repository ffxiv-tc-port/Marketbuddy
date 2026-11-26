using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Marketbuddy.Common
{
    // 本檔契約：不引用 Dalamud（離線測試直接編它）。落地格式＝UTF-8 BOM＋CRLF 列尾，
    // 與抽出前各紀錄檔自己寫的逐位元組相同；欄位內容與逸出仍由各檔自己的 Format 決定。

    /// <summary>整份重寫式的 CSV 檔：先寫暫存檔再覆蓋過去，寫到一半崩潰不會留下半份檔案。</summary>
    internal static class CsvRewriteFile
    {
        internal static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

        private static readonly UTF8Encoding Utf8NoBom = new(false);

        /// <summary>把 <paramref name="path"/> 換成「BOM＋<paramref name="text"/>」。只從執行緒池呼叫；失敗照常擲出。</summary>
        internal static void Write(string path, string text)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var bytes = Utf8NoBom.GetBytes(text);
            var temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(Utf8Bom, 0, Utf8Bom.Length);
                stream.Write(bytes, 0, bytes.Length);
            }

            File.Move(temp, path, true);
        }
    }

    /// <summary>
    /// 追加式 CSV 紀錄檔：任何執行緒都可以排列，寫檔一律在執行緒池上、同時只有一個 flush。
    /// 檔案不存在或長度 0 時先寫 BOM 與標題列。寫檔失敗整個工作階段只回報一次。
    /// 🔴 <see cref="Gate"/> 內只碰待寫清單：不做 I/O、不寫 log、不呼叫別的插件。
    /// </summary>
    internal sealed class CsvAppendLog<T>
    {
        private static readonly UTF8Encoding Utf8NoBom = new(false);

        private static readonly string LineEnd = new([(char)13, (char)10]);

        private readonly List<T> pending = [];
        private readonly Func<string> resolvePath;
        private readonly string header;
        private readonly Func<T, string> format;
        private readonly Action<Exception> reportFailure;
        private string? cachedPath;
        private Func<IReadOnlyList<T>>? rewrite;
        private int flushing;
        private int failureReported;

        /// <param name="gate">待寫清單的鎖；擁有者可以拿同一把鎖保護自己的狀態。</param>
        /// <param name="resolvePath">第一次需要時才呼叫一次，結果快取。</param>
        /// <param name="reportFailure">只會被呼叫一次；它擲出的例外會被吞掉。</param>
        internal CsvAppendLog(object gate, Func<string> resolvePath, string header, Func<T, string> format,
            Action<Exception> reportFailure)
        {
            Gate = gate;
            this.resolvePath = resolvePath;
            this.header = header;
            this.format = format;
            this.reportFailure = reportFailure;
        }

        internal object Gate { get; }

        internal string FilePath => cachedPath ??= resolvePath();

        /// <summary>還沒落地的列數。</summary>
        internal int PendingCount
        {
            get
            {
                lock (Gate)
                {
                    return pending.Count;
                }
            }
        }

        /// <summary>沒有待寫、沒有待重寫、也沒有 flush 在跑（測試等「真的落地了」用）。</summary>
        internal bool IsIdle
        {
            get
            {
                lock (Gate)
                {
                    return pending.Count == 0 && rewrite == null && Volatile.Read(ref flushing) == 0;
                }
            }
        }

        /// <summary>排一列但不啟動寫檔；之後要呼叫 <see cref="Kick"/>。</summary>
        internal void Enqueue(T row)
        {
            lock (Gate)
            {
                pending.Add(row);
            }
        }

        /// <summary>同 <see cref="Enqueue"/>，但呼叫端已經持有 <see cref="Gate"/>。</summary>
        internal void EnqueueLocked(T row) => pending.Add(row);

        /// <summary>
        /// 下一次 flush 改成整份重寫（暫存檔＋取代）。呼叫端持有 <see cref="Gate"/>；之後要呼叫 <see cref="Kick"/>。
        /// 🔴 <paramref name="snapshotLocked"/> 在 <see cref="Gate"/> 內被呼叫，回傳的列必須涵蓋到那一刻為止排過的每一列
        /// （寫成功後那些待寫列就丟掉）；寫失敗時待寫列照常追加到原檔，原檔不會被動到。
        /// </summary>
        internal void RequestRewriteLocked(Func<IReadOnlyList<T>> snapshotLocked) => rewrite = snapshotLocked;

        /// <summary>沒有 flush 在跑就開一個；在跑的那個離開前會再看一次待寫清單。</summary>
        internal void Kick()
        {
            if (Interlocked.CompareExchange(ref flushing, 1, 0) != 0)
                return;
            Task.Run(Flush);
        }

        private void Flush()
        {
            try
            {
                while (true)
                {
                    List<T> batch;
                    IReadOnlyList<T>? all = null;
                    var covered = 0;
                    lock (Gate)
                    {
                        if (rewrite is { } snapshot)
                        {
                            rewrite = null;
                            all = snapshot();
                            covered = pending.Count;
                            batch = [];
                        }
                        else
                        {
                            if (pending.Count == 0)
                                break;
                            batch = new List<T>(pending);
                            pending.Clear();
                        }
                    }

                    if (all != null)
                    {
                        RewriteAll(all);
                        lock (Gate)
                        {
                            pending.RemoveRange(0, covered);
                        }

                        continue;
                    }

                    WriteBatch(batch);
                }
            }
            catch (Exception e)
            {
                ReportFailure(e);
            }
            finally
            {
                Volatile.Write(ref flushing, 0);

                bool again;
                lock (Gate)
                {
                    again = pending.Count > 0 || rewrite != null;
                }

                if (again)
                    Kick();
            }
        }

        private void WriteBatch(List<T> batch)
        {
            var path = FilePath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var needHeader = !File.Exists(path) || new FileInfo(path).Length == 0;

            var text = new StringBuilder();
            if (needHeader)
                text.Append(header).Append("\r\n");
            foreach (var row in batch)
                text.Append(format(row)).Append("\r\n");

            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            if (needHeader)
                stream.Write(CsvRewriteFile.Utf8Bom, 0, CsvRewriteFile.Utf8Bom.Length);
            var bytes = Utf8NoBom.GetBytes(text.ToString());
            stream.Write(bytes, 0, bytes.Length);
        }

        private void RewriteAll(IReadOnlyList<T> rows)
        {
            var path = FilePath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(CsvRewriteFile.Utf8Bom, 0, CsvRewriteFile.Utf8Bom.Length);
                using var writer = new StreamWriter(stream, Utf8NoBom, 1 << 16);
                writer.Write(header);
                writer.Write(LineEnd);
                foreach (var row in rows)
                {
                    writer.Write(format(row));
                    writer.Write(LineEnd);
                }
            }

            File.Move(temp, path, true);
        }

        private void ReportFailure(Exception e)
        {
            if (Interlocked.Exchange(ref failureReported, 1) != 0)
                return;

            try
            {
                reportFailure(e);
            }
            catch
            {
                // 記錄失敗絕不能中斷寫檔流程。
            }
        }
    }
}
