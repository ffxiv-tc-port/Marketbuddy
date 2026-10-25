using System;
using System.Collections.Generic;

namespace Marketbuddy
{
    /// <summary>右鍵的那一格（容器型別以整數存，本檔不引用 ClientStructs）。同一格連按兩次只會有一筆上架。</summary>
    internal readonly record struct ListingSource(int Container, int Slot, int Quantity);

    internal readonly record struct MatchedListing(uint ItemId, ulong RetainerId);

    internal readonly record struct SourceMatch(ulong RetainerId, uint ItemId, ListingSource Source, short Slot, DateTime At);

    /// <summary>
    /// 按下確認之後的等待分兩段：過 <see cref="SlowNoticeMs"/> 只提示「還在確認」並繼續認領，過 <see cref="SentTimeoutMs"/> 才說沒上架。
    /// 沒按確認的那種（延後確認放棄）沒有第一段，維持短逾時。
    /// </summary>
    internal static class QuickListAckTiming
    {
        internal const int SlowNoticeMs = 6000;
        internal const int SentTimeoutMs = 45000;
        internal const int UnsentTimeoutMs = 6000;

        internal enum Stage { Waiting, Slow, TimedOut }

        internal static int TimeoutMs(bool sent) => sent ? SentTimeoutMs : UnsentTimeoutMs;

        internal static Stage Classify(bool sent, double ageMs, bool noticeSent)
        {
            if (ageMs > (sent ? SentTimeoutMs : UnsentTimeoutMs))
                return Stage.TimedOut;
            return sent && !noticeSent && ageMs > SlowNoticeMs ? Stage.Slow : Stage.Waiting;
        }

        /// <summary>認領紀錄至少留到「最晚逾時＋前後各一個重按視窗」，逾時時的重按比對才找得到。</summary>
        internal static int SiblingRetentionMs(int duplicateWindowMs) => SentTimeoutMs + 2 * duplicateWindowMs;
    }

    /// <summary>快速上架「哪一格是剛上架的那一筆」的純判斷；呼叫端當幀把市場容器讀成道具 id 陣列再交進來。</summary>
    internal static class QuickListMatch
    {
        /// <summary>
        /// 第一個「現在是這件、確認前不是這件、沒被認領過」的格；沒有回 -1。
        /// 確認前是別的道具、之後被下架空出來再補進這件的格也算（引擎邊跑邊下架時常見）。
        /// </summary>
        internal static short FindNewListingSlot(ReadOnlySpan<uint> slotItems, uint itemId,
            IReadOnlyDictionary<short, uint> preListingItems, ISet<short> claimed,
            IReadOnlyDictionary<short, MatchedListing> reserved)
        {
            for (short i = 0; i < slotItems.Length; i++)
            {
                if (slotItems[i] != itemId || claimed.Contains(i) || reserved.ContainsKey(i))
                    continue;
                if (preListingItems.TryGetValue(i, out var preItem) && preItem == itemId)
                    continue;
                return i;
            }

            return -1;
        }

        /// <summary>
        /// 要放掉的認領格：換了僱員、不在僱員身上，或那一格已經不是認領時的道具。
        /// <paramref name="slotItems"/> 為 null＝容器沒載入，只依僱員判斷。
        /// 每幀呼叫：參數用具體型別，foreach 走結構列舉器、不裝箱。
        /// </summary>
        internal static List<short>? StaleReservations(Dictionary<short, MatchedListing> reserved,
            ulong activeRetainer, uint[]? slotItems, int slotCount)
        {
            List<short>? drop = null;
            foreach (var (slot, matched) in reserved)
            {
                var gone = activeRetainer == 0 || matched.RetainerId != activeRetainer;
                if (!gone && slotItems != null)
                    gone = slot >= slotCount || slotItems[slot] != matched.ItemId;
                if (gone)
                    (drop ??= []).Add(slot);
            }

            return drop;
        }

        /// <summary>
        /// 同一名僱員、同一件、同一格背包同數量，在這筆建立前 <paramref name="windowMs"/> 毫秒起（含之後）已經認領成功的那一筆。
        /// </summary>
        internal static SourceMatch? FindSiblingMatch(IReadOnlyList<SourceMatch> recent, ulong retainerId, uint itemId,
            ListingSource source, DateTime createdAt, int windowMs)
        {
            var from = createdAt.AddMilliseconds(-windowMs);
            foreach (var m in recent)
            {
                if (m.RetainerId == retainerId && m.ItemId == itemId && m.At >= from &&
                    m.Source.Container == source.Container && m.Source.Slot == source.Slot &&
                    m.Source.Quantity == source.Quantity)
                    return m;
            }

            return null;
        }
    }
}
