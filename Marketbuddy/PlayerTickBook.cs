using System;
using System.Collections.Generic;

namespace Marketbuddy
{
    // 本檔只有純資料與純函式：不讀遊戲記憶體、不碰 Dalamud，離線測試直接編這個檔（加 VendorSellPlan.cs）。

    /// <summary>
    /// 批次上架面板（玩家背包）與背包上架巡迴共用的勾選：只在記憶體、只屬於目前的角色，換角就清空。
    /// 預先勾選：每次重讀背包都把合格、還沒勾、這次遊戲期間沒被取消勾選過的道具勾起來（含之後才進背包的）；
    /// 這個角色按過「全部取消」就不再預先勾。🔴 市場紅線：這裡只改勾選，不開始任何事。只在 framework 執行緒用。
    /// </summary>
    internal sealed class PlayerTickBook
    {
        private readonly HashSet<VendorSellKey> ticks = new();

        /// <summary>每個角色這次遊戲期間取消勾選過的道具；預先勾選跳過它們。不存檔。</summary>
        private readonly Dictionary<ulong, HashSet<VendorSellKey>> declined = new();

        /// <summary>這次遊戲期間按過「全部取消」的角色：不再預先勾。不存檔。</summary>
        private readonly HashSet<ulong> optedOut = new();

        public ulong ContentId { get; private set; }

        public int Count => ticks.Count;

        public IEnumerable<VendorSellKey> Keys => ticks;

        public bool Contains(VendorSellKey key) => ticks.Contains(key);

        /// <summary>對上目前的角色；換了角色就清空勾選。回 true＝真的換了。</summary>
        public bool SyncCharacter(ulong contentId)
        {
            if (contentId == ContentId)
                return false;
            ticks.Clear();
            ContentId = contentId;
            return true;
        }

        /// <summary>使用者勾或取消一種道具；取消的記成取消過，自己再勾回就解除。</summary>
        public void Set(VendorSellKey key, bool ticked)
        {
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

        /// <summary>使用者按「全部取消」：清掉的每一種都記成取消過，這個角色這次遊戲期間不再預先勾。</summary>
        public void Clear()
        {
            optedOut.Add(ContentId);
            if (ticks.Count == 0)
                return;
            DeclinedOf(ContentId).UnionWith(ticks);
            ticks.Clear();
        }

        /// <summary>
        /// 預先勾選：<paramref name="contentId"/> 要是目前角色、這個角色沒按過「全部取消」。
        /// 勾 <c>Eligible</c>、還沒勾、這次遊戲期間沒被取消勾選過的；回傳這次新勾了幾種。
        /// </summary>
        public int TryPreTick(bool enabled, ulong contentId, IEnumerable<(VendorSellKey Key, bool Eligible)> candidates)
        {
            if (!enabled || contentId == 0 || contentId != ContentId || optedOut.Contains(contentId))
                return 0;

            declined.TryGetValue(ContentId, out var skip);
            var n = 0;
            foreach (var (key, eligible) in candidates)
            {
                if (eligible && (skip == null || !skip.Contains(key)) && ticks.Add(key))
                    n++;
            }

            return n;
        }

        /// <summary>預先勾選的資格：不在製作清單上、不在「不自動上架」清單、近期沒被判成低於門檻。</summary>
        public static bool PreTickEligible(bool onCraftList, bool excluded, bool recentlyBelowThreshold)
            => !onCraftList && !excluded && !recentlyBelowThreshold;

        /// <summary>這個角色這次遊戲期間按過「全部取消」。</summary>
        public bool OptedOut(ulong contentId) => optedOut.Contains(contentId);

        /// <summary>
        /// 多角色巡迴的背包段准上架這種道具嗎：勾著、符合預先勾選資格、這個角色沒按過「全部取消」。
        /// 比面板上看得到的勾選只少不多（手動多勾的不合格道具與按過全部取消之後手動勾的都不上）。
        /// </summary>
        public static bool LegMayList(bool ticked, bool preTickEligible, bool optedOut)
            => ticked && preTickEligible && !optedOut;

        private HashSet<VendorSellKey> DeclinedOf(ulong contentId)
        {
            if (!declined.TryGetValue(contentId, out var set))
                declined[contentId] = set = new HashSet<VendorSellKey>();
            return set;
        }
    }

    internal enum CraftListState
    {
        /// <summary>讀到了（這個角色之後不再重讀）。</summary>
        Known,

        /// <summary>InventoryTools 沒載入：當成沒有製作清單，下次照樣先看它載入了沒。</summary>
        NotInstalled,

        /// <summary>InventoryTools 載入了但讀不到：不知道哪些是材料，這一輪不准預先勾。</summary>
        NotReady,
    }

    /// <summary>InventoryTools 製作清單：每個角色讀到一次就留著；對方載入了卻讀不到時每次重試。</summary>
    internal sealed class CraftListReader
    {
        private bool read;

        public HashSet<uint> Ids { get; private set; } = new();

        /// <summary>換角時呼叫：下一次重讀。</summary>
        public void Reset()
        {
            read = false;
            Ids = new HashSet<uint>();
        }

        /// <param name="pluginLoaded">InventoryTools 此刻是否已載入（呼叫端即時讀）。</param>
        /// <param name="readIds">讀製作清單；回 ok＝false 表示讀不到。只在 <paramref name="pluginLoaded"/> 時呼叫。</param>
        public CraftListState Refresh(bool pluginLoaded, Func<(bool Ok, HashSet<uint> Ids)> readIds)
        {
            if (read)
                return CraftListState.Known;
            if (!pluginLoaded)
            {
                if (Ids.Count > 0)
                    Ids = new HashSet<uint>();
                return CraftListState.NotInstalled;
            }

            var (ok, ids) = readIds();
            if (!ok)
                return CraftListState.NotReady;
            Ids = ids;
            read = true;
            return CraftListState.Known;
        }
    }
}
