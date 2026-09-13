using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    /// <summary>
    /// <c>[MBDIAG]</c> 高頻診斷行的等級閘門。
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
