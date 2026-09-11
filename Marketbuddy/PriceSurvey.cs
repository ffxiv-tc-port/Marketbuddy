using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dalamud.Game.Network.Structures;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    /// <summary>
    /// 跨世界掛售價格巡檢。
    ///
    /// <para>
    /// 「帶著自己正在賣的清單，站在某一個世界的市場前面，把每一件的行情逐一問過一遍並記下來」。
    /// 預設跑完一個世界就停；要換世界是**另一顆按鈕**，換到之後仍然要使用者再按一次「掃描這個世界」。
    /// </para>
    ///
    /// <para>
    /// 🔴 <b>一律手動觸發。</b>啟動的入口只有兩個，兩個都是使用者按的按鈕：
    /// 「掃描這個世界」（<see cref="RequestStart"/>），以及「武裝一輪」
    /// （<see cref="RequestArmTour"/>，2026-09-11 新增的自動續跑）。
    /// 這個類別**沒有**訂閱 AutoRetainer 的任何事件、**沒有**訂閱任何 addon 生命週期事件，
    /// 也**不會**在 <c>Framework.Update</c> 裡因為任何遊戲狀態自己開始跑
    /// （<see cref="OnFrameworkUpdate"/> 在閒置時的唯一副作用，就是把那兩個
    /// 「使用者按了按鈕」的旗標消費掉）。
    /// </para>
    ///
    /// <para>
    /// 🔴 <b>自動續跑（武裝）只在「使用者手動武裝的那一輪」裡有效。</b>
    /// 武裝要 <c>PriceSurveyAutoTour</c> 這個<b>預設關</b>的設定先打開，武裝狀態
    /// <b>刻意不存檔</b>（重開遊戲、重載外掛一律回到解除狀態），而且有三道停止閘：
    /// ①同一輪裡每個世界最多去一次 ②一輪最多換 <c>PriceSurveyAutoTourMaxWorlds</c> 個世界
    /// ③只有「這個世界的清單整份掃完」才會續跑——逾時、使用者按停、關視窗、離開遊戲世界、
    /// 世界被動改變、讓路給重掛／下架，一律當場解除武裝並說明原因。
    /// 換世界一律走 Lifestream 的具名 IPC 端點，<b>絕不</b>用空參數的 <c>/li</c> 聊天指令。
    /// </para>
    ///
    /// <para>
    /// 🔴 <b>整條路徑只讀不寫。</b>它只呼叫
    /// <c>InfoProxyItemSearch.RequestData()</c>（＝遊戲自己的市場查詢，跟玩家開市場板搜尋
    /// 送的是同一個請求）並接收 <c>IMarketBoard.OfferingsReceived</c>。
    /// 沒有 <c>SetRetainerMarketPrice</c>、沒有任何 <c>InventoryManager</c> 寫入、
    /// 沒有任何掛售／改價／下架動作，也不碰 <see cref="BatchReprice"/>／
    /// <see cref="BatchDelist"/>／<see cref="MultiCharacterTour"/> 的狀態。
    /// </para>
    ///
    /// <para>
    /// 🔴 <b>節流沿用全外掛共用的 <see cref="MarketRequestGate"/></b>（同一個 static 實例，
    /// 不另開更短的路徑）：每一件之間至少隔閘門當下的間隔，而且每一次送出都記進閘門，
    /// 所以巡檢跟批次改價、跟互動視窗的重查共用同一份「上次送出是什麼時候」。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>未經實機證實的前提</b>：本功能假設「站在市場前面、**沒有**開啟僱員出售品視窗」
    /// 時 <c>RequestData()</c> 仍然送得出去。假設不成立時的表現被刻意設計成
    /// 「只是查不到東西」而不是崩潰：不對回應解參任何指標、不假設 <c>ListingCount</c>
    /// 會被寫、逾時走既有的重試路徑；而且**第一件連續逾時 3 次就停下來**並在畫面與
    /// 記錄檔上明說「這個情境送不出查詢」，不會傻跑幾百件。
    /// </para>
    /// </summary>
    internal sealed unsafe class PriceSurvey : IDisposable
    {
        // ---- 節奏常數 ---------------------------------------------------------
        // 🔑 這些值刻意與 BatchReprice 對齊（那組是 2026-08-02/03 兩輪實機量測定下來的），
        //    只有嘗試次數放少：巡檢沒有「這一格一定要有答案」的壓力，問不到就記一筆
        //    「問不到」往下走，比反覆重試更誠實也更快。
        private const int NoResponseDeadlineMs = 2500; // 什麼封包都沒來 => 大概被吞掉了
        private const int ResponseTimeoutMs = 3500;    // 有東西來了之後的硬上限
        private const int EmptyResultGraceMs = 1000;   // history 來了、offerings 沒來 => 沒人在賣
        private const int RetryBackoffBaseMs = 300;
        private const int RetryBackoffCapMs = 1200;
        private const int MaxAttempts = 3;

        /// <summary>第一件連續這麼多次「什麼都沒回來」就判定這個情境送不出查詢，整輪停下。</summary>
        private const int FirstItemGiveUpAfter = 3;

        /// <summary>準備階段最多等採購清單重讀這麼久；超過就用上一份繼續，不卡住。</summary>
        private const double ShoppingReloadWaitSeconds = 5;

        /// <summary>武裝中：請 Lifestream 送過去之後最多等這麼久，還沒到就解除武裝。</summary>
        private const double TourTravelTimeoutSeconds = 300;

        /// <summary>武裝中：抵達之後先站定這麼久再開始掃描（讓 Lifestream 把它自己的收尾做完）。</summary>
        private const double TourSettleSeconds = 8;

        /// <summary>
        /// 武裝中：等「現在可以動了」最多等這麼久（別的引擎在用市場、AR 忙、Lifestream 還在忙）。
        /// 🔴 一定要有上限：沒有的話它會安安靜靜地永遠等下去，而使用者看不出它其實卡住了。
        /// </summary>
        private const double TourWaitSeconds = 120;

        private const string Diag = "[MBDIAG]";

        internal enum SurveyState
        {
            Idle,
            Preparing,
            Running,

            /// <summary>
            /// 暫停：整輪的清單、進度、統計全部留著，只是不再送任何查詢。
            ///
            /// <para>
            /// 🔴 <b>離開暫停只有兩條路，兩條都要使用者按按鈕</b>：按「繼續掃描」
            /// （<see cref="RequestResume"/>）或按「停止」（<see cref="RequestStop"/>）。
            /// 沒有任何遊戲狀態、任何事件、任何逾時會讓它自己繼續跑——
            /// 「換完世界自動接著掃」那種串接正是這個功能刻意不做的事。
            /// </para>
            ///
            /// <para>
            /// ⚠️ 暫停中 <see cref="IsRunning"/> 是 <c>false</c>：重掛／下架／手動重查
            /// 拿它當互斥判準，暫停的巡檢沒有在用市場查詢的額度，不該擋住它們。
            /// </para>
            /// </summary>
            Paused,
        }

        private enum ItemPhase
        {
            Throttle,
            Request,
            Wait,
        }

        /// <summary>
        /// 自動續跑（武裝一輪）走到哪一步。
        /// ⚠️ 刻意給 <see cref="Off"/> 明確的 0：沒有零值的列舉會讓 <c>default</c>
        /// 落在一個有意義的值上，而那種壞法是靜默的。
        /// </summary>
        internal enum TourPhase
        {
            /// <summary>沒有武裝。</summary>
            Off = 0,

            /// <summary>正在（或即將）掃這個世界，等它收場。</summary>
            Scanning = 1,

            /// <summary>這個世界收場了，正在挑下一個世界。</summary>
            Choosing = 2,

            /// <summary>已經請 Lifestream 送過去，等抵達。</summary>
            Travelling = 3,

            /// <summary>到了，站定一下再開始掃描。</summary>
            Settling = 4,
        }

        private readonly MarketGuiEventHandler gui;

        // ---- 使用者按鈕交過來的意圖（唯一入口）--------------------------------
        private bool startRequested;
        private string? stopRequested;
        private string? pauseRequested;
        private bool resumeRequested;
        private DateTime pausedAt;

        // ---- 準備階段 --------------------------------------------------------
        private Task<PrepareResult>? prepareTask;

        // ---- 一輪的狀態 ------------------------------------------------------
        private PriceSurveyItemList? list;
        private readonly List<uint> itemQueue = [];
        private readonly Dictionary<uint, List<PriceSurveyItem>> byItemId = new();
        private int queueIndex;
        private uint worldId;
        private string worldName = string.Empty;
        private DateTime runStartedAt;
        private int recordedRows;
        private int okCount, emptyCount, refusedCount, timeoutCount, cacheCount;
        private int skippedAlreadyDone;

        // ---- 採購清單（第四種來源，與上面那份「我在賣什麼」完全分開）------------
        // 🔴 這一整組只有 conf.PriceSurveyShoppingList 打開時才有內容，而那個設定預設關。
        //    關著的時候下面每一個集合都是空的，整條路徑等於不存在。
        /// <summary>這一輪開始時的設定快照：中途改設定不會讓半輪的規則不一致。</summary>
        private bool shoppingEnabled;

        /// <summary>這一輪要順便查行情的採購清單道具（已經與掛售清單去重）。</summary>
        private readonly HashSet<uint> shoppingIds = [];

        /// <summary>這一輪因為採購清單而多排進佇列的件數（不含與掛售清單重疊、順路查到的）。</summary>
        private int shoppingExtraQueued;

        /// <summary>這一輪寫進 <see cref="ShoppingSurveyLog"/> 的列數。</summary>
        private int shoppingRecordedRows;

        /// <summary>因為保留時間內已經問過而略過的採購清單件數。</summary>
        private int shoppingSkippedAlreadyDone;

        /// <summary>開始這一輪時的 <see cref="ShoppingList.Generation"/>；用來等那次重讀回來。</summary>
        private int shoppingGenerationAtStart;

        /// <summary>
        /// 開始這一輪的是哪一個角色。
        /// 🔑 刻意在開始時抄下來、不在收場時現讀：登出那條收場路徑上
        /// <see cref="CurrentContentId"/> 已經變成 0，現讀會把「誰掃的」寫成「不知道」。
        /// </summary>
        private ulong runContentId;

        // ---- 目前這一件 -----------------------------------------------------
        private ItemPhase phase;
        private uint currentItemId;
        private int attempt;
        private DateTime notBefore;
        private DateTime waitStart;
        private double sendGapMs = -1;
        private bool probeAnswered, probeRefused, probeEmpty;
        private int firstItemNoResponseStreak;

        // ---- 換世界（Lifestream）------------------------------------------
        // 🔴 手動那條路刻意與掃描完全分離：按「前往」不會開始掃描。
        //    「到了下一個世界」之後仍然要使用者自己再按一次「掃描這個世界」——
        //    唯一的例外是使用者自己武裝過的那一輪自動續跑（見下面那一組）。
        private string? travelRequestWorld;
        private readonly List<(uint WorldId, string Name)> travelTargets = [];
        private uint travelTargetsBuiltFor = uint.MaxValue;

        /// <summary>上一次建 <see cref="travelTargets"/> 時看到的排除清單修訂號。</summary>
        private int travelTargetsExclusionRevision = -1;

        private bool lifestreamMissing;

        /// <summary>
        /// 這個資料中心的全部世界（含目前所在的這一個，<b>不</b>套用排除清單）。
        /// 畫面上的「哪些世界不要碰」勾選清單就是它。
        /// </summary>
        /// <remarks>
        /// 🔴 <b>整份換掉，不就地改</b>：這份清單在 framework 執行緒上重建、由繪製執行緒讀，
        /// 組好一份新的再把參考換過去，繪製執行緒就永遠拿到一份完整的清單，
        /// 不會在別人 <c>Clear()</c> 到一半的時候去列舉它。
        /// </remarks>
        private IReadOnlyList<(uint WorldId, string Name)> dataCentreWorlds = [];

        // ---- 自動續跑（使用者手動武裝的那一輪）------------------------------
        // 🔴 這一整組都不存檔：重開遊戲、重載外掛一律回到解除狀態。
        private bool tourArmed;
        private TourPhase tourPhase;
        private bool armTourRequested;
        private string? disarmTourRequested;

        /// <summary>這一輪已經去過（或正要去）的世界：同一輪裡每個世界最多去一次。</summary>
        private readonly HashSet<uint> tourVisited = [];

        private int tourWorldsChanged;
        private int tourMaxWorlds;
        private uint tourTravelTargetId;
        private string tourTravelTargetName = string.Empty;

        /// <summary>目前這一步的耐心上限；<see cref="DateTime.MaxValue"/>＝這一步沒有上限。</summary>
        private DateTime tourDeadline = DateTime.MaxValue;

        /// <summary>抵達之後要站到什麼時候才開始掃描。</summary>
        private DateTime tourSettleUntil;

        /// <summary>這個世界收場了而且可以往下一個走。</summary>
        private bool tourAdvancePending;

        /// <summary>
        /// 這一輪收場的理由是「這個世界該問的都在保留時間內問過了」。
        /// 🔑 那不是失敗，所以武裝中可以往下一個世界走（見 <see cref="Finish"/>）。
        /// </summary>
        private bool nothingLeftOnThisWorld;

        // ---- 市場封包接收（形狀逐字比照 BatchReprice，只是不驅動任何改價）------
        // 🔑 這裡比 BatchReprice 多帶一個 Quantity：採購清單要回答「這個世界買得到幾個」。
        //    刻意只加在本類別自己的緩衝區上，MarketDataCache 與 BatchReprice 的三元組
        //    一個位元組都沒動——那條路上的定價邏輯不該因為一個顯示欄位而被碰。
        private readonly List<(uint Price, bool IsHq, ulong RetainerId, uint Quantity)> captured = [];
        private bool offeringsPending;
        private bool offeringsReceived;
        private bool historySeen;
        private DateTime historySeenAt;
        private uint pendingItemId;
        private int lastAcceptedRequestId = int.MinValue;

        private Configuration conf => Configuration.GetOrLoad();

        internal SurveyState State { get; private set; } = SurveyState.Idle;

        /// <summary>
        /// 其他引擎的互斥判準：巡檢在跑的時候大家讓開。
        /// ⚠️ <b>暫停中不算在跑</b>：暫停的巡檢一個查詢都沒在送，擋住重掛／下架只會讓人卡住。
        /// </summary>
        internal bool IsRunning => State is SurveyState.Preparing or SurveyState.Running;

        /// <summary>暫停中（清單與進度都還在，等使用者按「繼續掃描」或「停止」）。</summary>
        internal bool IsPaused => State == SurveyState.Paused;

        /// <summary>暫停的原因（已在地化）；沒有暫停時是空字串。</summary>
        internal string PauseReason { get; private set; } = string.Empty;

        /// <summary>暫停了多久（秒）；沒有暫停時 -1。</summary>
        internal double PausedForSeconds
            => State == SurveyState.Paused ? (DateTime.UtcNow - pausedAt).TotalSeconds : -1;

        /// <summary>
        /// 每一次一輪收場（跑完、停止、暫停）就 +1。
        /// 🔑 畫面拿它當「記錄檔可能變了」的訊號去重讀——少了它，
        /// 「哪些世界掃過」那份標示會停在視窗第一次打開時的樣子，掃完一個世界也不會更新。
        /// </summary>
        internal int RunSerial { get; private set; }

        internal int Total => itemQueue.Count;
        internal int Processed => queueIndex;
        internal uint CurrentItemId => currentItemId;
        internal uint WorldId => worldId;
        internal string WorldName => worldName;
        internal int RecordedRows => recordedRows;
        internal int SkippedAlreadyDone => skippedAlreadyDone;

        /// <summary>這一輪因為採購清單而多排進佇列的件數（0＝沒開那個功能，或全部順路）。</summary>
        internal int ShoppingExtraQueued => shoppingExtraQueued;

        /// <summary>這一輪已經寫進採購記錄檔的列數。</summary>
        internal int ShoppingRecordedRows => shoppingRecordedRows;

        /// <summary>這一輪採購清單裡因為保留時間內已經問過而略過的件數。</summary>
        internal int ShoppingSkippedAlreadyDone => shoppingSkippedAlreadyDone;
        internal string SourceLabel => list?.SourceLabel ?? string.Empty;

        /// <summary>
        /// 目前所在世界的名稱，<b>在 framework 執行緒上拍好的快照</b>。
        /// 🔴 繪製執行緒不可以自己去讀 <c>PlayerState</c>：那是原生指標解參
        /// （<c>PlayerState.Instance()</c> / 本地玩家實體），由遊戲主執行緒每幀重建，
        /// 讀到一半被換掉就是 AccessViolationException，而 AVE 在 .NET Core 是
        /// corrupted-state exception，<c>try</c>/<c>catch</c> 攔不到。所以畫面一律讀這份快照。
        /// </summary>
        internal string CurrentWorldName { get; private set; } = string.Empty;

        /// <summary>目前所在世界的 id（framework 執行緒快照，0＝還沒登入）。</summary>
        internal uint CurrentWorldId { get; private set; }

        /// <summary>已經進到遊戲世界裡了（framework 執行緒快照）。</summary>
        internal bool LoggedIn { get; private set; }

        /// <summary>目前角色的 ContentId（framework 執行緒快照，0＝還沒登入）。</summary>
        internal ulong CurrentContentId { get; private set; }

        /// <summary>畫面上那一行狀態文字；閒置時是上一輪的結果或失敗原因。</summary>
        internal string StatusText { get; private set; } = string.Empty;

        /// <summary>
        /// 上一輪停下來的原因是「這個情境送不出查詢」。畫面用它顯示那句具體的建議。
        /// </summary>
        internal bool LastRunLookedUnsupported { get; private set; }

        /// <summary>
        /// 同一個資料中心裡、Lifestream 說去得了、<b>而且不在排除清單上</b>的世界
        /// （不含目前這一個）。換世界選單與自動續跑<b>共用這一份</b>——
        /// 🔑 一份真值，所以選單上看得到的世界就是自動續跑可能選到的世界，反之亦然。
        /// </summary>
        internal IReadOnlyList<(uint WorldId, string Name)> TravelTargets => travelTargets;

        /// <summary>這個資料中心的全部世界（含目前這一個、含被排除的）；畫面上的勾選清單用。</summary>
        internal IReadOnlyList<(uint WorldId, string Name)> DataCentreWorlds => dataCentreWorlds;

        /// <summary>問過 Lifestream 但它不在（或 IPC 還沒好）。畫面要說的話與「沒有可去的世界」不同。</summary>
        internal bool LifestreamMissing => lifestreamMissing;

        /// <summary>上一次換世界請求的結果，顯示在按鈕旁邊。</summary>
        internal string TravelStatus { get; private set; } = string.Empty;

        /// <summary>自動續跑武裝中嗎。</summary>
        internal bool IsTourArmed => tourArmed;

        /// <summary>自動續跑走到哪一步（沒武裝時是 <see cref="TourPhase.Off"/>）。</summary>
        internal TourPhase TourState => tourPhase;

        /// <summary>這一輪武裝已經換過幾個世界。</summary>
        internal int TourWorldsChanged => tourWorldsChanged;

        /// <summary>這一輪武裝最多換幾個世界（武裝當下抄的，中途改設定不影響這一輪）。</summary>
        internal int TourMaxWorlds => tourMaxWorlds;

        /// <summary>正在前往的世界名（沒有在前往時是空字串）。</summary>
        internal string TourTravelTargetName => tourTravelTargetName;

        /// <summary>自動續跑那一行狀態文字（已在地化）。</summary>
        internal string TourStatus { get; private set; } = string.Empty;

        internal int OkCount => okCount;
        internal int EmptyCount => emptyCount;
        internal int RefusedCount => refusedCount;
        internal int TimeoutCount => timeoutCount;
        internal int CacheCount => cacheCount;

        /// <summary>剩下的預估秒數（用閘門當下的間隔算）。還沒開始跑時 -1。</summary>
        internal double EstimatedRemainingSeconds
        {
            get
            {
                if (State != SurveyState.Running)
                    return -1;
                var remaining = itemQueue.Count - queueIndex;
                if (remaining <= 0)
                    return 0;
                return remaining * MarketRequestGate.IntervalMs / 1000.0;
            }
        }

        public PriceSurvey(MarketGuiEventHandler gui)
        {
            this.gui = gui;
            MarketBoard.OfferingsReceived += OnOfferingsReceived;
            MarketBoard.HistoryReceived += OnHistoryReceived;
            Framework.Update += OnFrameworkUpdate;
        }

        public void Dispose()
        {
            Framework.Update -= OnFrameworkUpdate;
            MarketBoard.HistoryReceived -= OnHistoryReceived;
            MarketBoard.OfferingsReceived -= OnOfferingsReceived;
            // 🔴 卸載一律回到解除武裝，而且安靜地做：這條路不一定在 framework 執行緒上，
            //    不可以往聊天視窗印字（所以不走 DisarmTour）。
            tourArmed = false;
            tourPhase = TourPhase.Off;
            tourAdvancePending = false;
            armTourRequested = false;
            disarmTourRequested = null;

            // 卸載時安靜地收手；不重設 InfoProxy 以外的任何東西。
            if (State != SurveyState.Idle)
                ResetRun();
        }

        // =====================================================================
        //  使用者意圖（UI 執行緒 → framework 執行緒）
        // =====================================================================

        /// <summary>
        /// 🔴 <b>這是本功能唯一的啟動入口</b>，只由巡檢視窗上的按鈕呼叫。
        /// 這裡只立一個旗標，真正的檢查與啟動全部發生在 framework 執行緒上。
        /// </summary>
        internal void RequestStart()
        {
            if (IsRunning)
                return;
            startRequested = true;
        }

        /// <summary>
        /// 🔴 <b>「去下一個世界」的唯一入口</b>，只由巡檢視窗上那顆按鈕呼叫，
        /// <b>按一次只換一次</b>：這裡只記下一個目的地，framework 執行緒上消費掉之後就清空，
        /// 沒有重試、沒有佇列、也沒有「到了就自動開始掃描」的串接。
        /// </summary>
        internal void RequestChangeWorld(string world)
        {
            // 🔴 暫停中也不換世界：換過去之後那一輪的進度就對不上這個世界了，
            //    與其偷偷把它丟掉，不如讓使用者先決定要「繼續掃描」還是「停止」。
            if (IsRunning || IsPaused || string.IsNullOrWhiteSpace(world))
                return;
            travelRequestWorld = world;
            TravelStatus = string.Empty;
        }

        /// <summary>
        /// 🔴 <b>「武裝一輪自動續跑」的唯一入口</b>，只由巡檢視窗上那顆按鈕呼叫。
        /// 這裡只立旗標：真正的武裝（以及它會不會被拒絕）發生在 framework 執行緒上，
        /// 因為它要讀遊戲狀態、還要往聊天視窗印一行。
        /// </summary>
        internal void RequestArmTour()
        {
            if (tourArmed)
                return;
            armTourRequested = true;
        }

        /// <summary>
        /// 🔴 <b>解除武裝的唯一入口</b>（使用者按「解除武裝」）。同樣只立旗標。
        /// </summary>
        internal void RequestDisarmTour(string reason)
        {
            if (!tourArmed)
                return;
            disarmTourRequested = reason;
        }

        /// <summary>
        /// 現在能不能武裝。回 false 時 <paramref name="reason"/> 是給使用者看的一句話。
        /// 🔴 這個方法每一幀都被繪製執行緒呼叫（按鈕要不要變灰），所以只讀 framework 執行緒
        /// 拍好的快照與設定，<b>不</b>碰 <c>PlayerState</c> 的原生指標、<b>不</b>改任何狀態。
        /// </summary>
        internal bool CanArmTour(out string reason)
        {
            reason = string.Empty;
            if (tourArmed)
            {
                reason = "Already armed".Loc();
                return false;
            }

            if (!conf.PriceSurveyEnabled)
            {
                reason = "This feature is not enabled yet".Loc();
                return false;
            }

            if (!conf.PriceSurveyAutoTour)
            {
                reason = "Automatic world hopping is turned off".Loc();
                return false;
            }

            if (IsPaused)
            {
                reason = "A price survey is paused - continue it or stop it first".Loc();
                return false;
            }

            if (!LoggedIn || CurrentWorldId == 0)
            {
                reason = "Not logged in".Loc();
                return false;
            }

            if (lifestreamMissing)
            {
                reason = "Lifestream is not installed".Loc();
                return false;
            }

            return true;
        }

        /// <summary>
        /// 取消整輪（進度丟掉）。按鈕與任何不可回復的讓路條件都走這裡。
        /// ⚠️ 暫停中也收：那是「放棄這次暫停」。
        /// </summary>
        internal void RequestStop(string reason)
        {
            if (!IsRunning && !IsPaused)
                return;
            stopRequested = reason;
        }

        /// <summary>
        /// 暫停（進度留著）。按鈕、關視窗、以及可回復的讓路條件走這裡。
        /// 🔴 暫停<b>不會</b>自己解除：要繼續一定得使用者再按一次。
        /// </summary>
        internal void RequestPause(string reason)
        {
            if (!IsRunning)
                return;
            pauseRequested = reason;
        }

        /// <summary>
        /// 🔴 <b>「繼續掃描」的唯一入口</b>，只由巡檢視窗上那顆按鈕呼叫。
        /// 這裡只立旗標，真正的檢查與續跑發生在 framework 執行緒上。
        /// </summary>
        internal void RequestResume()
        {
            if (!IsPaused)
                return;
            resumeRequested = true;
        }

        /// <summary>
        /// 現在能不能開始。回 false 時 <paramref name="reason"/> 是給使用者看的一句話。
        /// </summary>
        internal bool CanStart(out string reason)
        {
            reason = string.Empty;
            if (IsRunning)
            {
                reason = "A price survey is already running".Loc();
                return false;
            }

            if (IsPaused)
            {
                reason = "A price survey is paused - continue it or stop it first".Loc();
                return false;
            }

            if (!conf.PriceSurveyEnabled)
            {
                reason = "This feature is not enabled yet".Loc();
                return false;
            }

            if (IPCManager.IsLocked)
            {
                reason = "locked via IPC by another plugin".Loc();
                return false;
            }

            if (AutoRetainerBridge.IsBusy)
            {
                reason = "AutoRetainer is busy (or MultiMode is enabled), stop it first".Loc();
                return false;
            }

            if (gui.BatchEngine?.IsRunning == true)
            {
                reason = "A relist is running".Loc();
                return false;
            }

            if (gui.DelistEngine?.IsRunning == true)
            {
                reason = "A delist is running".Loc();
                return false;
            }

            // 🔴 這個方法每一幀都被繪製執行緒呼叫（按鈕要不要變灰），所以只讀
            //    framework 執行緒拍好的快照，不直接碰 PlayerState 的原生指標。
            if (!LoggedIn || CurrentWorldId == 0)
            {
                reason = "Not logged in".Loc();
                return false;
            }

            // 🔴 排除清單的第三個用途：站在被排除的世界上也不准開始掃描。
            //    ⚠️ 刻意<b>不</b>套用在 CanResume 上——暫停中的那一輪已經收了一半的資料，
            //    讓它跑完永遠比把它擱在那裡好；要丟掉有「停止並放棄」那顆按鈕。
            if (conf.IsWorldExcluded(CurrentWorldId))
            {
                reason = "?? is on your excluded list".Loc(
                    CurrentWorldName.Length > 0 ? CurrentWorldName : $"#{CurrentWorldId}");
                return false;
            }

            return true;
        }

        /// <summary>
        /// 暫停中的這一輪現在能不能接著跑。回 false 時 <paramref name="reason"/> 是給使用者看的一句話。
        /// 🔴 這個方法每一幀都被繪製執行緒呼叫（按鈕要不要變灰），所以只讀 framework 執行緒
        /// 拍好的快照，不直接碰 <c>PlayerState</c> 的原生指標。
        /// </summary>
        internal bool CanResume(out string reason)
        {
            reason = string.Empty;
            if (State != SurveyState.Paused)
            {
                reason = "Nothing is paused".Loc();
                return false;
            }

            if (queueIndex >= itemQueue.Count)
            {
                reason = "That round has nothing left to survey".Loc();
                return false;
            }

            if (!conf.PriceSurveyEnabled)
            {
                reason = "This feature is not enabled yet".Loc();
                return false;
            }

            if (IPCManager.IsLocked)
            {
                reason = "locked via IPC by another plugin".Loc();
                return false;
            }

            if (AutoRetainerBridge.IsBusy)
            {
                reason = "AutoRetainer is busy (or MultiMode is enabled), stop it first".Loc();
                return false;
            }

            if (gui.BatchEngine?.IsRunning == true)
            {
                reason = "A relist is running".Loc();
                return false;
            }

            if (gui.DelistEngine?.IsRunning == true)
            {
                reason = "A delist is running".Loc();
                return false;
            }

            if (!LoggedIn || CurrentWorldId == 0)
            {
                reason = "Not logged in".Loc();
                return false;
            }

            // 🔴 這一輪的每一列都記著它是在 worldId 問到的。在別的世界接著跑會把
            //    另一個世界的行情寫成這個世界的，那是靜默的資料汙染，不是小問題。
            if (CurrentWorldId != worldId)
            {
                reason = "That round belongs to ??, and you are not there".Loc(worldName);
                return false;
            }

            return true;
        }

        // =====================================================================
        //  幀時鐘
        // =====================================================================

        private void OnFrameworkUpdate(IFramework framework)
        {
            UpdateWorldSnapshot();

            // 「哪些世界掃過」那份小記錄檔：第一次讀在執行緒池上，之後兩行都是 no-op。
            PriceSurveyWorldLog.BeginLoad();
            PriceSurveyWorldLog.PumpLoad();

            // 採購清單：讀檔在執行緒池上，名稱換 id 在這條（framework）執行緒上。
            // 🔴 沒有人要求重讀時這兩行都是 no-op；它們不會讓任何事情開始跑。
            ShoppingList.BeginLoad();
            ShoppingList.PumpLoad();

            // 🔴 武裝／解除武裝的旗標在<b>任何</b>狀態下都要收得到：使用者掃到一半按
            //    「解除武裝」時，這個類別正在 Running，TickIdle 根本不會跑。
            TourConsumeRequests();

            if (State == SurveyState.Idle)
            {
                TickIdle();
                return;
            }

            if (State == SurveyState.Paused)
            {
                TickPaused();
                return;
            }

            startRequested = false;
            resumeRequested = false;

            if (stopRequested is { } reason)
            {
                stopRequested = null;
                Finish(reason, unsupported: false);
                return;
            }

            if (pauseRequested is { } pauseReason)
            {
                pauseRequested = null;
                PauseOrFinish(pauseReason);
                return;
            }

            // 讓路條件：任何一個成立就立刻收手。
            // 🔑 可回復的（別的引擎在用市場、視窗關了）改成**暫停**，進度留著；
            //    不可回復的（登出、世界換了）才是真的結束——那兩種情況下的進度
            //    接不回去，留著只會讓人在錯的世界按「繼續」。
            if (CheckStandDown(out var standDownReason, out var recoverable))
            {
                if (recoverable)
                    PauseOrFinish(standDownReason);
                else
                    Finish(standDownReason, unsupported: false);
                return;
            }

            switch (State)
            {
                case SurveyState.Preparing:
                    TickPreparing();
                    return;
                case SurveyState.Running:
                    TickRunning(DateTime.UtcNow);
                    return;
            }
        }

        /// <summary>
        /// 暫停時的一格。
        /// 🔴 這裡<b>只</b>消費使用者的兩個旗標，外加一道「這一輪還接得回去嗎」的守衛。
        /// 沒有任何路徑會讓它自己恢復掃描。
        /// </summary>
        private void TickPaused()
        {
            // 暫停中不接受「開始」與「換世界」；旗標消費掉，免得之後莫名其妙生效。
            startRequested = false;
            travelRequestWorld = null;

            if (stopRequested is { } reason)
            {
                stopRequested = null;
                resumeRequested = false;
                Finish(reason, unsupported: false);
                return;
            }

            pauseRequested = null;

            // 🔴 守衛：登出或換了世界，這一輪就再也接不回去了——與其讓它掛在那裡，
            //    不如當場結束並說清楚，這樣「繼續掃描」永遠不會把別的世界的行情
            //    寫成這個世界的。
            if (!LoggedIn)
            {
                resumeRequested = false;
                Finish("Left the game world, the paused round was dropped".Loc(), unsupported: false);
                return;
            }

            if (CurrentWorldId != worldId)
            {
                resumeRequested = false;
                Finish("The world changed, the paused round was dropped".Loc(), unsupported: false);
                return;
            }

            if (!resumeRequested)
                return;
            resumeRequested = false;
            ResumeRun();
        }

        /// <summary>
        /// 閒置時的一格。
        /// 🔴 這裡會做的事只有三件，而且每一件都要有使用者按過按鈕才會發生：
        /// 消費「開始掃描」旗標、消費「去某個世界」旗標、以及在所在世界變了之後
        /// 重建可前往世界的清單（那只是一份下拉選單的內容，不會讓任何事情開始跑）。
        /// </summary>
        private void TickIdle()
        {
            if (CurrentWorldId != 0 && !TravelTargetsAreFresh())
                RebuildTravelTargets(CurrentWorldId);

            if (travelRequestWorld is { } destination)
            {
                travelRequestWorld = null;
                ChangeWorldOnce(destination);
            }

            // 🔴 只有使用者自己武裝過的那一輪，這一步才會做事；沒武裝時它整個是 no-op。
            //    刻意排在「重建可前往世界清單」之後、「消費開始旗標」之前：
            //    它挑世界要用剛建好的那一份，而它要求開始掃描的方式就是立同一個旗標。
            TourTick();

            // 🔴 沒有任何遊戲狀態可以讓巡檢自己開始跑：只有這個旗標。
            if (!startRequested)
                return;
            startRequested = false;
            BeginRun();
        }

        /// <summary>
        /// 「可前往世界清單」還算不算數。
        /// 🔑 除了換世界之外，<b>排除清單改過也要重建</b>——少了後面那個條件，
        /// 勾掉一個世界之後選單會維持舊內容直到下一次換世界，而那是靜默的。
        /// </summary>
        private bool TravelTargetsAreFresh()
            => CurrentWorldId == travelTargetsBuiltFor &&
               travelTargetsExclusionRevision == conf.WorldExclusionRevision;

        /// <summary>
        /// 重建「這裡去得了哪些世界」的下拉選單內容。
        /// 候選來自 World 表裡同一個資料中心的列，再逐一問 Lifestream 去不去得了——
        /// 🔑 刻意<b>不</b>自己維護一份世界名單，也不靠 <c>IsPublic</c>（台服八個正式世界
        /// 的 <c>IsPublic</c> 全是 false，照它篩會得到空清單）。Lifestream 才是
        /// 「這個角色現在去得了哪裡」的權威。
        /// </summary>
        private void RebuildTravelTargets(uint currentWorldId)
        {
            travelTargets.Clear();
            travelTargetsBuiltFor = currentWorldId;
            travelTargetsExclusionRevision = conf.WorldExclusionRevision;
            lifestreamMissing = false;

            var currentRow = PlayerState.CurrentWorld.ValueNullable;
            if (currentRow == null)
            {
                // 讀不到就當作還沒建起來，下一格再試。
                travelTargetsBuiltFor = uint.MaxValue;
                return;
            }

            var dataCentre = currentRow.Value.DataCenter.RowId;
            var sheet = DataManager.GetExcelSheet<World>();
            if (sheet == null)
                return;

            // ① 先把這個資料中心的世界整份列出來（含目前所在的、含被排除的）。
            //    🔑 這一份要完整：畫面上的勾選清單靠它，而下面問 Lifestream 的迴圈
            //    可能中途就 return（沒裝），半份清單會讓被排除的世界從畫面上消失，
            //    使用者就再也勾不掉它了。
            var everyWorld = new List<(uint WorldId, string Name)>();
            foreach (var row in sheet)
            {
                if (row.DataCenter.RowId != dataCentre)
                    continue;
                var name = row.Name.ExtractText();
                if (name.Length == 0)
                    continue;
                everyWorld.Add((row.RowId, name));
            }

            everyWorld.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            dataCentreWorlds = everyWorld;

            // ② 再篩出「去得了、而且使用者沒排除」的。
            foreach (var (worldId, name) in everyWorld)
            {
                if (worldId == currentWorldId)
                    continue;

                // 🔴 排除清單在這裡就把世界拿掉，所以換世界選單與自動續跑一次搞定：
                //    兩邊讀的都是 travelTargets，不可能只有一邊漏掉。
                if (conf.IsWorldExcluded(worldId))
                    continue;

                var reachable = IPCManager.CanLifestreamVisit(name);
                if (reachable == null)
                {
                    // 端點不存在 ⇒ Lifestream 沒裝。不必再問剩下的世界。
                    lifestreamMissing = true;
                    travelTargets.Clear();
                    return;
                }

                if (reachable == true)
                    travelTargets.Add((worldId, name));
            }

            travelTargets.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        }

        /// <summary>
        /// 🔴 <b>呼叫一次只換一次。</b>沒有重試、沒有排程。
        /// 抵達之後<b>不會</b>自己開始掃描——除非使用者武裝了自動續跑，那時候是
        /// <see cref="TourTickSettling"/> 在抵達之後另外立一次開始旗標。
        /// </summary>
        /// <returns>Lifestream 收下這次請求了才是 true。</returns>
        private bool ChangeWorldOnce(string destination)
        {
            if (IPCManager.IsLocked)
            {
                TravelStatus = "Halted by another plugin via IPC".Loc();
                return false;
            }

            if (AutoRetainerBridge.IsBusy)
            {
                TravelStatus = "AutoRetainer is busy (or MultiMode is enabled), stop it first".Loc();
                return false;
            }

            if (IPCManager.IsLifestreamBusy())
            {
                TravelStatus = "Lifestream is busy right now.".Loc();
                return false;
            }

            var accepted = IPCManager.LifestreamChangeWorld(destination);
            TravelStatus = accepted
                ? "Asked Lifestream to travel to ??. Press Scan this world again once you get there.".Loc(destination)
                : "Lifestream did not accept that request (it may be busy, or that world is not reachable right now).".Loc();
            Log.Information(
                $"[Marketbuddy] 巡檢：向 Lifestream 請求前往 {destination}，接受={accepted}。" +
                $"這次呼叫只換一次世界，自動續跑武裝中={tourArmed}。");
            return accepted;
        }

        // =====================================================================
        //  自動續跑（使用者手動武裝的那一輪）
        //
        //  🔴 三道停止閘，缺一不可：
        //     ①同一輪裡每個世界最多去一次（tourVisited）
        //     ②一輪最多換 tourMaxWorlds 個世界
        //     ③只有「整份掃完」或「這個世界該問的都在保留時間內問過了」才往下走
        //  🔴 換世界一律走 Lifestream 的具名 IPC 端點，絕不用空參數的 /li 聊天指令。
        //  🔴 每一種收場都會寫一行 Information 並往聊天視窗印一句：
        //     使用者永遠看得出「它為什麼沒有繼續」。
        // =====================================================================

        /// <summary>消費使用者的武裝／解除武裝旗標。🔴 任何狀態下每幀都跑。</summary>
        private void TourConsumeRequests()
        {
            if (disarmTourRequested is { } disarmReason)
            {
                disarmTourRequested = null;
                armTourRequested = false;
                DisarmTour(disarmReason);
            }

            if (!armTourRequested)
                return;
            armTourRequested = false;
            ArmTour();
        }

        /// <summary>
        /// 武裝一輪。🔴 只從 framework 執行緒呼叫（<see cref="TourConsumeRequests"/>）。
        /// </summary>
        private void ArmTour()
        {
            if (tourArmed)
                return;

            if (!CanArmTour(out var why))
            {
                TourStatus = "Cannot arm: ??".Loc(why);
                return;
            }

            tourArmed = true;
            tourVisited.Clear();

            // 🔴 起點也算「去過了」：同一輪裡不會再繞回來。
            tourVisited.Add(CurrentWorldId);
            tourWorldsChanged = 0;
            tourMaxWorlds = Math.Clamp(conf.PriceSurveyAutoTourMaxWorlds, 1, Configuration.MAX_TOUR_WORLDS);
            tourAdvancePending = false;
            tourTravelTargetId = 0;
            tourTravelTargetName = string.Empty;
            EnterTourPhase(TourPhase.Scanning, 0);

            string opening;
            if (IsRunning)
            {
                // 已經在掃了：等它收場，Finish 會接手。
                opening = "the scan already running";
            }
            else if (conf.IsWorldExcluded(CurrentWorldId))
            {
                // 站在被排除的世界上：這裡不掃，直接去挑下一個。
                tourAdvancePending = true;
                opening = "travelling on (this world is excluded)";
            }
            else
            {
                // 🔴 走既有的唯一啟動入口，同一格稍後就會被 TickIdle 消費掉。
                startRequested = true;
                opening = "scanning this world";
            }

            TourStatus = "Automatic world hopping armed: up to ?? world(s) this round.".Loc(tourMaxWorlds);
            Log.Information(
                $"[Marketbuddy] 巡檢自動續跑武裝：起點 {CurrentWorldName}({CurrentWorldId})，" +
                $"上限 {tourMaxWorlds} 個世界，開場動作={opening}。" +
                "武裝狀態不存檔；只有「整份掃完」才會往下一個世界走，其餘任何收場都會當場解除武裝。");
            ChatGui.Print("[Marketbuddy] " + TourStatus);
        }

        /// <summary>
        /// 解除武裝並說清楚為什麼。
        /// 🔴 只從 framework 執行緒呼叫（它會往聊天視窗印字）。
        /// </summary>
        private void DisarmTour(string reason)
        {
            if (!tourArmed)
            {
                tourPhase = TourPhase.Off;
                return;
            }

            tourArmed = false;
            tourPhase = TourPhase.Off;
            tourAdvancePending = false;
            tourTravelTargetId = 0;
            tourTravelTargetName = string.Empty;
            tourDeadline = DateTime.MaxValue;

            TourStatus = "Automatic world hopping stopped: ??".Loc(reason);
            Log.Information(
                $"[Marketbuddy] 巡檢自動續跑解除武裝（{reason}）：" +
                $"本輪換了 {tourWorldsChanged}/{tourMaxWorlds} 個世界，去過 {tourVisited.Count} 個。");
            ChatGui.Print("[Marketbuddy] " + TourStatus);
        }

        private void EnterTourPhase(TourPhase phase, double timeoutSeconds)
        {
            tourPhase = phase;
            tourDeadline = timeoutSeconds > 0
                ? DateTime.UtcNow.AddSeconds(timeoutSeconds)
                : DateTime.MaxValue;
        }

        /// <summary>
        /// 自動續跑的一格。🔴 只在 <see cref="SurveyState.Idle"/> 時被呼叫
        /// （<see cref="TickIdle"/>）——掃描中什麼都不必做，收場時 <see cref="Finish"/> 會接手。
        /// </summary>
        private void TourTick()
        {
            if (!tourArmed)
                return;

            // 使用者中途把設定關掉＝他不要這個功能了，當場解除。
            if (!conf.PriceSurveyEnabled || !conf.PriceSurveyAutoTour)
            {
                DisarmTour("the setting was turned off".Loc());
                return;
            }

            switch (tourPhase)
            {
                case TourPhase.Scanning:
                    TourTickScanning();
                    return;
                case TourPhase.Choosing:
                    TourChooseNextWorld();
                    return;
                case TourPhase.Travelling:
                    TourTickTravelling();
                    return;
                case TourPhase.Settling:
                    TourTickSettling();
                    return;
            }
        }

        /// <summary>
        /// 掃描那一步：只有兩種合法結果——收場時 <see cref="Finish"/> 立了
        /// <see cref="tourAdvancePending"/>，或者開始旗標還沒被消費掉。
        /// </summary>
        /// <remarks>
        /// 🔑 其餘情況表示這一輪<b>根本沒開始</b>（<see cref="BeginRun"/> 的
        /// <see cref="CanStart"/> 擋下了它，那條路不會走到 <see cref="Finish"/>）。
        /// 沒有這道守衛的話武裝會安安靜靜地永遠掛在那裡。
        /// </remarks>
        private void TourTickScanning()
        {
            if (tourAdvancePending)
            {
                tourAdvancePending = false;
                EnterTourPhase(TourPhase.Choosing, TourWaitSeconds);
                TourChooseNextWorld();
                return;
            }

            if (startRequested)
                return;

            DisarmTour(StatusText.Length > 0
                ? "the scan did not start (??)".Loc(StatusText)
                : "the scan did not start".Loc());
        }

        /// <summary>
        /// 挑下一個世界：<b>資料最舊的優先</b>，從來沒掃過的算最舊。
        /// </summary>
        /// <remarks>
        /// 🔑 候選就是換世界選單那一份（<see cref="travelTargets"/>）——已經濾掉排除清單、
        /// 濾掉 Lifestream 說去不了的，所以「選單上看得到的」與「自動續跑可能選到的」
        /// 是同一組，不可能一邊漏掉。
        /// <para>
        /// 🔑 「最舊」讀的是 <see cref="PriceSurveyWorldLog"/> 現成的 <c>AtUtc</c>，
        /// 不另外存一份時間——兩份時間遲早會不一致，而不一致是靜默的。
        /// </para>
        /// </remarks>
        private void TourChooseNextWorld()
        {
            if (tourWorldsChanged >= tourMaxWorlds)
            {
                DisarmTour("this round's limit of ?? world(s) was reached".Loc(tourMaxWorlds));
                return;
            }

            if (lifestreamMissing)
            {
                DisarmTour("Lifestream is not installed".Loc());
                return;
            }

            // 讓路條件：這些都是「現在不行」，所以等一下再看，但不是無限期地等。
            if (!TourCanProceed(out var blocked))
            {
                if (DateTime.UtcNow > tourDeadline)
                {
                    DisarmTour(blocked);
                    return;
                }

                TourStatus = "Waiting before travelling on: ??".Loc(blocked);
                return;
            }

            // 可前往世界清單還沒為現在這個世界建好（剛換完世界的那幾格）：下一格再挑。
            if (!TravelTargetsAreFresh())
            {
                if (DateTime.UtcNow > tourDeadline)
                    DisarmTour("the list of worlds you can travel to never became available".Loc());
                return;
            }

            uint bestId = 0;
            var bestName = string.Empty;
            var bestAt = DateTime.MaxValue;
            foreach (var (worldId, name) in travelTargets)
            {
                // 🔴 同一輪裡每個世界最多去一次。
                if (tourVisited.Contains(worldId))
                    continue;

                // 🔴 排除清單其實已經在 RebuildTravelTargets 濾掉了；這裡再擋一次，
                //    成本是零，而漏掉的代價是把角色送去一個去不了的世界。
                if (conf.IsWorldExcluded(worldId))
                    continue;

                // 從來沒掃過＝最舊。
                var at = PriceSurveyWorldLog.TryGet(worldId, out var row) ? row.AtUtc : DateTime.MinValue;
                if (bestId != 0 && at >= bestAt)
                    continue;

                bestId = worldId;
                bestName = name;
                bestAt = at;
            }

            if (bestId == 0)
            {
                DisarmTour("every world you can travel to has been visited this round".Loc());
                return;
            }

            // 🔴 先記成「去過」再請它送人：這樣連請求失敗都不會變成反覆重試同一個世界。
            tourVisited.Add(bestId);
            tourTravelTargetId = bestId;
            tourTravelTargetName = bestName;

            Log.Information(
                $"[Marketbuddy] 巡檢自動續跑：挑中 {bestName}({bestId})，" +
                $"它的巡檢資料時間＝{(bestAt == DateTime.MinValue ? "從來沒掃過" : bestAt.ToString("u"))}，" +
                $"這是本輪第 {tourWorldsChanged + 1}/{tourMaxWorlds} 次換世界。");

            if (!ChangeWorldOnce(bestName))
            {
                DisarmTour(TravelStatus.Length > 0
                    ? TravelStatus
                    : "Lifestream did not accept the travel request".Loc());
                return;
            }

            tourWorldsChanged++;
            EnterTourPhase(TourPhase.Travelling, TourTravelTimeoutSeconds);
            TourStatus = "Travelling to ?? (?? / ?? world(s) this round)".Loc(
                bestName, tourWorldsChanged, tourMaxWorlds);
        }

        /// <summary>前往中：等抵達，或等到不耐煩。</summary>
        private void TourTickTravelling()
        {
            if (LoggedIn && CurrentWorldId == tourTravelTargetId)
            {
                EnterTourPhase(TourPhase.Settling, TourWaitSeconds);
                tourSettleUntil = DateTime.UtcNow.AddSeconds(TourSettleSeconds);
                TourStatus = "Arrived at ??, waiting a moment before scanning".Loc(tourTravelTargetName);
                Log.Information(
                    $"[Marketbuddy] 巡檢自動續跑：已抵達 {tourTravelTargetName}({tourTravelTargetId})，" +
                    $"先站定 {TourSettleSeconds:F0} 秒再開始掃描。");
                return;
            }

            if (DateTime.UtcNow > tourDeadline)
                DisarmTour("travelling to ?? took too long".Loc(tourTravelTargetName));
        }

        /// <summary>到了：站定一下、確認沒有人在用市場，然後立開始旗標。</summary>
        private void TourTickSettling()
        {
            // 使用者自己先按了「掃描這個世界」也算數，跟著它走就好。
            if (IsRunning)
            {
                EnterTourPhase(TourPhase.Scanning, 0);
                return;
            }

            if (DateTime.UtcNow < tourSettleUntil)
                return;

            if (CurrentWorldId != tourTravelTargetId)
            {
                DisarmTour("the world changed while waiting to scan".Loc());
                return;
            }

            // 🔴 Lifestream 可能還在做它自己的收尾（走去市場、關視窗）。
            if (IPCManager.IsLifestreamBusy())
            {
                if (DateTime.UtcNow > tourDeadline)
                    DisarmTour("Lifestream was still busy after arriving".Loc());
                return;
            }

            // 🔴 與批次改價／下架共用同一個市場請求槽，所以要確認那道互斥仍然成立。
            if (!CanStart(out var why))
            {
                if (DateTime.UtcNow > tourDeadline)
                    DisarmTour(why);
                else
                    TourStatus = "Waiting before scanning ??: ??".Loc(tourTravelTargetName, why);
                return;
            }

            startRequested = true;
            EnterTourPhase(TourPhase.Scanning, 0);
            TourStatus = "Scanning ?? automatically (?? / ?? world(s) this round)".Loc(
                tourTravelTargetName, tourWorldsChanged, tourMaxWorlds);
            Log.Information(
                $"[Marketbuddy] 巡檢自動續跑：在 {tourTravelTargetName}({tourTravelTargetId}) 自動開始掃描" +
                $"（本輪第 {tourWorldsChanged}/{tourMaxWorlds} 次換世界）。");
        }

        /// <summary>
        /// 自動續跑現在可不可以動。
        /// 🔑 這一組與 <see cref="CanStart"/> 的讓路條件是同一組，
        /// 只是少了「登入後才有的那些」——換世界之前本來就不必問得起掃描。
        /// </summary>
        private bool TourCanProceed(out string reason)
        {
            reason = string.Empty;

            if (IPCManager.IsLocked)
            {
                reason = "Halted by another plugin via IPC".Loc();
                return false;
            }

            if (AutoRetainerBridge.IsBusy)
            {
                reason = "AutoRetainer is busy (or MultiMode is enabled), stop it first".Loc();
                return false;
            }

            if (gui.BatchEngine?.IsRunning == true)
            {
                reason = "A relist is running".Loc();
                return false;
            }

            if (gui.DelistEngine?.IsRunning == true)
            {
                reason = "A delist is running".Loc();
                return false;
            }

            if (!LoggedIn || CurrentWorldId == 0)
            {
                reason = "Not logged in".Loc();
                return false;
            }

            return true;
        }

        /// <summary>
        /// 把「我在哪個世界、有沒有登入」拍成快照給繪製執行緒用。
        /// 🔴 只在這裡（framework 執行緒）讀 <c>PlayerState</c>。
        /// </summary>
        private void UpdateWorldSnapshot()
        {
            CurrentContentId = PlayerState.ContentId;
            LoggedIn = CurrentContentId != 0;
            var world = PlayerState.CurrentWorld;
            CurrentWorldId = world.RowId;
            CurrentWorldName = CurrentWorldId == 0
                ? string.Empty
                : world.ValueNullable?.Name.ExtractText() ?? $"#{CurrentWorldId}";
        }

        /// <param name="recoverable">
        /// true＝這個理由消失之後這一輪還接得回去（暫停就好）；
        /// false＝這一輪的前提沒了（登出、換世界），只能結束。
        /// </param>
        private bool CheckStandDown(out string reason, out bool recoverable)
        {
            reason = string.Empty;
            recoverable = true;

            if (IPCManager.IsLocked)
            {
                reason = "Halted by another plugin via IPC".Loc();
                return true;
            }

            if (AutoRetainerBridge.IsBusy)
            {
                reason = "AutoRetainer started working, the survey stands down".Loc();
                return true;
            }

            if (gui.BatchEngine?.IsRunning == true || gui.DelistEngine?.IsRunning == true)
            {
                reason = "A relist or delist started, the survey stands down".Loc();
                return true;
            }

            if (!LoggedIn)
            {
                reason = "Left the game world".Loc();
                recoverable = false;
                return true;
            }

            if (State == SurveyState.Running && CurrentWorldId != worldId)
            {
                reason = "The world changed, this round stops here".Loc();
                recoverable = false;
                return true;
            }

            return false;
        }

        // =====================================================================
        //  準備
        // =====================================================================

        private void BeginRun()
        {
            if (!CanStart(out var why))
            {
                StatusText = "Cannot start: ??".Loc(why);
                return;
            }

            worldId = CurrentWorldId;
            worldName = CurrentWorldName;
            runContentId = CurrentContentId;

            // 🔴 來源①②讀遊戲／IPC，只能在這裡（framework 執行緒）做。
            list = conf.PriceSurveyAllCharacters
                ? null
                : PriceSurveyItemSource.TryFromSellList(gui) ?? PriceSurveyItemSource.TryFromAllaganTools();

            var needCsvList = list == null;
            var csvPath = needCsvList ? PriceSurveyItemSource.InventoryToolsCsvPath() : null;

            // 🔴 這一輪要不要順便查採購清單，在這裡定案；之後改設定不會讓半輪的規則不一致。
            shoppingEnabled = conf.PriceSurveyShoppingList;
            shoppingIds.Clear();
            shoppingExtraQueued = 0;
            shoppingRecordedRows = 0;
            shoppingSkippedAlreadyDone = 0;
            shoppingGenerationAtStart = ShoppingList.Generation;
            if (shoppingEnabled)
            {
                // 每一輪都重讀一次清單檔：使用者剛改完檔就按掃描是最自然的順序。
                // 讀檔在執行緒池上、名稱換 id 在 framework 執行緒上，兩段都由
                // OnFrameworkUpdate 開頭那兩行推動；TickPreparing 會等它回來。
                ShoppingList.RequestReload();
            }

            var wantShoppingHistory = shoppingEnabled;

            // 記錄檔的讀取（續掃用的「已掃過」集合、以及「只掃被壓價的」需要的上一輪資料）
            // 一律在執行緒池上做——那個檔案會長到幾萬列。
            prepareTask = Task.Run(() => new PrepareResult(
                needCsvList ? PriceSurveyItemSource.TryFromInventoryToolsCsv(csvPath) : null,
                PriceSurveyLog.LoadAll(),
                wantShoppingHistory ? ShoppingSurveyLog.LoadAll() : []));

            State = SurveyState.Preparing;
            LastRunLookedUnsupported = false;
            nothingLeftOnThisWorld = false;
            PauseReason = string.Empty;
            pauseRequested = null;
            resumeRequested = false;
            runStartedAt = DateTime.UtcNow;
            recordedRows = 0;
            okCount = emptyCount = refusedCount = timeoutCount = cacheCount = 0;
            skippedAlreadyDone = 0;
            queueIndex = 0;
            itemQueue.Clear();
            byItemId.Clear();
            StatusText = "Building the list to survey...".Loc();
        }

        private void TickPreparing()
        {
            var task = prepareTask;
            if (task == null)
            {
                Finish("Could not build the list".Loc(), unsupported: false);
                return;
            }

            if (!task.IsCompleted)
                return;

            // 🔑 還要等那次採購清單重讀回來（判準是「世代號變了」，不是 IsLoading——
            //    後者在「要求了但工作還沒被踢出去」那一格會誤判成已完成）。
            //    ⚠️ 給它一個上限：等不到就用手上這一份繼續，寧可清單舊一點也不要卡在準備階段。
            if (shoppingEnabled && ShoppingList.Generation == shoppingGenerationAtStart)
            {
                if ((DateTime.UtcNow - runStartedAt).TotalSeconds < ShoppingReloadWaitSeconds)
                    return;

                Log.Information(
                    $"[Marketbuddy] 巡檢：等採購清單重讀超過 {ShoppingReloadWaitSeconds} 秒，" +
                    "改用上一次讀到的那一份繼續。");
            }

            prepareTask = null;

            PrepareResult prepared;
            try
            {
                prepared = task.GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                Log.Information(e, "[Marketbuddy] 巡檢：準備清單時發生例外。");
                Finish("Could not build the list".Loc(), unsupported: false);
                return;
            }

            list ??= prepared.CsvList;

            // 🔑 沒有任何掛售中的道具，但採購清單上有東西時仍然可以跑：那是「我只想查要買的」。
            //    這時候用一份空的清單頂著（來源標成 shopping-only），既有的收場文字與
            //    OwnRetainerIds 判斷全部照舊成立。
            var shoppingOnly = false;
            if (list == null || list.Items.Count == 0)
            {
                if (!shoppingEnabled || ShoppingList.Items.Count == 0)
                {
                    Finish(
                        "No listed items found (needs InventoryTools/AllaganTools, or open a retainer's sell list first).".Loc(),
                        unsupported: false);
                    return;
                }

                shoppingOnly = true;
                list = new PriceSurveyItemList
                {
                    SourceKey = "shopping-only",
                    SourceLabel = "Shopping list only (nothing of yours is listed)".Loc(),
                    Items = [],
                    OwnRetainerIds = [],
                };
            }

            BuildQueue(prepared.History, prepared.ShoppingHistory);

            if (itemQueue.Count == 0)
            {
                // 🔑 「整份都在保留時間內問過了」＝這個世界該問的都問完了，那不是失敗，
                //    所以武裝中的自動續跑可以接著往下一個世界走（見 Finish）。
                //    ⚠️ 另一條（篩選之後什麼都不剩）刻意<b>不</b>算：那是篩選的結果，
                //    不是「這個世界掃完了」。
                nothingLeftOnThisWorld = skippedAlreadyDone + shoppingSkippedAlreadyDone > 0;
                Finish(
                    nothingLeftOnThisWorld
                        ? "Everything on this world's list was already surveyed within the keep-for window (?? item(s)).".Loc(
                            skippedAlreadyDone + shoppingSkippedAlreadyDone)
                        : "No item matches the current filters.".Loc(),
                    unsupported: false);
                return;
            }

            State = SurveyState.Running;
            phase = ItemPhase.Throttle;
            attempt = 0;
            firstItemNoResponseStreak = 0;
            currentItemId = itemQueue[0];
            notBefore = DateTime.MinValue;
            Log.Information(
                $"[Marketbuddy] 巡檢開始：世界 {worldName}({worldId})，{itemQueue.Count} 件，" +
                $"來源={list.SourceKey}{(shoppingOnly ? "（只有採購清單）" : string.Empty)}，" +
                $"略過已掃 {skippedAlreadyDone} 件，" +
                $"採購清單多排 {shoppingExtraQueued} 件（略過已掃 {shoppingSkippedAlreadyDone} 件），" +
                $"閘門 {MarketRequestGate.IntervalMs} ms。");
        }

        private void BuildQueue(List<PriceSurveyRow> history, List<ShoppingSurveyRow> shoppingHistory)
        {
            byItemId.Clear();
            foreach (var item in list!.Items)
            {
                if (!byItemId.TryGetValue(item.ItemId, out var bucket))
                    byItemId[item.ItemId] = bucket = [];
                bucket.Add(item);
            }

            // 續掃：這個世界在保留時間內已經有結論的道具就不必再問一次。
            var alreadyDone = new HashSet<uint>();
            if (conf.PriceSurveySkipHours > 0)
            {
                var cutoff = DateTime.UtcNow.AddHours(-conf.PriceSurveySkipHours);
                foreach (var row in history)
                {
                    if (row.WorldId != worldId || row.AtUtc <= cutoff)
                        continue;
                    // 只有真的問到答案才算「掃過」；被拒絕／逾時的要再問一次。
                    if (row.Verdict is "ok" or "empty")
                        alreadyDone.Add(row.ItemId);
                }
            }

            // 「只掃被壓價的」：需要上一輪的資料才知道誰被壓價。
            HashSet<uint>? undercut = null;
            if (conf.PriceSurveyOnlyUndercut)
            {
                undercut = [];
                foreach (var row in history)
                {
                    if (row.UndercutBy > 0)
                        undercut.Add(row.ItemId);
                }
            }

            var seen = new HashSet<uint>();
            foreach (var item in list.Items)
            {
                if (!seen.Add(item.ItemId))
                    continue;
                if (undercut != null && !undercut.Contains(item.ItemId))
                    continue;
                if (alreadyDone.Contains(item.ItemId))
                {
                    skippedAlreadyDone++;
                    continue;
                }

                itemQueue.Add(item.ItemId);
            }

            AppendShoppingList(shoppingHistory);
        }

        /// <summary>
        /// 把採購清單的道具接在佇列<b>後面</b>。
        ///
        /// <para>
        /// 🔑 <b>刻意接在後面而不是混進去</b>：這樣「我在賣的那份」永遠先跑完，
        /// 中途被打斷時損失的一定是採購清單那一段——那一段本來就是順路的。
        /// 也因為順序如此，「這個世界的掛售清單掃到哪」那個進度才算得準
        /// （見 <see cref="NoteWorldProgress"/>）。
        /// </para>
        ///
        /// <para>
        /// 🔑 <b>與掛售清單重疊的道具不會多查一次</b>：它已經在佇列裡了，
        /// 這裡只把它記進 <see cref="shoppingIds"/>，一次查詢兩份記錄。
        /// </para>
        ///
        /// <para>
        /// ⚠️ 「只巡檢被壓價的」那個篩選<b>不套用</b>在採購清單上：被壓價講的是我方掛單，
        /// 而採購清單上的東西我根本沒掛。
        /// </para>
        /// </summary>
        /// <param name="shoppingHistory">採購清單的歷史行情（續掃用）。</param>
        private void AppendShoppingList(List<ShoppingSurveyRow> shoppingHistory)
        {
            if (!shoppingEnabled)
                return;

            var wanted = ShoppingList.Items;
            if (wanted.Count == 0)
                return;

            // 續掃：這個世界在保留時間內已經有結論的採購道具就不必再問一次。
            // 判準與掛售清單那邊逐字相同（被拒絕與逾時都不算掃過）。
            var alreadyDone = new HashSet<uint>();
            if (conf.PriceSurveySkipHours > 0)
            {
                var cutoff = DateTime.UtcNow.AddHours(-conf.PriceSurveySkipHours);
                foreach (var row in shoppingHistory)
                {
                    if (row.WorldId != worldId || row.AtUtc <= cutoff || !row.Answered)
                        continue;
                    alreadyDone.Add(row.ItemId);
                }
            }

            // 🔴 判準是「真的排進佇列了沒」，不是「掛售清單走訪過沒」——後者也包含
            //    被「已經掃過」「只掃被壓價的」篩掉的那些，拿它當順路的依據會讓那幾件
            //    永遠不會被查，而且是靜默的。
            var queued = new HashSet<uint>(itemQueue);

            foreach (var item in wanted)
            {
                // 已經在佇列裡（我自己也在賣這件）＝順路，一次查詢寫兩份記錄，不多送任何請求。
                if (queued.Contains(item.ItemId))
                {
                    shoppingIds.Add(item.ItemId);
                    continue;
                }

                if (alreadyDone.Contains(item.ItemId))
                {
                    shoppingSkippedAlreadyDone++;
                    continue;
                }

                shoppingIds.Add(item.ItemId);
                itemQueue.Add(item.ItemId);
                queued.Add(item.ItemId);
                shoppingExtraQueued++;
            }
        }

        // =====================================================================
        //  逐件查價
        // =====================================================================

        private void TickRunning(DateTime now)
        {
            if (queueIndex >= itemQueue.Count)
            {
                // 🔴 這是唯一一條「整份掃完」的出口，也是唯一一條會通知塔塔露的出口。
                Finish("Done".Loc(), unsupported: false, completed: true);
                return;
            }

            currentItemId = itemQueue[queueIndex];

            switch (phase)
            {
                case ItemPhase.Throttle:
                {
                    // 快取命中就完全不必送請求。預設 0 秒（＝每一件都重新問伺服器），
                    // 因為巡檢要的就是「現在的行情」；使用者可以自己把秒數調大。
                    if (MarketDataCache.TryGet(currentItemId, conf.PriceSurveyCacheSeconds, out var cached, out _))
                    {
                        captured.Clear();
                        // ⚠️ 共用快取沒有存件數（那是採購清單才要的欄位，不值得為它動
                        //    BatchReprice 的定價路徑）。件數在這裡一律寫 0，
                        //    CompleteItem 看到 source=="cache" 就把它記成 -1＝「不知道」。
                        foreach (var (price, isHq, retainerId) in cached)
                            captured.Add((price, isHq, retainerId, 0u));
                        cacheCount++;
                        CompleteItem(cached.Count > 0 ? "ok" : "empty", "cache", cached.Count);
                        return;
                    }

                    if (now < notBefore)
                        return;
                    if (!MarketRequestGate.IsReady(now))
                        return;

                    phase = ItemPhase.Request;
                    return;
                }

                case ItemPhase.Request:
                {
                    var proxy = GetItemSearchProxy();
                    if (proxy == null)
                    {
                        Finish("The market query interface is unavailable (InfoProxyItemSearch)".Loc(), unsupported: false);
                        return;
                    }

                    attempt++;

                    // ⚠️ 台服的 vf10 (EndRequest) 是空函式，所以真正的重設是下面兩行。
                    //    形狀與 BatchReprice 逐字相同（那裡有完整的鑑識註解）。
                    proxy->EndRequest();
                    proxy->ListingCount = 0;
                    proxy->EntryCount = 0;

                    proxy->SearchItemId = currentItemId;
                    captured.Clear();
                    offeringsReceived = false;
                    historySeen = false;
                    pendingItemId = currentItemId;
                    offeringsPending = true;
                    probeAnswered = probeRefused = probeEmpty = false;
                    MarketRequestResultProbe.ArmForRequest();
                    sendGapMs = MarketRequestGate.MsSinceLastRequest(now);
                    MarketRequestGate.NoteRequestSent(now);
                    waitStart = now;

                    var sent = proxy->RequestData();
                    MarketDiag.Trace(
                        $"{Diag} SURVEY-REQUEST item={currentItemId} attempt={attempt} RequestData={sent} " +
                        $"sendGapMs={sendGapMs:F0} gate={MarketRequestGate.IntervalMs} world={worldId}");

                    if (!sent)
                    {
                        offeringsPending = false;
                        if (NoteNoResponseAndShouldGiveUp())
                            return;
                        if (attempt >= MaxAttempts)
                        {
                            CompleteItem("timeout", "live", -1);
                            return;
                        }

                        notBefore = now.AddMilliseconds(RetryBackoffFor(attempt));
                        phase = ItemPhase.Throttle;
                        return;
                    }

                    phase = ItemPhase.Wait;
                    return;
                }

                case ItemPhase.Wait:
                {
                    if (!probeAnswered && MarketRequestResultProbe.TryTakeResult(currentItemId, out var verdict))
                    {
                        probeAnswered = true;
                        probeRefused = verdict.Refused;
                        probeEmpty = !verdict.Refused && verdict.ListingCount == 0;
                    }

                    if (offeringsReceived)
                    {
                        MarketRequestGate.NoteAccepted();
                        firstItemNoResponseStreak = 0;
                        CompleteItem(captured.Count > 0 ? "ok" : "empty", "live", captured.Count);
                        return;
                    }

                    if (probeRefused)
                    {
                        Log.Information(
                            $"{Diag} SURVEY-REFUSED item={currentItemId} attempt={attempt} " +
                            $"afterMs={(now - waitStart).TotalMilliseconds:F0} (伺服器真值，不是逾時)");
                        offeringsPending = false;
                        firstItemNoResponseStreak = 0;
                        MarketRequestGate.NoteRefused(sendGapMs);

                        if (attempt >= MaxAttempts)
                        {
                            CompleteItem("refused", "live", -1);
                            return;
                        }

                        notBefore = now.AddMilliseconds(RetryBackoffFor(attempt));
                        phase = ItemPhase.Throttle;
                        return;
                    }

                    if (probeEmpty || (historySeen && (now - historySeenAt).TotalMilliseconds >= EmptyResultGraceMs))
                    {
                        offeringsPending = false;
                        MarketRequestGate.NoteAccepted();
                        firstItemNoResponseStreak = 0;
                        captured.Clear();
                        MarketDataCache.StoreConfirmedEmpty(currentItemId);
                        CompleteItem("empty", "live", 0);
                        return;
                    }

                    var waitedMs = (now - waitStart).TotalMilliseconds;
                    var swallowed = !historySeen && waitedMs > NoResponseDeadlineMs;
                    if (!swallowed && waitedMs <= ResponseTimeoutMs)
                        return;

                    Log.Information(
                        $"{Diag} SURVEY-TIMEOUT item={currentItemId} attempt={attempt} historySeen={historySeen} " +
                        $"swallowed={swallowed} waitedMs={waitedMs:F0} " +
                        $"probeInstalled={MarketRequestResultProbe.IsInstalled}");
                    offeringsPending = false;

                    if (swallowed)
                    {
                        MarketRequestGate.NoteRefused(sendGapMs);
                        if (NoteNoResponseAndShouldGiveUp())
                            return;
                    }
                    else
                    {
                        firstItemNoResponseStreak = 0;
                    }

                    if (attempt >= MaxAttempts)
                    {
                        CompleteItem("timeout", "live", -1);
                        return;
                    }

                    notBefore = now.AddMilliseconds(RetryBackoffFor(attempt));
                    phase = ItemPhase.Throttle;
                    return;
                }
            }
        }

        /// <summary>
        /// ⚠️ 「假設 A 不成立」的守衛：**第一件**連續 <see cref="FirstItemGiveUpAfter"/> 次
        /// 什麼都沒回來，就判定這個情境（站在市場前但沒開出售品視窗）根本送不出查詢，
        /// 整輪停下並給一句可以照做的建議 —— 而不是拿幾百件去撞同一堵牆。
        /// </summary>
        /// <returns>true＝已經收手，呼叫端要立刻 return。</returns>
        private bool NoteNoResponseAndShouldGiveUp()
        {
            if (queueIndex != 0)
                return false;

            firstItemNoResponseStreak++;
            if (firstItemNoResponseStreak < FirstItemGiveUpAfter)
                return false;

            Finish(
                "Market queries cannot be sent in this situation: the first item got no response ?? times in a row. Walk up to a market board, or open a retainer's sell list first, then try again."
                    .Loc(firstItemNoResponseStreak),
                unsupported: true);
            return true;
        }

        /// <summary>把這一件的結果寫成記錄，並前進到下一件。</summary>
        private void CompleteItem(string verdict, string source, int listingCount)
        {
            long lowestNq = -1, lowestHq = -1;
            ulong lowestNqRetainer = 0, lowestHqRetainer = 0;

            // 採購清單要的「這裡買得到幾個」：這一頁掛單的合計件數，分品質算。
            // 🔑 沒有掛單時是 0（＝確認沒得買），快取來的答案是 -1（＝不知道）——
            //    兩者對使用者的意思完全不同，畫面也畫成不同的東西。
            long nqQuantity = 0, hqQuantity = 0;

            if (verdict is "ok" or "empty")
            {
                foreach (var (price, isHq, retainerId, quantity) in captured)
                {
                    if (isHq)
                    {
                        hqQuantity += quantity;
                        if (lowestHq < 0 || price < lowestHq)
                        {
                            lowestHq = price;
                            lowestHqRetainer = retainerId;
                        }
                    }
                    else
                    {
                        nqQuantity += quantity;
                        if (lowestNq < 0 || price < lowestNq)
                        {
                            lowestNq = price;
                            lowestNqRetainer = retainerId;
                        }
                    }
                }
            }
            else
            {
                nqQuantity = hqQuantity = -1;
            }

            // 共用快取沒有存件數 ⇒ 這一次的件數是「不知道」，不是 0。
            if (string.Equals(source, "cache", StringComparison.Ordinal))
            {
                if (lowestNq >= 0)
                    nqQuantity = -1;
                if (lowestHq >= 0)
                    hqQuantity = -1;
            }

            var knowOwners = list != null && list.OwnRetainerIds.Count > 0;
            var now = DateTime.UtcNow;
            var gate = MarketRequestGate.IntervalMs;

            if (byItemId.TryGetValue(currentItemId, out var ours))
            {
                foreach (var mine in ours)
                {
                    var lowest = mine.Hq ? lowestHq : lowestNq;
                    var lowestRetainer = mine.Hq ? lowestHqRetainer : lowestNqRetainer;
                    // 🔑 「不知道」寫 -1，不是 0：不知道最低價是不是自己的時候寫 0
                    //    等於斷言「不是我的」，那會讓比價畫面憑空生出一堆假的被壓價。
                    var lowestIsOurs = lowest < 0 || !knowOwners
                        ? -1
                        : list!.OwnRetainerIds.Contains(lowestRetainer) ? 1 : 0;

                    PriceSurveyLog.Append(new PriceSurveyRow(
                        now, worldId, worldName, mine.ItemId, mine.Hq, mine.OurPrice,
                        lowestNq, lowestHq, lowestIsOurs, listingCount, source, gate, verdict));
                    recordedRows++;
                }
            }

            // 🔑 採購清單的記錄寫在<b>另一個檔</b>：這幾件我根本沒有掛，寫進 price_survey.csv
            //    只會讓比價分頁與待處理清單多出一堆「我方價＝不知道」的幽靈列。
            //    同一次查詢可以同時滿足兩份清單，這裡不會多送任何一個請求。
            if (shoppingIds.Contains(currentItemId))
            {
                ShoppingSurveyLog.Append(new ShoppingSurveyRow(
                    now, worldId, worldName, currentItemId,
                    lowestNq, nqQuantity, lowestHq, hqQuantity, listingCount, source, verdict));
                shoppingRecordedRows++;
            }

            switch (verdict)
            {
                case "ok":
                    okCount++;
                    break;
                case "empty":
                    emptyCount++;
                    break;
                case "refused":
                    refusedCount++;
                    break;
                default:
                    timeoutCount++;
                    break;
            }

            offeringsPending = false;
            offeringsReceived = false;
            historySeen = false;
            captured.Clear();

            queueIndex++;
            attempt = 0;
            phase = ItemPhase.Throttle;
            notBefore = DateTime.MinValue;

            if (queueIndex < itemQueue.Count)
            {
                currentItemId = itemQueue[queueIndex];
                StatusText = "Surveying: ?? / ??".Loc(queueIndex, itemQueue.Count);
            }
        }

        // =====================================================================
        //  暫停與續跑
        // =====================================================================

        /// <summary>
        /// 能暫停就暫停，不能就結束。
        /// ⚠️ 只有 <see cref="SurveyState.Running"/> 暫停得起來：<see cref="SurveyState.Preparing"/>
        /// 階段還沒有佇列可以留（清單正在執行緒池上組），留一個空殼只會讓「繼續掃描」按下去什麼都沒有。
        /// </summary>
        private void PauseOrFinish(string reason)
        {
            // ⚠️ 最後一件已經處理完、只差下一格收尾的那一瞬間不可以暫停：
            //    那會把「整份掃完」的收場（塔塔露、待處理重算、世界標成掃完）
            //    擋在一個 <see cref="CanResume"/> 永遠不會放行的暫停後面。
            //    這時候該做的事跟 <see cref="TickRunning"/> 下一格會做的完全一樣。
            if (State == SurveyState.Running && queueIndex >= itemQueue.Count)
            {
                Finish("Done".Loc(), unsupported: false, completed: true);
                return;
            }

            if (State != SurveyState.Running)
            {
                Finish(reason, unsupported: false);
                return;
            }

            PauseRun(reason);
        }

        /// <summary>
        /// 把整輪凍住：清單、進度、統計全部原地留著，只把「正在等的那一件」清乾淨。
        /// 🔴 那一件<b>刻意不算數</b>——它還沒被寫進記錄檔，續跑時會重新問一次，
        /// 比留著半個等待狀態誠實得多。
        /// </summary>
        private void PauseRun(string reason)
        {
            State = SurveyState.Paused;
            pausedAt = DateTime.UtcNow;
            PauseReason = reason;
            RunSerial++;

            ClearInFlight();
            currentItemId = queueIndex < itemQueue.Count ? itemQueue[queueIndex] : 0;

            StatusText = "Paused: ?? (?? / ?? item(s) done)".Loc(reason, queueIndex, itemQueue.Count);

            Log.Information(
                $"[Marketbuddy] 巡檢暫停（{reason}）：世界 {worldName}({worldId})，" +
                $"處理 {queueIndex}/{itemQueue.Count} 件，記錄 {recordedRows} 列，" +
                "進度留著，要繼續必須使用者自己按「繼續掃描」——不會自己恢復。");

            MarketRequestGate.LogSummary("price survey paused");
            NoteWorldProgress();

            // 🔴 暫停＝有人介入了（使用者按的、視窗關了、別的引擎要用市場）。
            //    自動續跑<b>只</b>在「整份掃完」之後接續，所以這裡一律當場解除武裝，
            //    不可以讓它在暫停後面等著、然後在使用者已經忘記的時候突然自己換世界。
            if (tourArmed)
                DisarmTour("the survey was paused (??)".Loc(reason));
        }

        /// <summary>
        /// 從暫停處接著跑。
        /// 🔴 <b>不重建清單、不重讀任何檔案、不多送一次市場查詢</b>：佇列與
        /// <see cref="queueIndex"/> 從來沒被清掉，接的就是原來那一份。
        /// </summary>
        private void ResumeRun()
        {
            if (!CanResume(out var why))
            {
                StatusText = "Cannot continue: ??".Loc(why);
                return;
            }

            var pausedSeconds = (DateTime.UtcNow - pausedAt).TotalSeconds;

            State = SurveyState.Running;
            PauseReason = string.Empty;
            ClearInFlight();
            currentItemId = itemQueue[queueIndex];
            StatusText = "Surveying: ?? / ??".Loc(queueIndex, itemQueue.Count);

            Log.Information(
                $"[Marketbuddy] 巡檢續跑：世界 {worldName}({worldId})，" +
                $"從第 {queueIndex + 1} 件接續（共 {itemQueue.Count} 件），暫停了 {pausedSeconds:F0} 秒，" +
                $"閘門 {MarketRequestGate.IntervalMs} ms。清單沒有重建，已經問過的不會再問一次。");
        }

        /// <summary>
        /// 把「正在等的那一件」的狀態清乾淨——<b>不動</b>佇列、進度與統計。
        /// <see cref="ResetRun"/> 與 <see cref="PauseRun"/> 共用這一段。
        /// </summary>
        private void ClearInFlight()
        {
            phase = ItemPhase.Throttle;
            attempt = 0;
            notBefore = DateTime.MinValue;
            offeringsPending = false;
            offeringsReceived = false;
            historySeen = false;
            captured.Clear();

            // 探針槽裡任何還沒被取走的答覆都已經無主。
            MarketRequestResultProbe.ArmForRequest();

            var proxy = GetItemSearchProxy();
            if (proxy == null)
                return;
            proxy->EndRequest();
            proxy->ListingCount = 0;
            proxy->EntryCount = 0;
        }

        /// <summary>
        /// 把「這個世界掃到哪了」寫進 <see cref="PriceSurveyWorldLog"/>。
        ///
        /// <para>
        /// 🔑 <c>plannedTotal</c> 是<b>這個世界的完整清單</b>（這一輪的佇列 ＋ 因為
        /// 保留時間內已經問過而被略過的），所以連著跑好幾段的結果會自然累加：
        /// 第一段 78/633、第二段 127/633，而不是每段各自從 0 開始。
        /// </para>
        ///
        /// <para>
        /// ⚠️ 開著「只巡檢被壓價的」時<b>不記</b>：那一輪的佇列是被篩過的，
        /// 拿它當「這個世界掃到哪」會把一份挑過的子集講成整個世界的進度。
        /// </para>
        /// </summary>
        private void NoteWorldProgress()
        {
            if (worldId == 0 || conf.PriceSurveyOnlyUndercut)
                return;

            // 🔴 採購清單那一段<b>不算進</b>「這個世界的掛售清單掃到哪」：那份進度是拿來
            //    回答「我在賣的東西比完了沒」的，混進採購件數會讓已經掃完的世界看起來沒掃完。
            //    採購清單刻意排在佇列最後（見 AppendShoppingList），所以扣掉尾巴就是掛售那段。
            var ownQueued = itemQueue.Count - shoppingExtraQueued;
            var plannedTotal = ownQueued + skippedAlreadyDone;
            if (plannedTotal <= 0)
                return;

            // 真的問到答案的：這一輪查到的 ok／empty（快取命中已經算在裡面），
            // 加上一開始就因為「保留時間內已經問過」而略過的那些。
            // ⚠️ 進到採購那一段之後 ok+empty 會超過 plannedTotal，Math.Min 把它夾回
            //    「掛售那段全部做完」，那正是當下的事實。
            var surveyed = Math.Min(plannedTotal, skippedAlreadyDone + okCount + emptyCount);

            PriceSurveyWorldLog.Record(new SurveyWorldRow(
                DateTime.UtcNow,
                worldId,
                worldName,
                runContentId,
                list?.SourceKey ?? string.Empty,
                plannedTotal,
                surveyed,
                queueIndex >= ownQueued));
        }

        /// <param name="reason">給使用者看的收場原因（已在地化）。</param>
        /// <param name="unsupported">true＝這一輪看起來是「這個情境根本送不出查詢」。</param>
        /// <param name="completed">
        /// true＝清單整份掃完才收的。🔴 <b>只有這條路徑會通知塔塔露</b>：使用者按停、讓路退讓、
        /// 第一件連續沒回應而放棄、清單建不起來，一律用預設的 false。
        /// </param>
        private void Finish(string reason, bool unsupported, bool completed = false)
        {
            var elapsed = runStartedAt == DateTime.MinValue ? 0 : (DateTime.UtcNow - runStartedAt).TotalSeconds;
            var wasRunning = State is SurveyState.Running or SurveyState.Preparing or SurveyState.Paused;

            // 🔴 IPC 的實作跑在呼叫端的執行緒上。Finish 的每一個呼叫點都在 OnFrameworkUpdate
            //    的鏈上（framework 執行緒），所以這裡直接打過去就好，不必 marshal。
            //    對方沒裝／開關關著時這整條是安靜的 no-op，回 false。
            var praised = completed && wasRunning && TataruPraiseIPC.TryPraiseSurveyDone("跨世界價格巡檢完成");

            // 剛掃完的資料就是最新的，這時候把「待處理」清單重算一次。
            // 🔴 純計算：不改任何價格、不下架任何東西、不多送一次市場查詢（見
            //    PendingActionsBuilder 的類別註解）。與上面那個通知共用同一道
            //    「整份掃到最後才算數」的閘門——半份資料算出來的清單會誤導人。
            if (completed && wasRunning && conf.PendingRecomputeAfterSurvey)
                gui.Pending?.RequestRecompute("survey finished");

            if (wasRunning)
                Log.Information(
                    $"[Marketbuddy] 巡檢結束（{reason}）：世界 {worldName}({worldId})，" +
                    $"處理 {queueIndex}/{itemQueue.Count} 件，記錄 {recordedRows} 列，" +
                    $"採購清單 {shoppingRecordedRows} 列（多查 {shoppingExtraQueued} 件），" +
                    $"ok={okCount} empty={emptyCount} refused={refusedCount} timeout={timeoutCount} cache={cacheCount}，" +
                    $"耗時 {elapsed:F0} 秒，閘門 {MarketRequestGate.IntervalMs} ms，已通知塔塔露={praised}。");

            MarketRequestGate.LogSummary("price survey finished");
            LastRunLookedUnsupported = unsupported;
            StatusText = itemQueue.Count > 0
                ? "?? (?? / ?? item(s), ?? row(s) recorded)".Loc(reason, queueIndex, itemQueue.Count, recordedRows)
                : reason;

            // 🔑 這一輪的進度要在 ResetRun 之前記下來（那裡會把狀態收乾淨）。
            if (wasRunning)
            {
                NoteWorldProgress();
                RunSerial++;
            }

            // 🔴 自動續跑的分岔就在這裡，而且<b>只有兩條路會往下一個世界走</b>：
            //    ①completed＝這個世界的清單整份掃到最後
            //    ②nothingLeftOnThisWorld＝該問的都在保留時間內問過了（空佇列，不是失敗）
            //    其餘一律當場解除武裝：逾時、使用者按停、離開遊戲世界、世界被動改變、
            //    讓路給重掛／下架、清單建不起來、篩選之後什麼都不剩。
            //    🔴 這裡在 framework 執行緒上（Finish 的每個呼叫點都在 OnFrameworkUpdate 的鏈上），
            //    所以直接呼叫，不必 marshal。
            if (tourArmed)
            {
                if ((completed && wasRunning) || nothingLeftOnThisWorld)
                {
                    tourAdvancePending = true;
                    tourPhase = TourPhase.Scanning;
                    Log.Information(
                        $"[Marketbuddy] 巡檢自動續跑：{worldName}({worldId}) 收場（{reason}），" +
                        $"整份掃完={completed}，沒有需要掃的={nothingLeftOnThisWorld}，接著去挑資料最舊的世界。");
                }
                else
                {
                    DisarmTour(reason);
                }
            }

            ResetRun();
        }

        private void ResetRun()
        {
            State = SurveyState.Idle;
            prepareTask = null;
            currentItemId = 0;
            nothingLeftOnThisWorld = false;
            PauseReason = string.Empty;
            pauseRequested = null;
            resumeRequested = false;

            // 這一輪結束了，進行中那一件的狀態與探針槽一起清掉。
            ClearInFlight();
        }

        private static int RetryBackoffFor(int attemptNumber)
            => Math.Min(RetryBackoffBaseMs * attemptNumber, RetryBackoffCapMs);

        // =====================================================================
        //  市場封包（形狀比照 BatchReprice；只收自己正在等的那一件）
        // =====================================================================

        private void OnOfferingsReceived(IMarketBoardCurrentOfferings offerings)
        {
            if (!offeringsPending)
                return;

            var listings = offerings.ItemListings;
            if (listings.Count > 0)
            {
                if (offerings.RequestId == lastAcceptedRequestId)
                    return;
                if (listings[0].ItemId != pendingItemId)
                    return;
            }

            lastAcceptedRequestId = offerings.RequestId;
            captured.Clear();
            foreach (var listing in listings)
                captured.Add((listing.PricePerUnit, listing.IsHq, listing.RetainerId, listing.ItemQuantity));

            offeringsPending = false;
            offeringsReceived = true;
        }

        private void OnHistoryReceived(IMarketBoardHistory history)
        {
            if (!offeringsPending || history.ItemId != pendingItemId)
                return;
            historySeen = true;
            historySeenAt = DateTime.UtcNow;
        }

        private static InfoProxyItemSearch* GetItemSearchProxy()
        {
            var infoModule = InfoModule.Instance();
            return infoModule == null
                ? null
                : (InfoProxyItemSearch*)infoModule->GetInfoProxyById(InfoProxyId.ItemSearch);
        }

        /// <summary>準備階段在執行緒池上算出來的三份東西。</summary>
        /// <param name="ShoppingHistory">
        /// 採購清單的歷史行情；沒開那個功能時是空清單（那時候完全不讀那個檔）。
        /// </param>
        private readonly record struct PrepareResult(
            PriceSurveyItemList? CsvList,
            List<PriceSurveyRow> History,
            List<ShoppingSurveyRow> ShoppingHistory);
    }
}
