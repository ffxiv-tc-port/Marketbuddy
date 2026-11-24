using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Marketbuddy
{
    /// <summary>
    /// 這個角色每名可用僱員（鈴清單順序）的已知掛單格數。當幀抄值、不留指標、不發請求；framework 執行緒專用。
    /// 本插件的紀錄只在僱員銷售紀錄開著且已讀回時採用（與快速上架那則「市場已滿」訊息同一條件）：關著時紀錄不會跟著上架更新。
    /// </summary>
    internal static unsafe class RetainerSlotsReader
    {
        /// <summary>僱員資料沒載入回 null（不是空清單）。<paramref name="excludeRetainerId"/> 不為 0 時略過那一名。</summary>
        internal static List<RetainerSlots>? Read(ulong excludeRetainerId = 0)
        {
            var retainerManager = RetainerManager.Instance();
            if (retainerManager == null || !retainerManager->IsReady)
                return null;

            var useSnapshots = SnapshotsUsable;
            var list = new List<RetainerSlots>();
            var count = retainerManager->GetRetainerCount();
            for (var i = 0u; i < count; i++)
            {
                var retainer = retainerManager->GetRetainerBySortedIndex(i);
                if (retainer == null || retainer->RetainerId == 0 || !retainer->Available ||
                    retainer->RetainerId == excludeRetainerId)
                    continue;
                list.Add(Of(retainer->RetainerId, retainer->NameString, retainer->MarketItemCount, useSnapshots));
            }

            return list;
        }

        /// <summary>由已經抄好的僱員值組一筆（巡迴自己列舉僱員時用）。</summary>
        internal static RetainerSlots Of(ulong retainerId, string name, int counter, bool useSnapshots)
        {
            var snapshot = useSnapshots ? RetainerMarketState.Get(retainerId) : null;
            return new RetainerSlots(retainerId, name, snapshot?.Listings.Count, snapshot?.AtUtc, counter);
        }

        internal static bool SnapshotsUsable
            => Configuration.GetOrLoad().RetainerSalesLogEnabled && RetainerMarketState.Loaded;
    }
}
