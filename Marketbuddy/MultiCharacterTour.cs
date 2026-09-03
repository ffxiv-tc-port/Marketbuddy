using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Marketbuddy.Common;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy;

/// <summary>
/// 多角色重掛：跟 AutoRetainer 的「多開模式」(MultiMode) 協作，在 AR 處理完一個角色、
/// **準備登出換下一角之前**接手，跑一輪既有的全僱員重掛巡迴
/// （<see cref="MultiRetainerTour"/>，<see cref="TourMode.Reprice"/>），跑完把控制權還給 AR。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>零組件相依。</b>本外掛沒有 ECommons／AutoRetainerAPI，所以 AR 的契約全部用
/// Dalamud 原生 CallGate 的字串手動接（見 <see cref="IPCManager"/>）。名字打錯不會有錯誤訊息，
/// 只會永遠收不到事件——所以字串都寫成常數，而且是逐字抄自 AR 的 <c>AutoRetainerAPI/ApiConsts.cs</c>。
/// </para>
/// <para>
/// 🔴🔴 <b>AR 的等待沒有時限。</b>AR 端 <c>TaskPostprocessCharacterIPC</c> 排的等待任務是
/// <c>timeLimitMS: int.MaxValue</c>：我們只要有一條路徑忘記回覆
/// <c>AutoRetainer.FinishCharacterPostprocessRequest</c>，AR 就<b>永遠停在那裡</b>
/// （不會逾時、不會報錯）。所以「接手」與「歸還」由 <see cref="pendingFinish"/> 這一個旗標
/// 集中管理，成功／跳過／中止／逾時／使用者停止／外掛卸載每一條路徑都經過
/// <see cref="FinishIfPending"/>，而且它自己具冪等性。另外還有一道
/// <see cref="LegBackstopMinutes"/> 的總時限兜底，任何想不到的卡死都會在那裡歸還控制權。
/// </para>
/// <para>
/// 📌 <b>AR 端不需要任何改動</b>，這裡用的是它既有的 character postprocess 契約：
/// ① AR 廣播 <c>OnCharacterAdditionalTask</c>（要收尾的外掛現在登記）
/// ② 我們呼叫 <c>RequestCharacterPostprocess(自己的名字)</c>
/// ③ AR 逐一發 <c>OnCharacterReadyForPostprocess(外掛名)</c> 並開始無限等待
/// ④ 我們做完呼叫 <c>FinishCharacterPostprocessRequest</c>。
/// </para>
/// <para>
/// 🔑 <b>互斥靠的是 AR 自己的 TaskManager 而不是抑制旗標。</b>接手期間 AR 的
/// <c>TaskManager.IsBusy</c> 恆真（它正在跑那個等待任務），而 AR 兩條會搶僱員清單的路徑
/// （<c>SchedulerMain.Tick</c> 的接管、以及僱員感知自動開鈴）都寫著 <c>!TaskManager.IsBusy</c>，
/// 所以它不會來搶。⇒ 這裡<b>刻意不呼叫</b> <c>AutoRetainer.SetSuppressed</c>：那是給
/// 「使用者手動觸發、AR 不知情」的情境用的，在 AR 明明就在等我們的時候多按一個全域旗標，
/// 只會多出一條「忘記放開就永久癱瘓 AR」的失敗路徑。
/// </para>
/// <para>
/// 🔴 <b>零自動走位。</b>接手當下鈴不在互動距離內就直接跳過這個角色，不做任何導航、不移動角色。
/// </para>
/// </remarks>
internal sealed unsafe class MultiCharacterTour : IDisposable
{
    /// <summary>
    /// 傳喚鈴的物件名來源：<c>EObjName</c> 第 2000401 列。
    /// 🔑 抄 AutoRetainer <c>Lang.BellName</c>（台服實戰驗證過的做法）——**不寫死中文字串**，
    /// 名字從遊戲自己的表讀出來，換語言／換版本都跟著走。
    /// </summary>
    private const uint SummoningBellEObjNameRow = 2000401;

