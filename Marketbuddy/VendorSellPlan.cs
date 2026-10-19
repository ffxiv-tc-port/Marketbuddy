using System;
using System.Collections.Generic;

namespace Marketbuddy
{
    // 本檔只有純資料與純函式：不讀遊戲記憶體、不碰 Dalamud，離線測試直接編這一個檔。

    /// <summary>「同一件道具」的判準：道具 id ＋是否優質（兩者不會疊在同一格）。</summary>
    internal readonly record struct VendorSellKey(uint ItemId, bool Hq);

    /// <summary>這一件是被哪一道自動下架拿下來的。</summary>
    internal enum VendorSellCause
    {
        BelowVendor,
        BelowMinPrice,
    }

    /// <summary>一次遊戲收下的「下架回玩家背包」。</summary>
    internal readonly record struct VendorSellDelist(
        VendorSellKey Key,
        int Quantity,
        VendorSellCause Cause,
        uint VendorUnitPrice,
        bool Unique,
        string Name,
        DateTime AtUtc);

    /// <summary>一格有東西的背包／兵裝庫格子。</summary>
    /// <param name="Detail">魔晶石、精煉度、耐久、製作者、染色、投影的指紋；堆疊類道具恆為同一個值。</param>
    /// <param name="NeedsConfirmation">賣給商店時遊戲會跳確認框（鑲有魔晶石、精煉度滿）。</param>
    internal readonly record struct VendorSellSlot(
        uint Container,
        short Slot,
        VendorSellKey Key,
        int Quantity,
        ulong Detail,
        bool Armory,
        bool NeedsConfirmation);

    /// <summary>某一刻的背包內容：只存有東西的格子，另記哪些容器當時讀得到。</summary>
    internal sealed class VendorSellSnapshot
    {
        private readonly HashSet<uint> loaded;
        private readonly Dictionary<(uint Container, short Slot), VendorSellSlot> slots = new();

        public VendorSellSnapshot(IEnumerable<uint> loadedContainers, IEnumerable<VendorSellSlot> occupied)
        {
            loaded = new HashSet<uint>(loadedContainers);
            foreach (var slot in occupied)
                slots[(slot.Container, slot.Slot)] = slot;
        }

        public VendorSellSnapshot()
            => loaded = new HashSet<uint>();

        /// <summary>整份換成新的內容，結果等同用同樣的參數 new 一份；重複使用同一份就不必每次重配字典。</summary>
        public void Refill(List<uint> loadedContainers, List<VendorSellSlot> occupied)
        {
            loaded.Clear();
            foreach (var container in loadedContainers)
                loaded.Add(container);
            slots.Clear();
            foreach (var slot in occupied)
                slots[(slot.Container, slot.Slot)] = slot;
        }

        public IReadOnlyCollection<VendorSellSlot> Slots => slots.Values;

        public bool IsLoaded(uint container) => loaded.Contains(container);

        public VendorSellSlot? Get(uint container, short slot)
            => slots.TryGetValue((container, slot), out var s) ? s : null;
    }

    /// <summary>
    /// 一輪裡每種道具各自的「下架前」背包（只留那種道具的格子），在它這一輪第一次被記帳的下架送出之前讀。
    /// 🔴 已經有的永不覆蓋：之後再讀，背包裡可能已經有這一輪下架回來的同款，會被當成原有的。
    /// 下架沒被遊戲收下就不留（<see cref="Commit"/> 只收剛暫存、而且是同一種的那一份）。
    /// </summary>
    internal sealed class VendorSellBaselines
    {
        /// <summary>沒有基準的道具用這一份：一個容器都不算讀得到，背包裡出現的同款一律判成需要手動。</summary>
        private static readonly VendorSellSnapshot Missing = new();

        private readonly Dictionary<VendorSellKey, VendorSellSnapshot> byKey = new();
        private VendorSellKey stagedKey;
        private VendorSellSnapshot? staged;

