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
    /// 跨世界價格巡檢的一列記錄。
    ///
    /// <para>
    /// 🔑 「不知道」一律用 <c>-1</c> 表示，**不是 0**。0 在價格欄位是一個合法但荒謬的值，
    /// 把「沒查到」畫成 0 會讓使用者以為某個世界真的有人用 0 gil 在賣。
    /// 讀出來之後 UI 必須把 -1 畫成 <c>?</c> 或灰字，不可以畫成 0。
    /// </para>
    /// </summary>
    /// <param name="AtUtc">這一列是什麼時候寫下的（UTC）。</param>
    /// <param name="WorldId">查詢當下所在的世界 id。</param>
    /// <param name="WorldName">同上的世界名（寫進檔案是為了事後看檔的人，程式一律用 id 比對）。</param>
    /// <param name="ItemId">道具 id。</param>
    /// <param name="Hq">這一列講的是我方**優質品**掛單還是普通品掛單。</param>
    /// <param name="OurPrice">我方在這個品質上的**最低**掛售單價；不知道時 -1。</param>
    /// <param name="LowestNq">該世界普通品最低單價；沒有掛單或不知道時 -1。</param>
    /// <param name="LowestHq">該世界優質品最低單價；沒有掛單或不知道時 -1。</param>
    /// <param name="LowestIsOurs">同品質的最低價是不是我方僱員掛的：1 是、0 不是、-1 不知道。</param>
    /// <param name="ListingCount">這次查到的掛單筆數（第一頁）；不知道時 -1。</param>
    /// <param name="Source">資料怎麼來的：<c>live</c>＝這次真的向伺服器問過、<c>cache</c>＝用了外掛的市場快取。</param>
    /// <param name="GateIntervalMs">送出當下 <see cref="MarketRequestGate"/> 的間隔，事後判讀節奏用。</param>
    /// <param name="Verdict">這次查詢的結局：<c>ok</c>／<c>empty</c>／<c>refused</c>／<c>timeout</c>。</param>
    internal readonly record struct PriceSurveyRow(
        DateTime AtUtc,
        uint WorldId,
        string WorldName,
        uint ItemId,
        bool Hq,
        long OurPrice,
        long LowestNq,
        long LowestHq,
        int LowestIsOurs,
        int ListingCount,
        string Source,
        int GateIntervalMs,
        string Verdict)
    {
        /// <summary>這一列在自己的品質上，該世界的最低價（不知道時 -1）。</summary>
        public long LowestForQuality => Hq ? LowestHq : LowestNq;

        /// <summary>
        /// 被壓價的幅度（我方價 − 別人的最低價）。
        /// 最低價是我們自己的、或任一邊未知時回 -1（＝「不知道」，不是 0）。
        /// </summary>
        public long UndercutBy
        {
            get
            {
                if (LowestIsOurs != 0)
                    return -1;
                var lowest = LowestForQuality;
                if (lowest < 0 || OurPrice < 0)
                    return -1;
                return OurPrice - lowest;
            }
        }
    }

    /// <summary>
    /// 巡檢結果的 CSV 記錄檔（追加寫入）。
    ///
    /// <para>
    /// 🔴 <b>刻意寫在 Marketbuddy 自己的設定目錄底下</b>，不碰 InventoryTools 的
    /// <c>market_cache.csv</c>：那是別的外掛的資料檔，我們沒有它的格式契約，
    /// 寫進去只會在對方下一次整份覆寫時靜默消失（而且可能弄壞對方的狀態）。
    /// </para>
    ///
    /// <para>
    /// 🔴 <b>寫檔一律在 framework 執行緒之外</b>：巡檢每一件查完就要記一列，
    /// 在遊戲主執行緒上做檔案 I/O 會直接變成掉幀。這裡的形狀是
    /// 「鎖只用來把待寫清單拍快照／清空，真正的 I/O 在鎖外做」——
    /// 鎖內絕不做 I/O、不寫 log、不呼叫別的外掛。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 同一時間只會有一個 flush 工作在跑（<see cref="flushing"/> 用
    /// <see cref="Interlocked"/> 當單一飛行閘），所以不必為了「檔案不要被兩條執行緒同時開啟」
    /// 而在 I/O 期間持鎖。收尾時會再看一次待寫清單，避免「剛好在放掉閘門與producer 加入之間」
    /// 掉一批資料。
    /// </para>
    /// </summary>
    internal static class PriceSurveyLog
    {
        internal const string FileName = "price_survey.csv";

        private const string Header =
            "timestampUtc,worldId,worldName,itemId,hq,ourPrice,lowestNq,lowestHq,lowestIsOurs,listingCount,source,gateIntervalMs,verdict";

        private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";

        private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

        /// <summary>待寫清單。🔴 只在持有 <see cref="PendingGate"/> 時碰它。</summary>
        private static readonly List<PriceSurveyRow> Pending = [];

        /// <summary>🔴 鎖內只碰 <see cref="Pending"/>：不做 I/O、不寫 log、不呼叫任何別的外掛。</summary>
        private static readonly object PendingGate = new();

        /// <summary>單一飛行閘：0＝沒有 flush 工作在跑，1＝有。</summary>
        private static int flushing;

        /// <summary>寫檔失敗只吵一次，避免每一件都刷一行。</summary>
        private static int writeFailureLogged;

        /// <summary>記錄檔的完整路徑。</summary>
        internal static string FilePath
            => Path.Combine(PluginInterface.ConfigDirectory.FullName, FileName);

        /// <summary>
        /// 追加一列。可以從任何執行緒呼叫；實際寫檔一定在執行緒池上。
        /// </summary>
        internal static void Append(PriceSurveyRow row)
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
                    List<PriceSurveyRow> batch;
                    lock (PendingGate)
                    {
                        if (Pending.Count == 0)
                            break;
                        batch = new List<PriceSurveyRow>(Pending);
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

                // 放掉閘門與上面那次「清單是空的」判斷之間可能又有人加了東西進來。
                var again = false;
                lock (PendingGate)
                {
                    again = Pending.Count > 0;
                }

                if (again)
                    Kick();
            }
        }

        private static void WriteBatch(List<PriceSurveyRow> batch)
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

            // 要使用者回報的診斷寫 Information（他的記錄等級收得到，而且不會被 Debug 淹沒）。
            try
            {
                Log.Information(
                    e,
                    $"[Marketbuddy] 巡檢記錄檔寫入失敗（{FileName}）；這一輪的結果只留在畫面上，不會落地。" +
                    "本工作階段不再重複回報這個錯誤。");
            }
            catch
            {
                // 記錄失敗絕不能中斷巡檢。
            }
        }

        private static string Format(PriceSurveyRow row)
            => string.Join(",",
                row.AtUtc.ToString(TimestampFormat, CultureInfo.InvariantCulture),
                row.WorldId.ToString(CultureInfo.InvariantCulture),
                Escape(row.WorldName),
                row.ItemId.ToString(CultureInfo.InvariantCulture),
                row.Hq ? "1" : "0",
                row.OurPrice.ToString(CultureInfo.InvariantCulture),
                row.LowestNq.ToString(CultureInfo.InvariantCulture),
                row.LowestHq.ToString(CultureInfo.InvariantCulture),
                row.LowestIsOurs.ToString(CultureInfo.InvariantCulture),
                row.ListingCount.ToString(CultureInfo.InvariantCulture),
                Escape(row.Source),
                row.GateIntervalMs.ToString(CultureInfo.InvariantCulture),
                Escape(row.Verdict));

        private static string Escape(string? value)
        {
            var s = value ?? string.Empty;
            if (s.IndexOfAny([',', '"', '\r', '\n']) < 0)
                return s;
            return "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }

        /// <summary>
        /// 把整份記錄檔讀回來。🔴 <b>只從執行緒池呼叫</b>（UI 用 <c>Task.Run</c> 包起來），
        /// 檔案可能有幾萬列。檔案不存在或讀不動時回空清單——「沒有資料」與「讀失敗」
        /// 對呼叫端的處置相同，但後者會寫一行 Information。
        /// </summary>
        internal static List<PriceSurveyRow> LoadAll()
        {
            var result = new List<PriceSurveyRow>();
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
                Log.Information(e, $"[Marketbuddy] 巡檢記錄檔讀取失敗（{FileName}），比價畫面會顯示成沒有資料。");
            }

            return result;
        }

        /// <summary>
        /// 解析一列。標題列（第一欄不是時間戳）與任何壞掉的列都靜默略過——
        /// 這是使用者可以自己編輯的檔案，一列壞掉不該讓整份資料消失。
        /// </summary>
        private static bool TryParse(string line, out PriceSurveyRow row)
        {
            row = default;
            var fields = SplitCsv(line);
            if (fields.Count < 13)
                return false;

            if (!DateTime.TryParseExact(fields[0], TimestampFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
                return false;
            if (!uint.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var worldId))
                return false;
            if (!uint.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemId))
                return false;
            if (!int.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hq))
                return false;
            if (!long.TryParse(fields[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ourPrice))
                return false;
            if (!long.TryParse(fields[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var lowestNq))
                return false;
            if (!long.TryParse(fields[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var lowestHq))
                return false;
            if (!int.TryParse(fields[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out var lowestIsOurs))
                return false;
            if (!int.TryParse(fields[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out var listingCount))
                return false;
            if (!int.TryParse(fields[11], NumberStyles.Integer, CultureInfo.InvariantCulture, out var gate))
                return false;

            row = new PriceSurveyRow(at, worldId, fields[2], itemId, hq != 0, ourPrice, lowestNq, lowestHq,
                lowestIsOurs, listingCount, fields[10], gate, fields[12]);
            return true;
        }

        private static List<string> SplitCsv(string line)
        {
            var fields = new List<string>(13);
            var current = new StringBuilder();
            var quoted = false;
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (quoted)
                {
                    if (c != '"')
                    {
                        current.Append(c);
                        continue;
                    }

                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                        continue;
                    }

                    quoted = false;
                    continue;
                }

                switch (c)
                {
                    case '"':
                        quoted = true;
                        continue;
                    case ',':
                        fields.Add(current.ToString());
                        current.Clear();
                        continue;
                    case '\r':
                        continue;
                    default:
                        current.Append(c);
                        continue;
                }
            }

            fields.Add(current.ToString());
            return fields;
        }
    }
}
