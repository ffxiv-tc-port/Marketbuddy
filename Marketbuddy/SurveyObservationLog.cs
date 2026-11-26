using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Marketbuddy.Common;

namespace Marketbuddy
{
    /// <summary>
    /// 市場觀測記錄檔（<c>survey_observations.csv</c>，追加寫）＋記憶體索引＋送往另一台的待送佇列。
    /// <para>🔑 一份檔三個用途：①每筆本機觀測帶遞增序號，序號大於「對方已確認」游標的就是待送（斷線、重開遊戲都不丟）；
    /// ②(道具, 世界) 最新時間的索引：收到的觀測不比索引新就當重複（同一批重送、同一檔匯兩次都只算一次）；
    /// ③代掃時「這個世界保留時間內問過哪些」看兩邊合起來的資料。</para>
    /// <para>🔴 <see cref="gate"/> 內只碰欄位與待寫清單：不做 I/O、不寫 log。所有操作都是 O(1) 或 O(本批)，
    /// 只有 <see cref="AnsweredSince"/> 掃一次索引（每個世界開始掃描時一次，在執行緒池上）。</para>
    /// </summary>
    internal sealed class SurveyObservationLog
    {
        internal const string FileName = "survey_observations.csv";
        internal const string AckFileName = "survey_link_ack.txt";

        /// <summary>待送上限；超過就丟最舊的並記一次 log（對方長期不上線時不讓記憶體無限長）。</summary>
        internal const int MaxOutbox = 300_000;

        private const string Header =
            "seq,origin,timestampUtc,worldId,worldName,itemId,lowestNq,lowestNqRetainer,lowestHq,lowestHqRetainer,listingCount,source,gateIntervalMs,verdict";

        private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";
        private const char OriginLocal = 'L';
        private const char OriginRemote = 'R';

        internal enum Intake
        {
            Accepted = 0,
            Duplicate = 1,
            NotReady = 2,
        }

        private readonly record struct Line(long Seq, char Origin, MarketObservation Obs);

        private readonly record struct Latest(DateTime Any, DateTime Answered);

        private readonly object gate = new();
        private readonly string directory;
        private readonly Action<string> log;
        private readonly CsvAppendLog<Line> writer;

        // ---- gate 內 ----
        private readonly Dictionary<(uint ItemId, uint WorldId), Latest> latest = new();
        private readonly Queue<(long Seq, MarketObservation Obs)> outbox = new();
        private readonly List<MarketObservation> beforeLoad = [];
        private long nextSeq = 1;
        private long ackedSeq;
        private long ackToWrite;
        private long ackWritten;
        private int dropped;

        private int loadState;
        private int ackWriting;

        /// <param name="directory">放記錄檔的資料夾（插件設定目錄；測試給暫存目錄）。</param>
        /// <param name="log">診斷輸出；會從執行緒池呼叫。</param>
        internal SurveyObservationLog(string directory, Action<string> log)
        {
            this.directory = directory;
            this.log = log;
            writer = new CsvAppendLog<Line>(gate, () => Path.Combine(directory, FileName), Header, Format,
                e => log($"觀測記錄檔寫入失敗（{FileName}）：{e.GetType().Name}。這次啟動不再重複回報。"));
        }

        internal bool IsLoaded => Volatile.Read(ref loadState) == 2;

        internal int OutboxCount
        {
            get
            {
                lock (gate)
                {
                    return outbox.Count;
                }
            }
        }

        /// <summary>第一次呼叫時在執行緒池上讀檔；之後 no-op。</summary>
        internal Task BeginLoad()
        {
            if (Interlocked.CompareExchange(ref loadState, 1, 0) != 0)
                return Task.CompletedTask;
            return Task.Run(LoadCore);
        }

        private void LoadCore()
        {
            var lines = new List<Line>();
            long acked = 0;
            try
            {
                var ackPath = Path.Combine(directory, AckFileName);
                if (File.Exists(ackPath))
                    long.TryParse(File.ReadAllText(ackPath).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out acked);

                var path = Path.Combine(directory, FileName);
                if (File.Exists(path))
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream, Encoding.UTF8, true);
                    while (reader.ReadLine() is { } text)
                    {
                        if (text.Length > 0 && TryParse(text, out var line))
                            lines.Add(line);
                    }
                }
            }
            catch (Exception e)
            {
                log($"觀測記錄檔讀取失敗：{e.GetType().Name}。待送佇列從空的開始，已確認游標照舊。");
            }

