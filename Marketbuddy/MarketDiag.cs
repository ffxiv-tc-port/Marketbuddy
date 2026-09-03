using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    /// <summary>
    /// <c>[MBDIAG]</c> 高頻診斷行的等級閘門。
    ///
    /// <para>
    /// 由來：2026-08-02 那輪「台服市場查價會靜默拒絕」的鑑識留下了一整套 MBDIAG 探針，
    /// 而且全部寫 <c>Information</c>。它們當時是必要的（第二輪診斷推翻了第一輪的結論，
    /// 靠的就是這些行），但留在正式版之後變成常態噪音：實機兩天量到 3,990 行，
    /// 其中 QUERY／GATE 兩類就佔絕大多數。
    /// </para>
    ///
    /// <para>
    /// 🔑 <b>刻意不刪</b>：查市場問題時這些行仍然是唯一的證據來源。改成
    /// 「預設走 <c>Debug</c>、把設定打開才走 <c>Information</c>」——使用者的
    /// <c>LogLevel</c> 是 1（Serilog <c>Debug</c>），所以關著的時候這些行**還是在 log 裡**，
    /// 只是不再混進 <c>[INF]</c> 軸；要請使用者回報時再叫他打開開關。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 只管高頻的敘事行。<c>REFUSED</c>／<c>TIMEOUT</c>／<c>MKTRESULT-ERR</c>／
    /// 快取清除這些「低頻＋代表真的出事了」的行**不經過這裡**，一律維持原本的等級。
    /// 也絕不用 ECommons 的 <c>DuoLog</c>：那個在每一個等級都會無條件印進使用者的聊天視窗。
    /// </para>
    /// </summary>
    internal static class MarketDiag
    {
        /// <summary>設定關著時這些行走 Debug，打開時走 Information。</summary>
        internal static void Trace(string message)
        {
            if (Configuration.GetOrLoad().VerboseMarketDiagnostics)
                Log.Information(message);
            else
                Log.Debug(message);
        }
    }
}
