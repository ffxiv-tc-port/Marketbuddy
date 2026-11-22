using System;
using System.Collections.Generic;

namespace Marketbuddy
{
    /// <summary>
    /// 一個定價參考價是從哪裡來的。字串刻意是穩定的小寫標籤：log、聊天訊息與待處理清單
    /// 都直接印它，所以改字面值等於改使用者看到的東西。
    /// </summary>
    internal static class PriceSourceTag
    {
        /// <summary>這一輪真的向伺服器問到的掛單（<c>InfoProxyItemSearch</c> 的答覆）。</summary>
        internal const string Live = "live";

        /// <summary>外掛自己的市場快取（<see cref="MarketDataCache"/>），沒有再送新的查詢。</summary>
        internal const string Cache = "cache";

        /// <summary>跨世界價格巡檢的記錄（<see cref="PriceSurveyLog"/>）裡<b>家世界</b>那一列。</summary>
        internal const string Survey = "survey";

        /// <summary>Universalis 的「資料中心最近一次實際成交」（<see cref="LastSoldPriceSource"/>）。</summary>
        internal const string Sale = "sale";

        /// <summary>新上架的退路：窗內最新多筆成交排除特殊交易後的共識價（<see cref="PriceConsensus"/>）。</summary>
        internal const string Consensus = "consensus";

        /// <summary>新上架的退路：資料中心<b>別的世界</b>最便宜的一般掛單（Universalis 現在掛單＋巡檢記錄）。</summary>
        internal const string DcListing = "dc-listing";
    }

    /// <summary>
    /// 一個定價候選：它的參考價、套完規則之後的目標價、出處，以及異常低價判定的結果。
    /// </summary>
    /// <param name="Reference">原始參考價（別人的最低掛售價，或成交價）；<b>-1＝沒有觀測到</b>。</param>
    /// <param name="Target">
    /// 套完規則（降價／捨去到百位）之後的目標價；<b>-1＝這個候選不能用</b>
    /// （被判成異常低價，或成交紀錄太舊）。
    /// </param>
    /// <param name="Source">出處標籤，見 <see cref="PriceSourceTag"/>；空字串＝沒有候選。</param>
    /// <param name="Anomaly">異常低價判定的完整結果（含「為什麼」），只給 log 與畫面用。</param>
    /// <param name="AtUtc">這個觀測是什麼時候的；<see cref="DateTime.MinValue"/>＝不知道。</param>
    /// <param name="Stale">成交紀錄超出新鮮度窗（或時間戳不知道）而被丟掉。</param>
    internal readonly record struct PriceCandidate(
        long Reference,
        long Target,
        string Source,
        PriceAnomaly Anomaly,
        DateTime AtUtc,
        bool Stale)
    {
        /// <summary>「沒有這個候選」的單一表示法。🔑 價格一律 -1（＝不知道），<b>絕不用 0</b>。</summary>
        internal static readonly PriceCandidate None =
            new(-1, -1, string.Empty, PriceAnomaly.Clear, DateTime.MinValue, false);

        /// <summary>真的觀測到一個參考價（即使它之後被判成異常、或太舊而不能用）。</summary>
        public bool IsObserved => Reference > 0;

        /// <summary>這個候選可以拿來定價。</summary>
        public bool IsUsable => Target >= Configuration.MIN_PRICE;

        /// <summary>這個候選被異常價保護擋下來了（而且不是因為太舊被丟掉）。</summary>
        public bool HeldByAnomaly => Anomaly.IsAnomalous && !Stale;
    }

    /// <summary>「這一格該怎麼辦」的三態。</summary>
    /// <remarks>
    /// ⚠️ <see cref="NoData"/> 刻意給明確的 0：沒有零值的列舉會讓 <c>default</c>
    /// 落在一個無效值上，而那種壞法是靜默的。
    /// </remarks>
    internal enum RelistOutcome
    {
        /// <summary>兩個候選都沒有可用的價格，而且沒有任何東西被擋下來 ⇒ 這一格不要動。</summary>
        NoData = 0,

        /// <summary>有目標價。</summary>
        Price = 1,

        /// <summary>唯一的參考價被異常低價保護擋下來了 ⇒ 這一格不要動，但要說明原因。</summary>
        Hold = 2,
    }