    /// <summary>互動距離。抄 AutoRetainer <c>Utils.GetValidInteractionDistance</c>。</summary>
    /// <remarks>
    /// ⚠️ AR 對「旅館」另外放寬到 4.75；這裡沒有旅館地圖清單，所以一律用比較嚴的 4.6。
    /// 差別只會讓「站在 4.6～4.75 米之間」被判成搆不到 ⇒ 跳過這個角色，**不會做錯事**。
    /// </remarks>
    private const float HousingBellInteractDistance = 6.5f;

    private const float DefaultBellInteractDistance = 4.6f;

    private const int BellInteractThrottleMs = 3000;
    private const int CloseListThrottleMs = 1000;
    private const int OpenBellTimeoutSeconds = 25;
    private const int StartTourTimeoutSeconds = 15;
    private const int CloseListTimeoutSeconds = 15;

    /// <summary>
    /// 單一角色從接手到歸還的總時限。
    /// 🔴 這是 AR 永不逾時的那個等待的**唯一**兜底，寧可切斷一趟跑很久的巡迴，
    /// 也不能讓 AR 卡死。一名僱員的批次自己就有 600 秒的看門狗，十名僱員的最壞情況
    /// 仍在這個數字之內。
    /// </summary>
    private const int LegBackstopMinutes = 120;

    private enum Phase
    {
        /// <summary>沒有接手中（可能已武裝、正在等 AR 叫號）。</summary>
        Idle,

        /// <summary>正在點傳喚鈴、等僱員選單開起來。</summary>
        OpeningBell,

        /// <summary>僱員選單開了，正在等巡迴可以起跑。</summary>
        StartingTour,

        /// <summary>巡迴跑著（結束由巡迴的事件通知）。</summary>
        Touring,

        /// <summary>收尾：把我們自己開的僱員選單關掉，然後歸還控制權。</summary>
        Closing,
    }

    /// <summary>這一名角色的結果。</summary>
    private enum LegOutcome
    {
        /// <summary>重掛跑完了（或這個角色本來就沒東西掛，那也是完成）。</summary>
        Done,

        /// <summary>沒做（搆不到鈴、起跑條件不成立…）。不是錯誤，整輪繼續。</summary>
        Skipped,

        /// <summary>跑到一半停了。整輪就此解除武裝。</summary>
        Aborted,
    }

    private readonly MarketGuiEventHandler gui;
    private readonly MultiRetainerTour tour;

    /// <summary>訂閱用的委派實例。Unsubscribe 必須傳**同一個實例**，所以存起來。</summary>
    private readonly Action onCharacterAdditionalTask;

    private readonly Action<string> onCharacterReadyForPostprocess;

    /// <summary>
    /// 本外掛在 Dalamud 眼中的名字。AR 的登記表與叫號都用這個字串比對，
    /// 🔴 <b>不寫死</b>：manifest 改名時寫死的那份會靜默失聯。
    /// </summary>
    private readonly string selfName;

    /// <summary>
    /// 這一輪還沒處理過的目標角色（CID）。武裝當下抄一份，處理完一個拿掉一個，
    /// 空了就是「整輪跑完」。
    /// </summary>
    private readonly HashSet<ulong> remaining = [];

    private readonly Dictionary<ulong, string> targetNames = [];

    /// <summary>
    /// 🔴 <b>「AR 正在等我們回覆」的唯一真相。</b>true 的期間，每一條離開路徑都必須經過
    /// <see cref="FinishIfPending"/>。
    /// </summary>
    private bool pendingFinish;

    private bool armed;
    private bool disposed;
    private Phase phase = Phase.Idle;
    private DateTime phaseStartedAt = DateTime.MinValue;
    private DateTime legStartedAt = DateTime.MinValue;
    private DateTime lastBellInteract = DateTime.MinValue;
    private DateTime lastCloseAttempt = DateTime.MinValue;

    /// <summary>
    /// 總時限已經觸發過了。🔴 沒有這個旗標的話,總時限的條件(以 <see cref="legStartedAt"/> 為準)
    /// 會每一幀都成立,把收尾流程一直打斷。
    /// </summary>
    private bool backstopFired;

