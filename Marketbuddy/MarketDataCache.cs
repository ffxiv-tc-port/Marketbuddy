using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Dalamud.Game.Network.Structures;
using Dalamud.Plugin.Services;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    /// <summary>
    /// 全域市場資料快取。
    ///
    /// <para>
    /// <c>IMarketBoard.OfferingsReceived</c> 是**全域事件**：不管那筆查詢是誰送出去的
    /// （我們自己的批次、DailyRoutines 的改價器、PriceInsight 的滑鼠提示、或是玩家自己
    /// 點開市場板），封包都會送到每一個訂閱者手上。舊版的 BatchReprice 只收「自己正在等
    /// 的那一件」，其餘整包丟掉 —— 2026-08-02 的實機 log 裡光是
    /// <c>OFFERINGS dropped: no request pending</c> 就有 1047 筆。
    /// </para>
    ///
    /// <para>
    /// 這個類別把每一筆看得到的掛單資料都存下來，之後要查同一件道具時就不必再問伺服器。
    /// 兩個直接效果：
    /// <list type="number">
    ///   <item>別人剛查過的道具我們直接用（實測 .15 那段有 5.8% 的第一次查詢可以完全省掉）。</item>
    ///   <item>**逾時之後才到的資料不再被丟掉** —— 它會落進快取，重試前的快取檢查就會命中，
    ///         於是那次重試根本不會送出去（實測 63 次重試裡有 18 次，答案在我們重問之前就已經到了）。</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// 🔴 這裡是**純被動接收**：只寫進字典，不觸發任何遊戲操作、不啟動任何流程。
    /// 收到別人查的資料**不會**讓我們開始改價；改價一律仍由使用者按下的按鈕發動。
    /// </para>
    ///
    /// <para>
    /// 正確性邊界（為什麼可以拿別人查的資料當自己的答案）：
    /// <list type="bullet">
    ///   <item><b>HQ／NQ</b>：存的是整頁原始掛單（含每一筆的 IsHq），HQ/NQ 的取捨仍然由
    ///         BatchReprice 依該格自己的品質逐格判斷，快取不做任何預先篩選。</item>
    ///   <item><b>伺服器</b>：市場板是**每個世界各自一份**，所以整份快取綁在**世界**上；
    ///         世界一變（跨界／服務器旅行）就整份清掉。世界 ID 由 Framework tick 讀取後
    ///         快取起來，封包處理器只讀這個欄位，不在事件裡碰任何遊戲狀態。
    ///         <para>
    ///         🔑 2026-08-02：**身分刻意不含角色 ID。** 掛售資料是「世界」的屬性而不是
    ///         「角色」的屬性 —— 同一個世界上任何角色查同一件道具，伺服器回的掛售完全相同。
    ///         而「最低價是不是自己的」這個唯一跟角色有關的判斷，發生在**讀取時**
    ///         （<c>BatchReprice.ownRetainerIds</c> 每批重算後才過濾），快取存的是**未經任何
    ///         過濾的原始 listings**，所以換角色之後這份資料仍然完全正確。
    ///         舊版把角色 ID 放進身分，等於使用者每換一個角色就把整份快取清光 ——
    ///         而「一輪整理所有角色的包包」正是這個外掛最主要的使用情境。
    ///         </para></item>
    ///   <item><b>雇員</b>：掛單裡帶 RetainerId，「最低價是不是自己的」是拿**當下**的雇員清單
    ///         去比對的（BatchReprice.ownRetainerIds 每批重算），所以換雇員不影響快取正確性。</item>
    ///   <item><b>「沒人在賣」</b>：零掛單的道具伺服器根本不送 offerings 封包，
    ///         沒辦法從被動觀察推斷出來，所以**只有我們自己走完 history-grace 流程確認過**的
    ///         空結果才會進快取（<see cref="StoreConfirmedEmpty"/>）。被動路徑永遠不寫空結果。</item>
    /// </list>
    /// </para>
    /// </summary>
    internal static class MarketDataCache
    {
        private const string Diag = "[MBDIAG]";

        /// <summary>同一個 RequestId 的後續分頁在這段時間內視為「同一個答案的續頁」而不是新答案。</summary>
        private const int SamePageWindowSeconds = 15;

        /// <summary>
        /// 硬性保存上限（秒）。設定值最大只能到 3600，所以比這還舊的資料任何設定都用不到，
        /// 直接丟掉。沒有這道清理，長時間掛著遊戲時字典會隨著 PriceInsight 之類的滑鼠提示
        /// 無限成長（每一件被滑過的道具都會留一筆）。
        /// </summary>
        private const int HardRetentionSeconds = 3600;

        /// <summary>清理的頻率（秒）。純粹避免每一幀都去掃整個字典。</summary>
        private const int PruneIntervalSeconds = 60;

        private static DateTime lastPruneAt = DateTime.MinValue;

        private sealed class Entry
        {
            public required DateTime At;
            public required int RequestId;
            public required List<(uint Price, bool IsHq, ulong RetainerId)> Listings;
        }

        private static readonly Dictionary<uint, Entry> Cache = new();

        /// <summary>
        /// 交給**別的外掛**看的唯讀快照。所有欄位在建構後不再改動,所以從任何執行緒讀都安全。
        /// </summary>
        /// <remarks>
        /// 🔴 <b>價格 0 代表「這個品質沒有掛單」</b>,不是「免費」——真實掛單不可能是 0 gil。
        /// 這樣就不必用可空值型別:CallGate 的 <c>InvokeFunc</c> 對 null 走 <c>(TRet)result</c>,
        /// 回傳可空**值**型別時會擲一個看起來與 IPC 完全無關的 NullReferenceException。
        /// <para>
        /// ⚠️ <c>ListingCount*</c> 是<b>第一頁</b>的筆數,不是「總共有幾件在賣」——
        /// 後續分頁刻意不覆蓋第一頁(見 <see cref="OnOfferingsReceived"/>)。要當「至少 N 件」讀。
        /// </para>
        /// </remarks>
        internal sealed class PublicSnapshot
        {
            /// <summary>道具 ID。</summary>
            public uint ItemId { get; init; }

            /// <summary>這份資料屬於哪一個**世界**(整份快取隨世界切換清掉,見類別註解)。</summary>
            public uint WorldId { get; init; }

            /// <summary>看到這筆資料的時間(Unix 毫秒,UTC)。</summary>
            public long ObservedAtUnixMs { get; init; }

            /// <summary>第一頁裡的普通品掛單筆數。</summary>
            public int ListingCountNq { get; init; }

            /// <summary>第一頁裡的高品質掛單筆數。</summary>
            public int ListingCountHq { get; init; }

            /// <summary>普通品最低單價;<b>0 代表沒有普通品掛單</b>。</summary>
            public uint LowestPriceNq { get; init; }

            /// <summary>高品質最低單價;<b>0 代表沒有高品質掛單</b>。</summary>
            public uint LowestPriceHq { get; init; }

            /// <summary>
            /// true 代表**我們自己走完流程確認過「沒人在賣」**(見 <see cref="StoreConfirmedEmpty"/>),
            /// 而不是「還沒查過」。被動路徑永遠不會寫出 true。
            /// </summary>
            public bool ConfirmedEmpty { get; init; }
        }

        /// <summary>
        /// <see cref="Cache"/> 的<b>對外鏡像</b>。這不是快取本身,是另一份只放摘要的表。
        /// </summary>
        /// <remarks>
        /// 🔴🔴 <b>為什麼要有兩份。</b><see cref="Cache"/> 是裸 <c>Dictionary</c>,只被遊戲
        /// 主執行緒碰(<c>OfferingsReceived</c> 是遊戲函式 hook、<c>Framework.Update</c>、
        /// 以及 ImGui 的繪製,三者都在主執行緒)。而 <b>IPC 端點跑在呼叫端外掛的執行緒上</b>,
        /// 讓它去讀 <c>Cache</c> 的失敗形式不是「拿到舊值」而是<b>字典本身壞掉</b>,
        /// 那會連帶弄壞批次改價與查價——而且例外可能被既有的 catch 吞成「這件查不到價」。
        /// <para>
        /// ⇒ 這裡另外維護一份 <c>ConcurrentDictionary</c>:<b>寫入端仍然只有那條主執行緒</b>
        /// (與 <c>Cache</c> 的每一次異動一對一),讀取端可以是任何執行緒。
        /// <b>既有的 <c>Cache</c> 讀寫路徑一個字都沒有改</b>,所以改價/查價的行為完全不變。
        /// </para>
        /// <para>
        /// 🔴 這裡刻意<b>不用鎖</b>:沒有鎖就不可能發生「鎖內呼叫 ImGui/做 I/O」那類問題,
        /// 而 <c>PublicSnapshot</c> 不可變 ⇒ 讀到的物件內容不會在讀的過程中被換掉。
        /// </para>
        /// </remarks>
        private static readonly ConcurrentDictionary<uint, PublicSnapshot> Published = new();

        /// <summary>
        /// IPC 用的唯讀查詢。<b>任何執行緒都可以呼叫</b>——它只碰 <see cref="Published"/>。
        /// 沒有這筆資料時回 <c>null</c>(參考型別,CallGate 對 null 的參考型別回傳是安全的)。
        /// </summary>
        internal static PublicSnapshot? GetPublished(uint itemId)
            => Published.TryGetValue(itemId, out var snapshot) ? snapshot : null;

        /// <summary>把一次觀察到的掛單摘要成快照放進對外鏡像。只在遊戲主執行緒上被呼叫。</summary>
        private static void PublishSnapshot(uint itemId,
            List<(uint Price, bool IsHq, ulong RetainerId)> listings)
        {
            uint lowestNq = 0, lowestHq = 0;
            var countNq = 0;
            var countHq = 0;
            foreach (var (price, isHq, _) in listings)
            {
                if (isHq)
                {
                    countHq++;
                    if (lowestHq == 0 || price < lowestHq)
                        lowestHq = price;
                }
                else
                {
                    countNq++;
                    if (lowestNq == 0 || price < lowestNq)
                        lowestNq = price;
                }
            }

            Published[itemId] = new PublicSnapshot
            {
                ItemId = itemId,
                WorldId = ownerWorldId,
                ObservedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ListingCountNq = countNq,
                ListingCountHq = countHq,
                LowestPriceNq = lowestNq,
                LowestPriceHq = lowestHq,
                ConfirmedEmpty = false,
            };
        }

        // 這份快取屬於哪一個**世界**（刻意不含角色，理由見類別註解）。
        // 只在 Framework tick 裡更新，封包處理器只讀。
        private static uint ownerWorldId;

        private static bool initialized;

        /// <summary>目前快取裡的道具數（不分新鮮度）。</summary>
        public static int Count => Cache.Count;

        public static void Init()
        {
            if (initialized)
                return;
            initialized = true;
            MarketBoard.OfferingsReceived += OnOfferingsReceived;
            Framework.Update += OnFrameworkUpdate;
        }

        public static void Shutdown()
        {
            if (!initialized)
                return;
            initialized = false;
            Framework.Update -= OnFrameworkUpdate;
            MarketBoard.OfferingsReceived -= OnOfferingsReceived;
            Cache.Clear();
            Published.Clear();
            ownerWorldId = 0;
        }

        /// <summary>手動清空（設定畫面的按鈕）。</summary>
        public static void Clear()
        {
            if (Cache.Count == 0)
                return;
            Log.Information($"{Diag} CACHE-CLEAR reason=manual dropped={Cache.Count}");
            Cache.Clear();
            Published.Clear();
        }

        /// <summary>新鮮度仍在 <paramref name="maxAgeSeconds"/> 以內的道具數（設定畫面顯示用）。</summary>
        public static int FreshCount(int maxAgeSeconds)
        {
            if (maxAgeSeconds <= 0)
                return 0;
            var cutoff = DateTime.UtcNow.AddSeconds(-maxAgeSeconds);
            var n = 0;
            foreach (var entry in Cache.Values)
            {
                if (entry.At > cutoff)
                    n++;
            }

            return n;
        }

        /// <summary>
        /// 取用快取。<paramref name="maxAgeSeconds"/> 為 0（或負數）時一律未命中，
        /// 等同於停用快取。回傳的是複本，呼叫端可以自由改動。
        /// </summary>
        public static bool TryGet(uint itemId, int maxAgeSeconds,
            out List<(uint Price, bool IsHq, ulong RetainerId)> listings, out double ageMs)
        {
            listings = [];
            ageMs = -1;
            if (maxAgeSeconds <= 0)
                return false;
            if (!Cache.TryGetValue(itemId, out var entry))
                return false;

            ageMs = (DateTime.UtcNow - entry.At).TotalMilliseconds;
            if (ageMs > maxAgeSeconds * 1000d)
                return false;

            listings = new List<(uint, bool, ulong)>(entry.Listings);
            return true;
        }

        /// <summary>快取裡這件道具的年紀（毫秒）；沒有這筆時回 -1。用於診斷訊息。</summary>
        public static double AgeMsOf(uint itemId)
            => Cache.TryGetValue(itemId, out var entry) ? (DateTime.UtcNow - entry.At).TotalMilliseconds : -1;

        /// <summary>
        /// 記錄一筆**我們自己確認過**的「沒人在賣」。零掛單的道具伺服器不送 offerings 封包，
        /// 只有走完 history + grace 的流程才能確定，所以被動路徑不會寫這種空結果。
        /// </summary>
        public static void StoreConfirmedEmpty(uint itemId)
        {
            if (ownerWorldId == 0)
                return;
            Cache[itemId] = new Entry { At = DateTime.UtcNow, RequestId = int.MinValue, Listings = [] };
            Published[itemId] = new PublicSnapshot
            {
                ItemId = itemId,
                WorldId = ownerWorldId,
                ObservedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ConfirmedEmpty = true,
            };
            // 每一筆查價都會走到這裡，屬於細節而非摘要 -> Debug。
            // 一次查價的 Information 級摘要由 BatchReprice 的 QUERY 那一行負責。
            Log.Debug($"{Diag} CACHE-STORE item={itemId} n=0 source=confirmed-empty total={Cache.Count}");
        }

        private static void OnFrameworkUpdate(IFramework framework)
        {
            // 只在這裡讀遊戲狀態（保證在遊戲主執行緒上），封包處理器只讀 ownerWorldId。
            var worldId = PlayerState.CurrentWorld.RowId;

            // ⚠️ ContentId **只當「真的登入了嗎」的閘門**，不是快取身分的一部分：
            // 換角色不清快取（見類別註解）。但在角色選擇／讀取畫面時 CurrentWorld 可能
            // 還留著上一個值，所以仍然要有一個獨立的登入判定才不會在切換途中誤判。
            var loggedIn = PlayerState.ContentId != 0;

            // 讀不到（讀取畫面／尚未登入）時什麼都不做：維持現狀，也不接受新資料。
            if (worldId == 0 || !loggedIn)
                return;

            if (worldId == ownerWorldId)
            {
                Prune();
                return;
            }

            if (Cache.Count > 0)
            {
                Log.Information(
                    $"{Diag} CACHE-CLEAR reason=world-changed {ownerWorldId}->{worldId} dropped={Cache.Count}");
                Cache.Clear();
            }

            // 對外鏡像無條件跟著清:它與 Cache 一對一,而世界一換舊資料就一律不能用了。
            Published.Clear();
            ownerWorldId = worldId;
        }

        /// <summary>丟掉任何設定值都已經用不到的舊資料，讓字典不會無限成長。</summary>
        private static void Prune()
        {
            var now = DateTime.UtcNow;
            if ((now - lastPruneAt).TotalSeconds < PruneIntervalSeconds)
                return;
            lastPruneAt = now;
            if (Cache.Count == 0)
                return;

            var cutoff = now.AddSeconds(-HardRetentionSeconds);
            List<uint>? stale = null;
            foreach (var (itemId, entry) in Cache)
            {
                if (entry.At <= cutoff)
                    (stale ??= []).Add(itemId);
            }

            if (stale == null)
                return;

            foreach (var itemId in stale)
            {
                Cache.Remove(itemId);
                Published.TryRemove(itemId, out _);
            }
            MarketDiag.Trace($"{Diag} CACHE-PRUNE dropped={stale.Count} remaining={Cache.Count}");
        }

        private static void OnOfferingsReceived(IMarketBoardCurrentOfferings offerings)
        {
            // 不知道自己在哪個世界就不敢存 —— 存了也不知道該不該用。
            if (ownerWorldId == 0)
                return;

            var listings = offerings.ItemListings;
            if (listings.Count == 0)
                return; // 空頁無法歸屬到道具，也無法證明「沒人在賣」，見 StoreConfirmedEmpty

            var itemId = listings[0].ItemId;
            if (itemId == 0)
                return;

            // 同一個答案的後續分頁：不覆蓋第一頁。第一頁就是價格最低的那一頁，
            // BatchReprice 從以前就只用第一頁（後續頁在 RequestId 檢查那裡被丟掉），
            // 這裡刻意維持完全相同的行為，不改變任何既有的定價結果。
            if (Cache.TryGetValue(itemId, out var existing)
                && existing.RequestId == offerings.RequestId
                && (DateTime.UtcNow - existing.At).TotalSeconds < SamePageWindowSeconds)
                return;

            var captured = new List<(uint Price, bool IsHq, ulong RetainerId)>(listings.Count);
            foreach (var listing in listings)
                captured.Add((listing.PricePerUnit, listing.IsHq, listing.RetainerId));

            Cache[itemId] = new Entry
            {
                At = DateTime.UtcNow, RequestId = offerings.RequestId, Listings = captured,
            };
            PublishSnapshot(itemId, captured);

            // 被動處理器：遊戲裡任何一次掛單查詢都會來一次，是 log 的大宗 -> Debug。
            Log.Debug(
                $"{Diag} CACHE-STORE item={itemId} n={captured.Count} reqId={offerings.RequestId} total={Cache.Count}");
        }
    }
}
