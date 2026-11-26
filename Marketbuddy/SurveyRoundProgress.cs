using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Marketbuddy.Common;

namespace Marketbuddy
{
    /// <summary>
    /// 代掃與全市場掃描的「每個世界掃到哪」（<c>survey_link_rounds.csv</c>，整份重寫，至多幾十列）。
    /// 只拿來挑下一個世界與顯示；🔴 讀回來不會讓任何一輪自己開始（重開遊戲照紅線不自動續跑）。
    /// <para>代掃的列以清單內容的指紋分組：主機重推一份內容相同的清單時沿用；換了清單就是另一組。</para>
    /// <para>契約：不引用 Dalamud（離線測試直接編它）；I/O 全在執行緒池上，鎖內只碰欄位。</para>
    /// </summary>
    internal sealed class SurveyRoundProgress
    {
        internal const string FileName = "survey_link_rounds.csv";
        internal const string ModeProxy = "proxy";
        internal const string ModeFull = "full";

        private const string Header = "mode,listKey,timestampUtc,worldId,worldName,plannedTotal,surveyed,completed";
        private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";

        private readonly object gate = new();
        private readonly string directory;
        private readonly Action<string> log;
        private readonly Dictionary<(string Mode, string ListKey, uint WorldId), SurveyWorldRow> rows = new();
        private int loadState;
        private int dirty;
        private int writing;
        private int failureLogged;

        internal SurveyRoundProgress(string directory, Action<string> log)
        {
            this.directory = directory;
            this.log = log;
        }

        internal bool IsLoaded => Volatile.Read(ref loadState) == 2;

        /// <summary>第一次呼叫時在執行緒池上讀檔；之後回同一個已完成的工作。</summary>
        internal Task BeginLoad()
        {
            if (Interlocked.CompareExchange(ref loadState, 1, 0) != 0)
                return Task.CompletedTask;
            return Task.Run(() =>
            {
                var loaded = new List<(string, string, SurveyWorldRow)>();
                try
                {
                    var path = Path.Combine(directory, FileName);
                    if (File.Exists(path))
                    {
                        foreach (var text in File.ReadAllLines(path, Encoding.UTF8))
                        {
                            if (TryParse(text, out var mode, out var key, out var row))
                                loaded.Add((mode, key, row));
                        }
                    }
                }
                catch (Exception e)
                {
                    log($"世界進度檔讀取失敗（{FileName}）：{e.GetType().Name}。從空的開始。");
                }

                lock (gate)
                {
                    foreach (var (mode, key, row) in loaded)
                    {
                        // 讀檔前已經記下的（較新）不被檔案蓋掉。
                        if (!rows.TryGetValue((mode, key, row.WorldId), out var known) || known.AtUtc < row.AtUtc)
                            rows[(mode, key, row.WorldId)] = row;
                    }
                }

                Volatile.Write(ref loadState, 2);
                if (Volatile.Read(ref dirty) == 1)
                    KickWrite();
            });
        }

        internal void Record(string mode, string listKey, SurveyWorldRow row)
        {
            lock (gate)
            {
                rows[(mode, listKey, row.WorldId)] = row;
            }

            Interlocked.Exchange(ref dirty, 1);
            KickWrite();
        }

        internal bool TryGet(string mode, string listKey, uint worldId, out SurveyWorldRow row)
        {
            lock (gate)
            {
                return rows.TryGetValue((mode, listKey, worldId), out row);
            }
        }

        internal List<SurveyWorldRow> Rows(string mode, string listKey)
        {
            var result = new List<SurveyWorldRow>();
            lock (gate)
            {
                foreach (var ((m, k, _), row) in rows)
                {
                    if (m == mode && k == listKey)
                        result.Add(row);
                }
            }

            return result;
        }

        /// <summary>寫完最後一次排的再收手（寫的時候又有人記，就再寫一次）。</summary>
        internal Task KickWrite()
        {
            if (!IsLoaded || Interlocked.CompareExchange(ref writing, 1, 0) != 0)
                return Task.CompletedTask;
            return Task.Run(() =>
            {
                try
                {
                    while (Interlocked.Exchange(ref dirty, 0) == 1)
                    {
                        var text = new StringBuilder();
                        text.Append(Header).Append("\r\n");
                        lock (gate)
                        {
                            foreach (var ((mode, key, _), row) in rows)
                                text.Append(Format(mode, key, row)).Append("\r\n");
                        }

                        CsvRewriteFile.Write(Path.Combine(directory, FileName), text.ToString());
                    }
                }
                catch (Exception e)
                {
                    if (Interlocked.Exchange(ref failureLogged, 1) == 0)
                        log($"世界進度檔寫入失敗（{FileName}）：{e.GetType().Name}。這次啟動不再重複回報。");
                }
                finally
                {
                    Volatile.Write(ref writing, 0);
                    if (Volatile.Read(ref dirty) == 1)
                        KickWrite();
                }
            });
        }

        /// <summary>代掃清單的指紋：同一組（道具, 品質）不管順序都得到同一個值。</summary>
        internal static string ListKeyOf(IEnumerable<SharedListItem> items)
        {
            var sorted = new List<SharedListItem>(items);
            sorted.Sort((a, b) =>
            {
                var c = a.ItemId.CompareTo(b.ItemId);
                return c != 0 ? c : a.Hq.CompareTo(b.Hq);
            });
            var bytes = new byte[sorted.Count * 5];
            for (var i = 0; i < sorted.Count; i++)
            {
                BitConverter.TryWriteBytes(bytes.AsSpan(i * 5, 4), sorted[i].ItemId);
                bytes[(i * 5) + 4] = sorted[i].Hq ? (byte)1 : (byte)0;
            }

            return Convert.ToHexString(SHA256.HashData(bytes), 0, 8).ToLowerInvariant();
        }

        private static string Format(string mode, string key, SurveyWorldRow row)
            => string.Join(",",
                mode,
                key,
                row.AtUtc.ToString(TimestampFormat, CultureInfo.InvariantCulture),
                row.WorldId.ToString(CultureInfo.InvariantCulture),
                PriceSurveyLog.Escape(row.WorldName),
                row.PlannedTotal.ToString(CultureInfo.InvariantCulture),
                row.Surveyed.ToString(CultureInfo.InvariantCulture),
                row.Completed ? "1" : "0");

        private static bool TryParse(string text, out string mode, out string key, out SurveyWorldRow row)
        {
            mode = key = string.Empty;
            row = default;
            var f = PriceSurveyLog.SplitCsv(text);
            if (f.Count != 8 || f[0] is not (ModeProxy or ModeFull) || (f[1].Length > 0 && !SurveyShare.IsSafeId(f[1])))
                return false;
            if (!DateTime.TryParseExact(f[2], TimestampFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
                || !uint.TryParse(f[3], NumberStyles.None, CultureInfo.InvariantCulture, out var worldId)
                || !int.TryParse(f[5], NumberStyles.None, CultureInfo.InvariantCulture, out var planned)
                || !int.TryParse(f[6], NumberStyles.None, CultureInfo.InvariantCulture, out var surveyed)
                || f[7] is not ("0" or "1"))
                return false;
            row = new SurveyWorldRow(at, worldId, f[4], 0, f[0] == ModeFull ? "full-market" : "imported", planned, surveyed, f[7] == "1");
            if (SurveyShare.Validate(row, null, DateTime.UtcNow) != null)
                return false;
            mode = f[0];
            key = f[1];
            return true;
        }
    }
}
