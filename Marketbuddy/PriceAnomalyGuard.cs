using System;
using System.Collections.Generic;
using System.Globalization;

namespace Marketbuddy
{
    /// <summary>把一個參考價判成「異常低價」時，拿來當「正常價」的是什麼。</summary>
    /// <remarks>
    /// ⚠️ 刻意給 <see cref="None"/> 明確的 0：沒有零值的列舉會讓 <c>default</c>
    /// 落在一個無效值上，而那種壞法是靜默的。
    /// </remarks>
    internal enum AnomalyBaseline
    {
        /// <summary>不是異常（或保護沒開、資料不足以判斷）。</summary>
        None = 0,

        /// <summary>
        /// 同一個市場上<b>另一位賣家</b>的價格。
        /// 🔑 這是唯一擋得住「整體崩盤」誤判的判準：大家一起降價時，
        /// 最低價與下一位賣家的價會貼在一起，比值接近 1，不會被判成異常。
        /// </summary>
        Peer = 1,

        /// <summary>
        /// 沒有第二位賣家可以比，只好拿<b>自己目前的掛售價</b>當基準。
        /// ⚠️ 這一種分不出「別人打錯字」與「我自己開得太貴」，是誤判率最高的一種。
        /// </summary>
        Own = 2,
    }

    /// <summary>一次異常低價判定的完整結果（含「為什麼」），UI 與 log 都直接用它。</summary>
    /// <param name="Baseline">「正常價」的出處；<see cref="AnomalyBaseline.None"/>＝不是異常。</param>
    /// <param name="Reference">被判為可疑的那個參考價（別人的掛售價或成交價）；-1＝不知道。</param>
    /// <param name="NormalPrice">拿來比的那個「正常價」；-1＝不知道。</param>
    internal readonly record struct PriceAnomaly(AnomalyBaseline Baseline, long Reference, long NormalPrice)
    {
        /// <summary>「沒有異常」的單一表示法。🔑 價格一律 -1（＝不知道），<b>絕不用 0</b>。</summary>
        internal static readonly PriceAnomaly Clear = new(AnomalyBaseline.None, -1, -1);

        public bool IsAnomalous => Baseline != AnomalyBaseline.None;

        /// <summary>正常價是參考價的幾倍；算不出來時 -1（＝不知道，不是 0）。</summary>
        public double Ratio => Reference > 0 && NormalPrice > 0 ? (double)NormalPrice / Reference : -1d;

        /// <summary>給畫面與聊天訊息用的倍數字串；不知道時是 <c>?</c>。</summary>
        public string RatioText
        {
            get
            {
                var ratio = Ratio;
                return ratio < 0 ? "?" : ratio.ToString("0.#", CultureInfo.InvariantCulture);
            }
        }

        /// <summary>寫進 log 與待處理清單的來源標籤。</summary>
        public string Tag => Baseline switch
        {
            AnomalyBaseline.Peer => "anomaly-peer",
            AnomalyBaseline.Own => "anomaly-own",
            _ => string.Empty,
        };
    }

    /// <summary>
    /// 「異常低價保護」：別人手滑少打一個 0 的時候，不要跟著把自己的東西降到那個價。
    /// 🔴 <b>這個類別不改任何價格、不碰任何遊戲指標、不做 I/O、不寫 log。</b>
    /// 它只回答一個問題：「這個參考價看起來像不像打錯的？」
    /// 真正的處置（維持原價／不填入價格）由呼叫端做，那裡才印得出有意義的訊息。
    /// <b>判準是「離群」不是「變低」</b>——這是整個功能的核心取捨：
    /// <list type="number">
    ///   <item><b>同業基準（<see cref="AnomalyBaseline.Peer"/>，優先）</b>：
    ///         拿最低價與<b>下一位賣家</b>的最低價比。
    ///         ⚠️ 「下一位」是<b>另一個 RetainerId</b>，不是「第二便宜的那一列」——
    ///         同一個人把一堆拆成好幾筆、全部打錯同一個價是很常見的，
    ///         用「第二便宜」會拿打錯的價去跟打錯的價比，比值 1，整條保護靜默失效。</item>
    ///   <item><b>自家現價（<see cref="AnomalyBaseline.Own"/>，退而求其次）</b>：
    ///         整個市場上只有那一位賣家時沒有同業可比，只好拿自己的掛售價當基準。</item>
    /// </list>
    /// 🔑 <b>有同業資料時它就是唯一判準</b>，不會再回頭看自家現價：
    /// 「別人全都降價了」與「有一個人打錯字」的差別<b>只有同業基準看得出來</b>，
    /// 而拿自家現價去補判會把前者也判成異常，那就變成「永遠不降價」。
    /// 兩個門檻都可設定（<see cref="Configuration.AnomalyGuardMinNormalPrice"/> 與
    /// <see cref="Configuration.AnomalyGuardRatio"/>）：正常價要先達到金額門檻，
    /// 參考價才會因為「便宜太多倍」被判成異常。低價品本來就常常整批在幾十 gil 之間跳，
    /// 沒有金額門檻的話那些全部會被誤判。
    /// </summary>
    internal static class PriceAnomalyGuard
    {
        /// <summary>倍數的合理下限。低於它等於「稍微便宜一點就不降價」，那不是保護是癱瘓。</summary>
        internal const int MinRatio = 2;