    /// <summary>傳喚鈴名稱的快取（見 <see cref="SummoningBellName"/>）。</summary>
    private static string cachedBellName = string.Empty;

    private ulong currentLegCid;
    private LegOutcome legOutcome = LegOutcome.Skipped;
    private string legReason = string.Empty;

    // 整輪的統計（只用來寫收尾那一行，不影響任何行為）。
    private int roundTargets;
    private int roundDone;
    private int roundSkipped;
    private int roundRepriced;

    public MultiCharacterTour(MarketGuiEventHandler gui, MultiRetainerTour tour)
    {
        this.gui = gui;
        this.tour = tour;
        selfName = PluginInterface.InternalName;

        onCharacterAdditionalTask = OnCharacterAdditionalTask;
        onCharacterReadyForPostprocess = OnCharacterReadyForPostprocess;

        // 純通知用的抑制閘，形狀與 BatchReprice.ExternalDriverActive 相同：
        // 多角輪在跑的時候，每個角色各自的「全僱員重掛巡迴完成」都不響——
        // 使用者要的是整輪跑完才響那一聲（由 CompleteRound 負責）。
        tour.ExternalDriverActive = () => phase == Phase.Touring;
        tour.TourCompleted += OnTourCompleted;
        tour.TourAborted += OnTourAborted;

        IPCManager.SubscribeCharacterAdditionalTask(onCharacterAdditionalTask);
        IPCManager.SubscribeCharacterReadyForPostprocess(onCharacterReadyForPostprocess);
        IPCManager.SubscribeMainControlsDraw(DrawArMainControlsButton);

        Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        // 先退訂再收尾：收尾會取消巡迴，取消會回呼進來，這裡不想再被回呼一次。
        Framework.Update -= OnFrameworkUpdate;
        IPCManager.UnsubscribeCharacterAdditionalTask(onCharacterAdditionalTask);
        IPCManager.UnsubscribeCharacterReadyForPostprocess(onCharacterReadyForPostprocess);
        IPCManager.UnsubscribeMainControlsDraw(DrawArMainControlsButton);
        tour.TourCompleted -= OnTourCompleted;
        tour.TourAborted -= OnTourAborted;
        tour.ExternalDriverActive = null;

        armed = false;
        if (phase == Phase.Touring && tour.IsRunning)
        {
            try
            {
                tour.CancelByButton();
            }
            catch (Exception e)
            {
                Log.Warning(e, "MultiCharacterTour: 卸載時取消巡迴失敗");
            }
        }

        phase = Phase.Idle;
        // 🔴 最後一道保險：外掛被卸載時 AR 可能正停在無限等待上。
        FinishIfPending("外掛卸載");
    }

    /// <summary>這個功能有沒有武裝（＝正在等 AutoRetainer 叫號）。</summary>
    public bool IsArmed => armed;

    /// <summary>此刻是不是正握著 AutoRetainer 的控制權在做事。</summary>
    public bool IsWorking => pendingFinish;

    public int RoundTargets => roundTargets;

    public int RoundCompleted => roundDone + roundSkipped;

    public int RoundRepriced => roundRepriced;

    /// <summary>還沒輪到的角色名（畫在 UI 上用；空集合＝都跑完了）。</summary>
    public IEnumerable<string> RemainingNames()
    {
        foreach (var cid in remaining)
            yield return targetNames.TryGetValue(cid, out var name) ? name : $"CID {cid:X}";
    }

    /// <summary>目前正在處理哪一個角色（沒在處理時是空字串）。</summary>
    public string CurrentCharacterName =>
        currentLegCid != 0 && targetNames.TryGetValue(currentLegCid, out var name) ? name : string.Empty;

