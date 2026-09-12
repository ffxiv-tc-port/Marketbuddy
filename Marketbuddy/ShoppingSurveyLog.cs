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
    /// 採購清單巡檢的一列：某一件想買的東西，在某一個世界、某一個時間點的行情。
    ///
    /// <para>
    /// 🔑 「不知道」一律 <c>-1</c>，<b>不是 0</b>。0 在價格與數量欄都是合法但荒謬的值，
    /// 把「沒查到」畫成 0 會讓人以為那個世界真的有人用 0 gil 在賣、或真的只剩 0 個。
    /// 讀出來之後 UI 必須把 -1 畫成 <c>?</c> 或灰字。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 數量是<b>伺服器回的那一頁</b>的合計，不是那個世界的全部庫存：市場板一次只回
    /// 一頁掛單。所以它回答的是「這裡至少買得到這麼多」，不是「這裡只有這麼多」。
    /// </para>
    /// </summary>
    /// <param name="AtUtc">這一列是什麼時候問到的（UTC）。</param>
    /// <param name="WorldId">查詢當下所在的世界 id。</param>
    /// <param name="WorldName">同上的世界名（程式一律用 id 比對，名字只給看檔的人）。</param>
    /// <param name="ItemId">道具 id。</param>
    /// <param name="LowestNq">普通品最低單價；沒人在賣或不知道時 -1。</param>
    /// <param name="NqQuantity">普通品掛單的合計件數；不知道時 -1。</param>
    /// <param name="LowestHq">優質品最低單價；沒人在賣或不知道時 -1。</param>
    /// <param name="HqQuantity">優質品掛單的合計件數；不知道時 -1。</param>
    /// <param name="ListingCount">這次看到的掛單筆數；不知道時 -1。</param>
    /// <param name="Source"><c>live</c>＝這次真的向伺服器問過、<c>cache</c>＝用了外掛的市場快取。</param>
    /// <param name="Verdict">這次查詢的結局：<c>ok</c>／<c>empty</c>／<c>refused</c>／<c>timeout</c>。</param>
    internal readonly record struct ShoppingSurveyRow(
        DateTime AtUtc,
        uint WorldId,
        string WorldName,
        uint ItemId,
        long LowestNq,
        long NqQuantity,
        long LowestHq,
        long HqQuantity,
        int ListingCount,
        string Source,
        string Verdict)
    {
        /// <summary>這次查詢有沒有得到答案（被拒絕與逾時都不算）。</summary>
        internal bool Answered => Verdict is "ok" or "empty";

        /// <summary>兩種品質裡比較便宜的那個是優質品。</summary>
        internal bool CheapestIsHq => LowestHq >= 0 && (LowestNq < 0 || LowestHq < LowestNq);

        /// <summary>這個世界買得到的最低單價（-1＝沒人在賣，或這次沒問到）。</summary>
        internal long CheapestPrice => CheapestIsHq ? LowestHq : LowestNq;

        /// <summary>最低價那一邊的合計件數（-1＝不知道）。</summary>
        internal long CheapestQuantity => CheapestIsHq ? HqQuantity : NqQuantity;
    }

    /// <summary>
    /// 採購清單的行情記錄檔（追加寫入）。
    /// 🔴 <b>刻意與 <see cref="PriceSurveyLog"/> 分開兩個檔</b>。<c>price_survey.csv</c> 的每一列
    /// 都帶著「我方掛價」與「最低價是不是我自己的」，而採購清單上的東西<b>我根本沒有掛</b>——
    /// 混進同一個檔只會讓比價分頁與待處理清單多出一堆 <c>ourPrice=-1</c> 的幽靈列。
    /// 🔴 形狀逐字沿用 <see cref="PriceSurveyLog"/>：同一個設定目錄、UTF-8 BOM ＋ CRLF、
    /// 同一份逸出／拆解器、追加寫入、<b>鎖只用來動待寫清單，真正的 I/O 在鎖外的執行緒池上做</b>。
    /// 鎖內絕不做 I/O、不寫 log、不碰 ImGui、不呼叫別的外掛。
    /// </summary>
    internal static class ShoppingSurveyLog
    {
        internal const string FileName = "shopping_survey.csv";

        private const string Header =
            "timestampUtc,worldId,worldName,itemId,lowestNq,nqQuantity,lowestHq,hqQuantity,listingCount,source,verdict";

        private const int FieldCount = 11;

        private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";

        private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

        /// <summary>待寫清單。🔴 只在持有 <see cref="PendingGate"/> 時碰它。</summary>
        private static readonly List<ShoppingSurveyRow> Pending = [];

        /// <summary>🔴 鎖內只碰 <see cref="Pending"/>：不做 I/O、不寫 log、不呼叫任何別的外掛。</summary>
        private static readonly object PendingGate = new();

        /// <summary>單一飛行閘：0＝沒有 flush 工作在跑，1＝有。</summary>
        private static int flushing;

        private static int writeFailureLogged;

        private static string? cachedFilePath;

        /// <summary>
        /// 記錄檔的完整路徑。
        /// 🔴 只算一次：<c>PluginInterface.ConfigDirectory</c> 每次存取都會去檔案系統確認。
        /// </summary>
        internal static string FilePath
            => cachedFilePath ??= Path.Combine(PluginInterface.ConfigDirectory.FullName, FileName);

        /// <summary>追加一列。任何執行緒都可以呼叫；實際寫檔一定在執行緒池上。</summary>
        internal static void Append(ShoppingSurveyRow row)
        {
            lock (PendingGate)
            {
                Pending.Add(row);
            }

            Kick();
        }

        /// <summary>還沒落地的列數（UI 顯示用）。</summary>
        internal static int PendingCount
        {
            get
            {
                lock (PendingGate)
                {
                    return Pending.Count;
                }
            }
        }

        private static void Kick()
        {
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
                    List<ShoppingSurveyRow> batch;
                    lock (PendingGate)
                    {
                        if (Pending.Count == 0)
                            break;
                        batch = new List<ShoppingSurveyRow>(Pending);
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
                lock (PendingGate)
                {
                    again = Pending.Count > 0;
                }

                if (again)
                    Kick();
            }
        }

        private static void WriteBatch(List<ShoppingSurveyRow> batch)
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

            try
            {
                Log.Information(
                    e,
                    $"[Marketbuddy] 採購清單記錄檔寫入失敗（{FileName}）；這一輪的結果只留在畫面上，不會落地。" +
                    "本工作階段不再重複回報這個錯誤。");
            }
            catch
            {
                // 記錄失敗絕不能中斷巡檢。
            }
        }

        private static string Format(ShoppingSurveyRow row)
            => string.Join(",",
                row.AtUtc.ToString(TimestampFormat, CultureInfo.InvariantCulture),
                row.WorldId.ToString(CultureInfo.InvariantCulture),
                PriceSurveyLog.Escape(row.WorldName),
                row.ItemId.ToString(CultureInfo.InvariantCulture),
                row.LowestNq.ToString(CultureInfo.InvariantCulture),
                row.NqQuantity.ToString(CultureInfo.InvariantCulture),
                row.LowestHq.ToString(CultureInfo.InvariantCulture),
                row.HqQuantity.ToString(CultureInfo.InvariantCulture),
                row.ListingCount.ToString(CultureInfo.InvariantCulture),
                PriceSurveyLog.Escape(row.Source),
                PriceSurveyLog.Escape(row.Verdict));

        /// <summary>
        /// 把整份記錄檔讀回來。🔴 <b>只從執行緒池呼叫</b>。
        /// 檔案不存在或讀不動時回空清單——後者會多寫一行 Information。
        /// </summary>
        internal static List<ShoppingSurveyRow> LoadAll()
        {
            var result = new List<ShoppingSurveyRow>();
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
                Log.Information(e, $"[Marketbuddy] 採購清單記錄檔讀取失敗（{FileName}），採購分頁會顯示成沒有資料。");
            }

            return result;
        }

        /// <summary>
        /// 解析一列。標題列與任何壞掉的列都靜默略過——這是使用者可以自己編輯的檔案，
        /// 一列壞掉不該讓整份資料消失。
        /// </summary>
        private static bool TryParse(string line, out ShoppingSurveyRow row)
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
            if (!uint.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemId))
                return false;
            if (!long.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var lowestNq))
                return false;
            if (!long.TryParse(fields[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var nqQuantity))
                return false;
            if (!long.TryParse(fields[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var lowestHq))
                return false;
            if (!long.TryParse(fields[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hqQuantity))
                return false;
            if (!int.TryParse(fields[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out var listingCount))
                return false;

            row = new ShoppingSurveyRow(at, worldId, fields[2], itemId, lowestNq, nqQuantity, lowestHq, hqQuantity,
                listingCount, fields[9], fields[10]);
            return true;
        }
    }
}
