using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    /// <summary>
    /// <c>[MBDIAG]</c> 高頻診斷行的開關：關著時一行都不寫。
    /// ⚠️ 只管高頻的敘事行。<c>REFUSED</c>／<c>TIMEOUT</c>／<c>MKTRESULT-ERR</c>／
    /// 快取清除這些「低頻＋代表真的出事了」的行**不經過這裡**，一律維持原本的等級。
    /// </summary>
    internal static class MarketDiag
    {
        /// <summary>關著時<b>完全不寫</b>（連 Debug 都不寫）：使用者的記錄等級收得到 Debug，逐筆寫會佔掉實機記錄檔一成以上。</summary>
        internal static void Trace(string message)
        {
            if (Configuration.GetOrLoad().VerboseMarketDiagnostics)
                Log.Information(message);
        }

        /// <summary>封包處理器與 hook detour 專用，語意同 <see cref="Trace"/>；設定還沒載入時當成關著。</summary>
        /// <remarks>🔴 只讀已載入的設定、絕不觸發載入：GetOrLoad 的冷路徑會讀檔並可能 Save()，那兩種執行緒上不能做。</remarks>
        internal static void TracePacket(string message)
        {
            if (Configuration.Loaded?.VerboseMarketDiagnostics == true)
                Log.Information(message);
        }
    }
}