    /// <summary>兩候選取低的結果。</summary>
    /// <param name="Outcome">處置。</param>
    /// <param name="Price">目標價；只有 <see cref="RelistOutcome.Price"/> 時才有意義（其餘一律 -1）。</param>
    /// <param name="Winner">
    /// 勝出（或被擋下來）的那個候選，聊天訊息與 log 靠它講出「用的是哪個來源」。
    /// </param>
    /// <param name="OwnSaleSkipped">S 比 L 低、但因為是自家掛過的價而改用 L（只有這一種情形為 true）。</param>
    internal readonly record struct RelistDecision(
        RelistOutcome Outcome, long Price, PriceCandidate Winner, bool OwnSaleSkipped);

    /// <summary>
    /// 重掛的定價規則——<b>唯一真值來源</b>。
    /// 🔴 <b>這個類別不改任何價格、不碰任何遊戲指標、不做 I/O、不寫 log、不讀設定物件。</b>
    /// <para><b>規則</b>：每一格算兩個候選，各自先過異常低價保護（<see cref="PriceAnomalyGuard"/>），
    /// 再<b>取低者</b>——</para>
    /// <list type="bullet">
    ///   <item><b>L</b>＝板上（<b>家世界</b>）<b>別人的</b>最低掛售價，再套使用者的降價設定。
    ///         🔴 <b>絕不拿別的世界的掛單當 L</b>：別的世界比我便宜不代表我在自己的市場吃虧。</item>
    ///   <item><b>S</b>＝資料中心最近一筆成交價，無條件捨去到百位，而且<b>只採新鮮度窗內</b>的紀錄。</item>
    /// </list>
    /// <para>為什麼取低：使用者的目標是<b>賣掉</b>。成交價告訴我們「市場上真的有人以這個價買走」，
    /// 板上最低價告訴我們「現在得低於誰才輪得到我」。只看成交價會在板上有人開更低時
    /// 掛在賣不掉的價；只看板上會在別的世界都靠更低價才賣掉時掛得太貴。</para>
    /// <para>🔑 平手（<c>S == L</c>）時取 <b>L</b>：使用者的降價設定是 0 時「跟板上最低價相同」
    /// 正是他明確要的行為，而 L 已經套過那個設定。</para>
    /// </summary>
    internal static class RelistPricing
    {
        /// <summary>降價設定的<b>值快照</b>（帶得過執行緒邊界，也帶得進待處理清單的背景段）。</summary>
        /// <param name="UsePercent">true＝按百分比降，false＝按固定金額降。</param>
        /// <param name="Percent">百分比模式的降幅。</param>
        /// <param name="Amount">固定金額模式的降幅。<b>0＝與板上最低價相同</b>。</param>
        /// <param name="RoundTo">降完再往下捨到這個數的倍數；<b>&lt;=1＝不捨</b>。</param>
        /// <param name="RoundFloor">捨完不得低於的價（使用者的「低於就下架」門檻；0＝只守最低合法價）。</param>
        internal readonly record struct UndercutRule(
            bool UsePercent, int Percent, int Amount, int RoundTo, long RoundFloor);

        /// <summary>異常價保護的<b>值快照</b>。語意逐字比照 <see cref="PriceAnomalyGuard"/>。</summary>
        /// <param name="UseHistory">有近期成交中位數時改用它判（兩個方向），見 <see cref="HistoryOutlier"/>。</param>
        /// <param name="HistoryMinMedian">中位數低於這個數就不用中位數判。</param>
        internal readonly record struct AnomalyRule(
            bool Enabled, int MinNormalPrice, int Ratio, bool UseHistory, int HistoryMinMedian);

        /// <summary>
        /// 把參考價換算成掛售價：百分比模式用浮點乘法（<b>絕不用整數除法</b>），然後夾到合法範圍，
        /// 最後照 <see cref="UndercutRule.RoundTo"/> 往下捨。
        /// </summary>
        /// <remarks>
        /// 刻意不在「捨完等於參考價」時再多減一個倍數：降價 0 的意思就是「與最低價相同」。
        /// </remarks>
        internal static long ApplyUndercut(long reference, UndercutRule rule)
        {
            var target = rule.UsePercent
                ? (long)(reference * (1f - rule.Percent / 100f))
                : reference - rule.Amount;
            var clamped = Math.Clamp(target, Configuration.MIN_PRICE, Configuration.MAX_PRICE);
            return RoundDownToMultiple(clamped, rule.RoundTo, rule.RoundFloor);
        }

