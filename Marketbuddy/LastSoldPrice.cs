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
    /// </summary>
    internal static class LastSoldPriceSource
    {
        /// <summary>一次 HTTP 請求最多帶幾件道具（aggregated 端點吃逗號分隔的清單）。</summary>
        private const int BatchSize = 60;

        /// <summary>兩次 HTTP 請求之間的最小間隔。Universalis 是公開服務，不要打快。</summary>
        private const int MinRequestGapMs = 1200;

        private const int RequestTimeoutSeconds = 15;

        /// <summary>問到答案的道具，這麼久之後才會再問一次。</summary>
        private const int FreshMinutes = 30;

        /// <summary>「Universalis 沒有這件的資料」也要記住一段時間，否則每一幀都會重問。</summary>
        private const int NoDataMinutes = 30;

        /// <summary>問失敗的道具比較快重試，但也不是立刻。</summary>
        private const int FailedMinutes = 2;

        /// <summary>連續失敗之後的退避（毫秒）。</summary>
        private const int FailureBackoffMs = 15000;

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

            public DateTime StampUtc;
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        /// <summary>道具 id → 目前知道的事。發布之後不再修改，換值一律整個換掉一個新實例。</summary>
        private static readonly ConcurrentDictionary<uint, ItemEntry> Cache = new();

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

        /// <summary>這件道具目前是什麼狀態。任何執行緒都可以呼叫。</summary>
        internal static LastSoldState StateOf(uint itemId)
            => Cache.TryGetValue(itemId, out var entry) ? entry.State : LastSoldState.Unknown;

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
            if (!Cache.TryGetValue(itemId, out var item) || item.State != LastSoldState.Ready)
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
        /// 這件道具目前的最低掛售價：本世界一個、整個資料中心一個。
        ///
        /// <para>
        /// 🔴 兩個都是 <c>MarketPricePoint?</c>：<b>「查不到」是 null，不會是 0</b>。
        /// </para>
        /// <para>
        /// ⚠️ 這裡<b>一律照這一格自己的品質</b>取，不受「忽略優質狀態」影響：
        /// 那個選項的用途是找「最近賣出價」，而拿自己的優質品去跟普通品的最低價比是錯的比較。
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

            var now = DateTime.UtcNow;
            var added = false;
            lock (Gate)
            {
                queueWorldId = worldId;
                foreach (var itemId in itemIds)
                {
                    if (itemId == 0)
                        continue;

                    if (Cache.TryGetValue(itemId, out var existing))
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
                    Cache[itemId] = new ItemEntry { State = LastSoldState.Loading, StampUtc = now };
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

            try
            {
                var url = "https://universalis.app/api/v2/aggregated/" + worldId + "/" +
                          string.Join(",", batch);
                using var response = await client.GetAsync(url, token).ConfigureAwait(false);

                // 400＝這一批的道具在 Universalis 的「可上市清單」裡全都不存在。那是「沒有資料」，
                // 不是失敗——重試一百次也一樣。
                if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    Mark(batch, LastSoldState.NoData);
                    return;
                }

                if (!response.IsSuccessStatusCode)
                {
                    NoteFailure($"HTTP {(int)response.StatusCode}");
                    Mark(batch, LastSoldState.Failed);
                    return;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                var json = await JsonSerializer
                    .DeserializeAsync<AggregatedResponse>(stream, JsonOptions, token)
                    .ConfigureAwait(false);

                if (json == null)
                {
                    NoteFailure("empty response");
                    Mark(batch, LastSoldState.Failed);
                    return;
                }

                var now = DateTime.UtcNow;
                var handled = new HashSet<uint>();

                foreach (var result in json.results ?? [])
                {
                    var nq = Pick(result.nq, false);
                    var hq = Pick(result.hq, true);
                    var minWorldNq = PickMin(result.nq?.minListing?.world);
                    var minWorldHq = PickMin(result.hq?.minListing?.world);
                    var minDcNq = PickMin(result.nq?.minListing?.dc);
                    var minDcHq = PickMin(result.hq?.minListing?.dc);

                    // 🔑 「有答案」不等於「有成交紀錄」：只有掛售、從來沒賣出過的道具也算 Ready，
                    // 否則最低價那兩欄會跟著「沒賣過」一起消失。哪一種資料缺席由各自的取值函式回報。
                    var anything = nq != null || hq != null ||
                                   minWorldNq != null || minWorldHq != null ||
                                   minDcNq != null || minDcHq != null;

                    Cache[result.itemId] = new ItemEntry
                    {
                        State = anything ? LastSoldState.Ready : LastSoldState.NoData,
                        Nq = nq,
                        Hq = hq,
                        MinWorldNq = minWorldNq,
                        MinWorldHq = minWorldHq,
                        MinDcNq = minDcNq,
                        MinDcHq = minDcHq,
                        StampUtc = now,
                    };
                    handled.Add(result.itemId);
                }

                foreach (var itemId in json.failedItems ?? [])
                {
                    Cache[itemId] = new ItemEntry { State = LastSoldState.NoData, StampUtc = now };
                    handled.Add(itemId);
                }

                // 回應裡完全沒提到的那幾件同樣是「沒有資料」——不標的話它們會卡在「查詢中」。
                foreach (var itemId in batch)
                {
                    if (!handled.Contains(itemId))
                        Cache[itemId] = new ItemEntry { State = LastSoldState.NoData, StampUtc = now };
                }

                Volatile.Write(ref failureLogged, 0);
            }
            catch (OperationCanceledException)
            {
                // 外掛卸載／換世界；不是錯誤。
                Mark(batch, LastSoldState.Failed);
            }
            catch (Exception e)
            {
                NoteFailure(e.Message);
                Mark(batch, LastSoldState.Failed);
            }
        }

        private static void Mark(IEnumerable<uint> itemIds, LastSoldState state)
        {
            var now = DateTime.UtcNow;
            foreach (var itemId in itemIds)
                Cache[itemId] = new ItemEntry { State = state, StampUtc = now };
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

        private static LastSoldEntry? Pick(AggregateDto? aggregate, bool hq)
        {
            // 🔑 使用者要的是「整個資料中心」的成交，所以先取 dc；dc 沒有時才退回本世界
            // （dc 的範圍涵蓋本世界，所以這個退路只會多給資料，不會少給）。
            var entry = aggregate?.recentPurchase?.dc ?? aggregate?.recentPurchase?.world;
            if (entry?.price is not > 0)
                return null;

            var world = string.Empty;
            if (entry.worldId is { } worldId && worldNames.TryGetValue(worldId, out var name))
                world = name;

            return new LastSoldEntry(entry.price.Value, ToUtc(entry.timestamp), hq, world);
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
        // ReSharper restore all
    }
}
