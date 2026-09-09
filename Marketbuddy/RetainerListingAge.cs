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
    /// <summary>一筆掛售在架上多久了。</summary>
    /// <param name="SinceUtc">從什麼時候起算。</param>
    /// <param name="Exact">
    /// true＝我們親眼看著它從「不在架上」變成「在架上」（含我們自己掛上去的），所以起點是精確的。
    /// false＝這個功能開始看之前它就已經在架上了，起點是拿外掛載入時間<b>暫定</b>的。
    /// </param>
    internal readonly record struct ListingAge(DateTime SinceUtc, bool Exact);

    /// <summary>
    /// 「這件東西掛在架上多久了」的紀錄。
    ///
    /// <para>
    /// 🔴 遊戲不會告訴我們一筆掛售是什麼時候掛上去的，所以這裡只有兩種來源，而且<b>畫面上要分得出來</b>：
    /// <list type="number">
    /// <item>我們親眼看著它出現（前一次快照沒有它、這一次有了）⇒ <b>精確</b>。</item>
    /// <item>我們第一次看到這位僱員時它就已經在架上 ⇒ 拿<b>外掛載入時間</b>當暫定起點。</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// 🔴🔴 <b>暫定值只在「完全沒有任何紀錄」時寫一次。</b>寫成「每次載入都蓋成載入時間」的話，
    /// 每次重開遊戲年齡就歸零 ⇒ 永遠顯示「剛掛上去」，而且看起來完全正常（有數字、會變、不報錯）。
    /// 判準只有一條：<b>有紀錄就不要動它</b>。
    /// </para>
    ///
    /// <para>
    /// 🔑 <b>比對鍵是 <c>(僱員, 道具, 品質)</c>，刻意不含價格也不含件數</b>：
    /// 重掛只是換價格、賣掉一部分只是件數變少，兩者都是「同一批東西還在架上」，
    /// 把它們算成重新開始會讓這個數字失去意義。所以
    /// <b>「掛 3 件賣掉 1 件」之後剩下的 2 件沿用原本的起點</b>；
    /// 只有整個 <c>(僱員, 道具, 品質)</c> 從架上消失、之後又重新出現時才會重新計時。
    /// ⚠️ 代價是：同一件道具分兩次掛上去（第一批還沒賣完就又補掛），第二批會沿用第一批的起點。
    /// 那是刻意的取捨——容器裡本來就分不出「哪一格是哪一次掛的」（格號會壓縮，見
    /// <see cref="RetainerMarketSnapshot"/>），硬要分只會分錯。
    /// </para>
    ///
    /// <para>
    /// 🔴 這是<b>純紀錄</b>：不會因為「掛太久」自動降價或自動下架。
    /// </para>
    ///
    /// <para>
    /// 執行緒：形狀比照 <see cref="RetainerSalesLog"/>——鎖只用來碰記憶體裡的字典，
    /// 真正的 I/O 在鎖外、在執行緒池上。<see cref="Observe"/> 只從 framework 執行緒呼叫。
    /// </para>
    /// </summary>
    internal static class RetainerListingAge
    {
        internal const string FileName = "retainer_listing_age.csv";

        private const string Header = "retainerId,itemId,hq,sinceUtc,exact";

        private const int FieldCount = 5;

        private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";

        private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

        /// <summary>🔴 只在持有這把鎖時碰 <see cref="Entries"/>／<see cref="RetainersSeen"/>。鎖內不做 I/O、不寫 log。</summary>
        private static readonly object Gate = new();

        /// <summary>(僱員, 道具, 品質) → 起算時間。itemId 0 不出現在這裡，它記在 <see cref="RetainersSeen"/>。</summary>
        private static readonly Dictionary<(ulong RetainerId, uint ItemId, bool Hq), ListingAge> Entries = [];

        /// <summary>我們第一次看到這位僱員的掛售清單是什麼時候（itemId 0 的那種列）。</summary>
        private static readonly Dictionary<ulong, DateTime> RetainersSeen = [];

        /// <summary>外掛這一次載入的時間，暫定值就用它。</summary>
        private static DateTime pluginLoadedAtUtc = DateTime.UtcNow;

        private static string? cachedFilePath;

        private static int loadStarted;
        private static Task<(List<Row> Rows, bool Ok)>? loadTask;
        private static bool loaded;

        private static long revision;
        private static long savedRevision;
        private static int saving;
        private static int writeFailureLogged;

        internal readonly record struct Row(ulong RetainerId, uint ItemId, bool Hq, DateTime SinceUtc, bool Exact);

        internal static string FilePath
            => cachedFilePath ??= Path.Combine(PluginInterface.ConfigDirectory.FullName, FileName);

        /// <summary>紀錄檔讀回來了沒。沒讀完之前一律不 seed，否則會把既有的起點蓋掉。</summary>
        internal static bool Loaded => loaded;

        internal static void Init()
        {
            pluginLoadedAtUtc = DateTime.UtcNow;
        }

        /// <summary>🔴 只從 framework 執行緒呼叫。</summary>
        internal static void BeginLoad()
        {
            if (Interlocked.CompareExchange(ref loadStarted, 1, 0) != 0)
                return;
            loadTask = Task.Run(ReadAll);
        }

        /// <summary>🔴 只從 framework 執行緒呼叫。</summary>
        internal static void PumpLoad()
        {
            var task = loadTask;
            if (task == null || !task.IsCompleted)
                return;
            loadTask = null;

            try
            {
                var (rows, _) = task.GetAwaiter().GetResult();
                lock (Gate)
                {
                    foreach (var row in rows)
                    {
                        if (row.ItemId == 0)
                            RetainersSeen[row.RetainerId] = row.SinceUtc;
                        else
                            Entries[(row.RetainerId, row.ItemId, row.Hq)] =
                                new ListingAge(row.SinceUtc, row.Exact);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Information(e, $"[Marketbuddy] 掛售年齡：讀取 {FileName} 失敗，這一輪會從頭建立。");
            }

            loaded = true;
        }

        /// <summary>
        /// 這一格的掛售年齡；沒有紀錄回 null（<b>不是 0，也不是「剛剛」</b>）。
        /// </summary>
        internal static ListingAge? Get(ulong retainerId, uint itemId, bool hq)
        {
            if (retainerId == 0 || itemId == 0)
                return null;
            lock (Gate)
            {
                return Entries.TryGetValue((retainerId, itemId, hq), out var age) ? age : null;
            }
        }

        /// <summary>
        /// 我們<b>第一次</b>看到這位僱員的出售品清單是什麼時候；<c>null</c>＝從來沒看過。
        ///
        /// <para>
        /// 🔑 這是「我們對這位僱員的觀察從什麼時候開始」的唯一權威來源，也是
        /// 「這件東西賣掉過嗎」那類統計唯一站得住腳的地基：<b>回 null 時「賣出 0 次」
        /// 是沒有根據的</b>，畫面上必須畫成灰色的 <c>?</c> 而不是 0——兩者長得一模一樣，
        /// 而後者會讓使用者做出相反的決定。
        /// </para>
        /// </summary>
        internal static DateTime? FirstSeen(ulong retainerId)
        {
            if (retainerId == 0)
                return null;
            lock (Gate)
            {
                return RetainersSeen.TryGetValue(retainerId, out var at) ? at : null;
            }
        }

        /// <summary>
        /// 所有僱員裡最早的那個「第一次看到」；<c>null</c>＝一位僱員都還沒看過。
        /// 也就是整份紀錄的<b>觀察起點</b>（下界）。
        /// </summary>
        internal static DateTime? EarliestSeen()
        {
            lock (Gate)
            {
                DateTime? earliest = null;
                foreach (var at in RetainersSeen.Values)
                {
                    if (earliest == null || at < earliest)
                        earliest = at;
                }

                return earliest;
            }
        }

        /// <summary>
        /// 拿一份「這位僱員此刻架上有什麼」的快照去更新紀錄。
        /// 🔴 只從 framework 執行緒呼叫；<see cref="Loaded"/> 為 false 時直接不做事
        /// （還沒讀完就 seed 會把既有起點蓋成今天）。
        /// </summary>
        internal static void Observe(RetainerMarketSnapshot snapshot)
        {
            if (!loaded || snapshot.RetainerId == 0)
                return;

            var now = snapshot.AtUtc == DateTime.MinValue ? DateTime.UtcNow : snapshot.AtUtc;
            var present = new HashSet<(ulong, uint, bool)>();
            foreach (var listing in snapshot.Listings)
            {
                if (listing.ItemId == 0)
                    continue;
                present.Add((snapshot.RetainerId, listing.ItemId, listing.Hq));
            }

            var changed = false;
            lock (Gate)
            {
                // 這一輪是不是第一次看到這位僱員？是的話，架上現有的東西全部只能給暫定值。
                var firstLook = !RetainersSeen.ContainsKey(snapshot.RetainerId);
                if (firstLook)
                {
                    RetainersSeen[snapshot.RetainerId] = now;
                    changed = true;
                }

                foreach (var key in present)
                {
                    // 🔴 有紀錄就不要動它。這一條就是「每次載入都歸零」那個壞法的唯一防線。
                    if (Entries.ContainsKey(key))
                        continue;

                    Entries[key] = firstLook
                        // 我們第一次看到這位僱員時它就在架上了：起點只能暫定成外掛載入時間。
                        ? new ListingAge(pluginLoadedAtUtc, false)
                        // 上一次看這位僱員時還沒有它 ⇒ 這中間才掛上去的，起點是精確的。
                        : new ListingAge(now, true);
                    changed = true;
                }

                // 整個 (僱員, 道具, 品質) 從架上消失了 ⇒ 賣掉或下架了，紀錄跟著收掉，
                // 下次重新掛上去才會重新計時。
                List<(ulong, uint, bool)>? gone = null;
                foreach (var key in Entries.Keys)
                {
                    if (key.RetainerId != snapshot.RetainerId)
                        continue;
                    if (present.Contains(key))
                        continue;
                    (gone ??= []).Add(key);
                }

                if (gone != null)
                {
                    foreach (var key in gone)
                        Entries.Remove(key);
                    changed = true;
                }
            }

            if (!changed)
                return;

            Interlocked.Increment(ref revision);
            KickSave();
        }

        private static void KickSave()
        {
            if (Interlocked.CompareExchange(ref saving, 1, 0) != 0)
                return;
            _ = Task.Run(Save);
        }

        private static void Save()
        {
            var at = Interlocked.Read(ref revision);
            try
            {
                List<Row> rows;
                lock (Gate)
                {
                    rows = new List<Row>(Entries.Count + RetainersSeen.Count);
                    foreach (var (retainerId, seenAt) in RetainersSeen)
                        rows.Add(new Row(retainerId, 0, false, seenAt, true));
                    foreach (var (key, age) in Entries)
                        rows.Add(new Row(key.RetainerId, key.ItemId, key.Hq, age.SinceUtc, age.Exact));
                }

                // 🔴 I/O 在鎖外做。
                WriteAll(rows);
                Interlocked.Exchange(ref savedRevision, at);
            }
            catch (Exception e)
            {
                ReportWriteFailure(e);
                // 🔴 放棄這一代不重試：寫不進去的原因不會在下一毫秒消失，而「還有新東西就再踢一次」
                //    會把失敗變成燒 CPU 的死迴圈。
                Interlocked.Exchange(ref savedRevision, at);
            }
            finally
            {
                Volatile.Write(ref saving, 0);
                if (Interlocked.Read(ref revision) != Interlocked.Read(ref savedRevision))
                    KickSave();
            }
        }

        private static void WriteAll(List<Row> rows)
        {
            var path = FilePath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var text = new StringBuilder();
            text.Append(Header).Append("\r\n");
            foreach (var row in rows)
            {
                text.Append(string.Join(",",
                        row.RetainerId.ToString(CultureInfo.InvariantCulture),
                        row.ItemId.ToString(CultureInfo.InvariantCulture),
                        row.Hq ? "1" : "0",
                        row.SinceUtc.ToString(TimestampFormat, CultureInfo.InvariantCulture),
                        row.Exact ? "1" : "0"))
                    .Append("\r\n");
            }

            var bytes = new UTF8Encoding(false).GetBytes(text.ToString());
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

            try
            {
                Log.Information(e,
                    $"[Marketbuddy] 掛售年齡：寫入 {FileName} 失敗，重開遊戲之後那些起點會退回暫定值。" +
                    "本工作階段不再重複回報這個錯誤。");
            }
            catch
            {
                // 記錄失敗絕不能中斷任何流程。
            }
        }

        /// <summary>🔴 只從執行緒池呼叫。</summary>
        private static (List<Row> Rows, bool Ok) ReadAll()
        {
            var rows = new List<Row>();
            string path;
            try
            {
                path = FilePath;
            }
            catch
            {
                return (rows, false);
            }

            try
            {
                if (!File.Exists(path))
                    return (rows, true);

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                while (reader.ReadLine() is { } line)
                {
                    if (line.Length == 0)
                        continue;
                    if (TryParse(line, out var row))
                        rows.Add(row);
                }
            }
            catch (Exception e)
            {
                Log.Information(e, $"[Marketbuddy] 掛售年齡：讀取 {FileName} 時發生例外。");
                return (rows, false);
            }

            return (rows, true);
        }

        /// <summary>
        /// 解析一列。標題列與壞掉的列靜默略過。
        /// ⚠️ 只看前 <see cref="FieldCount"/> 個欄位：以後多加欄位時，舊版讀新檔不會壞。
        /// </summary>
        private static bool TryParse(string line, out Row row)
        {
            row = default;
            var fields = PriceSurveyLog.SplitCsv(line);
            if (fields.Count < FieldCount)
                return false;
            if (!ulong.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var retainerId)
                || retainerId == 0)
                return false;
            if (!uint.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemId))
                return false;
            if (!int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hq))
                return false;
            if (!DateTime.TryParseExact(fields[3], TimestampFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var since))
                return false;
            if (!int.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var exact))
                return false;

            row = new Row(retainerId, itemId, hq != 0, since, exact != 0);
            return true;
        }
    }
}
