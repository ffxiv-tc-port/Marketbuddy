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
    /// <summary>某個世界某件道具的觀測摘要（排全市場掃描用）。</summary>
    /// <param name="AnsweredUtc">最近一次問到答案的時間；MinValue＝從來沒問到。</param>
    /// <param name="NothingAtAll">最近一次問到的答案是「沒人賣、這一頁也沒有成交」（成交不知道時是 false）。</param>
    internal readonly record struct ItemBrief(DateTime AnsweredUtc, bool NothingAtAll);

    /// <summary>
    /// 市場觀測記錄檔（<c>survey_observations.csv</c>，追加寫、超過門檻整份重寫）＋記憶體索引＋送往另一台的待送佇列。
    /// <para>🔑 一份檔三個用途：①每筆本機觀測帶遞增序號，序號大於「對方已確認」游標的就是待送（斷線、重開遊戲都不丟）；
    /// ②(世界, 道具) 的最新資料：收到的觀測不比索引新就當重複；索引也是給別的畫面查行情的唯讀來源；
    /// ③代掃與全市場掃描「這個世界保留時間內問過哪些」看兩邊合起來的資料。</para>
    /// <para>🔑 有界：每個 (世界, 道具) 只留最近一次問到答案的那筆，外加比它新的一筆沒問到的；再加上待送的本機列。
    /// 讀檔時與檔案列數超過門檻時，在寫檔執行緒上整份重寫（暫存檔＋取代），已確認游標不動。</para>
    /// <para>🔴 <see cref="gate"/> 內只碰欄位與待寫清單：不做 I/O、不寫 log。重寫的快照在鎖內抄一份 O(索引) 的清單（執行緒池上）。</para>
    /// </summary>
    internal sealed class SurveyObservationLog
    {
        internal const string FileName = "survey_observations.csv";
        internal const string AckFileName = "survey_link_ack.txt";

        /// <summary>待送上限；超過就丟最舊的並記一次 log（對方長期不上線時不讓記憶體無限長）。</summary>
        internal const int MaxOutbox = 300_000;

        /// <summary>檔案列數超過「保留列數 ×2 ＋這個值」才重寫（測試可調小）。</summary>
        internal static int CompactSlack = 20_000;

        private const string Header =
            "seq,origin,timestampUtc,worldId,worldName,itemId,lowestNq,lowestNqRetainer,lowestHq,lowestHqRetainer,listingCount,source,gateIntervalMs,verdict," +
            "salesKnown,lastSaleNq,lastSaleNqUtc,lastSaleHq,lastSaleHqUtc,sales7d,salesPage";

        private const int OldColumns = 14;
        private const int Columns = 21;
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

        /// <summary>一個 (世界, 道具)：最近問到答案的一筆、比它新的一筆沒問到的、以及看過的最新時間。</summary>
        private struct Entry
        {
            public Line Answered;
            public bool HasAnswered;
            public Line Other;
            public bool HasOther;
            public DateTime Any;
        }

        private readonly object gate = new();
        private readonly string directory;
        private readonly Action<string> log;
        private readonly CsvAppendLog<Line> writer;

        // ---- gate 內 ----
        private readonly Dictionary<uint, Dictionary<uint, Entry>> index = new();
        private readonly Queue<(long Seq, MarketObservation Obs)> outbox = new();
        private readonly List<MarketObservation> beforeLoad = [];
        private long nextSeq = 1;
        private long ackedSeq;
        private long ackToWrite;
        private long ackWritten;
        private int dropped;
        private int keptLines;
        private int linesInFile;
        private bool compactQueued;
        private int compactions;

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

        /// <summary>目前檔案（含還沒落地的）有幾列資料、重寫過幾次（畫面與測試用）。</summary>
        internal (int LinesInFile, int KeptLines, int Compactions) Size
        {
            get
            {
                lock (gate)
                {
                    return (linesInFile, keptLines, compactions);
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
            bool compact;
            lock (gate)
            {
                ackedSeq = Math.Max(ackedSeq, acked);
                ackWritten = ackedSeq;
                // 記錄檔被刪過但游標還在時，新序號也要比游標大，否則下次啟動會被當成已送過。
                nextSeq = Math.Max(nextSeq, ackedSeq + 1);
                foreach (var line in lines)
                {
                    NoteLine(line);
                    if (line.Origin != OriginLocal)
                        continue;
                    nextSeq = Math.Max(nextSeq, line.Seq + 1);
                    if (line.Seq > ackedSeq)
                    {
                        outbox.Enqueue((line.Seq, line.Obs));
                        pendingLocal++;
                    }
                }

                linesInFile = lines.Count;
                late = new List<MarketObservation>(beforeLoad);
                beforeLoad.Clear();
                foreach (var obs in late)
                    RecordLocalLocked(obs);
                compact = MaybeCompactLocked(force: false);
                Volatile.Write(ref loadState, 2);
            }

            if (late.Count > 0 || compact)
                writer.Kick();
            log($"觀測記錄檔讀完：{lines.Count} 列（{CountKeys()} 組世界×道具），待送 {pendingLocal + late.Count} 筆（已確認到序號 {ackedSeq}）" +
                $"{(compact ? "，列數超過門檻，背景重寫成每組只留最新" : string.Empty)}。");
        }

        private int CountKeys()
        {
            lock (gate)
            {
                var n = 0;
                foreach (var world in index.Values)
                    n += world.Count;
                return n;
            }
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
                MaybeCompactLocked(force: false);
            }

            writer.Kick();
        }

        private void RecordLocalLocked(MarketObservation obs)
        {
            var seq = nextSeq++;
            var line = new Line(seq, OriginLocal, obs);
            NoteLine(line);
            outbox.Enqueue((seq, obs));
            if (outbox.Count > MaxOutbox)
            {
                outbox.Dequeue();
                dropped++;
            }

            writer.EnqueueLocked(line);
            linesInFile++;
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
                if (index.TryGetValue(obs.WorldId, out var world) && world.TryGetValue(obs.ItemId, out var known) && known.Any >= at)
                    return Intake.Duplicate;
                var line = new Line(0, OriginRemote, obs);
                NoteLine(line);
                writer.EnqueueLocked(line);
                linesInFile++;
                MaybeCompactLocked(force: false);
            }

            writer.Kick();
            return Intake.Accepted;
        }

        /// <summary>把一列併進索引（只在 gate 內）。同秒的後到者取代先到者。</summary>
        private void NoteLine(in Line line)
        {
            var obs = line.Obs;
            if (!index.TryGetValue(obs.WorldId, out var world))
                index[obs.WorldId] = world = new Dictionary<uint, Entry>();
            world.TryGetValue(obs.ItemId, out var e);
            var before = (e.HasAnswered ? 1 : 0) + (e.HasOther ? 1 : 0);
            var at = SurveyShare.TruncateToSecond(obs.AtUtc);
            if (at > e.Any)
                e.Any = at;
            if (obs.Answered)
            {
                if (!e.HasAnswered || at >= SurveyShare.TruncateToSecond(e.Answered.Obs.AtUtc))
                {
                    e.Answered = line;
                    e.HasAnswered = true;
                }
            }
            else if (!e.HasOther || at >= SurveyShare.TruncateToSecond(e.Other.Obs.AtUtc))
            {
                e.Other = line;
                e.HasOther = true;
            }

            if (e.HasOther && e.HasAnswered
                           && SurveyShare.TruncateToSecond(e.Other.Obs.AtUtc) <= SurveyShare.TruncateToSecond(e.Answered.Obs.AtUtc))
                e.HasOther = false;
            world[obs.ItemId] = e;
            keptLines += (e.HasAnswered ? 1 : 0) + (e.HasOther ? 1 : 0) - before;
        }

        /// <summary>檔案列數超過門檻（或 <paramref name="force"/>）就排一次整份重寫。只在 gate 內。</summary>
        private bool MaybeCompactLocked(bool force)
        {
            if (compactQueued)
                return false;
            if (!force && linesInFile <= (2L * (keptLines + outbox.Count)) + CompactSlack)
                return false;
            compactQueued = true;
            writer.RequestRewriteLocked(SnapshotLocked);
            return true;
        }

        /// <summary>立刻排一次重寫（測試與診斷用）。</summary>
        internal void RequestCompact()
        {
            lock (gate)
            {
                if (Volatile.Read(ref loadState) != 2)
                    return;
                MaybeCompactLocked(force: true);
            }

            writer.Kick();
        }

        /// <summary>
        /// 在寫檔執行緒、gate 內被呼叫：每組的保留列＋待送的本機列。待送列一律從佇列寫（序號遞增、讀回來照樣進待送），
        /// 索引裡的本機列若還在佇列裡就不重複寫。
        /// </summary>
        private IReadOnlyList<Line> SnapshotLocked()
        {
            compactQueued = false;
            compactions++;
            var firstPending = outbox.Count > 0 ? outbox.Peek().Seq : long.MaxValue;
            var rows = new List<Line>(keptLines + outbox.Count);
            foreach (var world in index.Values)
            {
                foreach (var e in world.Values)
                {
                    if (e.HasAnswered && !IsQueued(e.Answered, firstPending))
                        rows.Add(e.Answered);
                    if (e.HasOther && !IsQueued(e.Other, firstPending))
                        rows.Add(e.Other);
                }
            }

            foreach (var (seq, obs) in outbox)
                rows.Add(new Line(seq, OriginLocal, obs));
            linesInFile = rows.Count;
            return rows;
        }

        private static bool IsQueued(in Line line, long firstPending) => line.Origin == OriginLocal && line.Seq >= firstPending;

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
                if (!index.TryGetValue(worldId, out var world))
                    return result;
                foreach (var (itemId, e) in world)
                {
                    if (e.HasAnswered && SurveyShare.TruncateToSecond(e.Answered.Obs.AtUtc) > cutoffUtc)
                        result.Add(itemId);
                }
            }

            return result;
        }

        /// <summary>這個世界每件道具的摘要（排全市場掃描用）。執行緒池上呼叫。</summary>
        internal Dictionary<uint, ItemBrief> WorldBrief(uint worldId)
        {
            lock (gate)
            {
                if (!index.TryGetValue(worldId, out var world))
                    return new Dictionary<uint, ItemBrief>();
                var result = new Dictionary<uint, ItemBrief>(world.Count);
                foreach (var (itemId, e) in world)
                {
                    if (e.HasAnswered)
                        result[itemId] = new ItemBrief(SurveyShare.TruncateToSecond(e.Answered.Obs.AtUtc), e.Answered.Obs.NothingAtAll);
                }

                return result;
            }
        }

        /// <summary>
        /// 唯讀查詢：(世界, 道具) 最近一次問到答案的那筆（含成交摘要；成交不知道時 <c>Sales.Known</c>＝false）。
        /// 任何執行緒；讀檔完成前一律回 false。
        /// </summary>
        internal bool TryGetLatest(uint worldId, uint itemId, out MarketObservation latest)
        {
            latest = default;
            lock (gate)
            {
                if (!index.TryGetValue(worldId, out var world) || !world.TryGetValue(itemId, out var e) || !e.HasAnswered)
                    return false;
                latest = e.Answered.Obs;
                return true;
            }
        }

        /// <summary>唯讀查詢：這件道具在每個世界最近一次問到答案的那筆。任何執行緒。</summary>
        internal List<MarketObservation> LatestForItem(uint itemId)
        {
            var result = new List<MarketObservation>();
            lock (gate)
            {
                foreach (var world in index.Values)
                {
                    if (world.TryGetValue(itemId, out var e) && e.HasAnswered)
                        result.Add(e.Answered.Obs);
                }
            }

            return result;
        }

        /// <summary>還沒落地的列數（畫面用）。</summary>
        internal int PendingWrites => writer.PendingCount;

        /// <summary>每一列（含重寫）都已經落地、沒有寫檔工作在跑。</summary>
        internal bool IsSettled => writer.IsIdle;

        // =====================================================================
        //  格式
        // =====================================================================

        private static string Format(Line line)
        {
            var o = line.Obs;
            var s = o.Sales;
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
                PriceSurveyLog.Escape(o.Verdict),
                s.Known ? "1" : "0",
                s.Known ? s.LastNq.ToString(CultureInfo.InvariantCulture) : string.Empty,
                s.Known ? LinkJson.UnixOrZero(s.LastNqUtc).ToString(CultureInfo.InvariantCulture) : string.Empty,
                s.Known ? s.LastHq.ToString(CultureInfo.InvariantCulture) : string.Empty,
                s.Known ? LinkJson.UnixOrZero(s.LastHqUtc).ToString(CultureInfo.InvariantCulture) : string.Empty,
                s.Known ? s.RecentCount.ToString(CultureInfo.InvariantCulture) : string.Empty,
                s.Known ? s.PageCount.ToString(CultureInfo.InvariantCulture) : string.Empty);
        }

        /// <summary>
        /// 標題列與壞列靜默略過；讀回來的列一樣過 <see cref="SurveyShare.Validate(in MarketObservation, Func{uint,bool}?, Func{uint,bool}?, DateTime)"/>。
        /// 舊版的 14 欄列照收，成交摘要當「不知道」。
        /// </summary>
        private static bool TryParse(string text, out Line line)
        {
            line = default;
            var f = PriceSurveyLog.SplitCsv(text);
            if (f.Count != OldColumns && f.Count != Columns)
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

            var sales = f.Count == Columns ? ParseSales(f) : default;
            var obs = new MarketObservation(at, worldId, string.Intern(f[4]), itemId, lowNq, nqRetainer, lowHq, hqRetainer, count,
                string.Intern(f[11]), gateMs, string.Intern(f[13]), sales);
            if (SurveyShare.Validate(obs, null, null, DateTime.UtcNow) != null)
                return false;
            line = new Line(seq, f[1][0], obs);
            return true;
        }

        /// <summary>成交欄位讀不懂就當「不知道」，不丟整列。</summary>
        private static SaleSummary ParseSales(List<string> f)
        {
            if (f[14] != "1"
                || !long.TryParse(f[15], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var nq)
                || !long.TryParse(f[16], NumberStyles.None, CultureInfo.InvariantCulture, out var nqAt)
                || !long.TryParse(f[17], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var hq)
                || !long.TryParse(f[18], NumberStyles.None, CultureInfo.InvariantCulture, out var hqAt)
                || !int.TryParse(f[19], NumberStyles.None, CultureInfo.InvariantCulture, out var recent)
                || !int.TryParse(f[20], NumberStyles.None, CultureInfo.InvariantCulture, out var page)
                || nqAt > 4_102_444_800 || hqAt > 4_102_444_800)
                return default;
            var s = new SaleSummary(true, nq, LinkJson.FromUnixOrMin(nqAt), hq, LinkJson.FromUnixOrMin(hqAt), recent, page);
            return SurveyShare.Validate(s, DateTime.UtcNow) == null ? s : default;
        }
    }
}
