using System;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    /// <summary>
    /// 全外掛共用的「市場查價送出節流」閘門。
    ///
    /// 🔑 2026-08-02 第二輪實機診斷（MBDIAG，v7.20.0.15，n=417 次請求）推翻了第一輪的結論：
    /// 決定一次 <c>InfoProxyItemSearch.RequestData()</c> 會不會被靜默丟掉的，是
    /// **距離上一次「送出」的毫秒數**，不是距離上一次「收到資料」。量到的分界非常乾淨：
    ///     ❌ 被吞掉（連 history 封包都沒有）：send→send 1130 / 1175 / 1403 ms（n=3）
    ///     ✅ 拿得到資料：                     send→send 1488 / 2006 / 2008 / 2030 ms 以及 406 次 ≥2937 ms
    /// 兩組零重疊 ⇒ 真實門檻落在 (1403, 1488]，也就是大約 1.5 秒。
    ///
    /// 那 3 次被吞掉的請求全部緊接在一個「沒人在賣」的道具之後 —— 那種道具只回 history、
    /// 不回 offerings，所以舊版用來計時的「上次收到資料」時間戳是停在**再上一件**，
    /// 於是節流檢查瞬間通過、實際 send→send 只隔了 1.1～1.4 秒就撞牆。改用送出時間當基準
    /// 之後這個洞自動消失（送出時間每次請求都會更新，不管伺服器回不回）。
    ///
    /// 舊版把間隔設在「收到資料後再等 2500 ms」＝實際 send→send 約 2975 ms，是真實門檻的兩倍；
    /// 而且它只會往上爬不會往下降，實機上已經爬到 4000（＝每件 4.5 秒）。這裡改成
    /// 送出基準 + 起始 1700 ms（比實測會過的 1488 多約 200 ms 餘裕），並且可以在連續成功後
    /// 往回收，但**永遠不會低於已知會失敗的間隔 + 一個級距**，所以不會來回震盪。
    ///
    /// 🔴 2026-08-02 第三輪（把 REQUEST 逐筆配對到後續封包，n=734 次請求，只看 attempt=1
    /// 這個沒有歸屬歧義的族群）發現上面那套自我校準有一個會**永久卡在上限**的破口：
    ///     送出間隔 &lt;700 ms  → 51/51 被吞（100%）
    ///     送出間隔 700–1500 ms → 3/4 被吞
    ///     送出間隔 1800–4000 ms → **0/411 被吞**
    ///     送出間隔 &gt;4000 ms   → 13/145 被吞（10.5% / 5.0%）
    /// 最後一行是關鍵：間隔**越長**反而又出現被吞的案例，所以那些絕對不是「送太快」造成的，
    /// 把閘門撴寬對它們完全沒有幫助。但舊的 <see cref="NoteRefused"/> 會照單全收：
    /// 一次 gap=4295 ms 的失敗會讓 knownBadGapMs=4295 → 地板變成 4595 &gt; 上限 4000，
    /// 於是 <see cref="NoteAccepted"/> 的 <c>intervalMs &lt;= floor</c> 永遠成立、**再也收不回來**，
    /// 間隔就永久釘在 4000 ms（＝每件 4 秒，比它想取代的 .15 還慢）。
    /// 兩道修正：(1) 間隔已經 ≥ <see cref="MaxLearnableGapMs"/> 的失敗不列入學習，
    /// (2) 地板一律夾在上限之下，保證任何情況下都收得回來。
    ///
    /// 這裡只做「節流」，不做任何自動化：送不送請求仍然由使用者按下的動作決定。
    /// </summary>
    internal static class MarketRequestGate
    {
        /// <summary>起始間隔：實測 1403 ms 會被吞、1488 ms 會過，取 1700 ms 留約 200 ms 餘裕。</summary>
        private const int StartIntervalMs = 1700;

        /// <summary>被吞掉時往上加的級距，也是往回收的級距。</summary>
        private const int StepMs = 300;

        /// <summary>上限（仍優於舊版實機爬到的 4000＋收資料後才起算）。</summary>
        private const int CapMs = 4000;

        /// <summary>
        /// 只從「間隔短到有可能是原因」的失敗學習。實測 attempt=1 在 1800–4000 ms 這個區間
        /// 是 0/411 被吞，2600 ms 取在乾淨區間裡面偏保守的位置；比這個還寬還被吞，成因
        /// 一定在別的地方（實測 &gt;4000 ms 反而有 10% 被吞），撴寬閘門只會白白拖慢每一件。
        /// </summary>
        private const int MaxLearnableGapMs = 2600;

        /// <summary>連續這麼多次乾淨的請求之後，試著把間隔往回收一個級距。</summary>
        private const int NarrowAfterCleanRequests = 15;

        private static int intervalMs = StartIntervalMs;

        /// <summary>目前為止觀察到「確實被吞掉」的最大 send→send 間隔；往回收時的硬地板。</summary>
        private static int knownBadGapMs;

        private static int cleanRequests;
        private static DateTime lastRequestAt = DateTime.MinValue;

        /// <summary>目前使用的最小 send→send 間隔（毫秒），供診斷顯示。</summary>
        public static int IntervalMs => intervalMs;

        /// <summary>距離上一次送出的毫秒數；從未送出過時回 -1（視為可以直接送）。</summary>
        public static double MsSinceLastRequest(DateTime now)
            => lastRequestAt == DateTime.MinValue ? -1 : (now - lastRequestAt).TotalMilliseconds;

        /// <summary>現在送出去會不會撞到節流。</summary>
        public static bool IsReady(DateTime now)
        {
            var since = MsSinceLastRequest(now);
            return since < 0 || since >= intervalMs;
        }

        /// <summary>每一次真的呼叫了 RequestData() 都要記一筆（包含互動視窗的重查）。</summary>
        public static void NoteRequestSent(DateTime now) => lastRequestAt = now;

        /// <summary>
        /// 這次請求確實拿到了伺服器的回應（offerings 或 history 任一）。
        /// 累積夠多次之後往回收一個級距，讓一次雜訊造成的加寬不會永久拖慢整輪。
        /// </summary>
        public static void NoteAccepted()
        {
            if (++cleanRequests < NarrowAfterCleanRequests)
                return;

            cleanRequests = 0;

            // 地板：起始值，以及「已知會失敗的間隔 + 一個級距」——兩者取大。
            // 有了這個地板，往回收就不可能收到已經證實會被吞掉的區間，也就不會震盪。
            // 🔴 再夾一次上限：地板一旦超過 CapMs，intervalMs <= floor 就永遠成立、
            // 間隔會永久釘在上限收不回來（.16 的實際破口）。
            var floor = Math.Min(Math.Max(StartIntervalMs, knownBadGapMs + StepMs), CapMs - StepMs);
            if (intervalMs <= floor)
                return;

            var before = intervalMs;
            intervalMs = Math.Max(intervalMs - StepMs, floor);
            Log.Information($"[MBDIAG] GATE narrowed {before} -> {intervalMs} ms (floor {floor}, {NarrowAfterCleanRequests} clean requests)");
        }

        /// <summary>
        /// 這次請求被靜默吞掉了。把觀察到的實際 send→send 間隔記成「已知不夠」，
        /// 並把門檻拉到它之上。
        /// </summary>
        /// <param name="sendGapMs">被吞掉的那次請求，距離前一次送出的實際毫秒數。</param>
        public static void NoteRefused(double sendGapMs)
        {
            cleanRequests = 0;

            // sendGapMs 為負（本輪第一次請求，沒有前一次）時無從學習，只把間隔往上推一級。
            var gap = sendGapMs < 0 ? 0 : (int)Math.Round(sendGapMs);

            // 間隔已經寬到不可能是原因：記下來但不學、也不撴寬。見類別註解的實測分佈。
            if (gap >= MaxLearnableGapMs)
            {
                Log.Information(
                    $"[MBDIAG] GATE ignored a swallow at send-gap {gap} ms (>= {MaxLearnableGapMs}, " +
                    $"spacing cannot be the cause); interval stays {intervalMs} ms");
                return;
            }

            if (gap > knownBadGapMs)
                knownBadGapMs = gap;

            var target = Math.Min(Math.Max(intervalMs, gap) + StepMs, CapMs);
            if (target <= intervalMs)
                return;

            var before = intervalMs;
            intervalMs = target;
            Log.Information($"[MBDIAG] GATE widened {before} -> {intervalMs} ms (request swallowed at send-gap {gap} ms)");
        }
    }
}