    /// <summary>
    /// 武裝：抄一份「這一輪要跑哪些角色」，然後等 AutoRetainer 叫號。
    /// </summary>
    /// <remarks>
    /// ⚠️ 武裝狀態<b>刻意不存檔</b>：重開遊戲／重載外掛一律回到解除狀態。
    /// 「上次忘了關」的自動化是這一類功能最容易出事的地方。
    /// </remarks>
    public bool TryArm(out string reason)
    {
        reason = string.Empty;
        if (armed)
            return true;

        var cids = IPCManager.GetAutoRetainerRegisteredCids();
        if (cids == null)
        {
            reason = "AutoRetainer is not installed or not ready".Loc();
            return false;
        }

        remaining.Clear();
        targetNames.Clear();
        foreach (var cid in cids)
        {
            var data = IPCManager.GetAutoRetainerCharacterData(cid);
            if (data == null || !data.Enabled || data.ExcludeRetainer)
                continue;
            remaining.Add(data.CID != 0 ? data.CID : cid);
            targetNames[data.CID != 0 ? data.CID : cid] =
                string.IsNullOrEmpty(data.Name) ? $"CID {cid:X}" : data.Name;
        }

        if (remaining.Count == 0)
        {
            reason = "no character is enabled in AutoRetainer's multi mode".Loc();
            targetNames.Clear();
            return false;
        }

        armed = true;
        roundTargets = remaining.Count;
        roundDone = 0;
        roundSkipped = 0;
        roundRepriced = 0;

        ChatGui.Print("[Marketbuddy] Multi-character relisting armed: ?? character(s) this round."
            .Loc(roundTargets));
        Log.Information($"[MultiCharacterTour] 武裝：本輪目標 {roundTargets} 角（{string.Join(", ", RemainingNames())}）。");

        // ⚠️ 沒開多開模式不是「不能武裝」——使用者可能正要去按 AR 的開關。
        // 但要當場講清楚，否則他會坐在那裡等一個永遠不會來的叫號。
        if (IPCManager.IsAutoRetainerMultiModeEnabled() != true)
            ChatGui.Print("[Marketbuddy] AutoRetainer's multi mode is not running yet - nothing will happen until you start it.".Loc());

        return true;
    }

    /// <summary>
    /// 解除武裝。正握著控制權的話會收尾（取消巡迴 → 關僱員選單 → 歸還控制權給 AutoRetainer）。
    /// </summary>
    public void Stop(string reason)
    {
        if (!armed && phase == Phase.Idle && !pendingFinish)
            return;

        armed = false;
        Log.Information($"[MultiCharacterTour] 解除武裝：{reason}。");
        ChatGui.Print("[Marketbuddy] Multi-character relisting stopped: ??".Loc(reason));

        if (phase == Phase.Touring && tour.IsRunning)
        {
            // 巡迴中止的回呼（OnTourAborted）會接手走完收尾流程。
            tour.CancelByButton();
            return;
        }

        if (phase != Phase.Idle)
        {
            legOutcome = LegOutcome.Skipped;
            legReason = reason;
            EnterPhase(Phase.Closing);
            return;
        }

        // 沒有進行中的工作，但控制權還在手上（理論上不該發生）——立刻還回去。
        FinishIfPending(reason);
    }

    // ── AutoRetainer 事件 ────────────────────────────────────────────────

    /// <summary>
    /// AR 要換角前的廣播：「想做角色收尾的外掛現在登記」。
    /// </summary>
    /// <remarks>
    /// ⚠️ AR 每次廣播前會先把登記表清空，所以每次都要重新登記；
    /// 但同一次廣播裡重複登記它會 <c>throw</c>，因此整段包在 try 裡。
    /// </remarks>
    private void OnCharacterAdditionalTask()
    {
        if (!armed || disposed)
            return;

        try
        {
            IPCManager.RequestAutoRetainerCharacterPostprocess(selfName);
            Log.Information($"[MultiCharacterTour] 已向 AutoRetainer 登記角色收尾（{selfName}）。");
        }
        catch (Exception e)
        {
            // 登記不上就是這一角不接手，AR 照常換角，不是災難。
            Log.Information($"[MultiCharacterTour] 向 AutoRetainer 登記角色收尾失敗：{e.Message}");
        }
    }

