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
    /// 一列「差異事件」有多少把握。
    /// ⚠️ 刻意給 <see cref="Unknown"/> 明確的 0：沒有零值的列舉會讓 <c>default</c>
    /// 落在一個無效值上，而那種壞法是靜默的。
    /// </summary>
    internal enum RetainerSaleConfidence
    {
        /// <summary>
        /// 低信心：那些掛單真的不見了，但金幣對不上，所以<b>不知道</b>是賣掉還是被手動下架。
        /// 🔴 這一列不可以被算進任何「收益」統計裡。
        /// </summary>
        Unknown = 0,

        /// <summary>
        /// 高信心：僱員錢包的增量對得上這批掛單的售價，所以判定為賣出。
        /// </summary>
        Sold = 1,

        /// <summary>
        /// 這是 <b>Marketbuddy 自己做的下架</b>（批次下架或重掛時的門檻下架），不是賣出。
        /// 留在記錄裡是為了讓帳對得起來——少了它，同一件事會以「消失」的樣子再出現一次。
        /// </summary>
        Delisted = 2,
    }

    /// <summary>
    /// 僱員市場容器的一次「差異事件」：兩次快照之間少掉的東西。
    /// 🔑 「不知道」一律用 <c>-1</c> 表示，<b>不是 0</b>。0 在單價與實收欄位是一個合法但
    /// 荒謬的值，把「沒算出來」畫成 0 會讓使用者以為那筆真的是 0 gil。
    /// 讀出來之後 UI 必須把 -1 畫成灰色的 <c>?</c>。
    /// 🔴 <b>這裡沒有「本週收益」這種東西。</b>這份記錄是事件清單，不是帳本：
    /// 低信心的列本來就可能不是賣出，把它們加總成一個數字等於用一個自信的數字
    /// 蓋掉「我們其實不知道」。要做總計只能限定在 <see cref="RetainerSaleConfidence.Sold"/>，
    /// 而且必須同時把低信心的件數放在旁邊。
    /// </summary>
    /// <param name="AtUtc">發現這件事的時間（＝後一次快照的時間，UTC）。</param>
    /// <param name="PrevAtUtc">前一次快照的時間（UTC）；這一列描述的是這兩個時間之間發生的事。</param>
    /// <param name="RetainerId">哪一位僱員（比對一律用 id）。</param>
    /// <param name="RetainerName">那位僱員的名字，只給人看；空＝不知道。</param>
    /// <param name="ItemId">道具 id。</param>
    /// <param name="Hq">優質品還是普通品。</param>
    /// <param name="Quantity">少掉幾件。</param>
    /// <param name="UnitPrice">
    /// 少掉的那些原本掛多少錢（單價）；<c>-1</c>＝算不出來。
    /// 同一件道具同時掛在兩個不同價格、而少掉的量橫跨兩邊時就是這種情況。
    /// </param>
    /// <param name="Received">
    /// 實收（已扣稅）；<c>-1</c>＝不知道。只有 <see cref="RetainerSaleConfidence.Sold"/> 才有值。
    /// </param>
    /// <param name="Confidence">這一列有多少把握。</param>
    /// <param name="Basis">
    /// 金幣是對上哪一種算法：<c>net</c>＝售價扣掉市場稅、<c>gross</c>＝售價未扣稅、
    /// 空＝沒有對上任何一種。
    /// 🔑 兩種都試是刻意的：稅到底由賣方還是買方負擔，離線只能從遊戲文案推定，
    /// 這個欄位讓實機資料自己講話。
    /// </param>
    /// <param name="TaxPercent">計算時用的市場稅率（%）；<c>-1</c>＝不知道。</param>
    /// <param name="GilDelta">
    /// 兩次快照之間僱員錢包的增減；<c>long.MinValue</c>＝其中一次讀不到錢包。
    /// </param>
    internal readonly record struct RetainerSaleRow(
        DateTime AtUtc,
        DateTime PrevAtUtc,
        ulong RetainerId,
        string RetainerName,
        uint ItemId,
        bool Hq,
        int Quantity,
        long UnitPrice,
        long Received,
        RetainerSaleConfidence Confidence,
        string Basis,
        int TaxPercent,
        long GilDelta)
    {
        /// <summary>錢包讀得到嗎（<see cref="GilDelta"/> 有沒有意義）。</summary>
        public bool HasGilDelta => GilDelta != long.MinValue;
    }

    /// <summary>
    /// 僱員銷售差異事件的 CSV 記錄檔（追加寫入），寫在 Marketbuddy 自己的設定目錄底下。
    /// 執行緒：形狀逐字比照 <see cref="PriceSurveyLog"/> —— <b>鎖只用來把待寫清單拍快照／
    /// 清空</b>，真正的 I/O 在鎖外、在執行緒池上做。鎖內絕不做 I/O、不寫 log、
    /// 不呼叫別的外掛、不碰 ImGui。
    /// 🔑 <b>另外留一份「這個工作階段寫過的列」在記憶體裡</b>：追加是非同步的，
    /// UI 一發現有新事件就去讀檔的話，很可能讀在 flush 之前，結果是「明明剛剛才記到，
    /// 畫面上卻沒有」。UI 把檔案列與這份記憶體列<b>用格式化後的整行字串去重</b>再合併，
    /// 就不會有那個空窗（<see cref="Format"/> 是決定性的，同一列一定產生同一行）。
    /// </summary>
    internal static class RetainerSalesLog
    {
        internal const string FileName = "retainer_sales.csv";

        private const string Header =
            "atUtc,prevAtUtc,retainerId,retainerName,itemId,hq,quantity,unitPrice,received," +
            "confidence,basis,taxPercent,gilDelta";

        private const int FieldCount = 13;

        private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";

        /// <summary>記憶體裡最多留幾列（只給 UI 補空窗用，不是資料的家）。</summary>
        private const int MaxSessionRows = 1000;

        private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

        /// <summary>待寫清單。🔴 只在持有 <see cref="Gate"/> 時碰它。</summary>
        private static readonly List<RetainerSaleRow> Pending = [];

        /// <summary>這個工作階段寫過的列。🔴 只在持有 <see cref="Gate"/> 時碰它。</summary>
        private static readonly List<RetainerSaleRow> Session = [];

        /// <summary>🔴 鎖內只碰上面那兩個清單：不做 I/O、不寫 log、不呼叫任何別的外掛。</summary>
        private static readonly object Gate = new();

        /// <summary>單一飛行閘：0＝沒有 flush 工作在跑，1＝有。</summary>
        private static int flushing;

        private static int writeFailureLogged;

        /// <summary>每追加一列 +1。UI 拿它判斷「要不要重新讀檔」。</summary>
        private static long revision;

        internal static long Revision => Volatile.Read(ref revision);

        /// <summary>
        /// 記錄檔的完整路徑。
        /// 🔴 只算一次：<c>PluginInterface.ConfigDirectory</c> 每次存取都會去檔案系統確認
        /// （不存在就建），而畫面上的 tooltip 每一幀都會取這個值——那等於每幀一次磁碟操作。
        /// </summary>
        internal static string FilePath
            => cachedFilePath ??= Path.Combine(PluginInterface.ConfigDirectory.FullName, FileName);

        private static string? cachedFilePath;

        /// <summary>追加一列。可以從任何執行緒呼叫；實際寫檔一定在執行緒池上。</summary>
        internal static void Append(RetainerSaleRow row)
        {
            lock (Gate)
            {
                Pending.Add(row);
                Session.Add(row);
                if (Session.Count > MaxSessionRows)
                    Session.RemoveRange(0, Session.Count - MaxSessionRows);
            }

            Interlocked.Increment(ref revision);
            Kick();
        }

        /// <summary>這個工作階段寫過的列的複本（UI 補讀檔空窗用）。</summary>
        internal static List<RetainerSaleRow> SessionRows()
        {
            lock (Gate)
            {
                return new List<RetainerSaleRow>(Session);
            }
        }

        private static void Kick()
        {
            // 已經有一個 flush 在跑就不再開第二個；它離開之前會再看一次待寫清單。
            if (Interlocked.CompareExchange(ref flushing, 1, 0) != 0)
                return;
            Task.Run(Flush);
        }

        private static void Flush()
        {
            try
            {
                while (true)
                {
                    List<RetainerSaleRow> batch;
                    lock (Gate)
                    {
                        if (Pending.Count == 0)
                            break;
                        batch = new List<RetainerSaleRow>(Pending);
                        Pending.Clear();
                    }

                    // 🔴 I/O 在鎖外做。
                    WriteBatch(batch);
                }
            }
            catch (Exception e)
            {
                ReportWriteFailure(e);
            }
            finally
            {
                Volatile.Write(ref flushing, 0);

                var again = false;
                lock (Gate)
                {
                    again = Pending.Count > 0;
                }

                if (again)
                    Kick();
            }
        }

        private static void WriteBatch(List<RetainerSaleRow> batch)
        {
            var path = FilePath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var needHeader = !File.Exists(path) || new FileInfo(path).Length == 0;

            var text = new StringBuilder();
            if (needHeader)
                text.Append(Header).Append("\r\n");
            foreach (var row in batch)
                text.Append(Format(row)).Append("\r\n");

            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            if (needHeader)
                stream.Write(Utf8Bom, 0, Utf8Bom.Length);
            var bytes = new UTF8Encoding(false).GetBytes(text.ToString());
            stream.Write(bytes, 0, bytes.Length);
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
                    $"[Marketbuddy] 僱員銷售記錄檔寫入失敗（{FileName}）；這一輪的事件只留在畫面上，" +
                    "重開遊戲就會消失。本工作階段不再重複回報這個錯誤。");
            }
            catch
            {
                // 記錄失敗絕不能中斷任何流程。
            }
        }

        /// <summary>
        /// 一列的字串形式。<b>決定性</b>：同一列永遠產生同一行，所以可以直接拿它當去重的鍵。
        /// </summary>
        internal static string Format(RetainerSaleRow row)
            => string.Join(",",
                row.AtUtc.ToString(TimestampFormat, CultureInfo.InvariantCulture),
                row.PrevAtUtc == DateTime.MinValue
                    ? string.Empty
                    : row.PrevAtUtc.ToString(TimestampFormat, CultureInfo.InvariantCulture),
                row.RetainerId.ToString(CultureInfo.InvariantCulture),
                PriceSurveyLog.Escape(row.RetainerName),
                row.ItemId.ToString(CultureInfo.InvariantCulture),
                row.Hq ? "1" : "0",
                row.Quantity.ToString(CultureInfo.InvariantCulture),
                row.UnitPrice.ToString(CultureInfo.InvariantCulture),
                row.Received.ToString(CultureInfo.InvariantCulture),
                ((int)row.Confidence).ToString(CultureInfo.InvariantCulture),
                PriceSurveyLog.Escape(row.Basis),
                row.TaxPercent.ToString(CultureInfo.InvariantCulture),
                row.HasGilDelta ? row.GilDelta.ToString(CultureInfo.InvariantCulture) : string.Empty);

        /// <summary>
        /// 把整份記錄檔讀回來。🔴 <b>只從執行緒池呼叫</b>（UI 用 <c>Task.Run</c> 包起來）。
        /// 檔案不存在或讀不動時回空清單——「沒有資料」與「讀失敗」對呼叫端的處置相同，
        /// 但後者會寫一行 Information。
        /// </summary>
        internal static List<RetainerSaleRow> LoadAll()
        {
            var result = new List<RetainerSaleRow>();
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

                // FileShare.ReadWrite：我們自己的 flush 工作可能正開著它在追加。
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
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
                Log.Information(e, $"[Marketbuddy] 僱員銷售記錄檔讀取失敗（{FileName}），銷售畫面會顯示成沒有資料。");
            }

            return result;
        }

        /// <summary>
        /// 解析一列。標題列與任何壞掉的列都靜默略過——這是使用者可以自己編輯的檔案，
        /// 一列壞掉不該讓整份資料消失。
        /// </summary>
        private static bool TryParse(string line, out RetainerSaleRow row)
        {
            row = default;
            var fields = PriceSurveyLog.SplitCsv(line);
            if (fields.Count < FieldCount)
                return false;

            if (!DateTime.TryParseExact(fields[0], TimestampFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
                return false;

            var prevAt = DateTime.MinValue;
            if (fields[1].Length > 0
                && DateTime.TryParseExact(fields[1], TimestampFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var pAt))
                prevAt = pAt;

            if (!ulong.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var retainerId))
                return false;
            if (!uint.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemId)
                || itemId == 0)
                return false;
            if (!int.TryParse(fields[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hq))
                return false;
            if (!int.TryParse(fields[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var quantity))
                return false;
            if (!long.TryParse(fields[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var unitPrice))
                return false;
            if (!long.TryParse(fields[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out var received))
                return false;
            if (!int.TryParse(fields[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out var confidence))
                return false;
            if (confidence is < (int)RetainerSaleConfidence.Unknown or > (int)RetainerSaleConfidence.Delisted)
                return false;
            if (!int.TryParse(fields[11], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tax))
                return false;

            var gilDelta = long.MinValue;
            if (fields[12].Length > 0
                && long.TryParse(fields[12], NumberStyles.Integer, CultureInfo.InvariantCulture, out var gd))
                gilDelta = gd;

            row = new RetainerSaleRow(at, prevAt, retainerId, fields[3], itemId, hq != 0, quantity,
                unitPrice, received, (RetainerSaleConfidence)confidence, fields[10], tax, gilDelta);
            return true;
        }
    }
}
