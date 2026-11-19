using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Marketbuddy
{
    /// <summary>
    /// 「哪些僱員算自家」——比價時自家的掛單不算對手、自家最低時不壓自己。
    /// 🔴 只在 framework 執行緒呼叫（讀 <see cref="RetainerManager"/> 與 AutoRetainer IPC）。
    /// <para>目前角色的僱員一律算；<see cref="Configuration.TreatAllCharactersRetainersAsOwn"/> 開著時再加上
    /// 別的角色的僱員，來源是 Marketbuddy 自己看過的出售品基準線（<see cref="RetainerMarketState"/>，
    /// 所有開過出售品視窗的僱員）與 AutoRetainer 的離線資料。兩個都拿不到時退回只認目前角色。</para>
    /// 📌 僱員 id 全服唯一，掛單上的 RetainerId 與 Universalis 的 retainerID 是同一個值，所以不必分世界。
    /// </summary>
    internal static unsafe class OwnRetainers
    {
        /// <summary>別的角色那一份多久重建一次（AutoRetainer 的離線資料經 IPC 會整份 JSON 轉一次，不每幀做）。</summary>
        private const int RefreshSeconds = 300;

        private static HashSet<ulong> others = [];
        private static DateTime othersBuiltUtc = DateTime.MinValue;
        private static int othersFromState;
        private static int othersFromAutoRetainer = -1;
        private static bool everBuilt;

        /// <summary>別的角色那一份目前有幾位（只給設定頁顯示）；AutoRetainer 那一份 -1＝沒拿到。</summary>
        internal static (int Total, int FromSellLists, int FromAutoRetainer) OtherCounts
            => everBuilt ? (others.Count, othersFromState, othersFromAutoRetainer) : (-1, -1, -1);

        /// <summary>把自家僱員填進 <paramref name="target"/>（先清空）。每幀呼叫也可以：重建有節流。</summary>
        internal static void Fill(HashSet<ulong> target, Configuration conf)
        {
            target.Clear();
            AddCurrentCharacter(target);

            if (!conf.TreatAllCharactersRetainersAsOwn)
                return;

            if ((DateTime.UtcNow - othersBuiltUtc).TotalSeconds >= RefreshSeconds)
                Rebuild();
            target.UnionWith(others);
        }

        /// <summary>把目前角色的僱員加進 <paramref name="target"/>（不清空）。</summary>
        internal static void AddCurrentCharacter(HashSet<ulong> target)
        {
            var retainerManager = RetainerManager.Instance();
            if (retainerManager == null)
                return;
            foreach (var retainer in retainerManager->Retainers)
            {
                if (retainer.RetainerId != 0)
                    target.Add(retainer.RetainerId);
            }
        }

        internal static HashSet<ulong> Snapshot(Configuration conf)
        {
            var set = new HashSet<ulong>();
            Fill(set, conf);
            return set;
        }

        /// <summary>下一次 <see cref="Fill"/> 重建別的角色那一份（例：剛換角色、剛載入基準線）。</summary>
        internal static void Invalidate() => othersBuiltUtc = DateTime.MinValue;

        private static void Rebuild()
        {
            othersBuiltUtc = DateTime.UtcNow;
            var next = new HashSet<ulong>();
            RetainerMarketState.CopyRetainerIdsTo(next);
            othersFromState = next.Count;
            othersFromAutoRetainer = AddFromAutoRetainer(next);
            next.Remove(0);
            others = next;
            everBuilt = true;
        }

        /// <returns>從 AutoRetainer 拿到幾位；-1＝AutoRetainer 沒裝或 IPC 沒回應。</returns>
        private static int AddFromAutoRetainer(HashSet<ulong> target)
        {
            var cids = IPCManager.GetAutoRetainerRegisteredCids();
            if (cids == null)
                return -1;

            var count = 0;
            foreach (var cid in cids)
            {
                var data = IPCManager.GetAutoRetainerCharacterData(cid);
                if (data?.RetainerData == null)
                    continue;
                foreach (var retainer in data.RetainerData)
                {
                    if (retainer == null || retainer.RetainerID == 0)
                        continue;
                    target.Add(retainer.RetainerID);
                    count++;
                }
            }

            return count;
        }
    }
}