    /// <summary>
    /// 輪到我們了。
    /// 🔴 從這一刻起 AR 停在無限等待上，<see cref="pendingFinish"/> 必須為 true，
    /// 而且**每一條**離開路徑都要把它還回去。
    /// </summary>
    private void OnCharacterReadyForPostprocess(string pluginName)
    {
        // ⚠️ 這個事件是廣播給所有訂閱者的，參數才是「輪到誰」。不是我們就一定要忽略，
        // 否則會替別的外掛把它的回合結束掉。
        if (!string.Equals(pluginName, selfName, StringComparison.Ordinal))
            return;
        if (disposed)
            return;

        // ⚠️ 已經在接手中又被叫一次(理論上 AR 不會這樣做)：忽略。
        //    重設狀態會把正在跑的那一趟弄丟，而我們手上的 Finish 只有一次要還。
        if (pendingFinish || phase != Phase.Idle)
        {
            Log.Information($"[MultiCharacterTour] 收到重複的接手通知（phase={phase}），忽略。");
            return;
        }

        pendingFinish = true;
        backstopFired = false;
        legStartedAt = DateTime.UtcNow;
        legOutcome = LegOutcome.Skipped;
        legReason = string.Empty;
        currentLegCid = PlayerState.ContentId;

        // 接手期間 AR 是「在等我們」而不是「在跟我們搶」，所以本外掛內部所有
        // 「AutoRetainer 忙碌中就不要動」的閘門要一起讓開（見 AutoRetainerBridge）。
        AutoRetainerBridge.ExternalDriveActive = true;

        if (!armed)
        {
            FinishIfPending("已解除武裝");
            return;
        }

        // 🔴 別人已經在驅動巡迴（使用者手動按的那顆按鈕）：不要插隊，更不要在收尾時
        //    把他的僱員選單關掉。直接把控制權還回去，這一角本輪算跳過。
        if (tour.IsRunning)
        {
            Log.Information("[MultiCharacterTour] 已經有一趟巡迴在跑（手動觸發），這一角不接手。");
            FinishIfPending("已有巡迴在跑");
            return;
        }

        if (currentLegCid == 0 || !remaining.Contains(currentLegCid))
        {
            // 武裝之後才登入的新角色，或不在多開名單裡的角色：不是我們的目標。
            Log.Information($"[MultiCharacterTour] 目前角色（CID {currentLegCid:X}）不在本輪目標裡，直接把控制權還給 AutoRetainer。");
            FinishIfPending("不在本輪目標");
            return;
        }

        Log.Information($"[MultiCharacterTour] 接手角色 {CurrentCharacterName}（CID {currentLegCid:X}），開始開鈴。");
        EnterPhase(Phase.OpeningBell);
    }

    // ── 巡迴事件 ────────────────────────────────────────────────────────

    private void OnTourCompleted()
    {
        if (phase != Phase.Touring)
            return;
        roundRepriced += tour.TotalRepriced;
        legOutcome = LegOutcome.Done;
        legReason = string.Empty;
        EnterPhase(Phase.Closing);
    }

    private void OnTourAborted(string reason)
    {
        if (phase != Phase.Touring)
            return;
        roundRepriced += tour.TotalRepriced;
        legOutcome = LegOutcome.Aborted;
        legReason = reason;
        EnterPhase(Phase.Closing);
    }

    // ── 狀態機 ──────────────────────────────────────────────────────────

    private void EnterPhase(Phase next)
    {
        phase = next;
        phaseStartedAt = DateTime.UtcNow;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (phase == Phase.Idle)
            return;

        // 🔴 總時限兜底。任何想不到的卡死都在這裡把控制權還給 AutoRetainer。
        // ⚠️ 走的是正常的收尾階段（Closing）而不是直接歸還：僱員選單開著的時候
        //    AR 的 IsOccupied() 是真的，它接著要跑的登出會卡住。Closing 自己有 15 秒
        //    的看門狗，所以「關不掉」也一定會走到歸還。
        if (!backstopFired && DateTime.UtcNow - legStartedAt > TimeSpan.FromMinutes(LegBackstopMinutes))
        {
            backstopFired = true;
            Log.Information($"[MultiCharacterTour] 單一角色超過 {LegBackstopMinutes} 分鐘上限，強制收尾並歸還控制權。");
            if (tour.IsRunning)
                tour.CancelByButton();
            legOutcome = LegOutcome.Aborted;
            legReason = "timed out".Loc();
            armed = false;
            EnterPhase(Phase.Closing);
            return;
        }

        switch (phase)
        {
            case Phase.OpeningBell:
                TickOpeningBell();
                break;
            case Phase.StartingTour:
                TickStartingTour();
                break;
            case Phase.Touring:
                // 巡迴自己有看門狗，結束時一定會發出 TourCompleted 或 TourAborted。
                // 這一條只是保險：真的兩者都沒來的話不要無限等下去。
                if (!tour.IsRunning && DateTime.UtcNow - phaseStartedAt > TimeSpan.FromSeconds(10))
                {
                    legOutcome = LegOutcome.Skipped;
                    legReason = "tour ended without a result".Loc();
                    EnterPhase(Phase.Closing);
                }

                break;
            case Phase.Closing:
                TickClosing();
                break;
        }
    }

