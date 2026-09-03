using System;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    /// <summary>
    /// 全外掛共用的「市場查價送出節流」閘門。
    ///
    /// 🔑 2026-08-02 第二輪實機診斷（MBDIAG，v7.20.0.15，n=417 次請求）推翻了第一輪的結論：
    /// 決定一次 <c>InfoProxyItemSearch.RequestData()</c> 會不會被丟掉的，是
    /// **距離上一次「送出」的毫秒數**，不是距離上一次「收到資料」。所以這裡以送出時間為基準。
    ///
    /// 🔴🔴 2026-08-03 第五輪（v7.20.0.20 跑完整整一輪，**加上 .18/.19 一起共 341 次
    /// 帶伺服器真值判決的請求**）推翻了第四輪的結論。拒絕**確實跟間隔有關**，
    /// 而且分界非常銳利：
    ///
    ///     閘門值    實際 send-gap   請求數   被拒   拒絕率
    ///     1700 ms   1712–1719 ms      81      19   **23.5%**
    ///     2000 ms   2012–2023 ms      24       0     0.0%
    ///     2019 ms   2032–2042 ms     236       0     0.0%
    ///                                ────     ───
    ///     ≥2000 合計                  260       0   **0.0%**
    ///
    /// 🔑 決定性的證據是 .20 那輪**自己做出來的 A/B 對照**（不是跨時段比較，所以
    /// 「拒絕只是剛好成群」解釋不掉）：
    ///     01:34:27  閘門因 6/20 保險絲撴寬 1700 → 2000
    ///     01:34:27–01:36:09   在 2000 ms 跑了 24 次請求，**0 次被拒**
    ///     01:36:09  乾淨窗滿了，往回收 2000 → 1700
    ///     01:36:15  **6 秒後就又被拒**，之後 23 次請求裡 5 次被拒（21.7%）
    /// 同一個雇員、同一批道具、三分鐘之內，唯一改變的變數就是間隔。
    ///
    /// ⚠️ 第四輪為什麼會得到相反的結論——這一點值得記住：
    ///   • 當時的證據 ② 來自 .16，而 **.16 根本沒有伺服器真值**（探針是 .18 才裝的），
    ///     它的「拒絕」全部是**逾時推論**出來的。逾時 ≠ 被拒；那批資料不能拿來論證。
    ///   • 證據 ① 是**單一一次** 1719 被拒 / 1718 通過。在 23.5% 的拒絕率下，
    ///     隨手就能抽到這種一正一反的配對，它從來不足以否定「間隔有關」。
    ///   🔑 教訓：**推翻一個模型要用跟建立它同等級的證據**，一個軼事不行。
    ///
    /// 📐 成本模型（實機量到的，不是推導的）：
    ///   • 一次拒絕的代價 = **恰好一個閘門週期**。.20 全部 17 次拒絕的重試，
    ///     無一例外都在下一個閘門格（1718–1719 ms 之後）送出。
    ///   • 所以每格期望耗時＝**有效間隔**＝ 間隔 / (1 - 拒絕率)。
    ///   • 代進上表：
    ///        1700 ms：1718 / (1 − 0.235) = **2246 ms/件**
    ///        2000 ms：2013 / (1 − 0)     = **2013 ms/件**
    ///     ⇒ **跑 2000 ms 且零拒絕，比跑 1700 ms 吃 23.5% 拒絕快 233 ms/件（10.4%）。**
    ///     一輪 9 個雇員約 180 件 ⇒ 省下約 42 秒，而且完全不再惹伺服器生氣。
    ///
    /// 🔴 因此 <see cref="StartIntervalMs"/> 從 1700 提高到 2000。
    /// ⚠️ 這**不違反**使用者的紅線。紅線是「不要比已知會成功的值更快」，
    /// 1700 從來就不是「最佳運行點」，它只是當時認定的**安全下限**；
    /// 而資料顯示它其實落在伺服器會回絕的區間裡。往**慢**的方向調整永遠是安全的。
    ///
    /// ⚠️ 刻意**不**往 1700–2000 之間探。那段區間一次都沒量過，而往下探的代價是
    /// 拿真實請求去撞伺服器；最多再省 5%，不值得。地板就是運行點。
    ///
    /// 🔴 策略仍然是「有代價意識的 AIMD」，只是地板換了位置：
    ///   • **單發拒絕一律吸收掉**（當一次便宜的重試），完全不動間隔。
    ///   • 只有拒絕**成群**（<see cref="ClusterWindow"/> 次請求內 ≥ <see cref="ClusterRefusals"/> 次）
    ///     或**持續率明顯超過損益兩平**（<see cref="RateWindowRequests"/> 次內
    ///     ≥ <see cref="RateWindowRefusals"/> 次）才撴寬一級。這是保險絲，不是主要路徑；
    ///     在 2000 ms 的正常運行下它**永遠不會燒**（實測拒絕率 0%）。
    ///   • 連續乾淨之後往回收，而且**從地板被撴寬**（代表上次收得太早）會讓下一次往下探
    ///     所需的乾淨次數加倍；在地板上撐夠久則讓這個懲罰逐步失效。
    ///
    /// 🔑 閘門狀態刻意是 <c>static</c> 且**跨批次、跨雇員、跨閒置空檔保留**。理由：
    ///   • 使用者的實際流程是一個雇員接一個（一輪 9 個雇員、每個 20 格），
    ///     每次重置等於把學到的東西重付 9 次。
    ///   • 而且「閒置就代表伺服器狀態恢復了」是錯的：.16 那輪閒置 113925 ms 之後第一次請求
    ///     仍然被拒。既然閒置不構成證據，就不該拿它當重置的理由。
    ///   • 舊版「保留狀態」之所以是陷阱，是因為它永遠不會回收；現在會自動衰減回地板，
    ///     所以保留不再有代價。
    ///
    /// 📊 離線重放（每回合 180 件＝實機一輪 9 個雇員，60 組隨機種子，每格耗時
    /// ＝間隔 + 18 ms 量測開銷，被拒就再付一整格）。⚠️ 刻意用**三種互相矛盾的世界模型**
    /// 各跑一次，因為新設計不可以只是「過度貼合最新那批資料」：
    ///     世界模型                        地板1700(舊)   地板2000(新)
    ///     M1 門檻在 1900（最貼合實測）    2105 ms/件     **2029**  拒絕 9.2% → 0.5%
    ///     M1 門檻剛好在 2000（邊界）      2105           **2029**
    ///     M1 門檻在 2100（地板還是太低）  2410           2435      ← 唯一略輸，但拒絕率較低
    ///     M2 拒絕與間隔無關（舊模型）     2201           2501      ← 舊模型下會輸
    ///     M3 拒絕率隨間隔平滑遞減         2411           **2331**
    /// ⚠️ 誠實記下來：**M2 成立的話這次改動是錯的**。但 M2 已經被資料否決——
    /// 在 M2 下「2000 ms 連續 260 次零拒絕、1700 ms 卻 19/81」的機率約 3×10⁻⁷。
    /// M1/M3 都支持提高地板，而它們是唯二跟實測相容的模型。
    ///
    /// 這裡只做「節流」，不做任何自動化：送不送請求仍然由使用者按下的動作決定。
    /// </summary>
    internal static class MarketRequestGate
    {
        private const string Diag = "[MBDIAG]";

        /// <summary>
        /// 起始間隔，同時也是**永久地板與正常運行點**。
        ///
        /// 🔴 2026-08-03 由 1700 提高到 2000。理由是實機 341 次帶伺服器真值的請求：
        /// 1700 ms（實際 send-gap 1712–1719）拒絕率 **23.5%**（19/81），
        /// 而 ≥2000 ms（實際 2012–2042）**260 次零拒絕**。
        /// 換算有效間隔 2246 → 2013 ms/件，**提高地板反而快 10.4%**。
        ///
        /// ⚠️ 2000 這個值本身是實機直接跑過的：.20 在閘門值正好 2000 時（實際 gap 2013）
        /// 連續 24 次零拒絕，.18/.19 在 2018/2019 時再 236 次零拒絕。不是外插出來的。
        /// </summary>
        private const int StartIntervalMs = 2000;

        /// <summary>撴寬的級距。往回收時至少也收這麼多。</summary>
        private const int StepMs = 300;

        /// <summary>保險絲的天花板。只有在拒絕成群時才會爬到這裡，之後會自動衰減回地板。</summary>
        private const int CapMs = 4000;

        // --- 保險絲 ① 群聚：短時間內連續拒絕 ---------------------------------
        /// <summary>群聚判定的觀察窗（最近幾次請求）。</summary>
        private const int ClusterWindow = 5;

        /// <summary>
        /// 觀察窗內達到這個拒絕次數就視為「成群」，立刻撴寬。
        ///
        /// 3/5 = 60%，不是 2/4 = 50%：離線重放 300 組隨機序列顯示 2/4 在**中等**拒絕率下
        /// 常常被隨機湊巧觸發（p=10% 時每 180 次請求誤觸約 5 次），每次誤觸都要花 24 次
        /// 請求才爬得回地板，整體反而比什麼都不做慢（p=10%：2339 vs 1977 ms/格）。
        /// 3/5 幾乎只有真爆發才湊得出來，而且仍然只要約 5 次請求（實機約 10 秒）就會反應，
        /// 比純靠持續率（需要 20 次窗內累積 6 次）快得多——爆發時的安全反應速度比吞吐重要。
        /// </summary>
        private const int ClusterRefusals = 3;

        // --- 保險絲 ② 持續率：拒絕率**明顯**超過損益兩平點 --------------------
        /// <summary>持續率的觀察窗大小。2026-08-03 由 20 放長到 50。</summary>
        private const int RateWindowRequests = 50;

        /// <summary>
        /// 觀察窗內達到這個拒絕次數就撴寬。地板 2000 時損益兩平率＝300/2300＝**13.0%**，
        /// 這裡取 10/50 ＝ **20%**（≈1.5 倍損益兩平）。
        ///
        /// 🔑 為什麼不是「窗放長就能把門檻壓到損益兩平本身」——這個直覺是錯的，實算過：
        ///     門檻        真實率=13%(損益兩平)  真實率=25%   判別倍率
        ///     3/20 =15%        誤觸 49.4%          90.9%      1.8×
        ///     7/50 =14%        誤觸 48.2%          98.1%      2.0×
        ///     8/50 =16%        誤觸 32.5%          95.5%      2.9×
        ///     6/20 =30%(舊)    誤觸  3.7%          38.3%     10.2×
        ///     10/50=20%(新)    誤觸 10.9%          83.6%      7.7×
        /// **門檻只要放在損益兩平上，判別就必然是擲銅板**（那正是損益兩平的定義），
        /// 放長窗改善的是估計精度，改變不了這件事。所以門檻仍要**明顯高於**損益兩平。
        ///
        /// 選 10/50 而不是繼續用 6/20：兩者誤觸都夠低，但 10/50 在真的出問題時
        /// 靈敏得多（25% 時 83.6% vs 38.3%），而且門檻本身（20%）比舊的 30% 更貼近
        /// 損益兩平，也就是「撴寬真的划算時才撴寬」。反應速度由群聚保險絲（3/5，約 10 秒）
        /// 負責，慢保險絲慢一點沒關係。
        ///
        /// ⚠️ 正常運行（2000 ms、實測 0% 拒絕）下這條路徑**永遠不會走到**。
        /// </summary>
        private const int RateWindowRefusals = 10;

        /// <summary>
        /// 「乾淨窗」的大小，只用在往回收之後放鬆探測懲罰。
        /// 刻意**不**跟著 <see cref="RateWindowRequests"/> 一起放長到 50——那會讓
        /// 懲罰幾乎沒機會失效（要連續 50 次全乾淨），等於偷偷改掉往回收的行為。
        /// </summary>
        private const int CleanWindowRequests = 20;

        // --- 往回收 -----------------------------------------------------------
        /// <summary>往下探所需的請求數（初始值）。實機約 12 × 2 秒 ≈ 24 秒。</summary>
        private const int BaseNarrowAfter = 12;

        /// <summary>懲罰累加的上限，避免一輪爆發之後整個工作階段都不敢往下探。</summary>
        private const int MaxNarrowAfter = 96;

        private static int intervalMs = StartIntervalMs;

        /// <summary>目前往下探需要幾次請求；從地板被撴寬時加倍，在地板撐久了減半。</summary>
        private static int narrowAfter = BaseNarrowAfter;

        /// <summary>
        /// 距離上一次改動間隔（撴寬或往回收）過了幾次請求。
        ///
        /// 🔴 刻意**不是**「連續乾淨的次數」：連續計數在中等拒絕率下永遠湊不滿
        /// （每 8 次就被打斷一次），結果只有撴寬會發生、往回收永遠不會 —— 這正是
        /// 離線重放抓到的走不回來路徑。改用「經過的請求數」＋「最近沒有群聚」當條件，
        /// 往回收在有雜訊時仍然走得動。
        /// </summary>
        private static int requestsSinceChange;

        private static DateTime lastRequestAt = DateTime.MinValue;

        // 最近 RateWindowRequests 次請求的結果（true = 被拒絕），環狀緩衝。
        private static readonly bool[] Recent = new bool[RateWindowRequests];
        private static int recentHead;
        private static int recentFilled;

        // 純診斷的工作階段統計。
        private static int seenRequests, seenRefusals, absorbedRefusals, widenCount, narrowCount;

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
        /// 這次請求確實拿到了伺服器的回應（offerings、history 或「零掛售」的明確答覆）。
        /// 累積夠多次之後往回收，讓一次雜訊造成的加寬不會永久拖慢每一件。
        /// </summary>
        public static void NoteAccepted()
        {
            seenRequests++;
            RecordOutcome(false);
            requestsSinceChange++;

            if (requestsSinceChange < narrowAfter)
                return;

            if (intervalMs > StartIntervalMs)
            {
                // 最近沒有群聚壓力才往下探；不要求「連續全乾淨」，否則有雜訊時永遠探不動。
                if (RefusalsInLast(ClusterWindow) > 0)
                    return;

                requestsSinceChange = 0;
                var before = intervalMs;
                // 一次至少收一級，離地板越遠收越快：從天花板掉下來不必花上百次請求。
                var decrease = Math.Max(StepMs, (intervalMs - StartIntervalMs) / 2);
                intervalMs = Math.Max(StartIntervalMs, intervalMs - decrease);
                narrowCount++;
                MarketDiag.Trace(
                    $"{Diag} GATE narrowed {before} -> {intervalMs} ms " +
                    $"(floor {StartIntervalMs}, after {narrowAfter} requests, " +
                    $"{RefusalsInLast(RateWindowRequests)}/{recentFilled} refused in window; " +
                    $"session {seenRefusals}/{seenRequests})");
                return;
            }

            // 已經在地板上：整個乾淨窗都乾淨就讓之前的懲罰逐步失效，否則一次爆發會讓
            // 整輪（實機一輪 9 個雇員、約 180 次請求）都不敢再往下探。
            if (narrowAfter > BaseNarrowAfter && RefusalsInLast(CleanWindowRequests) == 0)
            {
                requestsSinceChange = 0;
                var was = narrowAfter;
                narrowAfter = Math.Max(BaseNarrowAfter, narrowAfter / 2);
                MarketDiag.Trace(
                    $"{Diag} GATE probe-penalty relaxed {was} -> {narrowAfter} requests " +
                    $"(floor {StartIntervalMs} held with a fully clean " +
                    $"{Math.Min(CleanWindowRequests, recentFilled)}-request window)");
            }
        }

        /// <summary>
        /// 這次請求被伺服器拒絕了（探針的 errorCode 真值，或退路的「什麼都沒回來」逾時）。
        /// 單發拒絕一律吸收掉；只有成群或持續率超過損益兩平點才動間隔。
        /// </summary>
        /// <param name="sendGapMs">被拒絕的那次請求，距離前一次送出的實際毫秒數（診斷用）。</param>
        public static void NoteRefused(double sendGapMs)
        {
            seenRequests++;
            seenRefusals++;
            RecordOutcome(true);
            requestsSinceChange++;

            var gap = sendGapMs < 0 ? -1 : (int)Math.Round(sendGapMs);
            var inCluster = RefusalsInLast(ClusterWindow) >= ClusterRefusals;
            var overRate = RefusalsInLast(RateWindowRequests) >= RateWindowRefusals;

            if (!inCluster && !overRate)
            {
                // 🔑 這才是常態路徑：實機兩輪乾淨的批次各只有 1 次孤立的拒絕（2/151）。
                // 代價＝一個閘門週期；撴寬的代價＝之後每一件都多 StepMs，貴得多。
                absorbedRefusals++;
                MarketDiag.Trace(
                    $"{Diag} GATE absorbed a refusal at send-gap {gap} ms " +
                    $"(isolated: {RefusalsInLast(ClusterWindow)}/{Math.Min(ClusterWindow, recentFilled)} recent, " +
                    $"{RefusalsInLast(RateWindowRequests)}/{recentFilled} in window; " +
                    $"interval stays {intervalMs} ms; session {seenRefusals}/{seenRequests})");
                return;
            }

            if (intervalMs >= CapMs)
            {
                MarketDiag.Trace(
                    $"{Diag} GATE at cap {CapMs} ms, refusal at send-gap {gap} ms not actionable " +
                    $"(cluster={inCluster} overRate={overRate}; session {seenRefusals}/{seenRequests})");
                return;
            }

            // 從地板被撴寬 ⇒ 上一次往下探收得太早，下一次要更多證據才准再探。
            var penaltyNote = string.Empty;
            if (intervalMs == StartIntervalMs && narrowAfter < MaxNarrowAfter)
            {
                var was = narrowAfter;
                narrowAfter = Math.Min(MaxNarrowAfter, narrowAfter * 2);
                penaltyNote = $"; next probe needs {narrowAfter} clean requests (was {was})";
            }

            var before = intervalMs;
            intervalMs = Math.Min(intervalMs + StepMs, CapMs);
            requestsSinceChange = 0;
            widenCount++;
            MarketDiag.Trace(
                $"{Diag} GATE widened {before} -> {intervalMs} ms at send-gap {gap} ms " +
                $"(cluster={inCluster} {RefusalsInLast(ClusterWindow)}/{ClusterWindow}, " +
                $"overRate={overRate} {RefusalsInLast(RateWindowRequests)}/{recentFilled}; " +
                $"session {seenRefusals}/{seenRequests}){penaltyNote}");
        }

        /// <summary>
        /// 一次拒絕的代價實測**恰好是一個閘門週期**（.20 的 17 次拒絕，重試無一例外
        /// 落在下一個閘門格），所以「這個運行點實際上多快」＝ 間隔 / (1 − 拒絕率)。
        /// 這才是該拿來比較的數字：1700 ms 吃 23.5% 拒絕（有效 2222）其實比
        /// 2000 ms 零拒絕（有效 2000）**慢**。
        /// </summary>
        private static double EffectiveMs(int interval, double refusalRate)
            => interval / Math.Max(0.05, 1.0 - refusalRate);

        /// <summary>
        /// 一輪結束時把閘門的完整軌跡印出來，這樣事後只看一行就知道
        /// 「往下探 → 成功/被拒 → 收斂到多少」。狀態刻意**不**重設。
        /// </summary>
        public static void LogSummary(string reason)
        {
            if (seenRequests == 0)
                return;

            var rate = 100.0 * seenRefusals / seenRequests;

            // 🔑 有效間隔要用**目前運行點**的拒絕率算，不能用整場的：整場的率混了
            // 撴寬前後兩種間隔，會把現在跑得好不好糊掉。窗內率才代表「現在」。
            var windowSeen = Math.Min(RateWindowRequests, recentFilled);
            var windowRefused = RefusalsInLast(RateWindowRequests);
            var windowRate = windowSeen == 0 ? 0.0 : (double)windowRefused / windowSeen;

            MarketDiag.Trace(
                $"{Diag} GATE summary ({reason}): interval={intervalMs} ms (floor {StartIntervalMs}, cap {CapMs}), " +
                $"effective={EffectiveMs(intervalMs, windowRate):F0} ms/slot at this operating point " +
                $"(recent {windowRefused}/{windowSeen} = {100.0 * windowRate:F1}%), " +
                $"refusals {seenRefusals}/{seenRequests} = {rate:F1}% (break-even {100.0 * StepMs / (StartIntervalMs + StepMs):F0}%), " +
                $"session-effective={EffectiveMs(intervalMs, seenRefusals / (double)seenRequests):F0} ms/slot, " +
                $"absorbed={absorbedRefusals} widened={widenCount} narrowed={narrowCount}, " +
                $"nextProbeAfter={narrowAfter} requests (since last change: {requestsSinceChange})");
        }

        private static void RecordOutcome(bool refused)
        {
            Recent[recentHead] = refused;
            recentHead = (recentHead + 1) % RateWindowRequests;
            if (recentFilled < RateWindowRequests)
                recentFilled++;
        }

        /// <summary>最近 <paramref name="n"/> 次請求裡有幾次被拒絕（不足 n 次時看已有的全部）。</summary>
        private static int RefusalsInLast(int n)
        {
            n = Math.Min(n, recentFilled);
            var count = 0;
            for (var i = 1; i <= n; i++)
            {
                var index = ((recentHead - i) % RateWindowRequests + RateWindowRequests) % RateWindowRequests;
                if (Recent[index])
                    count++;
            }

            return count;
        }
    }
}