        /// <summary>把價格往下捨到 <paramref name="multiple"/> 的倍數（100 時 12,345 → 12,300）。</summary>
        /// <remarks>
        /// 契約：結果一定 ≤ <paramref name="price"/>（已經低於對手的價捨完仍低於對手）。
        /// <paramref name="multiple"/> &lt;=1 時原樣回傳；捨完會低於 <paramref name="floor"/>
        /// （至少 <see cref="Configuration.MIN_PRICE"/>）時也原樣回傳——捨去只是尾數偏好，
        /// 不可以把一件本來會被重新定價的東西變成 1 gil 或被下架。
        /// </remarks>
        internal static long RoundDownToMultiple(long price, int multiple, long floor)
        {
            if (multiple <= 1 || price <= 0)
                return price;
            var rounded = price - price % multiple;
            return rounded < Math.Max(floor, Configuration.MIN_PRICE) ? price : rounded;
        }

        /// <summary>
        /// 這一頁掛單裡的三個數字，一次算完（品質篩選只做一次，三者才不會分岔）。
        /// </summary>
        /// <param name="listings">原始掛單（價格、品質、僱員 id）。</param>
        /// <param name="hq">這一格掛的是優質品嗎。</param>
        /// <param name="compareHqOnly">使用者的「只比優質品」設定。</param>
        /// <param name="ownRetainers">自家僱員 id。</param>
        /// <param name="peerBaseline">
        /// 異常低價保護要用的「下一位賣家」最低價；-1＝沒有第二位賣家（見
        /// <see cref="PriceAnomalyGuard.PeerBaseline"/>）。
        /// </param>
        /// <param name="ownIsLowest">
        /// 這一頁（套完品質篩選）<b>最便宜的那一筆是我們自己掛的</b>。
        /// 🔑 同價平手時<b>先出現的贏</b>（與 <c>MinBy</c> 的行為相同）。
        /// </param>
        /// <param name="ownLowest">自家掛單裡最便宜的單價；-1＝這一頁沒有我們的掛單。</param>
        /// <returns><b>別人的</b>最低單價；<b>-1＝沒有別人在賣</b>（不是 0）。</returns>
        internal static long LowestCompetitor(
            IReadOnlyList<(uint Price, bool IsHq, ulong RetainerId)> listings,
            bool hq, bool compareHqOnly, IReadOnlySet<ulong>? ownRetainers,
            out long peerBaseline, out bool ownIsLowest, out long ownLowest)
        {
            peerBaseline = -1;
            ownIsLowest = false;
            ownLowest = -1;

            // 品質篩選：這一格是優質品、使用者開著「只比優質品」、而且這一頁真的有優質品掛單時，
            // 才只看優質品。
            // 走索引、不另建篩過的清單：出售品視窗開著時面板每幀對每一格呼叫一次。
            var hqOnly = false;
            if (hq && compareHqOnly)
            {
                for (var i = 0; i < listings.Count; i++)
                {
                    if (!listings[i].IsHq)
                        continue;
                    hqOnly = true;
                    break;
                }
            }

            long overallLowest = -1;
            var overallIsOurs = false;
            long competitor = -1;
            ulong competitorRetainerId = 0;
            for (var i = 0; i < listings.Count; i++)
            {
                var listing = listings[i];
                if (hqOnly && !listing.IsHq)
                    continue;
                var ours = ownRetainers != null && ownRetainers.Contains(listing.RetainerId);

                // 嚴格小於 ⇒ 同價平手時先出現的那一筆贏，與 MinBy 的行為相同。
                if (overallLowest < 0 || listing.Price < overallLowest)
                {
                    overallLowest = listing.Price;
                    overallIsOurs = ours;
                }

                if (ours)
                {
                    if (ownLowest < 0 || listing.Price < ownLowest)
                        ownLowest = listing.Price;
                    continue;
                }

                if (competitor >= 0 && listing.Price >= competitor)
                    continue;
                competitor = listing.Price;
                competitorRetainerId = listing.RetainerId;
            }

            ownIsLowest = overallLowest >= 0 && overallIsOurs;
            if (competitor >= 0)
                peerBaseline = PeerBaseline(listings, hqOnly, competitorRetainerId, ownRetainers);
            return competitor;
        }

