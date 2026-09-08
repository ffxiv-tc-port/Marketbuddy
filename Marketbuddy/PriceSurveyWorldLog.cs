using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    /// <summary>
    /// 「某一個世界的巡檢掃到哪了」——一個世界一列。
    ///
    /// <para>
    /// 🔑 這一列存的是**事實**，不是判斷：什麼時候停下的、那一輪的完整清單有幾件、
    /// 真的問到答案的有幾件、有沒有掃到最後。「還算不算掃過」是讀的時候才算的
    /// （見 <see cref="PriceSurveyWorldLog.Classify"/>），因為那取決於使用者當下的
    /// 保留時數設定與目前是哪一個角色——把結論寫死進檔案，設定一改就會對不上。
    /// </para>
    /// </summary>
    /// <param name="AtUtc">這個世界最後一次巡檢停下來的時間（UTC）。</param>
    /// <param name="WorldId">世界 id（程式一律用它比對）。</param>
    /// <param name="WorldName">世界名（只給看檔的人）。</param>
    /// <param name="ContentId">那一輪是哪一個角色跑的；0＝不知道。</param>
    /// <param name="SourceKey">清單來源：<c>csv</c>／<c>allagantools</c>／<c>sell-list</c>。</param>
    /// <param name="PlannedTotal">那一輪這個世界的完整清單件數（含因為已掃過而被略過的）。</param>
    /// <param name="Surveyed">其中真的有答案的件數（略過已掃 ＋ 這輪查到 ＋ 這輪確認沒人賣）。</param>
    /// <param name="Completed">清單整份掃到最後了沒。</param>
    internal readonly record struct SurveyWorldRow(
        DateTime AtUtc,
        uint WorldId,
        string WorldName,
        ulong ContentId,
        string SourceKey,
        int PlannedTotal,
        int Surveyed,
        bool Completed)
    {
        /// <summary>
        /// 這一列的清單是不是涵蓋所有角色的僱員。
        /// 只有 <c>csv</c>（InventoryTools 記錄檔）是；另外兩個來源只看得到當時那個角色，
        /// 所以換角色之後那一列對新角色不成立。
        /// </summary>
        internal bool CoversEveryCharacter => string.Equals(SourceKey, "csv", StringComparison.Ordinal);
    }

    /// <summary>
    /// 「這個世界還算不算掃過」的分類。
    /// ⚠️ 刻意給 <see cref="Never"/> 明確的 0：沒有零值的列舉會讓 <c>default</c>
    /// 落在一個有意義的值上，而那種壞法是靜默的。
    /// </summary>
    internal enum SurveyWorldFreshness
    {
        /// <summary>完全沒有這個世界的紀錄。</summary>
        Never = 0,

        /// <summary>使用者把「保留時數」設成 0＝不追蹤，所以「掃過」這件事沒有意義。</summary>
        Untracked = 1,

        /// <summary>有紀錄，但已經超過保留時數：下一輪會整份重掃。</summary>
        Stale = 2,

        /// <summary>紀錄是別的角色用「只看得到自己僱員」的來源掃的，對現在這個角色不成立。</summary>
        OtherCharacter = 3,

        /// <summary>在保留時數內，但沒掃到最後。</summary>
        Partial = 4,

        /// <summary>在保留時數內，而且整份掃完了。</summary>
        Complete = 5,
    }

    /// <summary>
    /// 每個世界的巡檢進度記錄檔。
    ///
    /// <para>
    /// 🔴 形狀刻意沿用 <see cref="PendingActions"/>：同一個設定目錄、同一種 CSV
    /// （UTF-8 BOM ＋ CRLF ＋ 同一份逸出／拆解器）、<b>整份重寫</b>而不是追加
    /// （一個世界只該有一列，追加寫不出「這一列被更新了」），而且重寫走
    /// 「暫存檔 → <see cref="File.Move(string,string,bool)"/>」，寫到一半崩潰
    /// 不會留下半份清單。
    /// </para>
    ///
    /// <para>
    /// 🔴 執行緒：<b>鎖只用來動字典</b>，真正的檔案 I/O 一律在鎖外、在執行緒池上做。
    /// 鎖內絕不做 I/O、不寫 log、不呼叫別的外掛、不碰 ImGui。
    /// </para>
    ///
    /// <para>
    /// 📌 這個檔<b>不是</b>行情資料，只有進度。行情一律在
    /// <see cref="PriceSurveyLog"/> 的 <c>price_survey.csv</c>；刪掉這個檔只會讓
    /// 「哪些世界掃過」回到未知，不會弄丟任何價格。
    /// </para>
    /// </summary>
    internal static class PriceSurveyWorldLog
    {
        internal const string FileName = "survey_worlds.csv";

        private const string Header =
            "atUtc,worldId,worldName,contentId,sourceKey,plannedTotal,surveyed,completed";

        private const int FieldCount = 8;

        private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";

        private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

        /// <summary>🔴 只在持有 <see cref="Gate"/> 時碰它。世界最多十來個，整份拷貝很便宜。</summary>
        private static readonly Dictionary<uint, SurveyWorldRow> Rows = new();

        /// <summary>🔴 鎖內只碰 <see cref="Rows"/> 與兩個修訂號。</summary>
        private static readonly object Gate = new();

        private static long revision;
        private static long savedRevision;

        /// <summary>單一飛行閘：0＝沒有存檔工作在跑，1＝有。</summary>
        private static int saving;

        /// <summary>0＝還沒讀過檔，1＝讀過了（或正在讀）。</summary>
        private static int loadStarted;

        private static int writeFailureLogged;

        private static volatile Task? loadTask;

        private static string? cachedFilePath;

        /// <summary>檔案讀完了沒（UI 用來分辨「沒有紀錄」與「還沒讀」）。</summary>
        internal static bool Loaded { get; private set; }

        internal static bool IsLoading => loadTask != null;

        /// <summary>
        /// 記錄檔的完整路徑。
        /// 🔴 只算一次：<c>PluginInterface.ConfigDirectory</c> 每次存取都會去檔案系統確認
        /// （不存在就建），而畫面上每一幀都會取這個值。
        /// </summary>
        internal static string FilePath
            => cachedFilePath ??= Path.Combine(PluginInterface.ConfigDirectory.FullName, FileName);

        /// <summary>第一次呼叫時在執行緒池上把檔案讀回來；之後是 no-op。</summary>
        internal static void BeginLoad()
        {
            if (Interlocked.CompareExchange(ref loadStarted, 1, 0) != 0)
                return;
            loadTask = Task.Run(LoadCore);
        }

        /// <summary>讀檔工作跑完了就把它收掉。framework 執行緒每幀呼叫。</summary>
        internal static void PumpLoad()
        {
            var task = loadTask;
            if (task == null || !task.IsCompleted)
                return;
            loadTask = null;
            Loaded = true;
        }

        private static void LoadCore()
        {
            var fromFile = ReadAll();
            lock (Gate)
            {
                // 🔑 只補上還沒有的世界：讀檔期間剛跑完的那一輪比檔案裡的新，不可以被蓋掉。
                foreach (var row in fromFile)
                    Rows.TryAdd(row.WorldId, row);
            }
        }

        /// <summary>記下（或更新）一個世界的進度。同一個世界永遠只留最新那一次。</summary>
        internal static void Record(SurveyWorldRow row)
        {
            if (row.WorldId == 0)
                return;

            lock (Gate)
            {
                Rows[row.WorldId] = row;
                revision++;
            }

            KickSave();
        }

        /// <summary>把全部紀錄清掉（使用者按「忘記掃過哪些世界」）。行情記錄檔完全不受影響。</summary>
        internal static void Clear()
        {
            lock (Gate)
            {
                if (Rows.Count == 0)
                    return;
                Rows.Clear();
                revision++;
            }

            KickSave();
        }

        /// <summary>取一個世界的紀錄；回傳的是結構的複本，呼叫端拿走之後與鎖無關。</summary>
        internal static bool TryGet(uint worldId, out SurveyWorldRow row)
        {
            lock (Gate)
            {
                return Rows.TryGetValue(worldId, out row);
            }
        }

        /// <summary>目前記得幾個世界。</summary>
        internal static int Count
        {
            get
            {
                lock (Gate)
                {
                    return Rows.Count;
                }
            }
        }

        /// <summary>
        /// 「這個世界還算不算掃過」。
        ///
        /// <para>
        /// 🔑 <b>失效條件與續掃真正會跳過的規則是同一條</b>（<c>PriceSurveySkipHours</c>），
        /// 所以畫面說的話永遠不會跟按下去之後的行為不一致。四種失效：
        /// ①超過保留時數 ②保留時數設成 0（等於不追蹤） ③紀錄是別的角色用只看得到
        /// 自己僱員的來源掃的 ④使用者自己按了清除。
        /// </para>
        /// </summary>
        /// <param name="row">紀錄。</param>
        /// <param name="skipHours">使用者設的保留時數；0＝不追蹤。</param>
        /// <param name="currentContentId">目前角色；0＝還沒登入（那就不拿角色去否定紀錄）。</param>
        /// <param name="nowUtc">現在（UTC）。</param>
        internal static SurveyWorldFreshness Classify(
            SurveyWorldRow row, int skipHours, ulong currentContentId, DateTime nowUtc)
        {
            if (skipHours <= 0)
                return SurveyWorldFreshness.Untracked;

            if (nowUtc - row.AtUtc > TimeSpan.FromHours(skipHours))
                return SurveyWorldFreshness.Stale;

            if (!row.CoversEveryCharacter && currentContentId != 0 && row.ContentId != 0 &&
                row.ContentId != currentContentId)
                return SurveyWorldFreshness.OtherCharacter;

            return row.Completed ? SurveyWorldFreshness.Complete : SurveyWorldFreshness.Partial;
        }

        // =====================================================================
        //  存檔（整份重寫，I/O 一律在鎖外、在執行緒池上）
        // =====================================================================

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
                    List<SurveyWorldRow> batch;
                    long at;
                    lock (Gate)
                    {
                        if (revision == savedRevision)
                            break;
                        at = revision;
                        batch = new List<SurveyWorldRow>(Rows.Values);
                    }

                    // 🔴 I/O 在鎖外做。
                    WriteAll(batch);

                    lock (Gate)
                    {
                        savedRevision = at;
                    }
                }
            }
            catch (Exception e)
            {
                ReportWriteFailure(e);

                // 🔴 放棄這一代，不要重試：寫不進去的原因不會在下一毫秒消失，
                //    而 finally 裡的「還有新東西就再踢一次」會把失敗變成燒 CPU 的死迴圈。
                lock (Gate)
                {
                    savedRevision = revision;
                }
            }
            finally
            {
                Volatile.Write(ref saving, 0);

                var again = false;
                lock (Gate)
                {
                    again = revision != savedRevision;
                }

                if (again)
                    KickSave();
            }
        }

        private static void WriteAll(List<SurveyWorldRow> rows)
        {
            var path = FilePath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            rows.Sort((a, b) => a.WorldId.CompareTo(b.WorldId));

            var text = new StringBuilder();
            text.Append(Header).Append("\r\n");
            foreach (var row in rows)
                text.Append(Format(row)).Append("\r\n");
            var bytes = new UTF8Encoding(false).GetBytes(text.ToString());

            // 🔴 先寫暫存檔再換過去。
            var temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(Utf8Bom, 0, Utf8Bom.Length);
                stream.Write(bytes, 0, bytes.Length);
            }

            File.Move(temp, path, true);
        }

        private static void ReportWriteFailure(Exception e)
        {
            if (Interlocked.Exchange(ref writeFailureLogged, 1) != 0)
                return;

            // 要使用者回報的診斷寫 Information。
            try
            {
                Log.Information(
                    e,
                    $"[Marketbuddy] 巡檢世界進度寫入失敗（{FileName}）；「哪些世界掃過」這一份只會留在" +
                    "本次工作階段，重開遊戲之後會忘記。行情記錄檔不受影響。本工作階段不再重複回報。");
            }
            catch
            {
                // 記錄失敗絕不能中斷巡檢。
            }
        }

        private static string Format(SurveyWorldRow row)
            => string.Join(",",
                row.AtUtc.ToString(TimestampFormat, CultureInfo.InvariantCulture),
                row.WorldId.ToString(CultureInfo.InvariantCulture),
                PriceSurveyLog.Escape(row.WorldName),
                row.ContentId.ToString(CultureInfo.InvariantCulture),
                PriceSurveyLog.Escape(row.SourceKey),
                row.PlannedTotal.ToString(CultureInfo.InvariantCulture),
                row.Surveyed.ToString(CultureInfo.InvariantCulture),
                row.Completed ? "1" : "0");

        /// <summary>
        /// 把檔案讀回來。🔴 <b>只從執行緒池呼叫</b>。檔案不存在或讀不動時回空清單。
        /// </summary>
        private static List<SurveyWorldRow> ReadAll()
        {
            var result = new List<SurveyWorldRow>();
            string path;
            try
            {
                path = FilePath;
            }
            catch
            {
                return result;
            }

            try
            {
                if (!File.Exists(path))
                    return result;

                // 🔴 FileShare.Delete 是必要的：存檔走的是「暫存檔 → File.Move 覆蓋」。
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                while (reader.ReadLine() is { } line)
                {
                    if (line.Length == 0)
                        continue;
                    if (TryParse(line, out var row))
                        result.Add(row);
                }
            }
            catch (Exception e)
            {
                Log.Information(
                    e, $"[Marketbuddy] 巡檢世界進度讀取失敗（{FileName}），畫面會顯示成沒有任何世界掃過。");
            }

            return result;
        }

        /// <summary>解析一列。標題列與壞掉的列靜默略過（這是使用者可以自己編輯的檔案）。</summary>
        private static bool TryParse(string line, out SurveyWorldRow row)
        {
            row = default;
            var fields = PriceSurveyLog.SplitCsv(line);
            if (fields.Count < FieldCount)
                return false;

            if (!DateTime.TryParseExact(fields[0], TimestampFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
                return false;
            if (!uint.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var worldId))
                return false;
            if (worldId == 0)
                return false;
            if (!ulong.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var contentId))
                return false;
            if (!int.TryParse(fields[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var planned))
                return false;
            if (!int.TryParse(fields[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var surveyed))
                return false;
            if (!int.TryParse(fields[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var completed))
                return false;

            row = new SurveyWorldRow(at, worldId, fields[2], contentId, fields[4], planned, surveyed, completed != 0);
            return true;
        }
    }
}
