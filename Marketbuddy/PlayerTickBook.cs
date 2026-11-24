using System.Collections.Generic;

namespace Marketbuddy
{
    // 本檔只有純資料與純函式：不讀遊戲記憶體、不碰 Dalamud，離線測試直接編這個檔（加 VendorSellPlan.cs）。

    /// <summary>
    /// 批次上架面板（玩家背包）與背包上架巡迴共用的勾選：只在記憶體、只屬於目前的角色，換角就清空。
    /// 預先勾選：這個角色換上來後勾選是空的、使用者還沒動過，才把可上架的道具先勾起來。
    /// 🔴 市場紅線：這裡只改勾選，不開始任何事。不是執行緒安全的，只在 framework 執行緒用。
    /// </summary>
    internal sealed class PlayerTickBook
    {
        private readonly HashSet<VendorSellKey> ticks = new();

        /// <summary>每個角色這次遊戲期間取消勾選過的道具；換角回來預先勾選時跳過它們。不存檔。</summary>
        private readonly Dictionary<ulong, HashSet<VendorSellKey>> declined = new();

        /// <summary>這個角色這一段已經預先勾過，或使用者已經自己動過勾選：之後不再預先勾。</summary>
        private bool settled;

        public ulong ContentId { get; private set; }

        public int Count => ticks.Count;

        public IEnumerable<VendorSellKey> Keys => ticks;

        public bool Contains(VendorSellKey key) => ticks.Contains(key);

        /// <summary>對上目前的角色；換了角色就清空勾選並重新允許一次預先勾選。回 true＝真的換了。</summary>
        public bool SyncCharacter(ulong contentId)
        {
            if (contentId == ContentId)
                return false;
            ticks.Clear();
            ContentId = contentId;
            settled = false;
            return true;
        }

        /// <summary>使用者勾或取消一種道具。</summary>
        public void Set(VendorSellKey key, bool ticked)
        {
            settled = true;
            if (ticked)
            {
                ticks.Add(key);
                if (declined.TryGetValue(ContentId, out var set))
                    set.Remove(key);
            }
            else if (ticks.Remove(key))
            {
                DeclinedOf(ContentId).Add(key);
            }
        }

        /// <summary>使用者按「全部取消」：清掉的每一種都記成取消過。</summary>
        public void Clear()
        {
            settled = true;
            if (ticks.Count == 0)
                return;
            DeclinedOf(ContentId).UnionWith(ticks);
            ticks.Clear();
        }

        /// <summary>
        /// 預先勾選：只在 <paramref name="contentId"/> 就是目前角色、這一段還沒預先勾過、使用者也沒動過、勾選是空的時候做。
        /// 勾 <c>Eligible</c> 的、這次遊戲期間沒被取消勾選過的。回傳勾了幾種；一種都沒勾到就留著下次再試（背包還是空的時候）。
        /// </summary>
        public int TryPreTick(bool enabled, ulong contentId, IEnumerable<(VendorSellKey Key, bool Eligible)> candidates)
        {
            if (!enabled || settled || contentId == 0 || contentId != ContentId)
                return 0;
            if (ticks.Count > 0)
            {
                settled = true;
                return 0;
            }

            declined.TryGetValue(ContentId, out var skip);
            var n = 0;
            foreach (var (key, eligible) in candidates)
            {
                if (eligible && (skip == null || !skip.Contains(key)) && ticks.Add(key))
                    n++;
            }

            if (n > 0)
                settled = true;
            return n;
        }

        /// <summary>預先勾選的資格：不在製作清單上、不在「不自動上架」清單、近期沒被判成低於門檻。</summary>
        public static bool PreTickEligible(bool onCraftList, bool excluded, bool recentlyBelowThreshold)
            => !onCraftList && !excluded && !recentlyBelowThreshold;

        private HashSet<VendorSellKey> DeclinedOf(ulong contentId)
        {
            if (!declined.TryGetValue(contentId, out var set))
                declined[contentId] = set = new HashSet<VendorSellKey>();
            return set;
        }
    }
}
