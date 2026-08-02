using System;
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
    ///   <item><b>伺服器</b>：市場板是**每個世界各自一份**，所以整份快取綁在一個
    ///         (世界, 角色) 身分上；世界一變（跨界／服務器旅行）或換角色就整份清掉。
    ///         世界 ID 由 Framework tick 讀取後快取起來，封包處理器只讀這個欄位，
    ///         不在事件裡碰任何遊戲狀態。</item>
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

        // 這份快取屬於哪一個 (世界, 角色)。只在 Framework tick 裡更新，封包處理器只讀。
        private static uint ownerWorldId;
        private static ulong ownerContentId;

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
            ownerWorldId = 0;
            ownerContentId = 0;
        }

        /// <summary>手動清空（設定畫面的按鈕）。</summary>
        public static void Clear()
        {
            if (Cache.Count == 0)
                return;
            Log.Information($"{Diag} CACHE-CLEAR reason=manual dropped={Cache.Count}");
            Cache.Clear();
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
            Log.Information($"{Diag} CACHE-STORE item={itemId} n=0 source=confirmed-empty total={Cache.Count}");
        }

        private static void OnFrameworkUpdate(IFramework framework)
        {
            // 只在這裡讀遊戲狀態（保證在遊戲主執行緒上），封包處理器只讀下面兩個欄位。
            var worldId = PlayerState.CurrentWorld.RowId;
            var contentId = PlayerState.ContentId;

            // 讀不到（讀取畫面／尚未登入）時什麼都不做：維持現狀，也不接受新資料。
            if (worldId == 0 || contentId == 0)
                return;

            if (worldId == ownerWorldId && contentId == ownerContentId)
            {
                Prune();
                return;
            }

            if (Cache.Count > 0)
            {
                Log.Information(
                    $"{Diag} CACHE-CLEAR reason=identity world {ownerWorldId}->{worldId} " +
                    $"character {ownerContentId:X}->{contentId:X} dropped={Cache.Count}");
                Cache.Clear();
            }

            ownerWorldId = worldId;
            ownerContentId = contentId;
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
                Cache.Remove(itemId);
            Log.Information($"{Diag} CACHE-PRUNE dropped={stale.Count} remaining={Cache.Count}");
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

            Log.Information(
                $"{Diag} CACHE-STORE item={itemId} n={captured.Count} reqId={offerings.RequestId} total={Cache.Count}");
        }
    }
}
