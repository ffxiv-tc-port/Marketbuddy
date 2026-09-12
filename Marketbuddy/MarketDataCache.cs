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
    /// 🔴 這裡是**純被動接收**：只寫進字典，不觸發任何遊戲操作、不啟動任何流程。
    /// 收到別人查的資料**不會**讓我們開始改價；改價一律仍由使用者按下的按鈕發動。
    /// 🔴 市場板是**每個世界各自一份**，所以整份快取綁在**世界**上，世界一變就整份清掉；世界 ID 由 Framework tick 讀取後快取起來，封包處理器只讀這個欄位。
    /// 🔑 身分刻意**不含角色 ID**：快取存的是未經任何過濾的原始 listings，「最低價是不是自己的」在讀取時才逐格過濾，所以換角色之後這份資料仍然完全正確。
    /// ⚠️ 零掛單的道具伺服器根本不送 offerings 封包，所以只有我們自己確認過的空結果才會進快取（<see cref="StoreConfirmedEmpty"/>）；被動路徑永遠不寫空結果。
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
        /// ⚠️ <c>ListingCount*</c> 是<b>第一頁</b>的筆數,不是「總共有幾件在賣」——
        /// 後續分頁刻意不覆蓋第一頁(見 <see cref="OnOfferingsReceived"/>)。要當「至少 N 件」讀。
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
        /// 🔴🔴 <b>為什麼要有兩份。</b><see cref="Cache"/> 是裸 <c>Dictionary</c>、只被遊戲主執行緒碰，而 IPC 端點跑在呼叫端外掛的執行緒上。
        /// ⇒ 這裡另外維護一份 <c>ConcurrentDictionary</c>:<b>寫入端仍然只有那條主執行緒</b>
        /// (與 <c>Cache</c> 的每一次異動一對一),讀取端可以是任何執行緒。
        /// 🔴 這裡刻意<b>不用鎖</b>:沒有鎖就不可能發生「鎖內呼叫 ImGui/做 I/O」那類問題,
        /// 而 <c>PublicSnapshot</c> 不可變 ⇒ 讀到的物件內容不會在讀的過程中被換掉。
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
