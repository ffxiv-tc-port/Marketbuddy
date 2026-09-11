using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Networking.Http;
using Lumina.Excel.Sheets;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    /// <summary>
    /// 一件道具的「歷史最近賣出價」目前是什麼狀態。
    ///
    /// <para>
    /// 🔴 <see cref="Unknown"/> 刻意給明確的 0：沒有零值的列舉會讓 <c>default</c> 落在一個
    /// 無效值上，而那種壞法是靜默的。
    /// </para>
    /// <para>
    /// 🔑 「查不到」（<see cref="NoData"/>）與「查詢中」（<see cref="Loading"/>）是<b>兩件事</b>，
    /// 而且兩個都必須在畫面上分得出來。把任何一種畫成 0 或空白會被讀成「這件賣過 0 gil」。
    /// </para>
    /// </summary>
    internal enum LastSoldState
    {
        /// <summary>還沒有問過。</summary>
        Unknown = 0,

        /// <summary>已排進佇列或請求已送出，還在等答覆。</summary>
        Loading = 1,

        /// <summary>問到了，而且至少有一種品質有成交紀錄。</summary>
        Ready = 2,

        /// <summary>問到了，但 Universalis 手上沒有這件的成交紀錄。</summary>
        NoData = 3,

        /// <summary>問失敗（連線、逾時、伺服器錯誤）。與 <see cref="NoData"/> 不同，這是暫時的。</summary>
        Failed = 4,
    }

    /// <summary>
    /// 一筆「最近一次賣出」。
    /// </summary>
    /// <param name="UnitPrice">成交的單價（未扣稅，與遊戲市場板上顯示的掛售價同一個口徑）。</param>
    /// <param name="SoldAtUtc">成交時間；<see cref="DateTime.MinValue"/>＝時間戳不可用。</param>
    /// <param name="Hq">這筆成交是優質品還是普通品。</param>
    /// <param name="World">成交發生在哪個世界；空＝回應裡沒有帶世界。</param>
    internal readonly record struct LastSoldEntry(long UnitPrice, DateTime SoldAtUtc, bool Hq, string World);

    /// <summary>
    /// 一個「目前最低掛售價」的資料點。
    /// 🔑 這是 <c>struct?</c> 的形式在用：<b>沒有資料時是 null，不是 0</b>。
    /// </summary>
    /// <param name="UnitPrice">最低掛售單價（未扣稅）。</param>
    /// <param name="World">掛在哪個世界；空＝回應裡沒有帶世界。</param>
    internal readonly record struct MarketPricePoint(long UnitPrice, string World);

    /// <summary>
    /// 「歷史最近賣出價」的來源：Universalis 的 <c>aggregated</c> 端點，取<b>整個資料中心</b>的
    /// <c>recentPurchase</c>。
    ///
    /// <para>
    /// 🔴 <b>範圍是「整個資料中心，但扣掉世界排除清單上的世界」</b>，不是整個資料中心。
    /// 理由是實機事實：台服的拉姆（4034）已經停止營運，而一件道具只要自它關閉之後在其他
    /// 世界都沒賣出過，它留下的舊成交價就會變成重掛的定價依據。清單用的是設定裡
    /// <b>既有</b>那一份（<see cref="Configuration.PriceSurveyExcludedWorlds"/>），
    /// 這裡不另外判斷哪個世界該不該用。
    /// </para>
    /// <para>
    /// ⚠️ 被排除之後<b>不會</b>拿別的來源代打：查不到可用的成交就不定這一件，
    /// 呼叫端落回它原本的市場比價。沒有成交紀錄的往往正是稀有的東西，拿別的來源湊會賤賣。
    /// </para>
    ///
    /// <para>
    /// 🔴 <b>完全不碰遊戲內的市場查詢。</b>這條路徑不送 <c>InfoProxyItemSearch.RequestData()</c>、
    /// 不開任何原生視窗、不掛 hook、不碰任何封包——只有一個對公開 HTTP API 的 GET。
    /// 因此它也天生避開了台服「查詢被拒絕時完全靜默」那個已知問題。
    /// </para>
    ///
    /// <para>
    /// 🔑 為什麼不去問 PriceInsight：2026-09-09 逐字掃過 <c>D:/ffxiv-tc-port/PriceInsight</c>
    /// 的全部 <c>.cs</c>，<b>它一個 IPC 端點都沒有提供</b>（<c>Ipc</c>／<c>CallGate</c> 零命中），
    /// 所以「跟 PriceInsight 要價格」在技術上不存在。它自己也是打同一個 Universalis 端點，
    /// 所以這裡直接打同一個端點得到的是同一份資料。
    /// </para>
    ///
    /// <para>
    /// 執行緒：<see cref="Request"/>／<see cref="EnsureWorldNames"/> 只從 framework 執行緒呼叫
    /// （它們會讀遊戲資料表）；HTTP 與解析全在執行緒池上，<b>續行不會回到 framework 執行緒，
    /// 所以那一段不碰任何遊戲狀態</b>——世界名稱是在 framework 執行緒先建好的唯讀字典。
    /// <see cref="TryGet"/>／<see cref="StateOf"/> 讀的是 <see cref="ConcurrentDictionary{TKey,TValue}"/>，
    /// 任何執行緒都可以呼叫。
    /// 🔴 刻意<b>不</b>用 <c>ECommons.Throttlers.EzThrottler</c>（本外掛也根本沒有 ECommons 相依）：
    /// 節流是這裡自己的 <see cref="MinRequestGapMs"/> ＋ <see cref="Gate"/>。
    /// </para>
    /// <para>
    /// 🔴 <b>例外只有一個</b>：掛售發生的那個世界（家世界）<b>永遠不會</b>被排除，
    /// 即使使用者把它勾進清單裡（見 <see cref="Configuration.BuildPricingExclusions()"/>）。
    /// 那是自己要定價的那一個市場，把它排掉之後畫面上只會顯示「查不到成交」，
    /// 使用者看不出來那是自己勾出來的。
    /// </para>
    /// </summary>
    internal static class LastSoldPriceSource
    {
        /// <summary>一次 HTTP 請求最多帶幾件道具（aggregated 端點吃逗號分隔的清單）。</summary>
        private const int BatchSize = 60;

        /// <summary>兩次 HTTP 請求之間的最小間隔。Universalis 是公開服務，不要打快。</summary>
        private const int MinRequestGapMs = 1200;

        /// <summary>
        /// 單一 HTTP 請求的時間預算。
        ///
        /// <para>
        /// ⚠️ 2026-09-10 校對後從 15 秒改成 30 秒：原本的 15 秒比兩個「實測會成功」的參考實作
        /// 都短——PriceInsight 用 <c>HttpClient</c> 的預設值（100 秒），Artisan 明確設 30 秒，
        /// 而且 Artisan 的註解記著它是從 10 秒調上來的，原因是「10 秒的預算都花在排隊等前一個
        /// 請求上」。這個值只管請求本身；呼叫端（重掛批次）另有自己的 25 秒等待上限。
        /// </para>
        /// </summary>
        private const int RequestTimeoutSeconds = 30;

        /// <summary>問到答案的道具，這麼久之後才會再問一次。</summary>
        private const int FreshMinutes = 30;

        /// <summary>「Universalis 沒有這件的資料」也要記住一段時間，否則每一幀都會重問。</summary>
        private const int NoDataMinutes = 30;

        /// <summary>問失敗的道具比較快重試，但也不是立刻。</summary>
        private const int FailedMinutes = 2;

        /// <summary>
        /// 連續失敗之後的退避（毫秒）。
        ///
        /// <para>
        /// ⚠️ 2026-09-10 從 15 秒改成 30 秒。理由是同一台機器上的實機證據：Universalis 在
        /// 2026-09-09 一天內對 InventoryTools 吐了 95 次 Cloudflare 錯誤頁（回應內容逐字是
        /// <c>error code: 520</c>）。服務自己在喘的時候，加快重問只會讓它更喘；
        /// InventoryTools 在同樣的情境下用的也是 30 秒。
        /// 使用者看得到的差別只有「這段期間重掛照原本的市場比價進行」，那本來就是安全的退路。
        /// </para>
        /// </summary>
        private const int FailureBackoffMs = 30000;

        /// <summary>
        /// 暫時性錯誤（408／429／5xx）之後重試一次的等待（毫秒）。
        ///
        /// <para>
        /// 📌 Universalis 是免費的公開服務，偶發的 429／504／520 是常態不是故障。
        /// <b>兩個實測會成功的參考實作都會重試</b>（PriceInsight 退 2 秒重試一次；
        /// Artisan 退 2／5／10 秒重試三次），原本這裡一次都不重試，是校對出來的落差。
        /// </para>
        /// </summary>
        private const int TransientRetryDelayMs = 2000;

        /// <summary>伺服器自己在 <c>Retry-After</c> 裡建議的等待，最多採信到這麼久。</summary>
        private const int MaxRetryAfterSeconds = 30;

        /// <summary>
        /// v3 補查一次同時打幾件。
        /// v3 是「一件一個請求」，所以這是唯一會扇出的地方；照 PriceInsight 實測可行的 3。
        /// </summary>
        private const int FallbackChunkSize = 3;

        /// <summary>Universalis 自己回報的資料中心組成表要快取多久（小時）。</summary>
        private const int DataCentersCacheHours = 12;

        private sealed class ItemEntry
        {
            public required LastSoldState State;
            public LastSoldEntry? Nq;
            public LastSoldEntry? Hq;

            /// <summary>目前本世界的最低掛售價（依品質分開）。null＝沒有資料，<b>不是 0</b>。</summary>
            public MarketPricePoint? MinWorldNq;

            public MarketPricePoint? MinWorldHq;

            /// <summary>目前整個資料中心的最低掛售價（依品質分開）。null＝沒有資料。</summary>
            public MarketPricePoint? MinDcNq;

            public MarketPricePoint? MinDcHq;

            /// <summary>
            /// 普通品<b>有</b>成交紀錄，但可以用的那幾筆全部落在被排除的世界上。
            /// 🔑 與「從來沒賣過」必須分得出來：兩者都不定價，但原因不同，
            /// 而只有知道原因的使用者才決定得了要不要把那個世界勾回來。
            /// </summary>
            public bool ExcludedNq;

            /// <summary>優質品同上。</summary>
            public bool ExcludedHq;

            /// <summary>
            /// 這筆答案是照哪一版排除清單算出來的
            /// （<see cref="ExclusionSnapshot.Revision"/>）。
            /// 🔴 在解析時過濾等於<b>把過濾結果寫進快取</b>，所以少了這個版號，
            /// 使用者把一個世界勾回來之後拿到的還是舊的過濾結果——而且完全無聲。
            /// </summary>
            public int ExclusionRevision;

            public DateTime StampUtc;
        }

        /// <summary>
        /// 排除清單的一份不可變快照。
        ///
        /// <para>
        /// 🔴 <see cref="Configuration.PriceSurveyExcludedWorlds"/> 是一個裸
        /// <c>List&lt;uint&gt;</c>，勾選框在<b>繪製執行緒</b>上改它，而這裡的解析跑在
        /// 執行緒池上 ⇒ <b>絕不</b>從背景執行緒直接讀那個 List。作法與待處理清單重算
        /// （<c>PendingActionsBuilder</c>）同一個形狀：在 framework 執行緒當場拍一份快照，
        /// 之後全程只讀快照。
        /// </para>
        /// <para>
        /// 🔑 清單與版號綁在<b>同一個物件</b>裡：兩個分開的 volatile 欄位讀起來不是原子的，
        /// 會出現「拿到新清單卻配到舊版號」那種對不上的組合。
        /// </para>
        /// </summary>
        internal sealed class ExclusionSnapshot(
            int configRevision, uint sellingWorldId, IReadOnlySet<uint> worlds)
        {
            private static int stampCounter;

            /// <summary>
            /// 這份快照的編號。🔴 <b>設定的版號或掛售所在的世界任一變了就是新的一號</b>，
            /// 所以快取裡的答案只要編號對不上就一律作廢重問。
            /// <para>
            /// ⚠️ 這<b>不是</b> <see cref="Configuration.WorldExclusionRevision"/>：換成一個
            /// 家世界不同的角色時那個版號不會動，但「該扣掉哪個世界」變了——只比版號會拿到
            /// 一份對不上的舊答案，而那種壞法是靜默的。
            /// </para>
            /// </summary>
            public int Revision { get; } = Interlocked.Increment(ref stampCounter);

            /// <summary>拍這份快照時設定裡的版號（只拿來判斷要不要重拍，以及寫 log）。</summary>
            public int ConfigRevision { get; } = configRevision;

            /// <summary>
            /// 拍這份快照時掛售發生在哪個世界（已經從 <see cref="Worlds"/> 裡扣掉了）。
            /// 0＝當時還沒登入，那一份什麼都沒扣。
            /// </summary>
            public uint SellingWorldId { get; } = sellingWorldId;

            private IReadOnlySet<uint> Worlds { get; } = worlds;

            /// <summary>排除清單是空的（＝這次不做任何過濾）。</summary>
            public bool IsEmpty => Worlds.Count == 0;

            /// <summary>0＝不知道是哪個世界；<b>不知道一律當成沒被排除</b>。</summary>
            public bool IsExcluded(uint worldId) => worldId != 0 && Worlds.Contains(worldId);
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        /// <summary>道具 id → 目前知道的事。發布之後不再修改，換值一律整個換掉一個新實例。</summary>
        private static readonly ConcurrentDictionary<uint, ItemEntry> Cache = new();

        /// <summary>
        /// 目前生效的排除清單快照。
        ///
        /// <para>
        /// 🔴 初始版號刻意是 <c>-1</c>（<see cref="Configuration.WorldExclusionRevision"/> 最小是 0）：
        /// 這樣第一次 <see cref="Request"/> 一定會去同步一次。寫成 0 會把「還沒問過設定」
        /// 誤當成「清單是空的」——而使用者的清單在版號 0 的時候就可能已經有內容了
        /// （出廠排除是上一次遊戲期間套用並存檔的，本次啟動版號仍是 0）。
        /// </para>
        /// </summary>
        private static volatile ExclusionSnapshot exclusion = new(-1, 0, new HashSet<uint>());

        /// <summary>🔴 只在持有這把鎖時碰 <see cref="Queue"/>／<see cref="queueWorldId"/>。鎖內不做 I/O、不寫 log、不碰 ImGui。</summary>
        private static readonly object Gate = new();

        private static readonly HashSet<uint> Queue = [];

        private static uint queueWorldId;

        /// <summary>快取屬於哪個世界（換世界＝整份丟掉，資料中心可能不同）。</summary>
        private static uint cacheWorldId;

        /// <summary>單一飛行閘：0＝沒有 pump 在跑。</summary>
        private static int pumping;

        private static long lastRequestTicks;
        private static long backoffUntilTicks;

        private static HttpClient? http;
        private static HappyEyeballsCallback? eyeballs;
        private static CancellationTokenSource? cts;

        /// <summary>
        /// 世界 id → 名稱。<b>在 framework 執行緒建好</b>之後只讀，供解析用。
        /// <c>volatile</c>：寫在 framework 執行緒、讀在執行緒池，發布之後內容不再改動。
        /// </summary>
        private static volatile IReadOnlyDictionary<uint, string> worldNames = new Dictionary<uint, string>();

        private static int failureLogged;

        private static int overviewFailureLogged;

        /// <summary>成功路徑只在第一次報告一行，之後安靜。</summary>
        private static int successLogged;

        /// <summary>
        /// Universalis 不認得的世界 id。
        ///
        /// <para>
        /// 📌 台服的 <c>World</c> 資料表裡混著測試世界與已退役世界，而<b>台服每一個世界的
        /// <c>IsPublic</c> 都是 false</b>（活的那八個也是），所以那個旗標當不了過濾條件。
        /// 實機 log 逐字證實 Universalis 對 4000／4001／4002／4020／4021／4023／4024 回 404。
        /// ⇒ 只有 Universalis 自己答得出來，記住它拒絕過的世界就不要再問。
        /// </para>
        /// <para>
        /// 刻意只活在這一次遊戲期間、不落地：重載會重新探一次，一次連線異常不會永久註銷一個
        /// 真的存在的世界。
        /// </para>
        /// </summary>
        private static readonly ConcurrentDictionary<uint, byte> UnknownWorlds = new();

        private static readonly SemaphoreSlim DataCentersGate = new(1, 1);

        private static uint[]? dcWorlds;

        private static uint dcWorldsForWorld;

        private static DateTime dcWorldsExpiryUtc = DateTime.MinValue;

        internal static void Init()
        {
            if (http != null)
                return;

            eyeballs = new HappyEyeballsCallback();
            http = new HttpClient(new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                ConnectCallback = eyeballs.ConnectCallback,
            })
            {
                Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds),
            };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"Marketbuddy/{Assembly.GetExecutingAssembly().GetName().Version}");
            cts = new CancellationTokenSource();

            // 一次性的「只講一次」旗標在這裡歸零，重新載入外掛就會再報告一次第一筆成功。
            Volatile.Write(ref failureLogged, 0);
            Volatile.Write(ref overviewFailureLogged, 0);
            Volatile.Write(ref successLogged, 0);
        }

        internal static void Shutdown()
        {
            try
            {
                cts?.Cancel();
            }
            catch
            {
                // 取消失敗不能擋住後面的釋放。
            }

            var client = http;
            http = null;
            client?.Dispose();
            eyeballs?.Dispose();
            eyeballs = null;
            cts?.Dispose();
            cts = null;

            lock (Gate)
            {
                Queue.Clear();
            }

            Cache.Clear();
            UnknownWorlds.Clear();
            dcWorlds = null;
            dcWorldsForWorld = 0;
            dcWorldsExpiryUtc = DateTime.MinValue;
        }

        /// <summary>
        /// 世界 id → 名稱的對照表。
        /// 🔴 只從 framework 執行緒呼叫：它會讀遊戲的資料表。建好之後執行緒池那一側只讀。
        /// </summary>
        private static void EnsureWorldNames()
        {
            if (worldNames.Count > 0)
                return;

            try
            {
                var sheet = DataManager.GetExcelSheet<World>();
                if (sheet == null)
                    return;
                var map = new Dictionary<uint, string>();
                foreach (var world in sheet)
                {
                    var name = world.Name.ExtractText();
                    if (name.Length > 0)
                        map[world.RowId] = name;
                }

                if (map.Count > 0)
                    worldNames = map;
            }
            catch (Exception e)
            {
                Log.Information(e, "[Marketbuddy] 歷史賣出價：讀取世界名稱表失敗，成交世界會顯示成空白。");
            }
        }

        /// <summary>
        /// 把排除清單同步成一份新快照（版號沒變就沿用舊的那一份，不重新配置）。
        /// 🔴 只從 framework 執行緒呼叫：它會讀設定裡那個裸 <c>List</c>。
        /// </summary>
        private static ExclusionSnapshot SyncExclusions()
        {
            var current = exclusion;
            Configuration conf;
            try
            {
                conf = Configuration.GetOrLoad();
            }
            catch (Exception e)
            {
                // 讀不到設定時沿用現行快照：寧可少過濾一次，也不要讓整條查價路徑掛掉。
                Log.Information(e, "[Marketbuddy] 歷史賣出價：讀取世界排除清單失敗，沿用上一份快照。");
                return current;
            }

            // 🔴 兩個條件都要成立才可以沿用舊快照：設定版號沒變，但換成一個家世界不同的
            //    角色時「該扣掉哪個世界」就變了。只比版號會拿到一份對不上的清單，
            //    而失敗形式是靜默的——價格照算，只是多算或少算了一個世界。
            var selling = Configuration.SellingWorldId();
            if (current.ConfigRevision == conf.WorldExclusionRevision &&
                current.SellingWorldId == selling)
                return current;

            // 🔴 清單一律走這條唯一實作點拿：掛售發生的那個世界永遠不在裡面。
            //    id 用上面讀好的那一個，不讓它自己再讀一次——兩次讀到不同答案時
            //    會產生「清單扣掉 A、快照卻說扣掉 B」那種自相矛盾的組合。
            var worlds = conf.BuildPricingExclusions(selling);
            var next = new ExclusionSnapshot(conf.WorldExclusionRevision, selling, worlds);
            exclusion = next;

            // 要使用者回報的診斷一律寫 Information。
            var names = worlds.Count == 0
                ? "（空）"
                : string.Join("、", worlds.Select(x =>
                    worldNames.TryGetValue(x, out var n) ? $"{n}({x})" : x.ToString()));
            Log.Information(
                $"[Marketbuddy] 歷史賣出價：套用世界排除清單（第 {next.ConfigRevision} 版，{worlds.Count} 個）：{names}。" +
                "這些世界的成交紀錄不會被拿來當「最近成交價重掛」的價格；" +
                "已經查到的答案會照新清單重新查一次。");
            if (selling != 0 && conf.IsWorldExcluded(selling))
            {
                var sellingLabel = worldNames.TryGetValue(selling, out var sellingName)
                    ? $"{sellingName}({selling})"
                    : selling.ToString();
                Log.Information(
                    $"[Marketbuddy] 歷史賣出價：掛售所在的世界（{sellingLabel}）在排除清單上，" +
                    "但定價一律不排除它——那是你自己要定價的那一個市場。" +
                    "勾選仍然會讓它從換世界選單、自動續跑、掃描與比價／採購兩張表上消失。");
            }

            return next;
        }

        /// <summary>
        /// 這件道具目前是什麼狀態。任何執行緒都可以呼叫。
        ///
        /// <para>
        /// 🔴 照<b>舊版</b>排除清單算出來的答案一律回 <see cref="LastSoldState.Unknown"/>。
        /// 那不是「沒有資料」：呼叫端看到 Unknown 會自己補排一次查詢，所以這條路的語意是
        /// 「重新問一次」，而落在中間的那幾幀畫面上顯示的是「查詢中」而不是某個舊價。
        /// </para>
        /// </summary>
        internal static LastSoldState StateOf(uint itemId)
            => Cache.TryGetValue(itemId, out var entry) && entry.ExclusionRevision == exclusion.Revision
                ? entry.State
                : LastSoldState.Unknown;

        /// <summary>
        /// 取這件道具的「歷史最近賣出價」。
        /// </summary>
        /// <param name="hq">這一格掛的是優質品嗎。</param>
        /// <param name="ignoreQuality">
        /// true＝忽略優質狀態，把同一個 itemId 的優質與普通成交<b>視為同一件道具</b>，
        /// 取<b>時間最近</b>的那一筆（那才符合「最近賣出價」的字面）。
        /// </param>
        internal static bool TryGet(uint itemId, bool hq, bool ignoreQuality, out LastSoldEntry entry)
        {
            entry = default;
            if (!Cache.TryGetValue(itemId, out var item) || item.State != LastSoldState.Ready ||
                item.ExclusionRevision != exclusion.Revision)
                return false;

            var mine = hq ? item.Hq : item.Nq;
            if (!ignoreQuality)
            {
                if (mine == null)
                    return false;
                entry = mine.Value;
                return true;
            }

            var other = hq ? item.Nq : item.Hq;
            if (mine == null && other == null)
                return false;
            if (mine == null)
            {
                entry = other!.Value;
                return true;
            }

            if (other == null)
            {
                entry = mine.Value;
                return true;
            }

            entry = other.Value.SoldAtUtc > mine.Value.SoldAtUtc ? other.Value : mine.Value;
            return true;
        }

        /// <summary>
        /// 這件道具「<b>有</b>成交紀錄，但能用的那幾筆全部來自被排除的世界」嗎。
        ///
        /// <para>
        /// 🔑 存在的理由只有一個：它與「從來沒賣過」<b>必須在畫面上分得出來</b>。
        /// 兩者的處置相同（都不定價、落回原本的市場比價），但原因完全不同——
        /// 前者只要把那個世界勾回來就有價格了，後者勾什麼都沒用。
        /// 把兩件事畫成同一個符號等於叫使用者去猜。
        /// </para>
        /// <para>
        /// ⚠️ 只有在 <see cref="TryGet"/> 回 false 的時候問這個才有意義：
        /// 「忽略優質狀態」開著而另一個品質有可用成交時，<see cref="TryGet"/> 會給出價格，
        /// 這時本旗標仍可能為 true（某一個品質被排除了），但那並不是使用者要看的事。
        /// </para>
        /// </summary>
        internal static bool IsSaleExcluded(uint itemId, bool hq, bool ignoreQuality)
        {
            if (!Cache.TryGetValue(itemId, out var item) ||
                item.ExclusionRevision != exclusion.Revision)
                return false;

            var mine = hq ? item.ExcludedHq : item.ExcludedNq;
            if (!ignoreQuality)
                return mine;

            return mine || (hq ? item.ExcludedNq : item.ExcludedHq);
        }

        /// <summary>
        /// 這件道具目前的最低掛售價：本世界一個、整個資料中心一個。
        ///
        /// <para>
        /// 🔴 兩個都是 <c>MarketPricePoint?</c>：<b>「查不到」是 null，不會是 0</b>。
        /// </para>
        /// <para>
        /// ⚠️ 這裡<b>一律照這一格自己的品質</b>取，不受「忽略優質狀態」影響：
        /// 那個選項的用途是找「最近賣出價」，而拿自己的優質品去跟普通品的最低價比是錯的比較。
        /// </para>
        /// <para>
        /// 📌 <b>這兩個值刻意不套世界排除清單</b>，而且也不受清單版號影響。
        /// 排除清單的用途是「不要拿這個世界的價格幫你定價」，而最低掛售價是純顯示的現況
        /// （使用者拿它判斷要不要降價），把它藏起來只會讓畫面少講一件正在發生的事。
        /// ⚠️ 而且資料中心那一半是 Universalis <b>伺服器端算好的單一最小值</b>，
        /// 手上沒有原始清單，過濾不掉——只過濾得到的那一半會變成「時對時錯而且分不出來」。
        /// </para>
        /// </summary>
        internal static bool TryGetMinPrices(uint itemId, bool hq,
            out MarketPricePoint? world, out MarketPricePoint? datacenter)
        {
            world = null;
            datacenter = null;
            if (!Cache.TryGetValue(itemId, out var item) || item.State != LastSoldState.Ready)
                return false;

            world = hq ? item.MinWorldHq : item.MinWorldNq;
            datacenter = hq ? item.MinDcHq : item.MinDcNq;
            return world != null || datacenter != null;
        }

        /// <summary>
        /// 無條件捨去到百位。
        ///
        /// <para>
        /// 🔴 <b>全程整數運算</b>（<c>p / 100 * 100</c>）：浮點數在邊界值上會給錯答案，
        /// 而價格是錢。
        /// </para>
        /// <para>
        /// 捨去之後小於 100 就用 100——絕不掛出 0 gil。上限夾在
        /// <see cref="Configuration.MAX_PRICE"/>。
        /// </para>
        /// <para>
        /// 手算驗過的邊界：0→100、99→100、100→100、101→100、980→900、12345→12300、
        /// 999999999→999999900。
        /// </para>
        /// </summary>
        internal static uint RoundDownToHundred(long unitPrice)
        {
            var floored = unitPrice <= 0 ? 0L : unitPrice / 100L * 100L;
            if (floored < 100L)
                floored = 100L;
            if (floored > Configuration.MAX_PRICE)
                floored = Configuration.MAX_PRICE;
            return (uint)floored;
        }

        /// <summary>
        /// 把這些道具排進查詢佇列（已經有夠新的答案就跳過）。
        /// 🔴 只從 framework 執行緒呼叫。
        /// </summary>
        /// <param name="worldId">目前所在世界的 id；0＝還沒登入，直接不做事。</param>
        internal static void Request(uint worldId, IReadOnlyCollection<uint> itemIds)
        {
            if (http == null || worldId == 0 || itemIds.Count == 0)
                return;

            EnsureWorldNames();
            var snapshot = SyncExclusions();

            if (worldId != cacheWorldId)
            {
                // 換世界＝可能換了資料中心，整份丟掉重來。
                Cache.Clear();
                lock (Gate)
                {
                    Queue.Clear();
                }

                cacheWorldId = worldId;
            }

            if (UnknownWorlds.ContainsKey(worldId))
            {
                // Universalis 不認得這個世界，問了只會再拿一次 404。直接標成「沒有資料」，
                // 讓呼叫端立刻落回它原本的定價方式——標成「查詢中」會讓每一格白等 25 秒逾時。
                Mark(itemIds.Where(x => x != 0), LastSoldState.NoData);
                return;
            }

            var now = DateTime.UtcNow;
            var added = false;
            lock (Gate)
            {
                queueWorldId = worldId;
                foreach (var itemId in itemIds)
                {
                    if (itemId == 0)
                        continue;

                    // 🔴 排除清單改過之後，照舊清單過濾出來的答案一律作廢重問。
                    //    不重問的話它會在快取裡放到 TTL 到期（最長 30 分鐘），
                    //    使用者把一個世界勾回來卻要等半小時才看得到變化。
                    if (Cache.TryGetValue(itemId, out var existing) &&
                        existing.ExclusionRevision == snapshot.Revision)
                    {
                        if (existing.State == LastSoldState.Loading)
                            continue;

                        var ttlMinutes = existing.State switch
                        {
                            LastSoldState.Ready => FreshMinutes,
                            LastSoldState.NoData => NoDataMinutes,
                            _ => FailedMinutes,
                        };
                        if (now - existing.StampUtc < TimeSpan.FromMinutes(ttlMinutes))
                            continue;
                    }

                    if (!Queue.Add(itemId))
                        continue;

                    // 一排進佇列就標成「查詢中」，畫面立刻分得出「還在問」與「問不到」。
                    Cache[itemId] = new ItemEntry
                    {
                        State = LastSoldState.Loading,
                        StampUtc = now,
                        ExclusionRevision = snapshot.Revision,
                    };
                    added = true;
                }
            }

            if (added)
                Kick();
        }

        private static void Kick()
        {
            if (Interlocked.CompareExchange(ref pumping, 1, 0) != 0)
                return;
            _ = Task.Run(PumpAsync);
        }

        private static async Task PumpAsync()
        {
            var token = cts?.Token ?? CancellationToken.None;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    uint worldId;
                    List<uint> batch;
                    lock (Gate)
                    {
                        if (Queue.Count == 0)
                            break;
                        worldId = queueWorldId;
                        batch = Queue.Take(BatchSize).ToList();
                        foreach (var itemId in batch)
                            Queue.Remove(itemId);
                    }

                    try
                    {
                        var delay = DelayBeforeNextRequest();
                        if (delay > TimeSpan.Zero)
                            await Task.Delay(delay, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        Mark(batch, LastSoldState.Failed);
                        break;
                    }

                    // FetchAsync 自己吞掉所有例外，而且保證把 batch 裡的每一件都標上結果，
                    // 不然它們會永遠停在「查詢中」。
                    await FetchAsync(worldId, batch, token).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                // 🔴 這裡自己再包一層：外掛卸載時 CancellationTokenSource 已經被釋放，
                //    連寫 log 都可能擲例外，而那會變成沒人接的 Task 例外。
                try
                {
                    Log.Information(e, "[Marketbuddy] 歷史賣出價：查詢迴圈意外結束。");
                }
                catch
                {
                    // 記錄失敗絕不能再往外擲。
                }
            }
            finally
            {
                Volatile.Write(ref pumping, 0);

                var again = false;
                lock (Gate)
                {
                    again = Queue.Count > 0;
                }

                if (again)
                    Kick();
            }
        }

        private static TimeSpan DelayBeforeNextRequest()
        {
            var now = DateTime.UtcNow;
            var wait = TimeSpan.Zero;

            var backoffUntil = new DateTime(Interlocked.Read(ref backoffUntilTicks), DateTimeKind.Utc);
            if (backoffUntil > now)
                wait = backoffUntil - now;

            var lastRequest = new DateTime(Interlocked.Read(ref lastRequestTicks), DateTimeKind.Utc);
            var gap = TimeSpan.FromMilliseconds(MinRequestGapMs) - (now - lastRequest);
            if (gap > wait)
                wait = gap;

            return wait;
        }

        /// <summary>🔴 保證不擲例外，而且 <paramref name="batch"/> 裡每一件都會被標上一個最終狀態。</summary>
        private static async Task FetchAsync(uint worldId, List<uint> batch, CancellationToken token)
        {
            var client = http;
            if (client == null)
            {
                Mark(batch, LastSoldState.Failed);
                return;
            }

            Interlocked.Exchange(ref lastRequestTicks, DateTime.UtcNow.Ticks);

            // 🔴 整批只讀這一次 volatile：中途換快照會讓同一批裡的道具用不同版號過濾，
            //    而版號是寫進快取的，那會變成一批答案裡有些永遠被判成過期。
            var ex = exclusion;

            try
            {
                var url = "https://universalis.app/api/v2/aggregated/" + worldId + "/" +
                          string.Join(",", batch);
                using var response = await SendWithRetryAsync(client, url, token).ConfigureAwait(false);

                // 404＝Universalis 不認得這個世界 id。那不是暫時的，重試沒有意義。
                // 🔑 這是台服特有的形狀：遊戲的 World 表裡混著測試／退役世界，而 IsPublic 全是
                //    false，分不出來——只有 Universalis 答得出哪些是活的。
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    if (UnknownWorlds.TryAdd(worldId, 0))
                        Log.Information(
                            $"[Marketbuddy] 歷史賣出價：Universalis 不認得世界 id {worldId}，" +
                            "這一次遊戲期間不再向它查詢；重掛會照原本的市場比價進行。");
                    Mark(batch, LastSoldState.NoData);
                    return;
                }

                var unresolved = new HashSet<uint>();

                // 400＝這一批的道具在 Universalis 的「可上市清單」裡一件都不存在。
                // 🔑 2026-09-10 用 curl 直打實測過這個端點的兩種形狀：只要有「任何一件」解得開
                //    就回 200，解不開的那幾件放進 failedItems；「整批都解不開」才回 400。
                // ⚠️ 但「aggregated 解不開」不等於「沒有市場資料」——同日實測道具 5730（染料）
                //    在 aggregated 是 failedItems，在 v3 overview 卻有 38 筆掛售、133 筆成交。
                //    ⇒ 這裡不再直接判「沒有資料」，改交給 v3 補查（見 ResolveWithOverviewAsync）。
                if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    unresolved.UnionWith(batch);
                }
                else if (!response.IsSuccessStatusCode)
                {
                    NoteFailure($"HTTP {(int)response.StatusCode}");
                    Mark(batch, LastSoldState.Failed);
                    return;
                }
                else
                {
                    await ParseAggregatedAsync(response, batch, unresolved, worldId, ex, token)
                        .ConfigureAwait(false);
                }

                HashSet<uint> recovered;
                try
                {
                    recovered = await ResolveWithOverviewAsync(worldId, unresolved, ex, token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    // 🔴 補查失敗絕不能連累同一批裡已經查到的那幾件——它們早就寫進 Cache 了。
                    NoteOverviewFailure(e.Message);
                    recovered = [];
                }

                var stamp = DateTime.UtcNow;
                foreach (var itemId in unresolved)
                {
                    if (!recovered.Contains(itemId))
                        Cache[itemId] = new ItemEntry
                        {
                            State = LastSoldState.NoData,
                            StampUtc = stamp,
                            ExclusionRevision = ex.Revision,
                        };
                }

                Volatile.Write(ref failureLogged, 0);
                LogFirstSuccess(batch.Count, unresolved.Count, recovered.Count);
            }
            catch (OperationCanceledException)
            {
                // 外掛卸載／換世界；不是錯誤。
                Mark(batch, LastSoldState.Failed);
            }
            catch (Exception e)
            {
                // 🔑 這裡也是「回應根本不是 JSON」的落點：Universalis 在 Cloudflare 後面，
                //    服務出狀況時會吐 `error code: 520` 這種純文字錯誤頁。實機 log 證實同一台
                //    機器上 InventoryTools 因為這個在 2026-09-09 一天內炸了 95 次。
                //    JsonException 是 Exception 的子類，所以會停在這裡：查詢迴圈不會被打死，
                //    這一批被標成「失敗」（暫時的），下一批照常。
                NoteFailure(e.Message);
                Mark(batch, LastSoldState.Failed);
            }
        }

        /// <summary>
        /// 解析 aggregated 端點的 200 回應，把結果寫進 <see cref="Cache"/>，
        /// 並把「這個端點答不出來」的道具收進 <paramref name="unresolved"/>。
        /// </summary>
        private static async Task ParseAggregatedAsync(
            HttpResponseMessage response, List<uint> batch, HashSet<uint> unresolved,
            uint worldId, ExclusionSnapshot ex, CancellationToken token)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var json = await JsonSerializer
                .DeserializeAsync<AggregatedResponse>(stream, JsonOptions, token)
                .ConfigureAwait(false);

            if (json == null)
            {
                // 合法的 JSON `null`。當成「這一批都沒答案」交給補查，而不是整批算失敗。
                unresolved.UnionWith(batch);
                return;
            }

            var now = DateTime.UtcNow;
            var handled = new HashSet<uint>();

            foreach (var result in json.results ?? [])
            {
                var nq = Pick(result.nq, false, ex, worldId, out var nqExcluded);
                var hq = Pick(result.hq, true, ex, worldId, out var hqExcluded);
                var minWorldNq = PickMin(result.nq?.minListing?.world);
                var minWorldHq = PickMin(result.hq?.minListing?.world);
                var minDcNq = PickMin(result.nq?.minListing?.dc);
                var minDcHq = PickMin(result.hq?.minListing?.dc);

                // 🔑 「有答案」不等於「有成交紀錄」：只有掛售、從來沒賣出過的道具也算 Ready，
                // 否則最低價那兩欄會跟著「沒賣過」一起消失。哪一種資料缺席由各自的取值函式回報。
                // 🔑 「唯一的成交紀錄被排除掉」也算問到了答案（Ready）：
                //    標成 NoData 會讓畫面說出「這件從來沒賣過」——那是錯的，
                //    而且它會抹掉「把那個世界勾回來就有價了」這個唯一有用的提示。
                var anything = nq != null || hq != null ||
                               minWorldNq != null || minWorldHq != null ||
                               minDcNq != null || minDcHq != null ||
                               nqExcluded || hqExcluded;

                Cache[result.itemId] = new ItemEntry
                {
                    State = anything ? LastSoldState.Ready : LastSoldState.NoData,
                    Nq = nq,
                    Hq = hq,
                    MinWorldNq = minWorldNq,
                    MinWorldHq = minWorldHq,
                    MinDcNq = minDcNq,
                    MinDcHq = minDcHq,
                    ExcludedNq = nqExcluded,
                    ExcludedHq = hqExcluded,
                    ExclusionRevision = ex.Revision,
                    StampUtc = now,
                };
                handled.Add(result.itemId);
            }

            // failedItems ＝「這件不在 Universalis 的可上市清單裡」，不是「這件沒有市場資料」。
            unresolved.UnionWith(json.failedItems ?? []);

            // 回應裡完全沒提到、failedItems 也沒有的那幾件一併交給補查；
            // 不處理的話它們會永遠卡在「查詢中」。
            foreach (var itemId in batch)
            {
                if (!handled.Contains(itemId))
                    unresolved.Add(itemId);
            }
        }

        private static void Mark(IEnumerable<uint> itemIds, LastSoldState state)
        {
            var now = DateTime.UtcNow;
            var revision = exclusion.Revision;
            foreach (var itemId in itemIds)
                Cache[itemId] = new ItemEntry
                {
                    State = state,
                    StampUtc = now,
                    ExclusionRevision = revision,
                };
        }

        /// <summary>
        /// 記一次失敗並退避。
        /// 要使用者回報的診斷寫 <c>Information</c>；同一段連續失敗只寫第一行，避免洗版。
        /// </summary>
        private static void NoteFailure(string reason)
        {
            Interlocked.Exchange(ref backoffUntilTicks,
                DateTime.UtcNow.AddMilliseconds(FailureBackoffMs).Ticks);

            if (Interlocked.Exchange(ref failureLogged, 1) != 0)
                return;

            Log.Information(
                $"[Marketbuddy] 歷史賣出價：向 Universalis 查詢失敗（{reason}）。" +
                "這只影響「以歷史最近賣出價重掛」這個定價方式，其他功能不受影響；" +
                "成功一次之後會再回報下一次失敗。");
        }

        /// <summary>
        /// 值得再試一次的狀態：408／429 加上任何 5xx。其餘不是成功就是重試也治不好的客戶端錯誤。
        /// 📌 這條判準與 PriceInsight、Artisan 兩邊逐字相同（兩邊都叫 <c>IsTransient</c>）。
        /// </summary>
        private static bool IsTransient(HttpStatusCode status)
            => status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                or >= HttpStatusCode.InternalServerError;

        /// <summary>
        /// 送一個 GET；遇到暫時性狀態就退一下再試一次（只重試一次）。
        ///
        /// <para>
        /// 伺服器自己在 <c>Retry-After</c> 裡給建議時以它為準，夾在
        /// <see cref="MaxRetryAfterSeconds"/>；沒給就用 <see cref="TransientRetryDelayMs"/>。
        /// </para>
        /// <para>
        /// 🔴 回傳的 <see cref="HttpResponseMessage"/> 由呼叫端負責釋放。
        /// </para>
        /// </summary>
        private static async Task<HttpResponseMessage> SendWithRetryAsync(
            HttpClient client, string url, CancellationToken token)
        {
            for (var attempt = 0; ; attempt++)
            {
                var response = await client.GetAsync(url, token).ConfigureAwait(false);
                if (attempt >= 1 || !IsTransient(response.StatusCode))
                    return response;

                var advised = response.Headers.RetryAfter?.Delta
                              ?? (response.Headers.RetryAfter?.Date is { } date
                                  ? date - DateTimeOffset.UtcNow
                                  : null);
                response.Dispose();

                var delay = advised is { } value && value > TimeSpan.Zero &&
                            value < TimeSpan.FromSeconds(MaxRetryAfterSeconds)
                    ? value
                    : TimeSpan.FromMilliseconds(TransientRetryDelayMs);
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// aggregated 端點答不出來的那幾件，改用 v3 <c>market/overview</c> 端點逐件補查。
        /// 回傳<b>真的補到資料</b>的道具 id（它們已經寫進 <see cref="Cache"/>）。
        ///
        /// <para>
        /// 🔑 為什麼需要這條路：aggregated 端點會擋掉不在它「可上市清單」裡的道具，而台服有一批
        /// 道具（實測是 5730 起的那批舊染料，還有 30118／30121／30122／48172）就落在那個縫裡。
        /// 對這些道具，aggregated 回 <c>failedItems</c>／400，v3 卻答得出來。
        /// 沒有這條路，使用者看到的是「這件沒賣過」——而那是錯的。
        /// </para>
        /// </summary>
        private static async Task<HashSet<uint>> ResolveWithOverviewAsync(
            uint worldId, IReadOnlyCollection<uint> itemIds, ExclusionSnapshot ex,
            CancellationToken token)
        {
            var recovered = new HashSet<uint>();
            if (itemIds.Count == 0)
                return recovered;

            var client = http;
            if (client == null)
                return recovered;

            var worlds = await GetDataCenterWorldsAsync(client, worldId, token).ConfigureAwait(false);
            if (worlds.Length == 0)
                return recovered;

            var worldList = string.Join(",", worlds);

            foreach (var chunk in itemIds.Chunk(FallbackChunkSize))
            {
                var results = await Task.WhenAll(chunk.Select(async itemId =>
                {
                    try
                    {
                        var entry = await FetchOverviewAsync(client, worldList, worldId, itemId, ex, token)
                            .ConfigureAwait(false);
                        return (ItemId: itemId, Entry: entry);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception e)
                    {
                        // 一件補查失敗不能拖垮同一批的其他件；它會照原路被標成「沒有資料」。
                        NoteOverviewFailure(e.Message);
                        return (ItemId: itemId, Entry: (ItemEntry?)null);
                    }
                })).ConfigureAwait(false);

                foreach (var (itemId, entry) in results)
                {
                    if (entry == null)
                        continue;
                    Cache[itemId] = entry;
                    recovered.Add(itemId);
                }
            }

            // 補查也是打 Universalis，所以下一批仍然要等滿 MinRequestGapMs。
            Interlocked.Exchange(ref lastRequestTicks, DateTime.UtcNow.Ticks);
            return recovered;
        }

        private static async Task<ItemEntry?> FetchOverviewAsync(
            HttpClient client, string worldList, uint worldId, uint itemId,
            ExclusionSnapshot ex, CancellationToken token)
        {
            var url = "https://universalis.app/api/v3/market/overview/" + worldList + "/" + itemId;
            using var response = await SendWithRetryAsync(client, url, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var overview = await JsonSerializer
                .DeserializeAsync<OverviewDto>(stream, JsonOptions, token)
                .ConfigureAwait(false);
            if (overview == null)
                return null;

            var entry = BuildFromOverview(overview, worldId, ex);
            if (entry != null)
                Volatile.Write(ref overviewFailureLogged, 0);
            return entry;
        }

        /// <summary>
        /// 目前世界所屬資料中心有哪些世界——直接<b>問 Universalis 自己</b>。
        ///
        /// <para>
        /// 🔴 刻意<b>不</b>從遊戲的 <c>World</c> 資料表推：台服的資料表把測試世界與已退役世界
        /// 跟活的放在同一個資料中心底下，而每一個世界的 <c>IsPublic</c> 都是 false，
        /// 光看資料表分不出來（PriceInsight 就是這樣掉進去，只好逐個世界試到 404 再排除）。
        /// 2026-09-10 實測 Universalis 的 <c>data-centers</c>：陸行鳥＝4028~4035 這八個，
        /// 剛好就是活的那八個。
        /// </para>
        /// <para>
        /// ⚠️ 只取<b>數字</b>，絕不用資料中心或世界的名字組 URL。同日實測逐字驗過：
        /// <c>/api/v2/利維坦/36256</c> 回 <b>404</b>，而 <c>/api/v2/4030/36256</c> 回 200 有資料。
        /// （這正是同一台機器上 InventoryTools 查價在台服拿不到東西的原因。）
        /// </para>
        /// </summary>
        private static async Task<uint[]> GetDataCenterWorldsAsync(
            HttpClient client, uint worldId, CancellationToken token)
        {
            var cached = dcWorlds;
            if (cached != null && dcWorldsForWorld == worldId && DateTime.UtcNow < dcWorldsExpiryUtc)
                return cached;

            await DataCentersGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                cached = dcWorlds;
                if (cached != null && dcWorldsForWorld == worldId && DateTime.UtcNow < dcWorldsExpiryUtc)
                    return cached;

                using var response = await SendWithRetryAsync(
                    client, "https://universalis.app/api/v2/data-centers", token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return [];

                await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                var centers = await JsonSerializer
                    .DeserializeAsync<List<DataCenterDto>>(stream, JsonOptions, token)
                    .ConfigureAwait(false);

                var worlds = centers?
                    .FirstOrDefault(c => c.worlds != null && c.worlds.Contains(worldId))?
                    .worlds?.ToArray() ?? [];
                if (worlds.Length == 0)
                    return [];

                dcWorlds = worlds;
                dcWorldsForWorld = worldId;
                dcWorldsExpiryUtc = DateTime.UtcNow.AddHours(DataCentersCacheHours);
                return worlds;
            }
            finally
            {
                DataCentersGate.Release();
            }
        }

        /// <summary>
        /// 把 v3 overview 的原始掛售／成交清單壓成與 aggregated 端點<b>同一個形狀</b>。
        ///
        /// <para>
        /// 🔑 2026-09-10 拿一件<b>兩個端點都查得到</b>的道具校準過（道具 36256、世界 4030），
        /// 四個值逐一相同：本世界最低價 130、資料中心最低價 99、最近成交單價 160、
        /// 時間戳 1788968528000（世界 4030）。沒有這一步就分不出「換算對」與
        /// 「換算錯但看起來很合理」。
        /// </para>
        /// <para>
        /// ⚠️ v3 的掛售 <c>price</c> 是<b>含稅</b>的每單位價而且是浮點數，遊戲介面與 aggregated
        /// 端點用的則是<b>未稅整數價</b> ⇒ 一律從 <c>total</c>／<c>quantity</c> 還原。
        /// 成交紀錄的 <c>price</c> 本來就是未稅單價，不要再換算。
        /// </para>
        /// <para>
        /// ⚠️ v3 每個世界最多回 20 筆成交，而且留下的是<b>最新</b>的 20 筆（同日實測：最舊的一筆
        /// 落在 2～4 天前）⇒ 拿它取「最近一次成交」是安全的；拿它算平均價或成交速度<b>不安全</b>，
        /// 所以這裡一個都不算。
        /// </para>
        /// </summary>
        private static ItemEntry? BuildFromOverview(OverviewDto overview, uint worldId,
            ExclusionSnapshot ex)
        {
            var listings = overview.listings ?? [];
            var sales = overview.sales ?? [];

            // 🔑 這條路手上有<b>原始</b>成交清單，所以過濾的結果是真的
            //    「排除清單以外的最近一次成交」，不是退一步的近似值。
            var nq = MostRecentSale(sales, false, ex, out var nqExcluded);
            var hq = MostRecentSale(sales, true, ex, out var hqExcluded);

            // 📌 最低掛售價刻意不過濾（理由寫在 TryGetMinPrices 上）。
            var minWorldNq = CheapestListing(listings, false, worldId);
            var minWorldHq = CheapestListing(listings, true, worldId);
            var minDcNq = CheapestListing(listings, false, null);
            var minDcHq = CheapestListing(listings, true, null);

            if (nq == null && hq == null && minWorldNq == null && minWorldHq == null &&
                minDcNq == null && minDcHq == null && !nqExcluded && !hqExcluded)
                return null;

            return new ItemEntry
            {
                State = LastSoldState.Ready,
                Nq = nq,
                Hq = hq,
                MinWorldNq = minWorldNq,
                MinWorldHq = minWorldHq,
                MinDcNq = minDcNq,
                MinDcHq = minDcHq,
                ExcludedNq = nqExcluded,
                ExcludedHq = hqExcluded,
                ExclusionRevision = ex.Revision,
                StampUtc = DateTime.UtcNow,
            };
        }

        /// <summary>
        /// 這個品質最近成交的那一筆；一筆都沒有就回 null，<b>絕不回 0</b>。
        ///
        /// <para>
        /// 🔴 被排除的世界一筆都不看。這裡與 aggregated 那條路的差別是：這裡拿得到
        /// <b>原始</b>成交清單，所以挑出來的真的是「排除清單以外的最近一次成交」。
        /// </para>
        /// </summary>
        /// <param name="excludedOnly">
        /// true＝這個品質有成交紀錄，但<b>每一筆</b>都落在被排除的世界上。
        /// </param>
        internal static LastSoldEntry? MostRecentSale(List<OverviewSaleDto> sales, bool hq,
            ExclusionSnapshot ex, out bool excludedOnly)
        {
            excludedOnly = false;
            OverviewSaleDto? best = null;
            var dropped = false;
            foreach (var sale in sales)
            {
                if (sale.hq != hq || sale.price is not > 0m)
                    continue;
                if (ex.IsExcluded(sale.world ?? 0u))
                {
                    dropped = true;
                    continue;
                }

                if (best == null || (sale.saleTime ?? 0L) > (best.saleTime ?? 0L))
                    best = sale;
            }

            if (best == null)
            {
                excludedOnly = dropped;
                return null;
            }

            var world = string.Empty;
            if (best.world is { } soldWorld && worldNames.TryGetValue(soldWorld, out var name))
                world = name;

            return new LastSoldEntry(
                (long)Math.Floor(best.price!.Value), ToUtc(best.saleTime), hq, world);
        }

        /// <summary>
        /// 這個品質最便宜的掛售；<paramref name="onlyWorld"/> 為 null＝整個資料中心。
        /// 沒有就回 null，<b>絕不回 0</b>。
        /// </summary>
        internal static MarketPricePoint? CheapestListing(
            List<OverviewListingDto> listings, bool hq, uint? onlyWorld)
        {
            var bestPrice = 0L;
            var bestWorld = 0u;
            foreach (var listing in listings)
            {
                if (listing.hq != hq)
                    continue;
                if (onlyWorld is { } wanted && listing.world != wanted)
                    continue;

                var price = UntaxedUnitPrice(listing);
                if (price <= 0L)
                    continue;
                if (bestPrice != 0L && price >= bestPrice)
                    continue;

                bestPrice = price;
                bestWorld = listing.world ?? 0u;
            }

            if (bestPrice == 0L)
                return null;

            var world = string.Empty;
            if (bestWorld != 0u && worldNames.TryGetValue(bestWorld, out var name))
                world = name;

            return new MarketPricePoint(bestPrice, world);
        }

        /// <summary>
        /// v3 掛售的<b>未稅</b>單價。
        /// <c>total</c> 是含稅總價（5% 市場稅）⇒ <c>total * 20 / (21 * quantity)</c> 還原；
        /// 全程整數運算，因為價格是錢。<c>total</c>／<c>quantity</c> 缺席時才退回含稅單價除以 1.05。
        /// </summary>
        internal static long UntaxedUnitPrice(OverviewListingDto listing)
        {
            var quantity = listing.quantity ?? 0;
            var total = listing.total ?? 0L;
            if (quantity > 0 && total > 0L)
                return total * 20L / (21L * quantity);

            var price = listing.price ?? 0m;
            return price > 0m ? (long)Math.Floor(price / 1.05m) : 0L;
        }

        /// <summary>
        /// 第一次查成功時報告一行，之後安靜。
        ///
        /// <para>
        /// 🔑 這一行存在的理由：在它之前，這個來源<b>只有失敗才寫 log</b>，成功路徑完全靜默
        /// ⇒「log 裡沒有訊息」同時代表「一切正常」與「一次都沒跑過」，兩者分不出來。
        /// </para>
        /// </summary>
        private static void LogFirstSuccess(int asked, int unresolved, int recovered)
        {
            if (Interlocked.Exchange(ref successLogged, 1) != 0)
                return;

            Log.Information(
                $"[Marketbuddy] 歷史賣出價：Universalis 查詢正常運作。第一次成功查了 {asked} 件，" +
                $"其中 {asked - unresolved} 件由 aggregated 端點直接回答、" +
                $"{recovered} 件靠 v3 端點補查補回、{unresolved - recovered} 件確實沒有資料。" +
                "成功之後不再重複回報，只有失敗才會再寫一行。");
        }

        /// <summary>
        /// v3 補查失敗的告知。同一段連續失敗只寫第一行；補查成功一次就會重新武裝。
        /// 🔴 <b>不</b>連帶設整體退避：一件補不到不代表 Universalis 有問題。
        /// </summary>
        private static void NoteOverviewFailure(string reason)
        {
            if (Interlocked.Exchange(ref overviewFailureLogged, 1) != 0)
                return;

            Log.Information(
                $"[Marketbuddy] 歷史賣出價：aggregated 端點答不出來的道具改用 v3 補查時失敗（{reason}）。" +
                "這些道具會顯示成「沒有成交紀錄」，重掛照原本的市場比價進行；" +
                "補查成功一次之後會再回報下一次失敗。");
        }

        /// <summary>
        /// aggregated 端點回來的「最近一次成交」——這是實機上絕大多數道具真正走的那條路。
        /// </summary>
        /// <param name="queriedWorldId">這一次是拿哪個世界的 id 去問的（<c>world</c> 範圍就是它）。</param>
        /// <param name="excludedOnly">
        /// true＝這個品質<b>有</b>成交紀錄，但能用的那幾筆都落在被排除的世界上。
        /// 🔑 與「從來沒賣過」分開回報，否則畫面上兩件事長得一模一樣。
        /// </param>
        private static LastSoldEntry? Pick(AggregateDto? aggregate, bool hq,
            ExclusionSnapshot ex, uint queriedWorldId, out bool excludedOnly)
        {
            excludedOnly = false;
            var purchase = aggregate?.recentPurchase;
            if (purchase == null)
                return null;

            // 🔑 使用者要的是「整個資料中心」的成交，所以先取 dc；dc 沒有時才退回本世界
            // （dc 的範圍涵蓋本世界，所以這個退路只會多給資料，不會少給）。
            // 🔴 但排除清單上的世界一筆都不採用：拉姆（4034）已經停止營運，而一件道具
            //    只要「自它關閉之後在其他世界都沒賣出過」，重掛就會照拉姆留下的舊價定價。
            // ⚠️ dc 被擋掉時退回 world 範圍<b>不是</b>「找別的價來湊」：那仍然是一筆真的
            //    成交紀錄（本世界自己的），只是範圍比較窄；這個退路本來就存在。
            //    兩個範圍都沒有可用的成交時就回 null，呼叫端落回原本的市場比價。
            var dropped = false;
            var entry = UsablePurchase(purchase.dc, ex, 0u, ref dropped)
                        ?? UsablePurchase(purchase.world, ex, queriedWorldId, ref dropped);
            // 🔑 價格用模式比對取出來，不要寫 entry.price.Value：
            //    UsablePurchase 已經保證 price > 0，但那是跨方法的不變式，編譯器看不到
            //    ⇒ 寫 .Value 會留一個 CS8629，而那個警告指的正是「這裡看不出來」。
            if (entry?.price is not { } unitPrice)
            {
                excludedOnly = dropped;
                return null;
            }

            var world = string.Empty;
            if (entry.worldId is { } worldId && worldNames.TryGetValue(worldId, out var name))
                world = name;

            return new LastSoldEntry(unitPrice, ToUtc(entry.timestamp), hq, world);
        }

        /// <summary>
        /// 一筆 <c>recentPurchase</c> 能不能用：有價格，而且成交的世界不在排除清單上。
        ///
        /// <para>
        /// ⚠️ <paramref name="fallbackWorldId"/> ＝「回應沒帶 <c>worldId</c> 時當成哪個世界」。
        /// <c>world</c> 範圍就是這次查詢的世界；<c>dc</c> 範圍<b>沒有</b>合理的替代值 ⇒ 傳 0，
        /// 而 0 一律當成「不知道」＝不排除。
        /// </para>
        /// <para>
        /// 🔴 那是本次過濾唯一一個縫。Universalis 對 <c>dc</c> 範圍照 schema 一定帶
        /// <c>worldId</c>（同一個欄位這裡本來就在讀，用來顯示成交世界），真的缺了的話
        /// <see cref="NoteWorldlessDc"/> 會寫一行讓它<b>看得見</b>，而不是靜默照用。
        /// </para>
        /// </summary>
        private static EntryDto? UsablePurchase(EntryDto? entry, ExclusionSnapshot ex,
            uint fallbackWorldId, ref bool dropped)
        {
            if (entry?.price is not > 0)
                return null;

            var worldId = entry.worldId ?? fallbackWorldId;
            if (worldId == 0)
            {
                NoteWorldlessDc(ex);
                return entry;
            }

            if (!ex.IsExcluded(worldId))
                return entry;

            dropped = true;
            return null;
        }

        private static int worldlessDcLogged;

        /// <summary>
        /// 成交紀錄沒有帶 <c>worldId</c> ⇒ 判斷不了它是不是來自被排除的世界。
        /// 只在排除清單非空時報告（清單空的時候這件事沒有後果），而且整個遊戲期間只報一次。
        /// </summary>
        private static void NoteWorldlessDc(ExclusionSnapshot ex)
        {
            if (ex.IsEmpty || Interlocked.Exchange(ref worldlessDcLogged, 1) != 0)
                return;

            Log.Information(
                "[Marketbuddy] 歷史賣出價：Universalis 回了一筆沒有帶 worldId 的成交紀錄，" +
                "所以確認不了它是不是來自被排除的世界——這一筆照用了。" +
                "如果重掛價看起來像來自已停止營運的世界，這一行就是原因。");
        }

        /// <summary>一個「最低掛售價」資料點；<b>沒有就回 null，絕不回 0</b>。</summary>
        private static MarketPricePoint? PickMin(EntryDto? entry)
        {
            if (entry?.price is not > 0)
                return null;

            var world = string.Empty;
            if (entry.worldId is { } worldId && worldNames.TryGetValue(worldId, out var name))
                world = name;

            return new MarketPricePoint(entry.price.Value, world);
        }

        /// <summary>
        /// Universalis 的時間戳是 Unix <b>毫秒</b>。這裡多一道防呆：小於 1e11 就當成秒
        /// ——兩種都解得出合理的日期，猜錯的代價是畫面上顯示一個離譜的年份，
        /// 而那是看得見的錯誤，不是靜默的。
        /// </summary>
        private static DateTime ToUtc(long? stamp)
        {
            if (stamp is not > 0)
                return DateTime.MinValue;

            try
            {
                return stamp.Value < 100_000_000_000L
                    ? DateTimeOffset.FromUnixTimeSeconds(stamp.Value).UtcDateTime
                    : DateTimeOffset.FromUnixTimeMilliseconds(stamp.Value).UtcDateTime;
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        // =====================================================================
        //  Universalis aggregated 端點的回應（只宣告我們真的會讀的欄位）
        // =====================================================================

        // ReSharper disable all
        private sealed class AggregatedResponse
        {
            public List<ResultDto>? results { get; set; }
            public List<uint>? failedItems { get; set; }
        }

        private sealed class ResultDto
        {
            public uint itemId { get; set; }
            public AggregateDto? nq { get; set; }
            public AggregateDto? hq { get; set; }
        }

        private sealed class AggregateDto
        {
            public ScopeDto? minListing { get; set; }
            public ScopeDto? recentPurchase { get; set; }
        }

        private sealed class ScopeDto
        {
            public EntryDto? world { get; set; }
            public EntryDto? dc { get; set; }
            public EntryDto? region { get; set; }
        }

        private sealed class EntryDto
        {
            public long? price { get; set; }
            public uint? worldId { get; set; }
            public long? timestamp { get; set; }
        }

        // =====================================================================
        //  Universalis v3 market/overview 端點的回應（同樣只宣告我們真的會讀的欄位）
        //  ⚠️ 每一個數值欄位都刻意可空：回應裡缺欄位時「靜默讀成 0」比擲例外更難查。
        // =====================================================================

        internal sealed class OverviewDto
        {
            public uint item { get; set; }
            public List<OverviewListingDto>? listings { get; set; }
            public List<OverviewSaleDto>? sales { get; set; }
        }

        internal sealed class OverviewListingDto
        {
            public uint? world { get; set; }

            /// <summary>⚠️ <b>含稅</b>每單位價，而且是<b>浮點數</b>（實測 260.4040404040404）。</summary>
            public decimal? price { get; set; }

            public int? quantity { get; set; }

            /// <summary>含稅總價（整數）。這才是還原未稅單價的來源。</summary>
            public long? total { get; set; }

            public bool hq { get; set; }
        }

        internal sealed class OverviewSaleDto
        {
            public uint? world { get; set; }
            public bool hq { get; set; }

            /// <summary>成交單價，<b>未稅</b>（實測與 aggregated 的 recentPurchase 逐字相同）。</summary>
            public decimal? price { get; set; }

            /// <summary>Unix <b>毫秒</b>。</summary>
            public long? saleTime { get; set; }
        }

        internal sealed class DataCenterDto
        {
            public string? name { get; set; }
            public string? region { get; set; }
            public List<uint>? worlds { get; set; }
        }
        // ReSharper restore all
    }
}