            var pendingLocal = 0;
            List<MarketObservation> late;
            lock (gate)
            {
                ackedSeq = Math.Max(ackedSeq, acked);
                ackWritten = ackedSeq;
                // 記錄檔被刪過但游標還在時，新序號也要比游標大，否則下次啟動會被當成已送過。
                nextSeq = Math.Max(nextSeq, ackedSeq + 1);
                foreach (var line in lines)
                {
                    NoteLatest(line.Obs);
                    if (line.Origin != OriginLocal)
                        continue;
                    nextSeq = Math.Max(nextSeq, line.Seq + 1);
                    if (line.Seq > ackedSeq)
                    {
                        outbox.Enqueue((line.Seq, line.Obs));
                        pendingLocal++;
                    }
                }

                late = new List<MarketObservation>(beforeLoad);
                beforeLoad.Clear();
                foreach (var obs in late)
                    RecordLocalLocked(obs);
                Volatile.Write(ref loadState, 2);
            }

            if (late.Count > 0)
                writer.Kick();
            log($"觀測記錄檔讀完：{lines.Count} 列，待送 {pendingLocal + late.Count} 筆（已確認到序號 {ackedSeq}）。");
        }

        /// <summary>本機掃到一筆。任何執行緒；檔案在執行緒池上落地。</summary>
        internal void RecordLocal(MarketObservation obs)
        {
            lock (gate)
            {
                if (Volatile.Read(ref loadState) != 2)
                {
                    beforeLoad.Add(obs);
                    return;
                }

                RecordLocalLocked(obs);
            }

            writer.Kick();
        }

        private void RecordLocalLocked(MarketObservation obs)
        {
            var seq = nextSeq++;
            NoteLatest(obs);
            outbox.Enqueue((seq, obs));
            if (outbox.Count > MaxOutbox)
            {
                outbox.Dequeue();
                dropped++;
            }

            writer.EnqueueLocked(new Line(seq, OriginLocal, obs));
        }

        /// <summary>
        /// 收下另一台送來的一筆。索引裡同一件同一個世界已經有同秒或更新的資料＝重複，不寫。
        /// 🔴 收到的不會再送回去（只有 L 列進待送佇列），所以兩台之間不會來回彈。
        /// </summary>
        internal Intake AcceptRemote(MarketObservation obs)
        {
            lock (gate)
            {
                if (Volatile.Read(ref loadState) != 2)
                    return Intake.NotReady;
                var at = SurveyShare.TruncateToSecond(obs.AtUtc);
                if (latest.TryGetValue((obs.ItemId, obs.WorldId), out var known) && known.Any >= at)
                    return Intake.Duplicate;
                NoteLatest(obs);
                writer.EnqueueLocked(new Line(0, OriginRemote, obs));
            }

            writer.Kick();
            return Intake.Accepted;
        }

        private void NoteLatest(MarketObservation obs)
        {
            var key = (obs.ItemId, obs.WorldId);
            var at = SurveyShare.TruncateToSecond(obs.AtUtc);
            latest.TryGetValue(key, out var known);
            var any = at > known.Any ? at : known.Any;
            var answered = obs.Answered && at > known.Answered ? at : known.Answered;
            latest[key] = new Latest(any, answered);
        }

        /// <summary>待送佇列最前面的至多 <paramref name="max"/> 筆（不移除；對方確認後 <see cref="Ack"/>）。</summary>
        internal List<(long Seq, MarketObservation Obs)> PeekOutbox(int max)
        {
            var result = new List<(long, MarketObservation)>();
            int lost;
            lock (gate)
            {
                foreach (var entry in outbox)
                {
                    if (result.Count >= max)
                        break;
                    result.Add(entry);
                }

                lost = dropped;
                dropped = 0;
            }

            if (lost > 0)
                log($"待送佇列超過 {MaxOutbox} 筆，丟掉最舊的 {lost} 筆（對方太久沒連上）。");
            return result;
        }

        /// <summary>對方確認收到序號 ≤ <paramref name="upToSeq"/> 的全部；游標在執行緒池上落地。</summary>
        internal void Ack(long upToSeq)
        {
            lock (gate)
            {
                while (outbox.Count > 0 && outbox.Peek().Seq <= upToSeq)
                    outbox.Dequeue();
                if (upToSeq <= ackedSeq)
                    return;
                ackedSeq = upToSeq;
                ackToWrite = upToSeq;
            }

            KickAckWrite();
        }

        private void KickAckWrite()
        {
            if (Interlocked.CompareExchange(ref ackWriting, 1, 0) != 0)
                return;
            Task.Run(() =>
            {
                try
                {
                    while (true)
                    {
                        long value;
                        lock (gate)
                        {
                            if (ackToWrite == ackWritten)
                                break;
                            value = ackToWrite;
                        }

                        Directory.CreateDirectory(directory);
                        var path = Path.Combine(directory, AckFileName);
                        File.WriteAllText(path + ".tmp", value.ToString(CultureInfo.InvariantCulture));
                        File.Move(path + ".tmp", path, true);
                        lock (gate)
                        {
                            ackWritten = value;
                        }
                    }
                }
                catch (Exception e)
                {
                    // 游標沒落地只會讓下次啟動重送一些對方已經有的（對方去重），不會丟資料。
                    log($"已確認游標寫入失敗：{e.GetType().Name}。");
                    lock (gate)
                    {
                        ackWritten = ackToWrite;
                    }
                }
                finally
                {
                    Volatile.Write(ref ackWriting, 0);
                }
            });
        }

        /// <summary>這個世界在 <paramref name="cutoffUtc"/> 之後問到答案的道具（兩台合起來）。執行緒池上呼叫。</summary>
        internal HashSet<uint> AnsweredSince(uint worldId, DateTime cutoffUtc)
        {
            var result = new HashSet<uint>();
            lock (gate)
            {
                foreach (var ((itemId, world), value) in latest)
                {
                    if (world == worldId && value.Answered > cutoffUtc)
                        result.Add(itemId);
                }
            }

            return result;
        }

        /// <summary>還沒落地的列數（畫面用）。</summary>
        internal int PendingWrites => writer.PendingCount;

        // =====================================================================
        //  格式
        // =====================================================================

        private static string Format(Line line)
        {
            var o = line.Obs;
            return string.Join(",",
                line.Seq.ToString(CultureInfo.InvariantCulture),
                line.Origin.ToString(),
                o.AtUtc.ToString(TimestampFormat, CultureInfo.InvariantCulture),
                o.WorldId.ToString(CultureInfo.InvariantCulture),
                PriceSurveyLog.Escape(o.WorldName),
                o.ItemId.ToString(CultureInfo.InvariantCulture),
                o.LowestNq.ToString(CultureInfo.InvariantCulture),
                o.LowestNqRetainer.ToString(CultureInfo.InvariantCulture),
                o.LowestHq.ToString(CultureInfo.InvariantCulture),
                o.LowestHqRetainer.ToString(CultureInfo.InvariantCulture),
                o.ListingCount.ToString(CultureInfo.InvariantCulture),
                PriceSurveyLog.Escape(o.Source),
                o.GateIntervalMs.ToString(CultureInfo.InvariantCulture),
                PriceSurveyLog.Escape(o.Verdict));
        }

        /// <summary>標題列與壞列靜默略過；讀回來的列一樣過 <see cref="SurveyShare.Validate(in MarketObservation, Func{uint,bool}?, Func{uint,bool}?, DateTime)"/>。</summary>
        private static bool TryParse(string text, out Line line)
        {
            line = default;
            var f = PriceSurveyLog.SplitCsv(text);
            if (f.Count != 14)
                return false;
            if (!long.TryParse(f[0], NumberStyles.None, CultureInfo.InvariantCulture, out var seq))
                return false;
            if (f[1].Length != 1 || (f[1][0] != OriginLocal && f[1][0] != OriginRemote))
                return false;
            if (!DateTime.TryParseExact(f[2], TimestampFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
                return false;
            if (!uint.TryParse(f[3], NumberStyles.None, CultureInfo.InvariantCulture, out var worldId)
                || !uint.TryParse(f[5], NumberStyles.None, CultureInfo.InvariantCulture, out var itemId)
                || !long.TryParse(f[6], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var lowNq)
                || !ulong.TryParse(f[7], NumberStyles.None, CultureInfo.InvariantCulture, out var nqRetainer)
                || !long.TryParse(f[8], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var lowHq)
                || !ulong.TryParse(f[9], NumberStyles.None, CultureInfo.InvariantCulture, out var hqRetainer)
                || !int.TryParse(f[10], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var count)
                || !int.TryParse(f[12], NumberStyles.None, CultureInfo.InvariantCulture, out var gateMs))
                return false;

            var obs = new MarketObservation(at, worldId, f[4], itemId, lowNq, nqRetainer, lowHq, hqRetainer, count,
                f[11], gateMs, f[13]);
            if (SurveyShare.Validate(obs, null, null, DateTime.UtcNow) != null)
                return false;
            line = new Line(seq, f[1][0], obs);
            return true;
        }
    }
}