        /// <summary>
        /// <see cref="PriceAnomalyGuard.PeerBaseline"/> 套在「先依 <paramref name="hqOnly"/> 篩過」的掛單上；
        /// 同一個走訪順序與判斷，只是不先複製出篩過的清單。
        /// </summary>
        private static long PeerBaseline(
            IReadOnlyList<(uint Price, bool IsHq, ulong RetainerId)> listings, bool hqOnly,
            ulong cheapestRetainerId, IReadOnlySet<ulong>? ownRetainers)
        {
            long best = -1;
            for (var i = 0; i < listings.Count; i++)
            {
                var listing = listings[i];
                if (hqOnly && !listing.IsHq)
                    continue;
                if (listing.RetainerId == cheapestRetainerId)
                    continue;
                if (ownRetainers != null && ownRetainers.Contains(listing.RetainerId))
                    continue;
                if (best >= 0 && listing.Price >= best)
                    continue;
                best = listing.Price;
            }

            return best;
        }

        /// <summary>
        /// 板上候選 <b>L</b>。
        /// </summary>
        /// <param name="lowestCompetitor">別人的最低掛售價；&lt;=0＝沒有候選。</param>
        /// <param name="peerBaseline">下一位賣家的最低價；-1＝不知道（巡檢那條路一律 -1）。</param>
        /// <param name="ownListedPrice">自己這一格目前的掛售價；-1＝不知道／不可信（停在上限價的那種）。</param>
        /// <param name="source">出處標籤（<see cref="PriceSourceTag.Live"/>／<c>Cache</c>／<c>Survey</c>）。</param>
        /// <param name="atUtc">這個觀測是什麼時候的；<see cref="DateTime.MinValue"/>＝不知道。</param>
        /// <param name="historyMedian">近期成交共識價（這一格的品質）；-1＝沒有。</param>
        /// <param name="saleWitness">可以佐證行情的成交單價（過期的不算）；-1＝沒有。</param>
        /// <param name="peerReference">判下一位賣家是不是特殊掛單的參照價（見 <see cref="PriceConsensus.SanitizePeer"/>）；-1＝沒有。</param>
        /// <remarks>偏高只認成交當佐證：下一位賣家一定不比最低價便宜，拿它佐證「行情真的這麼高」沒有意義。</remarks>
        internal static PriceCandidate FromListing(
            long lowestCompetitor, long peerBaseline, long ownListedPrice,
            string source, DateTime atUtc, UndercutRule undercut, AnomalyRule anomaly,
            long historyMedian, long saleWitness, long peerReference)
        {
            if (lowestCompetitor <= 0)
                return PriceCandidate.None;

            if (anomaly.UseHistory)
                peerBaseline = PriceConsensus.SanitizePeer(lowestCompetitor, peerBaseline, peerReference, anomaly.Ratio);
            var verdict = PriceAnomalyGuard.Evaluate(
                lowestCompetitor, peerBaseline, ownListedPrice, anomaly, historyMedian, peerReference,
                [peerBaseline, saleWitness], [saleWitness]);

            return new PriceCandidate(
                lowestCompetitor,
                verdict.IsAnomalous ? -1L : ApplyUndercut(lowestCompetitor, undercut),
                source, verdict, atUtc, false);
        }

        /// <summary>
        /// 成交價候選 <b>S</b>。
        /// </summary>
        /// <param name="unitPrice">成交單價；&lt;=0＝沒有候選。</param>
        /// <param name="soldAtUtc">成交時間；<see cref="DateTime.MinValue"/>＝不知道（一律當成過期）。</param>
        /// <param name="peerBaseline">
        /// 異常低價保護的同業基準。這一側傳的是「本世界目前最低掛售價」——那是與這筆成交
        /// <b>完全獨立</b>的另一個觀測，整體崩盤時它會跟著低，所以不會把崩盤誤判成異常。
        /// </param>
        /// <param name="ownListedPrice">自己這一格目前的掛售價；-1＝不知道／不可信。</param>
        /// <param name="nowUtc">現在（呼叫端傳進來，這個類別不讀時鐘）。</param>
        /// <param name="maxAgeDays">新鮮度窗（天）；<b>0＝不限</b>。</param>
        /// <param name="historyMedian">近期成交共識價（這筆成交的品質）；-1＝沒有。</param>
        /// <param name="listingWitness">這一輪看到的板上別人最低價（自家最低時也照填）；-1＝沒有。</param>
        /// <param name="peerReference">同 <see cref="FromListing"/>。</param>
        internal static PriceCandidate FromSale(
            long unitPrice, DateTime soldAtUtc, long peerBaseline, long ownListedPrice,
            DateTime nowUtc, int maxAgeDays, AnomalyRule anomaly,
            long historyMedian, long listingWitness, long peerReference)
        {
            if (unitPrice <= 0)
                return PriceCandidate.None;

            var stale = IsSaleStale(soldAtUtc, nowUtc, maxAgeDays);
            if (anomaly.UseHistory)
                peerBaseline = PriceConsensus.SanitizePeer(unitPrice, peerBaseline, peerReference, anomaly.Ratio);
            var verdict = PriceAnomalyGuard.Evaluate(
                unitPrice, peerBaseline, ownListedPrice, anomaly, historyMedian, peerReference,
                [peerBaseline, listingWitness], [peerBaseline, listingWitness]);

            return new PriceCandidate(
                unitPrice,
                stale || verdict.IsAnomalous ? -1L : LastSoldPriceSource.RoundDownToHundred(unitPrice),
                PriceSourceTag.Sale, verdict, soldAtUtc, stale);
        }