        /// <summary>倍數的合理上限（純粹避免設定檔被手改成荒謬值之後整數溢位）。</summary>
        internal const int MaxRatio = 1000;

        /// <summary>
        /// 這一頁掛單裡「<b>下一位賣家</b>」的最低價；沒有第二位賣家時回 -1（＝不知道，不是 0）。
        /// </summary>
        /// <param name="listings">已經套過 HQ/NQ 篩選的掛單（呼叫端負責篩，這裡不猜）。</param>
        /// <param name="cheapestRetainerId">最便宜那一筆是誰掛的；他名下<b>所有</b>掛單都不算數。</param>
        /// <param name="ownRetainers">自家僱員；拿自己的價當「正常價」等於自己幫自己背書，一律排除。</param>
        internal static long PeerBaseline(
            IEnumerable<(uint Price, bool IsHq, ulong RetainerId)> listings,
            ulong cheapestRetainerId,
            IReadOnlySet<ulong>? ownRetainers)
        {
            long best = -1;
            foreach (var listing in listings)
            {
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

        /// <summary>設定物件版的 <see cref="Evaluate(long,long,long,bool,int,int)"/>。</summary>
        internal static PriceAnomaly Evaluate(long reference, long peerBaseline, long ownPrice, Configuration conf)
            => Evaluate(reference, peerBaseline, ownPrice,
                conf.AnomalyGuardEnabled, conf.AnomalyGuardMinNormalPrice, conf.AnomalyGuardRatio);

        /// <summary>這個參考價是不是異常低價。</summary>
        /// <param name="reference">要檢查的參考價（別人的最低掛售價，或最近一次成交價）。</param>
        /// <param name="peerBaseline">下一位賣家的最低價；-1＝沒有（見 <see cref="PeerBaseline"/>）。</param>
        /// <param name="ownPrice">自己目前的掛售價；-1＝不知道／不可信（快速上架停在上限價的那種）。</param>
        /// <param name="enabled">保護總開關。</param>
        /// <param name="minNormalPrice">「正常價」要先達到這個金額，這道保護才會作用；0＝停用。</param>
        /// <param name="ratio">正常價是參考價的幾倍（含）以上算異常。</param>
        /// <remarks>
        /// 🔴 全程整數運算：價格是錢，浮點數在邊界值上會給錯答案。
        /// 乘積最大 999,999,999 × 1000 ≈ 1e12，<c>long</c> 裝得下。
        /// </remarks>
        internal static PriceAnomaly Evaluate(long reference, long peerBaseline, long ownPrice,
            bool enabled, int minNormalPrice, int ratio)
        {
            if (!enabled)
                return PriceAnomaly.Clear;

            // 參考價本身不知道就沒得比。0 在價格欄是一個合法但荒謬的值，一律當成不知道。
            if (reference <= 0)
                return PriceAnomaly.Clear;

            // 設定被手改成無意義的值時一律放行：保護寧可不作用，也不要擋住正常的降價。
            if (ratio < MinRatio || ratio > MaxRatio || minNormalPrice <= 0)
                return PriceAnomaly.Clear;

            var threshold = (long)minNormalPrice;
            var multiple = (long)ratio;

            // ① 同業基準。🔑 有它的時候它就是唯一判準（理由寫在類別註解裡）。
            if (peerBaseline > 0)
                return peerBaseline >= threshold && peerBaseline >= reference * multiple
                    ? new PriceAnomaly(AnomalyBaseline.Peer, reference, peerBaseline)
                    : PriceAnomaly.Clear;

            // ② 只剩自己的現價可以比。
            // 🔴 上限價（快速上架的停車位）絕不能當基準：那不是一個真的價格，
            //    拿它去比會讓每一件快速上架的東西都被判成異常。
            if (ownPrice <= 0 || ownPrice >= Configuration.MAX_PRICE)
                return PriceAnomaly.Clear;

            return ownPrice >= threshold && ownPrice >= reference * multiple
                ? new PriceAnomaly(AnomalyBaseline.Own, reference, ownPrice)
                : PriceAnomaly.Clear;
        }
    }
}