        public int Count => byKey.Count;

        public bool Has(VendorSellKey key) => byKey.ContainsKey(key);

        public VendorSellSnapshot BeforeOf(VendorSellKey key)
            => byKey.TryGetValue(key, out var s) ? s : Missing;

        /// <summary>下架送出之前呼叫：從整份背包取出這種道具的格子暫存起來。已經有基準就什麼都不做。</summary>
        public void Stage(VendorSellKey key, IEnumerable<uint> loadedContainers, IEnumerable<VendorSellSlot> occupied)
        {
            staged = null;
            if (Has(key))
                return;
            var mine = new List<VendorSellSlot>();
            foreach (var slot in occupied)
            {
                if (slot.Key == key)
                    mine.Add(slot);
            }

            stagedKey = key;
            staged = new VendorSellSnapshot(loadedContainers, mine);
        }

        /// <summary>下架被遊戲收下之後呼叫。回 true＝這種道具有基準，這一次下架可以記帳。</summary>
        public bool Commit(VendorSellKey key)
        {
            var pending = staged;
            staged = null;
            if (Has(key))
                return true;
            if (pending == null || stagedKey != key)
                return false;
            byKey[key] = pending;
            return true;
        }
    }

    internal enum VendorSellState
    {
        /// <summary>這一輪下架的量已經全部賣掉了，不再列出。</summary>
        Done,
        Sellable,
        Waiting,
        Manual,
    }

    internal enum VendorSellManualReason
    {
        None,
        MergedIntoExistingStack,
        ExistingStackChanged,
        UnknownContainer,
        QuantityMismatch,
        NotArrived,
        NoVendorPrice,
        NeedsConfirmation,
        InArmory,
    }

    internal sealed class VendorSellVerdict
    {
        public required VendorSellKey Key { get; init; }
        public required string Name { get; init; }
        public required VendorSellState State { get; init; }
        public VendorSellManualReason Reason { get; init; }

        /// <summary>這一輪才出現的格子；只有 <see cref="VendorSellState.Sellable"/> 時才可以拿去賣。</summary>
        public required IReadOnlyList<VendorSellSlot> NewSlots { get; init; }

        public required int Delisted { get; init; }
        public required int Sold { get; init; }
        public required int Arrived { get; init; }

        /// <summary>這一輪開始前就在背包裡的同款數量，與現在那幾格的數量。</summary>
        public required int ExistingBefore { get; init; }
        public required int ExistingNow { get; init; }

        public required uint VendorUnitPrice { get; init; }
        public required IReadOnlyList<VendorSellCause> Causes { get; init; }
        public required DateTime LastDelistUtc { get; init; }

        public int Remaining => Delisted - Sold;

        public long EstimatedGil => (long)Arrived * VendorUnitPrice;
    }

    /// <summary>一份計畫的摘要（診斷記錄用）；「種」＝<see cref="VendorSellKey"/>，<see cref="VendorSellState.Done"/> 不算。</summary>
    internal readonly record struct VendorSellPlanSummary(
        int Sellable,
        int Stacks,
        long Gil,
        int Manual,
        int Waiting,
        VendorSellManualReason TopReason,
        int TopReasonCount);

    internal static class VendorSellPlanner
    {
        private static readonly int ReasonCount = Enum.GetValues<VendorSellManualReason>().Length;

        /// <summary>同數量的原因取列舉值小的那個，同一份計畫永遠得到同一個摘要。</summary>
        public static VendorSellPlanSummary Summarize(IReadOnlyList<VendorSellVerdict> plan)
        {
            int sellable = 0, stacks = 0, manual = 0, waiting = 0;
            var gil = 0L;
            var reasons = new int[ReasonCount];
            foreach (var v in plan)
            {
                switch (v.State)
                {
                    case VendorSellState.Sellable:
                        sellable++;
                        stacks += v.NewSlots.Count;
                        gil += v.EstimatedGil;
                        break;
                    case VendorSellState.Manual:
                        manual++;
                        reasons[(int)v.Reason]++;
                        break;
                    case VendorSellState.Waiting:
                        waiting++;
                        break;
                }
            }

            var top = 0;
            for (var i = 1; i < reasons.Length; i++)
            {
                if (reasons[i] > reasons[top])
                    top = i;
            }

            return new VendorSellPlanSummary(sellable, stacks, gil, manual, waiting,
                (VendorSellManualReason)top, reasons[top]);
        }