        /// <summary>
        /// 這筆成交太舊了嗎。
        /// </summary>
        /// <remarks>
        /// 🔴 <b>時間戳不知道（<see cref="DateTime.MinValue"/>）一律當成過期</b>，不是當成「剛剛」：
        /// 後者會讓一筆年代不明的成交價無條件蓋過板上的行情，而那正是「賣不掉」的成因。
        /// <para>📌 <paramref name="maxAgeDays"/> 為 0 時完全不判斷——那是<b>舊行為</b>的保留路。</para>
        /// </remarks>
        internal static bool IsSaleStale(DateTime soldAtUtc, DateTime nowUtc, int maxAgeDays)
        {
            if (maxAgeDays <= 0)
                return false;
            if (soldAtUtc == DateTime.MinValue)
                return true;
            return soldAtUtc < nowUtc.AddDays(-maxAgeDays);
        }

        /// <summary>
        /// 兩候選取低——<b>整個功能的核心，而且只有這一份實作</b>。
        /// </summary>
        /// <remarks>
        /// <para><list type="table">
        ///   <item><term>S 與 L 都能用、<c>S &gt;= L</c></term><description>L（跟板上最低價）</description></item>
        ///   <item><term>S 與 L 都能用、<c>S &lt; L</c></term><description>S（要更低才賣得掉）；但 <paramref name="saleIsOwnPrice"/> 時 L</description></item>
        ///   <item><term>只有 S</term><description>S</description></item>
        ///   <item><term>只有 L</term><description>L</description></item>
        ///   <item><term>都不能用，但有一個是被異常低價保護擋下來的</term><description><see cref="RelistOutcome.Hold"/>（一個 gil 都不改，但要說明原因）</description></item>
        ///   <item><term>什麼都沒有</term><description><see cref="RelistOutcome.NoData"/></description></item>
        /// </list></para>
        /// <para>🔑 兩個都被擋下來時回報 <b>L</b> 那一個：那是舊版（不開成交價定價）唯一會看到的原因，
        /// 判讀時比較不會被新來的那一側混淆。這是<b>刻意的、決定性的</b>選擇，不是巧合。</para>
        /// </remarks>
        /// <param name="saleIsOwnPrice">
        /// S 的成交單價是自家僱員掛過的價，而且使用者開了 <see cref="Configuration.RelistSkipOwnSalePrice"/>（呼叫端判好）。
        /// 只在兩個都能用、S 比 L 低時才有作用；只有 S 可用時照舊用 S。
        /// </param>
        internal static RelistDecision Decide(PriceCandidate listing, PriceCandidate sale, bool saleIsOwnPrice)
        {
            var listingOk = listing.IsUsable;
            var saleOk = sale.IsUsable;

            if (listingOk && saleOk)
            {
                if (sale.Target >= listing.Target)
                    return new RelistDecision(RelistOutcome.Price, listing.Target, listing, false);
                return saleIsOwnPrice
                    ? new RelistDecision(RelistOutcome.Price, listing.Target, listing, true)
                    : new RelistDecision(RelistOutcome.Price, sale.Target, sale, false);
            }

            if (listingOk)
                return new RelistDecision(RelistOutcome.Price, listing.Target, listing, false);
            if (saleOk)
                return new RelistDecision(RelistOutcome.Price, sale.Target, sale, false);

            if (listing.HeldByAnomaly)
                return new RelistDecision(RelistOutcome.Hold, -1, listing, false);
            if (sale.HeldByAnomaly)
                return new RelistDecision(RelistOutcome.Hold, -1, sale, false);

            return new RelistDecision(RelistOutcome.NoData, -1, PriceCandidate.None, false);
        }

