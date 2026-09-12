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
    /// 待處理清單的三個桶。
    /// ⚠️ 刻意給 <see cref="PriceCap"/> 明確的 0：沒有零值的列舉會讓 <c>default</c>
    /// 落在一個無效值上，而那種壞法是靜默的。
    /// </summary>
    internal enum PendingActionKind
    {
        /// <summary>快速上架之後查不到任何行情，刻意留在上限價等人工定價。</summary>
        PriceCap = 0,

        /// <summary>巡檢資料顯示同一個世界有別人賣得比我便宜。</summary>
        Undercut = 1,

        /// <summary>照目前行情重掛會落到使用者設的「最低價」以下，該下架而不是繼續掛。</summary>
        BelowMinimum = 2,

        /// <summary>
        /// 市場上最便宜的那個價看起來是打錯的（少打一個 0 之類），所以<b>沒有跟著降價</b>。
        /// </summary>
        /// <remarks>
        /// 🔴 這個桶記的是「<b>已經替你擋下來了</b>」，不是「你該做什麼」——
        /// 價格一個 gil 都沒有被改動。判準與門檻見 <see cref="PriceAnomalyGuard"/>。
        /// ⚠️ 這一桶的兩個欄位語意與其他桶不同（沒有別的欄位可以放，而加一欄 CSV
        /// 會讓既有的清單檔整份讀不回來）：
        /// <c>SuggestedPrice</c>＝<b>被擋下來的那個可疑價</b>（不是建議你掛的價），
        /// <c>SuggestionWorld</c>＝拿來比的那個<b>正常價</b>（已經格式化成字串）。
        /// <c>SuggestionSource</c> 是 <c>anomaly-peer</c>／<c>anomaly-own</c>，
        /// UI 靠它分辨這兩個欄位該怎麼讀。
        /// </remarks>
        PriceAnomaly = 3,
    }

    /// <summary>
    /// 一列待處理的身分。
    /// 🔑 <b>格號屬於某一位僱員</b>，所以識別一定要帶 <c>RetainerId</c>：
    /// 少了它，僱員 B 的第 3 格會被當成僱員 A 的第 3 格（<c>RecentChangesRetainerId</c>
    /// 存在的理由完全相同）。查不到位置時 <c>RetainerId=0、Slot=-1</c>，那代表
    /// 「知道有這件事、但不知道它掛在哪一格」——UI 要把它畫成 <c>?</c> 而不是 0。
    /// </summary>
    internal readonly record struct PendingActionKey(
        PendingActionKind Kind, uint ItemId, bool Hq, ulong RetainerId, short Slot);

    /// <summary>
    /// 待處理清單的一列。
    ///
    /// <para>
    /// 🔑 所有「不知道」一律用 <c>-1</c>（價格）或空字串（來源／世界）表示，<b>不是 0</b>。
    /// 0 在價格欄是一個合法但荒謬的值，把「沒查到」畫成 0 會直接誤導使用者。
    /// </para>
    /// </summary>
    /// <param name="AtUtc">這一列是什麼時候進到清單的（UTC）。</param>
    /// <param name="Kind">屬於哪一個桶。</param>
    /// <param name="ItemId">道具 id。</param>
    /// <param name="Hq">這一列講的是優質品還是普通品。</param>
    /// <param name="RetainerId">掛在哪一位僱員身上；0＝不知道。</param>
    /// <param name="RetainerName">那位僱員的名字（只給人看，比對一律用 id）；空＝不知道。</param>
    /// <param name="Slot">僱員市場容器的格號；-1＝不知道。</param>
    /// <param name="CurrentPrice">目前的掛售單價；-1＝不知道。</param>
    /// <param name="SuggestedPrice">建議的掛售單價；-1＝沒有任何可用的參考價。</param>
    /// <param name="SuggestionSource">
    /// 建議價的來源：<c>live</c>＝本世界的即時市場快取、<c>survey</c>＝巡檢記錄（同一個世界）、
    /// <c>sale</c>＝Universalis 的資料中心最近一次實際成交、
    /// <c>survey-other</c>＝巡檢記錄（別的世界，只能當參考）、空＝沒有來源。
    /// <c>survey-excluded</c>＝只有被排除的世界有資料，
    /// 所以沒有建議價：此時 <paramref name="SuggestedPrice"/> 是 -1，
    /// 而 <paramref name="SuggestionWorld"/> 是那個被排除的世界，
    /// 畫面才說得出「為什麼沒有」。
    /// </param>
    /// <param name="SuggestionWorld">建議價是哪一個世界的行情；空＝不知道。</param>
    /// <param name="SuggestionAtUtc">建議價的資料時間；<see cref="DateTime.MinValue"/>＝不知道。</param>
    /// <param name="Skipped">使用者按過「跳過」。留在檔案裡但預設不顯示，重算時會沿用。</param>
    internal readonly record struct PendingActionRow(
        DateTime AtUtc,
        PendingActionKind Kind,
        uint ItemId,
        bool Hq,
        ulong RetainerId,
        string RetainerName,
        short Slot,
        long CurrentPrice,
        long SuggestedPrice,
        string SuggestionSource,
        string SuggestionWorld,
        DateTime SuggestionAtUtc,
        bool Skipped)
    {
        public PendingActionKey Key => new(Kind, ItemId, Hq, RetainerId, Slot);

        /// <summary>這一列的建議價能不能真的拿來用（有價、而且不是別的世界的參考值）。</summary>
        /// <remarks>
        /// 🔴 <c>sale</c> 也算<b>本地</b>：那是整個資料中心真的成交過的價，重掛引擎會拿它定價
        /// （見 <see cref="RelistPricing"/>）。漏掉它的話那些列會被畫成灰色的「別的世界，只能
        /// 當參考」，而那句話是錯的——按下去真的會照它掛。
        /// </remarks>
        public bool SuggestionIsLocal
            => SuggestedPrice >= 0 && SuggestionSource is "live" or "survey" or PriceSourceTag.Sale;
    }

    /// <summary>
    /// 「還沒處理完的事」的持久清單，寫在 Marketbuddy 自己的設定目錄底下的
    /// <c>pending_actions.csv</c>。
    /// 🔴 <b>這個類別不會改任何價格。</b>它只記錄「哪一格需要人去看一下」。
    /// 真正的改價一律仍然由使用者在畫面上按下按鈕，走既有的
    /// <c>BatchReprice.StartQuickReprice</c>。
    /// 執行緒：形狀比照 <see cref="PriceSurveyLog"/> —— <b>鎖只用來動字典</b>，
    /// 真正的檔案 I/O 一律在鎖外、在執行緒池上做。鎖內絕不做 I/O、不寫 log、
    /// 不呼叫別的外掛、不碰 ImGui。
    /// ⚠️ 與 <see cref="PriceSurveyLog"/> 不同，這個檔是<b>整份重寫</b>而不是追加：
    /// 清單會縮短（處理完、跳過、重算），追加寫不出「某一列不見了」。重寫走
    /// 「暫存檔 → <see cref="File.Move(string,string,bool)"/>」，所以寫到一半崩潰
    /// 不會留下半份清單。
    /// </summary>
    internal static class PendingActions
    {
        internal const string FileName = "pending_actions.csv";

        private const string Header =
            "atUtc,kind,itemId,hq,retainerId,retainerName,slot,currentPrice,suggestedPrice," +
            "suggestionSource,suggestionWorld,suggestionAtUtc,skipped";

        private const int FieldCount = 13;

        private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";

        private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

        /// <summary>🔴 只在持有 <see cref="Gate"/> 時碰它。</summary>
        private static readonly Dictionary<PendingActionKey, PendingActionRow> Rows = new();

        /// <summary>🔴 鎖內只碰 <see cref="Rows"/> 與下面那兩個計數器。</summary>
        private static readonly object Gate = new();

        /// <summary>每一次改動就 +1；存檔工作拿它跟 <see cref="savedRevision"/> 比對決定要不要再寫一次。</summary>
        private static long revision;

        private static long savedRevision;

        /// <summary>單一飛行閘：0＝沒有存檔工作在跑，1＝有。</summary>
        private static int saving;

        /// <summary>0＝還沒讀過檔，1＝讀過了（或正在讀）。</summary>
        private static int loadStarted;

        private static int writeFailureLogged;

        /// <summary>讀檔工作；null＝沒有在讀。UI 拿它顯示「讀取中…」。</summary>
        private static volatile Task? loadTask;

        /// <summary>檔案讀完了沒（UI 用來分辨「清單是空的」與「還沒讀」）。</summary>
        internal static bool Loaded { get; private set; }

        /// <summary>
        /// 記錄檔的完整路徑。
        /// 🔴 只算一次：<c>PluginInterface.ConfigDirectory</c> 每次存取都會去檔案系統確認
        /// （不存在就建），而畫面上的 tooltip 每一幀都會取這個值——那等於每幀一次磁碟操作。
        /// </summary>
        internal static string FilePath
            => cachedFilePath ??= Path.Combine(PluginInterface.ConfigDirectory.FullName, FileName);

        private static string? cachedFilePath;

        /// <summary>目前清單裡的列數（含已跳過的）。</summary>
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

        /// <summary>正在讀檔嗎（UI 顯示用）。</summary>
        internal static bool IsLoading => loadTask != null;

        /// <summary>
        /// 第一次呼叫時在執行緒池上把檔案讀回來；之後是 no-op。
        /// 🔴 讀回來的列<b>只補上還沒有的鍵</b>：外掛剛載入就被快速上架記進來的那幾列
        /// 比檔案裡的新，不可以被覆蓋掉。
        /// </summary>
        internal static void BeginLoad()
        {
            if (Interlocked.CompareExchange(ref loadStarted, 1, 0) != 0)
                return;
            loadTask = Task.Run(LoadCore);
        }

        /// <summary>讀檔工作跑完了就把 <see cref="loadTask"/> 收掉。framework 執行緒每幀呼叫。</summary>
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
                foreach (var row in fromFile)
                    Rows.TryAdd(row.Key, row);
            }
        }

        /// <summary>
        /// 記下一列（同鍵覆蓋）。
        /// 🔑 覆蓋時<b>刻意把「已跳過」清掉</b>：同一格又一次掛在上限價，是新發生的事，
        /// 不該被上一次的「跳過」永久蓋住。
        /// </summary>
        internal static void Upsert(PendingActionRow row)
        {
            lock (Gate)
            {
                Rows[row.Key] = row;
                revision++;
            }

            KickSave();
        }

        /// <summary>
        /// 把某一位僱員某一格的所有待處理列拿掉（不分桶）。
        /// 改價成功、下架成功、或那一格已經在正確的價格上時呼叫。
        /// </summary>
        /// <returns>真的拿掉了幾列。</returns>
        internal static int ClearSlot(ulong retainerId, short slot)
        {
            if (retainerId == 0 || slot < 0)
                return 0;

            int removed;
            lock (Gate)
            {
                List<PendingActionKey>? doomed = null;
                foreach (var key in Rows.Keys)
                {
                    if (key.RetainerId == retainerId && key.Slot == slot)
                        (doomed ??= []).Add(key);
                }

                if (doomed == null)
                    return 0;

                foreach (var key in doomed)
                    Rows.Remove(key);
                removed = doomed.Count;
                revision++;
            }

            KickSave();
            return removed;
        }

        /// <summary>使用者按了「跳過」：留在檔案裡但不再顯示，重算時會沿用這個狀態。</summary>
        internal static void MarkSkipped(PendingActionKey key)
        {
            lock (Gate)
            {
                if (!Rows.TryGetValue(key, out var row) || row.Skipped)
                    return;
                Rows[key] = row with { Skipped = true };
                revision++;
            }

            KickSave();
        }

        /// <summary>把所有「已跳過」的標記清掉，讓它們重新出現在清單上。</summary>
        internal static int ClearSkipped()
        {
            int cleared;
            lock (Gate)
            {
                List<PendingActionKey>? keys = null;
                foreach (var (key, row) in Rows)
                {
                    if (row.Skipped)
                        (keys ??= []).Add(key);
                }

                if (keys == null)
                    return 0;

                foreach (var key in keys)
                    Rows[key] = Rows[key] with { Skipped = false };
                cleared = keys.Count;
                revision++;
            }

            KickSave();
            return cleared;
        }

        /// <summary>
        /// 用重算的結果換掉<b>算得出來的那兩個桶</b>（<see cref="PendingActionKind.Undercut"/>
        /// 與 <see cref="PendingActionKind.BelowMinimum"/>）。
        /// 🔴 <see cref="PendingActionKind.PriceCap"/> <b>一列都不動</b>：那個桶是事件記下來的
        /// （快速上架當下查不到行情），重算的輸入（巡檢記錄）裡根本沒有那件事，
        /// 一起換掉等於把它們全部靜默刪光。
        /// 「已跳過」會依鍵沿用過去，所以重算不會讓使用者跳過的東西又冒出來。
        /// </summary>
        internal static void ReplaceComputed(List<PendingActionRow> computed)
        {
            lock (Gate)
            {
                var carried = new Dictionary<PendingActionKey, bool>();
                List<PendingActionKey>? doomed = null;
                foreach (var (key, row) in Rows)
                {
                    if (key.Kind == PendingActionKind.PriceCap)
                        continue;
                    carried[key] = row.Skipped;
                    (doomed ??= []).Add(key);
                }

                if (doomed != null)
                {
                    foreach (var key in doomed)
                        Rows.Remove(key);
                }

                foreach (var row in computed)
                {
                    // 同一格已經在「上限價」桶裡的，不要再加一列被壓價 —— 那是同一件事。
                    var capKey = row.Key with { Kind = PendingActionKind.PriceCap };
                    if (Rows.ContainsKey(capKey))
                        continue;
                    Rows[row.Key] = carried.TryGetValue(row.Key, out var skipped) && skipped
                        ? row with { Skipped = true }
                        : row;
                }

                revision++;
            }

            KickSave();
        }

        /// <summary>清單的一份複本。framework 執行緒每幀拍一次給 UI 用。</summary>
        internal static List<PendingActionRow> Snapshot()
        {
            lock (Gate)
            {
                return new List<PendingActionRow>(Rows.Values);
            }
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
                    List<PendingActionRow> batch;
                    long at;
                    lock (Gate)
                    {
                        if (revision == savedRevision)
                            break;
                        at = revision;
                        batch = new List<PendingActionRow>(Rows.Values);
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

                // 🔴 放棄這一代，不要重試。
                //    寫不進去的常見原因（檔案被別的程式開著、目錄唯讀）不會在下一毫秒消失，
                //    而 finally 裡的「還有新東西就再踢一次」會把失敗變成一個燒 CPU 的死迴圈。
                //    下一次真的有人改動清單時 revision 會再往前走，那時自然會重試。
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

        private static void WriteAll(List<PendingActionRow> rows)
        {
            var path = FilePath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            rows.Sort(CompareForFile);

            var text = new StringBuilder();
            text.Append(Header).Append("\r\n");
            foreach (var row in rows)
                text.Append(Format(row)).Append("\r\n");
            var bytes = new UTF8Encoding(false).GetBytes(text.ToString());

            // 🔴 先寫暫存檔再換過去：整份重寫的檔案寫到一半崩潰會留下半份清單，
            //    而半份清單與「真的只剩這些」長得一模一樣。
            var temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(Utf8Bom, 0, Utf8Bom.Length);
                stream.Write(bytes, 0, bytes.Length);
            }

            File.Move(temp, path, true);
        }

        private static int CompareForFile(PendingActionRow a, PendingActionRow b)
        {
            var c = a.Kind.CompareTo(b.Kind);
            if (c != 0)
                return c;
            c = a.ItemId.CompareTo(b.ItemId);
            if (c != 0)
                return c;
            c = a.Hq.CompareTo(b.Hq);
            if (c != 0)
                return c;
            c = a.RetainerId.CompareTo(b.RetainerId);
            return c != 0 ? c : a.Slot.CompareTo(b.Slot);
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
                    $"[Marketbuddy] 待處理清單寫入失敗（{FileName}）；這一輪的變動只留在記憶體裡，" +
                    "重開遊戲就會消失。本工作階段不再重複回報這個錯誤。");
            }
            catch
            {
                // 記錄失敗絕不能中斷任何流程。
            }
        }

        // =====================================================================
        //  格式化與解析
        // =====================================================================

        private static string Format(PendingActionRow row)
            => string.Join(",",
                row.AtUtc.ToString(TimestampFormat, CultureInfo.InvariantCulture),
                ((int)row.Kind).ToString(CultureInfo.InvariantCulture),
                row.ItemId.ToString(CultureInfo.InvariantCulture),
                row.Hq ? "1" : "0",
                row.RetainerId.ToString(CultureInfo.InvariantCulture),
                PriceSurveyLog.Escape(row.RetainerName),
                row.Slot.ToString(CultureInfo.InvariantCulture),
                row.CurrentPrice.ToString(CultureInfo.InvariantCulture),
                row.SuggestedPrice.ToString(CultureInfo.InvariantCulture),
                PriceSurveyLog.Escape(row.SuggestionSource),
                PriceSurveyLog.Escape(row.SuggestionWorld),
                row.SuggestionAtUtc == DateTime.MinValue
                    ? string.Empty
                    : row.SuggestionAtUtc.ToString(TimestampFormat, CultureInfo.InvariantCulture),
                row.Skipped ? "1" : "0");

        /// <summary>
        /// 整份讀回來。🔴 <b>只從執行緒池呼叫。</b>
        /// 檔案不存在或讀不動時回空清單——兩者對呼叫端的處置相同，但後者會寫一行 Information。
        /// </summary>
        private static List<PendingActionRow> ReadAll()
        {
            var result = new List<PendingActionRow>();
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

                // 🔴 FileShare.Delete 是必要的：存檔走的是「暫存檔 → File.Move 覆蓋」，
                //    而 Windows 的 MoveFileEx 在目標被別人開著又沒給 Delete 共用權時會失敗。
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
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
                Log.Information(e, $"[Marketbuddy] 待處理清單讀取失敗（{FileName}），畫面會顯示成沒有待處理項目。");
            }

            return result;
        }

        /// <summary>
        /// 解析一列。標題列與任何壞掉的列都靜默略過——這是使用者可以自己編輯的檔案，
        /// 一列壞掉不該讓整份清單消失。
        /// </summary>
        private static bool TryParse(string line, out PendingActionRow row)
        {
            row = default;
            var fields = PriceSurveyLog.SplitCsv(line);
            if (fields.Count < FieldCount)
                return false;

            if (!DateTime.TryParseExact(fields[0], TimestampFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
                return false;
            if (!int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kind))
                return false;
            if (kind is < (int)PendingActionKind.PriceCap or > (int)PendingActionKind.PriceAnomaly)
                return false;
            if (!uint.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemId)
                || itemId == 0)
                return false;
            if (!int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hq))
                return false;
            if (!ulong.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var retainerId))
                return false;
            if (!short.TryParse(fields[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var slot))
                return false;
            if (!long.TryParse(fields[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var current))
                return false;
            if (!long.TryParse(fields[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out var suggested))
                return false;
            if (!int.TryParse(fields[12], NumberStyles.Integer, CultureInfo.InvariantCulture, out var skipped))
                return false;

            var suggestedAt = DateTime.MinValue;
            if (fields[11].Length > 0
                && DateTime.TryParseExact(fields[11], TimestampFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var sAt))
                suggestedAt = sAt;

            row = new PendingActionRow(at, (PendingActionKind)kind, itemId, hq != 0, retainerId, fields[5],
                slot, current, suggested, fields[9], fields[10], suggestedAt, skipped != 0);
            return true;
        }
    }
}
