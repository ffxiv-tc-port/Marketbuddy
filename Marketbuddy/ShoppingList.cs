using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Lumina.Excel.Sheets;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    /// <summary>採購清單裡的一件：道具，以及使用者寫在旁邊的「我想買幾個」（0＝沒寫）。</summary>
    internal readonly record struct ShoppingListItem(uint ItemId, int Wanted);

    /// <summary>
    /// 「我要買的東西」清單。
    /// 🔴 <b>刻意用一個純文字檔，而不是去讀別的外掛的私有檔案。</b>Artisan 的 IPC 沒有
    /// 「這張清單裡有什麼」的端點（<c>Artisan.IsListRunning</c> 那一組全是狀態與控制），
    /// AllaganTools 的 <c>GetCraftItems</c> 只回<b>成品</b>（<c>IsOutputItem</c>），
    /// 不是「要去買的材料」。猜別人的存檔格式的失敗形式是靜默拿到錯的清單，
    /// 然後在每一個世界多查幾百件——那比請使用者貼一份清單糟得多。
    /// 執行緒：<see cref="BeginLoad"/> 把<b>讀檔</b>丟到執行緒池，
    /// <see cref="PumpLoad"/> <b>只能在 framework 執行緒上呼叫</b>——名稱轉 id 要讀
    /// Lumina 的 <c>Item</c> 表，那份索引也只在那裡建。整條路徑<b>只讀不寫</b>，
    /// 唯一會寫檔的是使用者自己按下「建立範例清單檔」（<see cref="TryCreateTemplate"/>）。
    /// </summary>
    internal static class ShoppingList
    {
        internal const string FileName = "shopping_list.txt";

        /// <summary>清單再長也不該超過這個數；超過的部分不讀，並在畫面上說清楚。</summary>
        internal const int MaxEntries = 500;

        /// <summary>前置數量：<c>12x 銅礦</c>／<c>12 x 銅礦</c>／<c>12× 銅礦</c>。</summary>
        private static readonly Regex LeadingQuantity =
            new(@"^(\d{1,6})\s*[xX×\*]\s*(.+)$", RegexOptions.CultureInvariant);

        /// <summary>
        /// 後置數量：<c>銅礦 x12</c>／<c>銅礦 ×12</c>。
        /// 刻意要求那個乘號——少了它，名字結尾本來就有數字的道具會被切掉一段。
        /// </summary>
        private static readonly Regex TrailingQuantity =
            new(@"^(.+?)[\s,]+[xX×\*]\s*(\d{1,6})$", RegexOptions.CultureInvariant);

        /// <summary>逗號分隔：<c>銅礦,12</c>。</summary>
        private static readonly Regex CommaQuantity =
            new(@"^(.+?)\s*,\s*(\d{1,6})$", RegexOptions.CultureInvariant);

        private static string? cachedFilePath;

        /// <summary>1＝有人要求重讀（或還沒讀過第一次）。</summary>
        private static int reloadRequested = 1;

        private static volatile Task<RawFile>? readTask;

        // 🔑 三個都是 volatile：framework 執行緒整個換掉、繪製執行緒只讀。
        //    永遠不就地修改已經公佈出去的 list，所以畫面拿到的不是舊份就是新份，
        //    不會看到一份改到一半的清單。
        private static volatile List<ShoppingListItem> items = [];
        private static volatile List<string> unresolved = [];
        private static volatile List<string> unmarketable = [];

        /// <summary>名稱→道具 id 的索引。🔴 只在 framework 執行緒上建立與讀取。</summary>
        private static Dictionary<string, uint>? nameIndex;

        /// <summary>
        /// 清單檔的完整路徑。
        /// 🔴 只算一次：<c>PluginInterface.ConfigDirectory</c> 每次存取都會去檔案系統確認
        /// （不存在就建），而畫面上每一幀都會取這個值。
        /// </summary>
        internal static string FilePath
            => cachedFilePath ??= Path.Combine(PluginInterface.ConfigDirectory.FullName, FileName);

        /// <summary>讀過至少一次了（畫面用來分辨「清單是空的」與「還沒讀」）。</summary>
        internal static bool Loaded { get; private set; }

        /// <summary>
        /// 每公佈一份新的解析結果就 +1。
        /// 🔑 巡檢拿它當「我要求的那次重讀已經回來了」的判準——單看
        /// <see cref="IsLoading"/> 會在「要求了、但工作還沒被踢出去」那一格誤判成已完成。
        /// 🔴 <b>連讀檔失敗也要 +1</b>，否則等它的那一方會永遠等下去。
        /// </summary>
        internal static int Generation { get; private set; }

        internal static bool IsLoading => readTask != null;

        /// <summary>上一次讀完的時間（UTC）；<see cref="DateTime.MinValue"/>＝還沒讀過。</summary>
        internal static DateTime LoadedAtUtc { get; private set; } = DateTime.MinValue;

        /// <summary>上一次讀的時候檔案存不存在。</summary>
        internal static bool FileExists { get; private set; }

        /// <summary>清單裡被截掉的行數（超過 <see cref="MaxEntries"/> 的部分）。</summary>
        internal static int Truncated { get; private set; }

        /// <summary>解析好的清單。🔴 只在 framework 執行緒上換掉整個 list 實例。</summary>
        internal static IReadOnlyList<ShoppingListItem> Items => items;

        /// <summary>認不出來的那幾行（畫面要把它們顯示出來，不可以靜靜地少查幾件）。</summary>
        internal static IReadOnlyList<string> Unresolved => unresolved;

        /// <summary>認得、但這件道具不能在市場買賣的那幾行（同樣要顯示出來）。</summary>
        internal static IReadOnlyList<string> Unmarketable => unmarketable;

        /// <summary>要求重讀清單檔（使用者按了按鈕，或巡檢要開始了）。任何執行緒都可以呼叫。</summary>
        internal static void RequestReload() => Volatile.Write(ref reloadRequested, 1);

        /// <summary>
        /// 有人要求重讀就在執行緒池上把檔案讀成純文字條目。
        /// 🔴 framework 執行緒限定（與 <see cref="PumpLoad"/> 成對）。這裡<b>不</b>碰任何遊戲資料。
        /// </summary>
        internal static void BeginLoad()
        {
            if (readTask != null)
                return;
            if (Interlocked.Exchange(ref reloadRequested, 0) == 0)
                return;
            readTask = Task.Run(ReadFile);
        }

        /// <summary>
        /// 讀檔工作跑完了就把名稱換成道具 id 並公佈出去。
        /// 🔴 <b>framework 執行緒限定</b>：這裡會讀 Lumina 的 <c>Item</c> 表。
        /// </summary>
        internal static void PumpLoad()
        {
            var task = readTask;
            if (task == null || !task.IsCompleted)
                return;
            readTask = null;

            RawFile raw;
            try
            {
                raw = task.GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                Log.Information(e, $"[Marketbuddy] 採購清單：讀取 {FileName} 時發生例外，這一次維持上一份清單。");
                Loaded = true;
                Generation++;
                return;
            }

            Resolve(raw);
            Generation++;
        }

        /// <summary>
        /// 建立一份帶說明的空範例清單檔。
        /// 🔴 <b>只有使用者按下按鈕才會走到這裡</b>，而且<b>檔案已經存在時什麼都不做</b>——
        /// 絕不覆寫使用者自己寫的清單。
        /// </summary>
        /// <returns>true＝這次真的建立了檔案。</returns>
        internal static bool TryCreateTemplate()
        {
            try
            {
                var path = FilePath;
                if (File.Exists(path))
                    return false;

                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                var text = new StringBuilder();
                text.Append("# Marketbuddy 採購清單\r\n");
                text.Append("# 一行一件，可以寫道具名稱或道具 id。# 之後是註解。\r\n");
                text.Append("# 數量可寫可不寫，只是備註，不影響查詢：\r\n");
                text.Append("#   12x 銅礦\r\n");
                text.Append("#   銅礦 x12\r\n");
                text.Append("#   銅礦,12\r\n");
                text.Append("#   5106\r\n");
                text.Append("\r\n");

                // 🔴 先把整份 bytes 組好、寫暫存檔、再換過去：中途失敗不會留下半份檔案。
                var bytes = new UTF8Encoding(true).GetBytes(text.ToString());
                var temporary = path + ".tmp";
                File.WriteAllBytes(temporary, bytes);
                File.Move(temporary, path, true);
                RequestReload();
                return true;
            }
            catch (Exception e)
            {
                Log.Information(e, $"[Marketbuddy] 採購清單：建立範例清單檔（{FileName}）失敗。");
                return false;
            }
        }

        // =====================================================================
        //  讀檔（執行緒池）
        // =====================================================================

        private sealed class RawFile
        {
            public bool Exists;
            public int Truncated;
            public readonly List<(string Text, int Wanted)> Entries = [];
        }

        private static RawFile ReadFile()
        {
            var raw = new RawFile();
            string path;
            try
            {
                path = FilePath;
            }
            catch
            {
                return raw;
            }

            try
            {
                if (!File.Exists(path))
                    return raw;
                raw.Exists = true;

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                while (reader.ReadLine() is { } line)
                {
                    if (!TryParseLine(line, out var text, out var wanted))
                        continue;
                    if (raw.Entries.Count >= MaxEntries)
                    {
                        raw.Truncated++;
                        continue;
                    }

                    raw.Entries.Add((text, wanted));
                }
            }
            catch (Exception e)
            {
                Log.Information(e, $"[Marketbuddy] 採購清單：讀取 {FileName} 失敗，這一次當作空清單。");
            }

            return raw;
        }

        /// <summary>把一行拆成「道具名稱或 id」＋「想買幾個」。空行與整行註解回 false。</summary>
        private static bool TryParseLine(string line, out string text, out int wanted)
        {
            text = string.Empty;
            wanted = 0;

            var s = line.Trim();
            if (s.Length == 0)
                return false;

            // 註解：# 到行尾，以及整行的 //。
            var hash = s.IndexOf('#');
            if (hash >= 0)
                s = s[..hash].Trim();
            if (s.StartsWith("//", StringComparison.Ordinal))
                return false;
            if (s.Length == 0)
                return false;

            var leading = LeadingQuantity.Match(s);
            if (leading.Success)
            {
                wanted = ParseQuantity(leading.Groups[1].Value);
                text = leading.Groups[2].Value.Trim();
                return text.Length > 0;
            }

            var trailing = TrailingQuantity.Match(s);
            if (trailing.Success)
            {
                wanted = ParseQuantity(trailing.Groups[2].Value);
                text = trailing.Groups[1].Value.Trim();
                return text.Length > 0;
            }

            var comma = CommaQuantity.Match(s);
            if (comma.Success)
            {
                wanted = ParseQuantity(comma.Groups[2].Value);
                text = comma.Groups[1].Value.Trim();
                return text.Length > 0;
            }

            text = s;
            return true;
        }

        private static int ParseQuantity(string value)
            => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 0;

        // =====================================================================
        //  解析（framework 執行緒）
        // =====================================================================

        private static void Resolve(RawFile raw)
        {
            var resolved = new List<ShoppingListItem>(raw.Entries.Count);
            var bad = new List<string>();
            var notMarketable = new List<string>();
            var seen = new HashSet<uint>();

            var sheet = SafeItemSheet();

            foreach (var (entry, wanted) in raw.Entries)
            {
                uint itemId = 0;

                // 純數字＝道具 id。名字剛好是純數字的道具不存在，所以這個判斷沒有歧義。
                if (uint.TryParse(entry, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
                    parsed > 0)
                    itemId = parsed;
                else if (sheet != null && Index(sheet).TryGetValue(entry, out var byName))
                    itemId = byName;

                if (itemId == 0)
                {
                    bad.Add(entry);
                    continue;
                }

                // 🔑 「這件能不能在市場買賣」要看得見。不能買賣的道具送出去只會白白吃掉一次
                //    查詢額度並回一個空結果，而那與「沒人在賣」長得一模一樣。
                if (sheet != null && !IsMarketable(sheet, itemId))
                {
                    notMarketable.Add(entry);
                    continue;
                }

                if (!seen.Add(itemId))
                    continue;
                resolved.Add(new ShoppingListItem(itemId, wanted));
            }

            items = resolved;
            unresolved = bad;
            unmarketable = notMarketable;
            FileExists = raw.Exists;
            Truncated = raw.Truncated;
            LoadedAtUtc = DateTime.UtcNow;
            Loaded = true;

            Log.Information(
                $"[Marketbuddy] 採購清單：{FileName} {(raw.Exists ? "已讀取" : "不存在")}，" +
                $"可查 {resolved.Count} 件，認不出 {bad.Count} 行，不能買賣 {notMarketable.Count} 行，" +
                $"超過上限被略過 {raw.Truncated} 行。");
        }

        private static Lumina.Excel.ExcelSheet<Item>? SafeItemSheet()
        {
            try
            {
                return DataManager.GetExcelSheet<Item>();
            }
            catch (Exception e)
            {
                Log.Information(e, "[Marketbuddy] 採購清單：讀不到道具表，這一輪只認得純數字的道具 id。");
                return null;
            }
        }

        private static bool IsMarketable(Lumina.Excel.ExcelSheet<Item> sheet, uint itemId)
        {
            try
            {
                // 對不上的 id 一律當作「可以查」：那時候該說話的是伺服器，不是我們。
                return !sheet.TryGetRow(itemId, out var row) || row.ItemSearchCategory.RowId != 0;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// 名稱→道具 id。第一次用到才建，而且只在 framework 執行緒上建。
        /// ⚠️ 台服有 61 組同名不同 id 的道具（對 <c>exd-tc</c> 實數）——
        /// <b>先出現的（id 小的）勝出</b>，要指定另一個就在清單檔裡直接寫 id。
        /// </summary>
        private static Dictionary<string, uint> Index(Lumina.Excel.ExcelSheet<Item> sheet)
        {
            if (nameIndex != null)
                return nameIndex;

            var stopwatch = Stopwatch.StartNew();
            var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var row in sheet)
                {
                    // 只收得進市場的道具：不能買賣的東西進了索引只會遮住同名的可買品項。
                    if (row.ItemSearchCategory.RowId == 0)
                        continue;
                    var name = row.Name.ExtractText().Trim();
                    if (name.Length == 0)
                        continue;
                    map.TryAdd(name, row.RowId);
                }
            }
            catch (Exception e)
            {
                Log.Information(e, "[Marketbuddy] 採購清單：建立道具名稱索引時發生例外，索引可能不完整。");
            }

            stopwatch.Stop();
            Log.Information(
                $"[Marketbuddy] 採購清單：道具名稱索引建好了，{map.Count} 個名字，耗時 {stopwatch.ElapsedMilliseconds} ms。" +
                "這份索引一個工作階段只建一次。");
            nameIndex = map;
            return map;
        }
    }
}
