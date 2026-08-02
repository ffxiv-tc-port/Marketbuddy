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
    /// 🔴 2026-08-02 第四輪（v7.20.0.19，探針提供**真值**判決，205 次有 sendGap 的請求）
    /// 推翻了「拒絕是因為送太快、所以要把間隔撴寬」這整套模型：
    ///
    ///   ① **被拒絕的請求，用同樣的間隔立刻重送就成功了。** .19 那次拒絕的 send-gap 是 1719 ms，
    ///      重試的 send-gap 是 **1718 ms**（比被拒絕的還短）—— 卻通過了。
    ///      間隔若真是原因，1718 沒有道理會過。
    ///   ② **撴寬到上限也擋不住拒絕。** 19:52 那段（.16）閘門一路爬到 4000 ms，之後仍有
    ///      8/39 次請求被拒絕；其中一次的 send-gap 是 **113925 ms**（閒置快兩分鐘）也照樣被拒。
    ///      那一輪的拒絕在 ~2.5 分鐘後**自己**消失，跟我們的間隔無關。
    ///   ③ 拒絕在時間上**成群**：兩次乾淨的批次各只有 1 次拒絕（2/151 ≈ 1.3%），
    ///      而 .16 那輪在 2.5 分鐘內連續 12 次。
    ///
    /// 也就是說：拒絕比較像伺服器端的暫時狀態（token bucket 或機率性的），
    /// **不是我們現在建模的那種「單一門檻」**。真正的機制離線證明不了，
    /// 但可以證明的是：舊策略「一次拒絕就把下限永久墊高」買不到任何東西，只是每一件都變慢。
    ///
    /// ⚠️ 剩下的不確定性，以及它為什麼不影響這個設計：
    /// 在 ~1718 ms 這個間隔上實際只送出過 15 次請求（其中 2 次被拒＝13.3%），因為舊策略
    /// 一撞牆就把間隔墊高、再也回不來。所以「拒絕率跟間隔無關」與「1718 ms 的拒絕率真的
    /// 比較高」這兩個假設，用現有資料**分不開**。下面的成本模型直接把這件事納進來：
    /// 只要拒絕率超過損益兩平點就會自動撴寬，低於就自動收回去，不必先知道答案。
    ///
    /// 📐 成本模型（實機量到的，不是推導的）：
    ///   • 一次拒絕的代價 = **恰好一個閘門週期**。.19 實測：若那次沒被拒，下一件會在
    ///     21:39:31.681 送出；實際是 21:39:33.735，差 2054 ms ≈ 當時的 2036 ms 間隔。
    ///     （偵測本身只花 165 ms，被閘門的等待吸收掉了。）
    ///   • 所以每格期望耗時 = 間隔 / (1 - 拒絕率)。
    ///   • 把間隔從 I 加寬 S 值不值得，等價於問：I/(1-p) &gt; I+S 嗎？
    ///     ⇒ **損益兩平的拒絕率 p* = S / (I + S) = 300 / 2000 = 15%。**
    ///     下面的 <see cref="RateWindowRefusals"/>/<see cref="RateWindowRequests"/> = 3/20 = 15%
    ///     就是直接取這個值，不是拍腦袋挑的。
    ///
    /// 🔴 因此策略改成「有代價意識的 AIMD」：
    ///   • **單發拒絕一律吸收掉**（當一次便宜的重試），完全不動間隔。
    ///   • 只有拒絕**成群**（<see cref="ClusterWindow"/> 次請求內 ≥ <see cref="ClusterRefusals"/> 次）
    ///     或**持續率明顯超過損益兩平**（20 次內 ≥ 6 次 = 30%）才撴寬一級。
    ///     這是保險絲，不是主要路徑。
    ///   • 地板永遠是 <see cref="StartIntervalMs"/>，**不再是「已知會失敗的間隔 + 一級」**——
    ///     那個舊地板正是「學到一次就再也收不回來」的成因。
    ///   • 連續乾淨之後往回收，而且**從地板被撴寬**（代表上次收得太早）會讓下一次往下探
    ///     所需的乾淨次數加倍；在地板上撐夠久則讓這個懲罰逐步失效。
    ///
    /// ⚠️ **不會低於 1700 ms。** 這是實機大量成功過的值（.17 連續 8 次、.18/.19 各數次），
    /// 也是使用者定下的紅線：寧可慢一點也不要讓帳號承擔風險。這裡的「往下探」只是
    /// 收回撴寬，不是探索更快的區間。
    ///
    /// 🔑 閘門狀態刻意是 <c>static</c> 且**跨批次、跨雇員、跨閒置空檔保留**。理由：
    ///   • 使用者的實際流程是一個雇員接一個（一輪 9 個雇員、每個 20 格），
    ///     每次重置等於把學到的東西重付 9 次。
    ///   • 而且「閒置就代表伺服器狀態恢復了」是錯的：.16 那輪閒置 113925 ms 之後第一次請求
    ///     仍然被拒。既然閒置不構成證據，就不該拿它當重置的理由。
    ///   • 舊版「保留狀態」之所以是陷阱，是因為它永遠不會回收；現在會自動衰減回地板，
    ///     所以保留不再有代價。
    ///
    /// 📊 離線重放（把實機觀察到的 clean/refused 序列、以及 300 組隨機序列餵進兩套策略，
    /// 每格耗時 = 平均間隔 + 17 ms 量測開銷，再乘上 1/(1-拒絕率)）：
    ///     拒絕率     舊策略(.19)   新策略
    ///     1.3%(實測)  2040 ms/格   **1740 ms/格**   ← 實機兩輪都是這個區間
    ///     5%          2581         1811
    ///     10%         2824         1977
    ///     15%         2996         2461
    ///     20%         3147         3201             ← 交叉點大約在這裡
    ///     30%         3432         4404
    /// ⚠️ 也就是說 **拒絕率持續高於約 20% 時，新策略會比舊策略慢**。
    /// 那是爆發區間；舊策略在那裡「比較快」只是因為它把間隔永久釘在高點——代價是
    /// 爆發結束後**再也回不來**（重放中舊策略在 1.3% 的拒絕率下仍然收在 2353 ms）。
    /// 我們寧可在罕見的爆發裡慢一點，也不要每一件都被一次雜訊永久拖慢。
    ///
    /// 這裡只做「節流」，不做任何自動化：送不送請求仍然由使用者按下的動作決定。
    /// </summary>
    internal static class MarketRequestGate
    {
        private const string Diag = "[MBDIAG]";

        /// <summary>
        /// 起始間隔，同時也是**永久地板**。實機在這個值上成功過非常多次
        /// （.17 連續 8 次、.18 3 次、.19 4 次），而且是使用者核可的最低值。
        /// </summary>
        private const int StartIntervalMs = 1700;

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
        /// <summary>持續率的觀察窗大小。</summary>
        private const int RateWindowRequests = 20;

        /// <summary>
        /// 觀察窗內達到這個拒絕次數就撴寬。
        ///
        /// 🔴 這個常數本來取 3/20 = 15%（＝損益兩平率本身），離線重放實機序列時發現那是錯的：
        /// 20 次的窗太短，**真實率剛好落在 15% 時有大約一半的窗會湊到 3 次**，於是每次拒絕
        /// 都撴寬、卻永遠湊不齊往回收所需的乾淨次數 —— 模擬中一路衝到上限 4000 ms 就再也
        /// 下不來，等於換一種方式重現我們正要修掉的那顆雷。
        ///
        /// 改成 6/20 = 30%：在真實率 = 損益兩平的 15% 時 P(X≥6) ≈ 6.7%（幾乎不誤觸），
        /// 而真實率 30% 時 P ≈ 58%、40% 時 P ≈ 87%（爆發抓得到）。
        /// 損益兩平率仍然是設計基準，只是門檻放在它**明顯之上**才動手。
        /// </summary>
        private const int RateWindowRefusals = 6;

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
                Log.Information(
                    $"{Diag} GATE narrowed {before} -> {intervalMs} ms " +
                    $"(floor {StartIntervalMs}, after {narrowAfter} requests, " +
                    $"{RefusalsInLast(RateWindowRequests)}/{recentFilled} refused in window; " +
                    $"session {seenRefusals}/{seenRequests})");
                return;
            }

            // 已經在地板上：整個觀察窗都乾淨就讓之前的懲罰逐步失效，否則一次爆發會讓
            // 整輪（實機一輪 9 個雇員、約 180 次請求）都不敢再往下探。
            if (narrowAfter > BaseNarrowAfter && RefusalsInLast(RateWindowRequests) == 0)
            {
                requestsSinceChange = 0;
                var was = narrowAfter;
                narrowAfter = Math.Max(BaseNarrowAfter, narrowAfter / 2);
                Log.Information(
                    $"{Diag} GATE probe-penalty relaxed {was} -> {narrowAfter} requests " +
                    $"(floor {StartIntervalMs} held with a fully clean {recentFilled}-request window)");
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
                Log.Information(
                    $"{Diag} GATE absorbed a refusal at send-gap {gap} ms " +
                    $"(isolated: {RefusalsInLast(ClusterWindow)}/{Math.Min(ClusterWindow, recentFilled)} recent, " +
                    $"{RefusalsInLast(RateWindowRequests)}/{recentFilled} in window; " +
                    $"interval stays {intervalMs} ms; session {seenRefusals}/{seenRequests})");
                return;
            }

            if (intervalMs >= CapMs)
            {
                Log.Information(
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
            Log.Information(
                $"{Diag} GATE widened {before} -> {intervalMs} ms at send-gap {gap} ms " +
                $"(cluster={inCluster} {RefusalsInLast(ClusterWindow)}/{ClusterWindow}, " +
                $"overRate={overRate} {RefusalsInLast(RateWindowRequests)}/{recentFilled}; " +
                $"session {seenRefusals}/{seenRequests}){penaltyNote}");
        }

        /// <summary>
        /// 一輪結束時把閘門的完整軌跡印出來，這樣事後只看一行就知道
        /// 「往下探 → 成功/被拒 → 收斂到多少」。狀態刻意**不**重設。
        /// </summary>
        public static void LogSummary(string reason)
        {
            if (seenRequests == 0)
                return;

            var rate = 100.0 * seenRefusals / seenRequests;
            Log.Information(
                $"{Diag} GATE summary ({reason}): interval={intervalMs} ms (floor {StartIntervalMs}, cap {CapMs}), " +
                $"refusals {seenRefusals}/{seenRequests} = {rate:F1}% (break-even {100.0 * StepMs / (StartIntervalMs + StepMs):F0}%), " +
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
