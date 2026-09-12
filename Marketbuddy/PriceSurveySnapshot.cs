using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    /// <summary>
    /// 跨世界價格巡檢記錄的<b>記憶體索引</b>：每一個 (道具, 品質, 世界) 只留<b>最新</b>那一列。
    ///
    /// <para>
    /// 🔴 存在的理由只有一個：重掛引擎跑在 framework 執行緒上，而
    /// <see cref="PriceSurveyLog.LoadAll"/> 是讀一個可能幾萬列的檔案——在遊戲主執行緒上
    /// 做那件事就是掉幀。所以檔案<b>只在執行緒池上讀一次</b>，之後的更新由
    /// <see cref="PriceSurveyLog.Append"/> 就地餵進來（巡檢每查完一件就記一列，
    /// 那一列本來就在手上，不必再回去讀檔）。
    /// </para>
    ///
    /// <para>
    /// 執行緒：底層是 <see cref="ConcurrentDictionary{TKey,TValue}"/>，
    /// <b>任何執行緒都可以讀寫</b>。合併規則是「<see cref="PriceSurveyRow.AtUtc"/> 比較新的贏」，
    /// 所以讀檔那一段即使在巡檢進行中才完成，也不會把剛剛的新資料蓋回舊值。
    /// </para>
    ///
    /// <para>
    /// 📌 <b>純資料</b>：這個類別不定價、不改任何價格、不送任何查詢，也不決定要不要用這份資料
    /// （新鮮度由呼叫端傳 <c>maxAgeHours</c> 進來判斷）。
    /// </para>
    /// </summary>
    internal static class PriceSurveySnapshot
    {
        /// <summary>載入狀態：0＝還沒讀過、1＝正在讀、2＝讀完了。</summary>
        private const int NotLoaded = 0;
        private const int Loading = 1;
        private const int Loaded = 2;

        private static readonly ConcurrentDictionary<(uint ItemId, bool Hq, uint WorldId), PriceSurveyRow> Latest =
            new();

        private static int loadState = NotLoaded;

        /// <summary>記錄檔已經讀進來了嗎。false＝巡檢補位這條路暫時拿不到跨 session 的舊資料。</summary>
        internal static bool IsLoaded => Volatile.Read(ref loadState) == Loaded;

        /// <summary>索引裡有幾筆（診斷與畫面用）。</summary>
        internal static int Count => Latest.Count;

        /// <summary>
        /// 記下一列。<b>可以從任何執行緒呼叫</b>；已經有更新的同鍵資料時什麼都不做。
        /// </summary>
        internal static void Note(PriceSurveyRow row)
        {
            if (row.ItemId == 0 || row.WorldId == 0)
                return;

            var key = (row.ItemId, row.Hq, row.WorldId);
            Latest.AddOrUpdate(key, row, (_, existing) => row.AtUtc >= existing.AtUtc ? row : existing);
        }

        /// <summary>
        /// 第一次需要用到跨 session 的舊巡檢資料時，把記錄檔讀進來。
        ///
        /// <para>
        /// 🔴 檔案 I/O 一律在<b>執行緒池</b>上（<c>Task.Run</c>），呼叫端這一側只做一次
        /// <see cref="Interlocked"/> 比較，所以放在每幀會跑的路徑上也沒關係。
        /// </para>
        /// <para>
        /// ⚠️ 這是<b>一次性</b>的：之後的更新走 <see cref="Note"/>。刻意不做定期重讀——
        /// 記錄檔只有我們自己會寫，重讀只會多做一次幾萬列的解析。
        /// </para>
        /// </summary>
        internal static void EnsureLoaded()
        {
            if (Interlocked.CompareExchange(ref loadState, Loading, NotLoaded) != NotLoaded)
                return;

            Task.Run(() =>
            {
                try
                {
                    var rows = PriceSurveyLog.LoadAll();
                    foreach (var row in rows)
                        Note(row);
                    Log.Information(
                        $"[Marketbuddy] 巡檢記錄索引建立完成：{rows.Count} 列 → {Latest.Count} 筆最新值。");
                }
                catch (Exception e)
                {
                    // 讀失敗與「檔案裡沒有資料」對呼叫端的處置相同（拿不到補位資料），
                    // 但原因要看得見。
                    Log.Information(e, "[Marketbuddy] 巡檢記錄索引建立失敗，巡檢補位這條路暫時沒有資料。");
                }
                finally
                {
                    Volatile.Write(ref loadState, Loaded);
                }
            });
        }

        /// <summary>
        /// 某個世界對某一件道具（某個品質）最新的「別人的最低掛售價」。
        ///
        /// <para>
        /// 採用條件<b>逐字比照</b>待處理清單的 <c>survey</c> 那一條：
        /// <c>Verdict == "ok"</c>（那次查詢真的成功）、<c>LowestIsOurs == 0</c>
        /// （最低價<b>不是</b>我們自己掛的）、<c>LowestForQuality &gt;= 0</c>（真的有數字）。
        /// </para>
        /// </summary>
        /// <param name="maxAgeHours">這一列最多可以是幾小時前的；<b>0＝完全不採用</b>（功能關閉）。</param>
        /// <param name="lowest">別人的最低單價；回 false 時是 -1。</param>
        /// <param name="atUtc">那一列的時間戳；回 false 時是 <see cref="DateTime.MinValue"/>。</param>
        internal static bool TryGetLowest(
            uint itemId, bool hq, uint worldId, DateTime nowUtc, int maxAgeHours,
            out long lowest, out DateTime atUtc)
        {
            lowest = -1;
            atUtc = DateTime.MinValue;

            // 🔴 0 是「不要用巡檢補位」，不是「不限年代」：反過來的話一個手改成 0 的設定檔
            // 會讓半年前的行情拿去定價，而那種壞法是靜默的。
            if (maxAgeHours <= 0 || itemId == 0 || worldId == 0)
                return false;

            if (!Latest.TryGetValue((itemId, hq, worldId), out var row))
                return false;
            if (row.Verdict != "ok" || row.LowestIsOurs != 0)
                return false;

            var price = row.LowestForQuality;
            if (price < 0)
                return false;

            if (row.AtUtc == DateTime.MinValue || row.AtUtc < nowUtc.AddHours(-maxAgeHours))
                return false;

            lowest = price;
            atUtc = row.AtUtc;
            return true;
        }

        /// <summary>外掛卸載時清掉。下一次載入會重新讀檔。</summary>
        internal static void Reset()
        {
            Latest.Clear();
            Volatile.Write(ref loadState, NotLoaded);
        }
    }
}
