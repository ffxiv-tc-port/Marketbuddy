using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    /// <summary>
    /// 不自動上架的道具清單（<see cref="Configuration.BatchListExcludedItems"/>）的唯一寫入口。
    /// 🔴 只在 framework／繪製執行緒呼叫（兩者同一條）。只讓自動上架少做，不新增任何觸發。
    /// 判斷一律走 <see cref="BatchListRules.IsExcluded"/>；<see cref="Revision"/> 給畫面判斷快取要不要重組。
    /// </summary>
    internal static class BatchListExclusions
    {
        /// <summary>清單每變一次加一。</summary>
        internal static int Revision { get; private set; }

        internal static bool IsExcluded(uint itemId)
            => BatchListRules.IsExcluded(Configuration.GetOrLoad().BatchListExcludedItems, itemId);

        internal static void Set(uint itemId, bool exclude, string name)
        {
            var conf = Configuration.GetOrLoad();
            if (itemId == 0 || !BatchListRules.SetExcluded(conf.BatchListExcludedItems, itemId, exclude))
                return;
            Revision++;
            conf.Save();
            Log.Information($"[BatchList] 不自動上架清單：{(exclude ? "加入" : "移出")} {name}(#{itemId})，現有 {conf.BatchListExcludedItems.Count} 件");
        }

        internal static void Clear()
        {
            var conf = Configuration.GetOrLoad();
            var count = conf.BatchListExcludedItems.Count;
            if (count == 0)
                return;
            conf.BatchListExcludedItems.Clear();
            Revision++;
            conf.Save();
            Log.Information($"[BatchList] 不自動上架清單：全部移出 {count} 件");
        }
    }
}
