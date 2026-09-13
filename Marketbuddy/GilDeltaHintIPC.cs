using System;
using Dalamud.Plugin.Ipc.Exceptions;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy;

/// <summary>
/// 單向橋接到「金幣流水帳」(GilDelta)：在<b>知道原因的那一刻</b>告訴它下一筆金幣變動是什麼。
/// </summary>
/// <remarks>
/// 🔴 <b>零組件相依。</b>只用 Dalamud 原生 CallGate 的字串契約（本外掛沒有 ECommons），
/// 對方沒安裝時本檔的每一條路徑都是 no-op。
/// CallGate 是純字串比對，名字打錯不會有任何錯誤訊息，只會永遠得到「這個頻道沒有人註冊」——
/// <b>靜默斷線</b>。所以字串都寫成常數，不散在呼叫點上。
/// 🔴 <b>只能從主執行緒(framework tick)呼叫。</b>IPC 的實作是在<b>呼叫端</b>的執行緒上跑的。
/// </remarks>
internal static class GilDeltaHintIPC
{
    /// <summary>
    /// <c>Func&lt;string, string, int, bool&gt;</c>：<c>Hint(category, note, ttlMs)</c>。
    /// 回 <c>true</c> 代表提示被收下（不代表之後真的配到一筆金幣變動）。
    /// </summary>
    internal const string TagHint = "GilDelta.Hint";

    /// <summary>
    /// note 的前綴。對方那側<b>沒有辦法知道是誰呼叫的</b>（Dalamud 不傳呼叫者身分），
    /// 所以這個前綴是流水帳上唯一能追出處的東西——不能省。
    /// </summary>
    internal const string NotePrefix = "Marketbuddy:";

    /// <summary>從市場板買東西（<c>GilEventCategory.MarketBoardBuy</c>）。</summary>
    /// <remarks>對方只讓它認領<b>自己錢包的減少</b>（<c>HintScope.DirectionMatches</c>）。</remarks>
    internal const string CategoryMarketBoardBuy = "MarketBoardBuy";

    /// <summary>提示的有效期（毫秒）。對方的上限是 300000，超過會被夾掉。</summary>
    /// <remarks>
    /// 15 秒：從按下購買到金幣真的變動只有一個封包來回，這個長度已經非常寬鬆。
    /// <b>刻意不設更長</b>——提示活著的期間會壓過對方所有以視窗推斷的規則，
    /// 期間內任何一筆自己錢包的減少都會被它認領走。
    /// </remarks>
    internal const int MarketBoardBuyTtlMs = 15_000;

    /// <summary>對方沒安裝的說明只寫一次。</summary>
    private static bool missingReported;

    /// <summary>失敗（非「沒安裝」）的說明只寫一次，避免一路刷 log。</summary>
    private static bool failureReported;

    /// <summary>
    /// 送出一則提示。
    /// 🔴 只能在 framework 執行緒上呼叫。
    /// </summary>
    /// <param name="category"><c>GilEventCategory</c> 的成員名。</param>
    /// <param name="note">說明；<b>一定</b>會被補上 <see cref="NotePrefix"/> 前綴。</param>
    /// <param name="ttlMs">有效期，1~300000。</param>
    /// <returns>對方收下了沒有；對方沒裝時一律 <c>false</c>。</returns>
    internal static bool TrySend(string category, string note, int ttlMs)
    {
        if (string.IsNullOrEmpty(category) || ttlMs <= 0)
            return false;

        // 前綴一定要在。呼叫端已經帶了就不重複疊。
        var text = note ?? string.Empty;
        if (!text.StartsWith(NotePrefix, StringComparison.Ordinal))
            text = NotePrefix + text;

        try
        {
            var accepted = PluginInterface
                .GetIpcSubscriber<string, string, int, bool>(TagHint)
                .InvokeFunc(category, text, ttlMs);

            // Information 級：使用者說「流水帳沒分對類」時，這是唯一問得出
            // 「Marketbuddy 到底有沒有送出去」的一行（使用者跑 LogLevel 1）。
            // 頻率很低（一次購買一行），不會淹沒任何東西。
            Log.Information($"[GilDelta] 已送出歸因提示 {category}（{text}），對方回傳 {accepted}。");
            return accepted;
        }
        catch (IpcNotReadyError)
        {
            // 對方沒安裝／沒載入。完全正常的狀態，所以只說一次就好。
            if (!missingReported)
            {
                missingReported = true;
                Log.Information(
                    "[GilDelta] 沒有偵測到 GilDelta（端點 " + TagHint + " 沒有人註冊），" +
                    "本工作階段不再送出金幣歸因提示，也不再重複這行訊息。");
            }

            return false;
        }
        catch (Exception e)
        {
            if (!failureReported)
            {
                failureReported = true;
                Log.Information($"[GilDelta] 送出歸因提示失敗（{category}）：{e.Message}。本工作階段不再重複這行訊息。");
            }

            return false;
        }
    }
}