    private void TickOpeningBell()
    {
        if (gui.IsRetainerListOpen)
        {
            EnterPhase(Phase.StartingTour);
            return;
        }

        if (DateTime.UtcNow - phaseStartedAt > TimeSpan.FromSeconds(OpenBellTimeoutSeconds))
        {
            // 🔴 零自動走位：搆不到鈴就跳過這個角色，不去把角色移過去。
            legOutcome = LegOutcome.Skipped;
            legReason = "no summoning bell within reach".Loc();
            Log.Information($"[MultiCharacterTour] {CurrentCharacterName}：{OpenBellTimeoutSeconds} 秒內沒能開起僱員選單（多半是身邊沒有搆得到的傳喚鈴），跳過。");
            EnterPhase(Phase.Closing);
            return;
        }

        // ⚠️ 節流的是**嘗試**不是成功：找不到鈴的時候如果不記時間戳，這個函式會把
        //    整張物件表每一幀掃一次、掃滿 25 秒。25 秒 ÷ 3 秒 ≈ 8 次就夠了。
        if ((DateTime.UtcNow - lastBellInteract).TotalMilliseconds < BellInteractThrottleMs)
            return;
        lastBellInteract = DateTime.UtcNow;

        TryInteractWithReachableBell();
    }

    /// <summary>
    /// 找一個搆得到的傳喚鈴並且跟它互動。找不到回 false。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>不跨幀保存任何原生指標。</b>物件在這一幀找到、在這一幀用掉；
    /// <c>IGameObject</c> 的包裝是每格重用、存取時就地改寫位址的，存起來下一幀會靜默換人。
    /// </remarks>
    private bool TryInteractWithReachableBell()
    {
        var bellName = SummoningBellName();
        if (bellName.Length == 0)
            return false;

        var local = Objects.LocalPlayer;
        if (local == null)
            return false;

        var targetSystem = TargetSystem.Instance();
        if (targetSystem == null)
            return false;

        foreach (var obj in Objects)
        {
            if (obj.ObjectKind != ObjectKind.Housing && obj.ObjectKind != ObjectKind.EventObj)
                continue;
            if (!string.Equals(obj.Name.TextValue, bellName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!obj.IsTargetable)
                continue;

            var maxDistance = obj.ObjectKind == ObjectKind.Housing
                ? HousingBellInteractDistance
                : DefaultBellInteractDistance;
            if (Vector3.Distance(obj.Position, local.Position) >= maxDistance)
                continue;

            var address = obj.Address;
            if (address == IntPtr.Zero)
                continue;

            targetSystem->InteractWithObject(
                (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)address, false);
            Log.Information($"[MultiCharacterTour] {CurrentCharacterName}：已對傳喚鈴送出互動。");
            return true;
        }

        return false;
    }

    /// <summary>
    /// 傳喚鈴的物件名（讀遊戲自己的 <c>EObjName</c> 表，不寫死字串）。
    /// 查一次就記住：表的內容在執行期不會變。空字串代表查不到，那種情況下每次都會重查。
    /// </summary>
    private static string SummoningBellName()
    {
        if (cachedBellName.Length > 0)
            return cachedBellName;

        try
        {
            // ⚠️ 全名：Lumina.Excel.Sheets 有一個 Action 型別，整個 namespace 引進來會跟
            //    System.Action 撞名（本檔用了好幾個委派），所以只在這裡指名要哪一張表。
            var sheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.EObjName>();
            if (sheet != null && sheet.TryGetRow(SummoningBellEObjNameRow, out var row))
                return cachedBellName = row.Singular.ExtractText();
        }
        catch (Exception e)
        {
            Log.Warning(e, "MultiCharacterTour: EObjName 查表失敗");
        }

        return string.Empty;
    }

    private void TickStartingTour()
    {
        // 這個角色一件都沒掛：不是失敗，是「沒事可做」。
        // 🔴 僱員資料還沒 ready 的時候計數也是 0，跟「真的沒東西」同形——先確認讀得到，
        //    否則僱員選單剛開起來的那幾幀會被判成「沒事可做」而整個跳過。
        //    讀不到就什麼都不做，這一階段的看門狗會處理真的一直讀不到的情況。
        if (MultiRetainerTour.IsRetainerDataReady() && MultiRetainerTour.CountRetainersWithListings() == 0)
        {
            legOutcome = LegOutcome.Done;
            legReason = string.Empty;
            Log.Information($"[MultiCharacterTour] {CurrentCharacterName}：沒有僱員有掛單，視為完成。");
            EnterPhase(Phase.Closing);
            return;
        }

        if (tour.CanStart(TourMode.Reprice, out var reason))
        {
            tour.Start(TourMode.Reprice);
            if (tour.IsRunning)
            {
                EnterPhase(Phase.Touring);
                return;
            }
        }

        if (DateTime.UtcNow - phaseStartedAt > TimeSpan.FromSeconds(StartTourTimeoutSeconds))
        {
            legOutcome = LegOutcome.Skipped;
            legReason = reason.Length > 0 ? reason : "could not start the tour".Loc();
            Log.Information($"[MultiCharacterTour] {CurrentCharacterName}：巡迴無法起跑（{legReason}），跳過。");
            EnterPhase(Phase.Closing);
        }
    }

    private void TickClosing()
    {
        // 契約要求把畫面恢復成接手前的樣子：我們自己開的鈴自己關。
        if (!gui.IsRetainerListOpen)
        {
            CompleteLeg();
            return;
        }

        if (DateTime.UtcNow - phaseStartedAt > TimeSpan.FromSeconds(CloseListTimeoutSeconds))
        {
            Log.Information($"[MultiCharacterTour] {CurrentCharacterName}：僱員選單關不掉，仍然把控制權還給 AutoRetainer。");
            CompleteLeg();
            return;
        }

        if ((DateTime.UtcNow - lastCloseAttempt).TotalMilliseconds < CloseListThrottleMs)
            return;

        var retainerList = AddonHelpers.GetReadyAddon("RetainerList");
        if (retainerList == null)
            return;

        // 這是一個刻意的重試迴圈（關不掉就每秒再試一次，最多 15 秒）。
        // 守衛讓「同一扇窗」在它走完生命週期之前只按一次，逃生口之後才准補按；
        // 被擋下時不推進牆鐘，下一幀就能重新評估。
        if (AddonHelpers.FireIntCallback(retainerList, "RetainerList", -1))
            lastCloseAttempt = DateTime.UtcNow;
    }

    /// <summary>一名角色處理完（不論結果）：記帳 → 歸還控制權 → 看看整輪是不是跑完了。</summary>
    private void CompleteLeg()
    {
        var cid = currentLegCid;
        var name = CurrentCharacterName;
        remaining.Remove(cid);
        if (legReason.Length == 0)
            legReason = "no reason recorded".Loc();

        switch (legOutcome)
        {
            case LegOutcome.Done:
                roundDone++;
                Log.Information($"[MultiCharacterTour] {name}：重掛完成（本輪累計重掛 {roundRepriced} 件）。");
                break;
            case LegOutcome.Skipped:
                roundSkipped++;
                ChatGui.Print("[Marketbuddy] ??: skipped (??)".Loc(name, legReason));
                break;
            case LegOutcome.Aborted:
                roundSkipped++;
                armed = false;
                ChatGui.PrintError("[Marketbuddy] Multi-character relisting stopped at ??: ??".Loc(name, legReason));
                break;
        }

        phase = Phase.Idle;
        currentLegCid = 0;

        // 🔴 歸還控制權必須在記帳之後、而且不論結果都要做。
        FinishIfPending(legOutcome.ToString());

        if (armed && remaining.Count == 0)
            CompleteRound();
    }

    /// <summary>整輪跑完：這才是使用者要的那一聲。</summary>
    private void CompleteRound()
    {
        armed = false;
        ChatGui.Print("[Marketbuddy] Multi-character relisting finished: ?? character(s) relisted, ?? skipped, ?? item(s) repriced in total."
            .Loc(roundDone, roundSkipped, roundRepriced));
        Log.Information($"[MultiCharacterTour] 整輪完成：{roundDone} 角完成、{roundSkipped} 角跳過、共重掛 {roundRepriced} 件。");

        // 純通知，零行為。⚠️ 只有這裡響：途中每一角的巡迴收尾都被
        // tour.ExternalDriverActive 擋掉了（使用者明確要求「途中不要響」）。
        // 🔴 這裡在 Framework.Update 的鏈上，是主執行緒。
        TataruPraiseIPC.TryPraise("多角色重掛全完成");
    }

    /// <summary>
    /// 畫在 AutoRetainer 主視窗控制列（「重設計數器」那一列尾端）與僱員清單懸浮窗的
    /// 「武裝一輪」按鈕（2026-08-31 使用者要求）。AR 每幀 SendMessage 呼叫進來。
    /// 🔴 例外絕不能洩出去：這是 AR 的 Draw 鏈，Dalamud 對 Window.Draw 擲兩次例外會
    /// 永久關掉人家的主視窗。第一次失敗印一行 Information，之後靜默。
    /// </summary>
    private bool arDrawFaultReported;

    private void DrawArMainControlsButton()
    {
        try
        {
            if (!Configuration.GetOrLoad().MultiCharTourEnabled)
                return;

            ImGui.SameLine();
            if (!IsArmed)
            {
                if (ImGui.Button("Arm for one round".Loc() + "##mbmcarm_armain"))
                {
                    if (!TryArm(out var reason))
                        ChatGui.Print("[Marketbuddy] " + "Cannot arm: ??".Loc(reason));
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Marketbuddy multi-character relist: arm one round. Tataru praises only when every character is done.".Loc());
            }
            else
            {
                if (ImGui.Button("Stop round ??/??".Loc(RoundCompleted, RoundTargets) + "##mbmcstop_armain"))
                    Stop("stopped by user".Loc());
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Multi-character relisting armed. Click to stop this round.".Loc());
            }
        }
        catch (Exception e)
        {
            if (!arDrawFaultReported)
            {
                arDrawFaultReported = true;
                Log.Information($"[MultiCharacterTour] AR 控制列按鈕繪製失敗（只報這一次）：{e.Message}");
            }
        }
    }

    /// <summary>
    /// 🔴🔴 歸還 AutoRetainer 的控制權。具冪等性，而且**只有這一個地方**會呼叫 AR 的 Finish。
    /// </summary>
    private void FinishIfPending(string why)
    {
        AutoRetainerBridge.ExternalDriveActive = false;

        if (!pendingFinish)
            return;
        pendingFinish = false;

        try
        {
            IPCManager.FinishAutoRetainerCharacterPostprocess();
            Log.Information($"[MultiCharacterTour] 已把控制權還給 AutoRetainer（{why}）。");
        }
        catch (Exception e)
        {
            // 🔴 這一條是真正危險的失敗：AR 端的等待沒有時限。
            // 寫成 Information 讓使用者回報得到（他跑 LogLevel 1）。
            Log.Information($"[MultiCharacterTour] 🔴 歸還控制權失敗（{why}）：{e.Message}。AutoRetainer 可能停在等待狀態，請手動關掉它的多開模式。");
        }
    }
}