        /// <summary>新上架退路的輸入（值快照）。</summary>
        /// <param name="Consensus">這一格品質的共識價（不夠時可能是兩種品質合併算的，見 <paramref name="ConsensusMixed"/>）。</param>
        /// <param name="DcCheapest">別的世界最便宜的一般掛單；-1＝沒有。</param>
        /// <param name="DcReference">別的世界掛單的參照價（下中位數）；-1＝沒有。</param>
        /// <param name="DcCount">別的世界可用的掛單筆數。</param>
        /// <param name="LastSale">最近一筆成交（過期也算）；-1＝沒有。只拿來擋天價掛單。</param>
        internal readonly record struct FallbackInputs(
            ConsensusResult Consensus, bool ConsensusMixed, long DcCheapest, long DcReference, int DcCount, long LastSale);

        /// <summary>
        /// 新上架的退路候選：有共識價用共識價（捨去到百位，比照 S）；沒有就用別的世界最便宜的一般掛單（套降價設定）。
        /// </summary>
        /// <remarks>
        /// 別的世界的掛單比最近一筆成交貴到倍數（含）以上就不用：只剩一筆天價掛單時取最便宜也躲不掉它。
        /// </remarks>
        internal static PriceCandidate FallbackCandidate(in FallbackInputs inputs, UndercutRule undercut, int ratio)
        {
            if (inputs.Consensus.IsKnown)
                return new PriceCandidate(inputs.Consensus.Price,
                    LastSoldPriceSource.RoundDownToHundred(inputs.Consensus.Price),
                    PriceSourceTag.Consensus, PriceAnomaly.Clear, DateTime.MinValue, false);

            if (inputs.DcCheapest <= 0)
                return PriceCandidate.None;
            if (inputs.LastSale > 0 && PriceConsensus.ValidRatio(ratio) && inputs.DcCheapest >= inputs.LastSale * ratio)
                return PriceCandidate.None;
            return new PriceCandidate(inputs.DcCheapest, ApplyUndercut(inputs.DcCheapest, undercut),
                PriceSourceTag.DcListing, PriceAnomaly.Clear, DateTime.MinValue, false);
        }

        /// <summary>
        /// 新上架（批次上架、快速上架停在上限價那格）才套的退路：板上沒有別人、成交沒有或過期（<see cref="RelistOutcome.NoData"/>）時
        /// 用退路候選；唯一的參考價被判成特殊交易（<see cref="RelistOutcome.Hold"/>）而有共識價時改用共識價。其餘原樣回傳。
        /// </summary>
        /// <remarks>
        /// 🔴 呼叫端負責只在「把新東西掛上去」時呼叫：重掛已經有價的掛單一律照舊（維持原價）。
        /// 板上最低是自家掛單時不套：那時候自家掛價就是錨點，既有流程會與它齊平。
        /// </remarks>
        internal static RelistDecision WithNewListingFallback(RelistDecision decision, bool ownIsLowest,
            PriceCandidate fallback)
        {
            if (!fallback.IsUsable)
                return decision;
            if (decision.Outcome == RelistOutcome.NoData && !ownIsLowest)
                return new RelistDecision(RelistOutcome.Price, fallback.Target, fallback, false);
            if (decision.Outcome == RelistOutcome.Hold && fallback.Source == PriceSourceTag.Consensus)
                return new RelistDecision(RelistOutcome.Price, fallback.Target, fallback, false);
            return decision;
        }

        /// <summary>一個時間點的人類可讀形式；<see cref="DateTime.MinValue"/> 一律畫成 <c>?</c>，絕不畫成某個看起來合理的日期。</summary>
        internal static string FormatAt(DateTime atUtc)
            => atUtc == DateTime.MinValue ? "?" : atUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

        /// <summary>一個候選寫進 log 的樣子。<b>只給診斷用</b>，沒有任何行為作用。</summary>
        internal static string Describe(PriceCandidate candidate)
        {
            if (!candidate.IsObserved)
                return "none";

            var source = candidate.Source.Length == 0 ? "?" : candidate.Source;
            var target = candidate.Target < 0 ? "-" : candidate.Target.ToString();
            var text = $"{source}:{candidate.Reference}->{target}";
            if (candidate.AtUtc != DateTime.MinValue || candidate.Stale)
                text += $"@{FormatAt(candidate.AtUtc)}";
            if (candidate.Stale)
                text += " stale";
            if (candidate.Anomaly.IsAnomalous)
                text += $" anomaly({candidate.Anomaly.Tag},{candidate.Anomaly.RatioText}x)";
            return text;
        }
    }
}
