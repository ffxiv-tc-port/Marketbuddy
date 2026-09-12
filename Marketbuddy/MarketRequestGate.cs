using System;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    /// <summary>
    /// 全外掛共用的「市場查價送出節流」閘門。
    /// 決定一次 <c>InfoProxyItemSearch.RequestData()</c> 會不會被丟掉的，是
    /// **距離上一次「送出」的毫秒數**，不是距離上一次「收到資料」。所以這裡以送出時間為基準。
    /// ⚠️ 刻意**不**往 1700–2000 之間探。那段區間一次都沒量過，而往下探的代價是
    /// 拿真實請求去撞伺服器；最多再省 5%，不值得。地板就是運行點。
    /// 🔑 閘門狀態刻意是 <c>static</c>，跨批次、跨閒置空檔保留；會自動衰減回地板，所以保留不再有代價。
    /// 這裡只做「節流」，不做任何自動化：送不送請求仍然由使用者按下的動作決定。
    /// </summary>
    internal static class MarketRequestGate
    {
        private const string Diag = "[MBDIAG]";

        /// <summary>
        /// 起始間隔，同時也是**永久地板與正常運行點**。
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
        /// </summary>
        private const int ClusterRefusals = 3;

        // --- 保險絲 ② 持續率：拒絕率**明顯**超過損益兩平點 --------------------
        /// <summary>持續率的觀察窗大小。</summary>
        private const int RateWindowRequests = 50;

        /// <summary>
        /// 觀察窗內達到這個拒絕次數就撴寬。地板 2000 時損益兩平率＝300/2300＝**13.0%**，
        /// 這裡取 10/50 ＝ **20%**（≈1.5 倍損益兩平）。
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
        /// 🔴 刻意**不是**「連續乾淨的次數」：那在有雜訊時永遠湊不滿，往回收會走不回來。
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

        /// <summary>有效間隔＝間隔 / (1 − 拒絕率)：一次拒絕的代價實測恰好是一個閘門週期。</summary>
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
