using System;
using Dalamud.Plugin.Ipc.Exceptions;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy;

/// <summary>
/// 單向橋接到「塔塔露誇獎」(TataruPraise)：一整輪市場重掛跑完時請它念一句。
/// </summary>
/// <remarks>
/// 🔴 <b>零組件相依。</b>只用 Dalamud 原生 CallGate 的字串契約（本外掛沒有 ECommons），
/// 對方沒安裝時本檔的每一條路徑都是 no-op。
/// <para>
/// 🔴 契約名逐字取自 TataruPraise 的 <c>IpcContract.cs</c> 與 <c>PraiseCategory.cs</c>。
/// CallGate 是純字串比對，名字打錯不會有任何錯誤訊息，只會永遠得到
/// 「這個頻道沒有人註冊」——<b>靜默斷線</b>。所以字串都寫成常數，不散在呼叫點上。
/// </para>
/// <para>
/// 🔴 <b>只能從主執行緒(framework tick)呼叫。</b>IPC 的實作是在呼叫端的執行緒上跑的，
/// 從背景 Task 叫過去等於把對方的程式碼拉到背景執行緒。目前兩個呼叫點都在
/// <c>Framework.Update</c> 的鏈上：<see cref="BatchReprice"/> 與
/// <see cref="MultiRetainerTour"/> 各自訂閱 <c>Framework.Update</c>，在那裡呼叫
/// <c>TickTaskQueue.Update()</c>，而 <c>Completed</c> 事件是 <c>Update()</c> 同步叫出來的。
/// </para>
/// <para>
/// ⚠️ 這是<b>單向通知</b>：回傳值只拿來寫記錄，不影響 Marketbuddy 的任何流程，
/// 不重試，也不會因此觸發任何市場操作。
/// </para>
/// </remarks>
internal static class TataruPraiseIPC
{
    /// <summary><c>Func&lt;bool&gt;</c>：總開關開著而且真的有可播的內容。</summary>
    internal const string TagIsAvailable = "TataruPraise.IsAvailable";

    /// <summary>
    /// <c>Func&lt;string, bool&gt;</c>：<b>指定的那個情境</b>現在出得了聲嗎
    /// （總開關開著＋這個情境沒被關掉＋這個情境至少有一句已合成的語音）。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>閘門要問的是這一個，不是 <see cref="TagIsAvailable"/>。</b>後者問的是
    /// 「整池<b>有某個情境</b>播得出來」，於是「別的情境有語音、<b>市場</b>一句都沒有」時
    /// 照樣通過，接著 <c>Praise</c> 回 <c>false</c>——呼叫端就分不出「不能出聲」與「這次剛好沒出聲」。
    /// <para>
    /// 📌 它刻意<b>不看冷卻</b>：冷卻是「這次剛好不出聲」，不是「不能出聲」。
    /// </para>
    /// <para>
    /// 🔴 舊版 TataruPraise 沒有註冊這個端點，<c>InvokeFunc</c> 會擲 <c>IpcNotReadyError</c>，
    /// 剛好落進既有的 catch＝安靜不出聲，這是正確的 fail-safe。
    /// <b>失敗時絕不可以退回去叫 <see cref="TagIsAvailable"/></b>——那樣就把這個端點的意義整個抵銷掉了。
    /// </para>
    /// </remarks>
    internal const string TagIsAvailableFor = "TataruPraise.IsAvailableFor";

    /// <summary><c>Func&lt;string, bool&gt;</c>：從指定情境的誇獎池挑一句念。</summary>
    internal const string TagPraise = "TataruPraise.Praise";

    /// <summary>
    /// 送過去的情境字串。
    /// ⚠️ TataruPraise 拿這個字串當 <c>pool.json</c> 的鍵，<b>對不上就靜默不出聲</b>
    /// （它會寫一行 Information 說「情境沒有已合成語音的句子」）。
    /// 對方端的常數是 <c>PraiseCategory.Market</c>，逐字相同。
    /// </summary>
    internal const string CategoryMarket = "市場";

    /// <summary>
    /// 請塔塔露念一句。對方沒裝、關著、或池裡沒東西，這裡都是安靜的 no-op。
    /// </summary>
    /// <param name="reason">寫進記錄用的來源描述，讓 log 分得出是哪一條邊觸發的。</param>
    internal static void TryPraise(string reason)
    {
        if (!Configuration.GetOrLoad().TataruPraiseOnRelistDone)
            return;

        try
        {
            // 先問 IsAvailableFor(「市場」)：對方的總開關關著、這個情境被使用者關掉、或這個
            // 情境一句已合成的都沒有，就不要浪費它的冷卻。
            if (!PluginInterface.GetIpcSubscriber<string, bool>(TagIsAvailableFor).InvokeFunc(CategoryMarket))
                return;

            var accepted = PluginInterface.GetIpcSubscriber<string, bool>(TagPraise).InvokeFunc(CategoryMarket);
            // Information 級：這是「使用者說沒出聲」時唯一問得出真相的一行（使用者跑 LogLevel 1）。
            Log.Information($"[TataruPraise] {reason}：Praise(「{CategoryMarket}」) 回傳 {accepted}。");
        }
        catch (IpcNotReadyError)
        {
            // 對方沒安裝／沒載入。這是完全正常的狀態，刻意不寫 log——沒裝的人每輪都會走到這裡。
        }
        catch (Exception e)
        {
            Log.Information($"[TataruPraise] 呼叫失敗（{reason}）：{e.Message}");
        }
    }
}
