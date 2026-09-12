using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    /// <summary>
    /// <c>[MBDIAG]</c> 高頻診斷行的等級閘門。
    /// 🔑 <b>刻意不刪</b>：查市場問題時這些行仍然是唯一的證據來源。改成
    /// 「預設走 <c>Debug</c>、把設定打開才走 <c>Information</c>」——使用者的
    /// <c>LogLevel</c> 是 1（Serilog <c>Debug</c>），所以關著的時候這些行**還是在 log 裡**，
    /// 只是不再混進 <c>[INF]</c> 軸；要請使用者回報時再叫他打開開關。
    /// ⚠️ 只管高頻的敘事行。<c>REFUSED</c>／<c>TIMEOUT</c>／<c>MKTRESULT-ERR</c>／
    /// 快取清除這些「低頻＋代表真的出事了」的行**不經過這裡**，一律維持原本的等級。
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