        /// <summary>
        /// 逐件判定「這一輪下架回背包的東西」哪些可以整格賣掉。
        /// 🔴 可賣＝同款的舊堆疊一格都沒變、而且這一輪新出現的格子加起來剛好等於還沒賣掉的下架量；
        /// 其餘一律不賣（併進舊堆疊、數量不符、格子變動、還沒到齊）。
        /// </summary>
        /// <param name="sold">這一輪已經由本功能賣掉的量（賣掉之後那一格會消失，要從期望量裡扣掉）。</param>
        /// <param name="settleWindow">最後一次下架之後這麼久之內，數量不足算「還在路上」而不是「對不上」。</param>
        public static List<VendorSellVerdict> Evaluate(
            VendorSellSnapshot before,
            VendorSellSnapshot now,
            IReadOnlyList<VendorSellDelist> delists,
            IReadOnlyDictionary<VendorSellKey, int> sold,
            DateTime nowUtc,
            TimeSpan settleWindow)
            => Evaluate(_ => before, now, delists, sold, nowUtc, settleWindow);

        /// <param name="beforeOf">每種道具自己的「下架前」背包（見 <see cref="VendorSellBaselines"/>）。</param>
        public static List<VendorSellVerdict> Evaluate(
            Func<VendorSellKey, VendorSellSnapshot> beforeOf,
            VendorSellSnapshot now,
            IReadOnlyList<VendorSellDelist> delists,
            IReadOnlyDictionary<VendorSellKey, int> sold,
            DateTime nowUtc,
            TimeSpan settleWindow)
        {
            var order = new List<VendorSellKey>();
            var byKey = new Dictionary<VendorSellKey, List<VendorSellDelist>>();
            foreach (var d in delists)
            {
                if (!byKey.TryGetValue(d.Key, out var list))
                {
                    list = new List<VendorSellDelist>();
                    byKey[d.Key] = list;
                    order.Add(d.Key);
                }

                list.Add(d);
            }

            var result = new List<VendorSellVerdict>(order.Count);
            foreach (var key in order)
            {
                var soldQty = sold.TryGetValue(key, out var s) ? s : 0;
                result.Add(EvaluateKey(key, byKey[key], beforeOf(key), now, soldQty, nowUtc, settleWindow));
            }

            return result;
        }

        private static VendorSellVerdict EvaluateKey(
            VendorSellKey key,
            List<VendorSellDelist> delists,
            VendorSellSnapshot before,
            VendorSellSnapshot now,
            int soldQty,
            DateTime nowUtc,
            TimeSpan settleWindow)
        {
            var delisted = 0;
            var last = DateTime.MinValue;
            var vendorUnit = 0u;
            var unique = false;
            var name = string.Empty;
            var causes = new List<VendorSellCause>();
            foreach (var d in delists)
            {
                delisted += Math.Max(0, d.Quantity);
                if (d.AtUtc > last)
                    last = d.AtUtc;
                vendorUnit = Math.Max(vendorUnit, d.VendorUnitPrice);
                unique |= d.Unique;
                if (name.Length == 0)
                    name = d.Name;
                if (!causes.Contains(d.Cause))
                    causes.Add(d.Cause);
            }

            var existingBefore = 0;
            var existingNow = 0;
            var merged = false;
            var changed = false;
            foreach (var b in before.Slots)
            {
                if (b.Key != key)
                    continue;
                existingBefore += b.Quantity;
                if (now.Get(b.Container, b.Slot) is not { } cur || cur.Key != key)
                {
                    changed = true;
                    continue;
                }

                existingNow += cur.Quantity;
                if (cur.Quantity > b.Quantity)
                    merged = true;
                else if (cur.Quantity < b.Quantity || cur.Detail != b.Detail)
                    changed = true;
            }

            // 開頭時裝著別的東西、後來空出來又被下架品填進去的格子，一樣算新格子：
            // 舊堆疊已經逐格核對過，數量又要剛好對上，多出來的東西會在總量上現形。
            var newSlots = new List<VendorSellSlot>();
            var unknown = false;
            var arrived = 0;
            var totalNow = 0;
            foreach (var n in now.Slots)
            {
                if (n.Key != key)
                    continue;
                totalNow += n.Quantity;
                if (!before.IsLoaded(n.Container))
                {
                    unknown = true;
                    continue;
                }

                if (before.Get(n.Container, n.Slot) is { } prev && prev.Key == key)
                    continue;
                newSlots.Add(n);
                arrived += n.Quantity;
            }

            newSlots.Sort((a, b) => a.Container != b.Container
                ? a.Container.CompareTo(b.Container)
                : a.Slot.CompareTo(b.Slot));

            var remaining = delisted - soldQty;
            var state = VendorSellState.Sellable;
            var reason = VendorSellManualReason.None;
            if (remaining <= 0)
                state = VendorSellState.Done;
            else if (totalNow == 0) // 背包裡一件都沒有：還在路上，或使用者已經自己處理掉了。
                state = nowUtc - last < settleWindow ? VendorSellState.Waiting : VendorSellState.Done;
            else if (merged)
                (state, reason) = (VendorSellState.Manual, VendorSellManualReason.MergedIntoExistingStack);
            else if (changed)
                (state, reason) = (VendorSellState.Manual, VendorSellManualReason.ExistingStackChanged);
            else if (unknown)
                (state, reason) = (VendorSellState.Manual, VendorSellManualReason.UnknownContainer);
            else if (arrived > remaining)
                (state, reason) = (VendorSellState.Manual, VendorSellManualReason.QuantityMismatch);
            else if (arrived < remaining)
            {
                if (nowUtc - last < settleWindow)
                    state = VendorSellState.Waiting;
                else
                    (state, reason) = (VendorSellState.Manual, arrived == 0
                        ? VendorSellManualReason.NotArrived
                        : VendorSellManualReason.QuantityMismatch);
            }
            else if (vendorUnit == 0)
                (state, reason) = (VendorSellState.Manual, VendorSellManualReason.NoVendorPrice);
            else if (unique || newSlots.Exists(x => x.NeedsConfirmation))
                (state, reason) = (VendorSellState.Manual, VendorSellManualReason.NeedsConfirmation);
            else if (newSlots.Exists(x => x.Armory))
                (state, reason) = (VendorSellState.Manual, VendorSellManualReason.InArmory);

            return new VendorSellVerdict
            {
                Key = key,
                Name = name,
                State = state,
                Reason = reason,
                NewSlots = newSlots,
                Delisted = delisted,
                Sold = soldQty,
                Arrived = arrived,
                ExistingBefore = existingBefore,
                ExistingNow = existingNow,
                VendorUnitPrice = vendorUnit,
                Causes = causes,
                LastDelistUtc = last,
            };
        }

        /// <summary>把一個值混進格子指紋（FNV-1a 形狀，只要求「不同內容大概率不同值」）。</summary>
        public static ulong MixDetail(ulong hash, ulong value)
        {
            for (var i = 0; i < 8; i++)
            {
                hash ^= (value >> (i * 8)) & 0xFF;
                hash *= 1099511628211UL;
            }

            return hash;
        }

        public const ulong DetailSeed = 14695981039346656037UL;
    }
}
