using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    /// <summary>
    /// 一件道具（一個品質）在整份僱員銷售紀錄裡的彙總。
    /// 🔴 <b><see cref="RetainerSaleConfidence.Unknown"/> 的列永遠不算進賣出。</b>
    /// 那一級的語意是「我們看到它從清單消失，但錢包增量對不上」——把它算進去等於
    /// 用一個自信的數字蓋掉「我們其實不知道」。它有自己的欄位
    /// （<see cref="UnknownEvents"/>），而且畫面上一定要跟賣出並排看得見。
    /// 🔑 <b>「全部是 0」不等於「沒有資料」。</b>這個結構本身分不出那兩件事——
    /// 分辨的責任在呼叫端：有沒有觀察基礎要問
    /// <see cref="RetainerListingAge.FirstSeen"/>／<see cref="RetainerListingAge.EarliestSeen"/>，
    /// 沒有基礎就必須畫成灰色的 <c>?</c>，<b>不可以畫成 0</b>。
    /// </summary>
    /// <param name="SoldEvents">高信心「賣出」事件數。</param>
    /// <param name="SoldQuantity">那些事件加起來賣掉幾件。</param>
    /// <param name="SoldGil">實收合計；只加總 <c>Received &gt;= 0</c> 的列。</param>
    /// <param name="SoldGilIncomplete">
    /// 有賣出事件算不出金額 ⇒ <see cref="SoldGil"/> 是<b>下界</b>不是總數。
    /// </param>
    /// <param name="DelistedEvents">Marketbuddy 自己下架的事件數（不是賣出）。</param>
    /// <param name="DelistedQuantity">那些事件加起來下架幾件。</param>
    /// <param name="UnknownEvents">只知道消失、對不上金幣的事件數。</param>
    /// <param name="UnknownQuantity">那些事件加起來消失幾件。</param>
    /// <param name="LastSoldUtc">最後一次高信心賣出；<see cref="DateTime.MinValue"/>＝從來沒有。</param>
    /// <param name="LastEventUtc">最後一次任何事件；<see cref="DateTime.MinValue"/>＝從來沒有。</param>
    internal readonly record struct ItemSaleHistory(
        int SoldEvents,
        int SoldQuantity,
        long SoldGil,
        bool SoldGilIncomplete,
        int DelistedEvents,
        int DelistedQuantity,
        int UnknownEvents,
        int UnknownQuantity,
        DateTime LastSoldUtc,
        DateTime LastEventUtc)
    {
        /// <summary>這件道具在紀錄裡一次事件都沒有。⚠️ 這不代表「沒有資料」，見類別說明。</summary>
        public bool IsEmpty => SoldEvents == 0 && DelistedEvents == 0 && UnknownEvents == 0;

        /// <summary>「賣出以外的消失」次數＝我方下架＋原因不明。</summary>
        public int OffBoardEvents => DelistedEvents + UnknownEvents;
    }

    /// <summary>
    /// 把僱員銷售紀錄的<b>事件清單</b>換算成<b>每件道具的彙總</b>的一份不可變快照。
    /// 🔴 建好之後就不再改動，所以可以直接把整個物件的參考交給繪製執行緒讀，
    /// 不需要鎖、也不會讀到蓋到一半的字典。要更新就整份換掉
    /// （<see cref="RetainerSalesHistory"/> 用 <see cref="Volatile"/> 換參考）。
    /// </summary>
    internal sealed class SalesHistorySnapshot
    {
        internal static readonly SalesHistorySnapshot Empty = Build([]);

        private readonly Dictionary<(uint ItemId, bool Hq), ItemSaleHistory> byItemHq;

        private readonly Dictionary<uint, ItemSaleHistory> byItem;

        private SalesHistorySnapshot(
            Dictionary<(uint, bool), ItemSaleHistory> byItemHq,
            Dictionary<uint, ItemSaleHistory> byItem,
            DateTime firstEventUtc,
            DateTime lastEventUtc,
            int rowCount)
        {
            this.byItemHq = byItemHq;
            this.byItem = byItem;
            FirstEventUtc = firstEventUtc;
            LastEventUtc = lastEventUtc;
            RowCount = rowCount;
        }

        /// <summary>紀錄裡最早的一筆事件；<see cref="DateTime.MinValue"/>＝一筆都沒有。</summary>
        internal DateTime FirstEventUtc { get; }

        /// <summary>紀錄裡最晚的一筆事件；<see cref="DateTime.MinValue"/>＝一筆都沒有。</summary>
        internal DateTime LastEventUtc { get; }

        /// <summary>總共彙總了幾列。</summary>
        internal int RowCount { get; }

        /// <summary>(道具, 品質) → 彙總。給「一件一列」的表用。</summary>
        internal IReadOnlyDictionary<(uint ItemId, bool Hq), ItemSaleHistory> ByItemAndQuality => byItemHq;

        /// <summary>某個品質的彙總；沒有紀錄回全 0 的 <see cref="ItemSaleHistory"/>（<c>IsEmpty</c> 為真）。</summary>
        internal ItemSaleHistory Get(uint itemId, bool hq)
            => byItemHq.GetValueOrDefault((itemId, hq));

        /// <summary>不分品質的彙總（優質＋普通合起來）。</summary>
        internal ItemSaleHistory GetAnyQuality(uint itemId)
            => byItem.GetValueOrDefault(itemId);

        /// <summary>
        /// 從事件清單建一份彙總。
        /// 🔴 純計算、無 I/O、無 ImGui：可以從任何執行緒呼叫，而且<b>應該</b>在執行緒池上呼叫。
        /// </summary>
        internal static SalesHistorySnapshot Build(IReadOnlyList<RetainerSaleRow> rows)
        {
            var hqAcc = new Dictionary<(uint, bool), Acc>();
            var itemAcc = new Dictionary<uint, Acc>();
            var first = DateTime.MinValue;
            var last = DateTime.MinValue;

            foreach (var row in rows)
            {
                if (row.ItemId == 0)
                    continue;

                if (first == DateTime.MinValue || row.AtUtc < first)
                    first = row.AtUtc;
                if (row.AtUtc > last)
                    last = row.AtUtc;

                var key = (row.ItemId, row.Hq);
                if (!hqAcc.TryGetValue(key, out var a))
                    hqAcc[key] = a = new Acc();
                if (!itemAcc.TryGetValue(row.ItemId, out var b))
                    itemAcc[row.ItemId] = b = new Acc();

                a.Add(row);
                b.Add(row);
            }

            var byItemHq = new Dictionary<(uint, bool), ItemSaleHistory>(hqAcc.Count);
            foreach (var (key, value) in hqAcc)
                byItemHq[key] = value.Freeze();

            var byItem = new Dictionary<uint, ItemSaleHistory>(itemAcc.Count);
            foreach (var (key, value) in itemAcc)
                byItem[key] = value.Freeze();

            return new SalesHistorySnapshot(byItemHq, byItem, first, last, rows.Count);
        }

        /// <summary>累加用的可變桶子；只活在 <see cref="Build"/> 裡面。</summary>
        private sealed class Acc
        {
            private int soldEvents;
            private int soldQuantity;
            private long soldGil;
            private bool soldGilIncomplete;
            private int delistedEvents;
            private int delistedQuantity;
            private int unknownEvents;
            private int unknownQuantity;
            private DateTime lastSold = DateTime.MinValue;
            private DateTime lastEvent = DateTime.MinValue;

            internal void Add(in RetainerSaleRow row)
            {
                if (row.AtUtc > lastEvent)
                    lastEvent = row.AtUtc;

                // 件數是 CSV 裡的值，使用者改得動；負數會讓總和往回走，所以夾一次。
                var quantity = Math.Max(0, row.Quantity);

                switch (row.Confidence)
                {
                    case RetainerSaleConfidence.Sold:
                        soldEvents++;
                        soldQuantity += quantity;
                        // 🔴 -1 是「不知道」，不是 0：加進去會把總額灌水成一個看起來很準的數字。
                        if (row.Received >= 0)
                            soldGil += row.Received;
                        else
                            soldGilIncomplete = true;
                        if (row.AtUtc > lastSold)
                            lastSold = row.AtUtc;
                        break;

                    case RetainerSaleConfidence.Delisted:
                        delistedEvents++;
                        delistedQuantity += quantity;
                        break;

                    default:
                        // 🔴 Unknown 只進自己的欄位，永遠不進賣出。
                        unknownEvents++;
                        unknownQuantity += quantity;
                        break;
                }
            }

            internal ItemSaleHistory Freeze()
                => new(soldEvents, soldQuantity, soldGil, soldGilIncomplete,
                    delistedEvents, delistedQuantity, unknownEvents, unknownQuantity,
                    lastSold, lastEvent);
        }
    }

    /// <summary>
    /// 「這件東西以前賣掉過嗎」的背景快取。
    /// 🔴 <b>繪製路徑零 I/O。</b><see cref="Pump"/> 只從 framework 執行緒呼叫，
    /// 而且它自己也不讀檔——讀檔與彙總全部在 <see cref="Task.Run(Func{object})"/> 上。
    /// 🔑 更新時機：<see cref="RetainerSalesLog.Revision"/> 一變就重算（節流
    /// <see cref="RebuildThrottle"/>），但<b>不是每次都重讀檔</b>——新事件同時也在
    /// <see cref="RetainerSalesLog.SessionRows"/> 裡，折進來就夠了。
    /// ⚠️ 那份記憶體清單有 1000 列的上限，超過會從最舊的丟；所以每
    /// <see cref="FileRefreshEvery"/> 還是要重讀一次檔把帳補回來，
    /// 否則長工作階段會<b>靜默少算</b>最舊的那些事件。
    /// </summary>
    internal static class RetainerSalesHistory
    {
        /// <summary>兩次重算之間至少隔這麼久（一次巡迴會連續寫進很多列）。</summary>
        private static readonly TimeSpan RebuildThrottle = TimeSpan.FromSeconds(3);

        /// <summary>至少多久重讀一次檔（修正 SessionRows 上限造成的漏算）。</summary>
        private static readonly TimeSpan FileRefreshEvery = TimeSpan.FromMinutes(2);

        private static SalesHistorySnapshot current = SalesHistorySnapshot.Empty;

        /// <summary>
        /// 目前的彙總快照。可以從任何執行緒讀：物件本身不可變，換的只是參考。
        /// ⚠️ <see cref="Ready"/> 為 false 時這裡是空的，那代表「還沒讀完」<b>不是</b>「沒有紀錄」。
        /// </summary>
        internal static SalesHistorySnapshot Current => Volatile.Read(ref current);

        private static int readyFlag;

        /// <summary>至少完成過一次載入。false ⇒ 畫面上要顯示「讀取中」而不是「沒有」。</summary>
        internal static bool Ready => Volatile.Read(ref readyFlag) != 0;

        // 底下這些只在 framework 執行緒上碰（Pump 是唯一的呼叫點）。
        private static Task<JobResult>? job;
        private static List<RetainerSaleRow> fileRows = [];
        private static long builtRevision = -1;
        private static DateTime lastRebuildAt = DateTime.MinValue;
        private static DateTime lastFileReadAt = DateTime.MinValue;
        private static int loadFailureLogged;

        private readonly record struct JobResult(
            SalesHistorySnapshot Snapshot, List<RetainerSaleRow> FileRows, bool ReadFile, long Revision);

        /// <summary>
        /// 收工作、必要時開新工作。🔴 <b>只從 framework 執行緒呼叫</b>，而且它自己不做 I/O。
        /// </summary>
        internal static void Pump()
        {
            var running = job;
            if (running != null)
            {
                if (!running.IsCompleted)
                    return;

                job = null;
                try
                {
                    var result = running.GetAwaiter().GetResult();
                    Volatile.Write(ref current, result.Snapshot);
                    fileRows = result.FileRows;
                    builtRevision = result.Revision;
                    if (result.ReadFile)
                        lastFileReadAt = DateTime.UtcNow;
                }
                catch (Exception e)
                {
                    // 讀不到就維持上一份快照。要使用者回報的診斷寫 Information，而且只寫一次。
                    if (Interlocked.Exchange(ref loadFailureLogged, 1) == 0)
                        Log.Information(e,
                            "[Marketbuddy] 僱員銷售彙總：建立「這件賣掉過嗎」的統計時失敗，" +
                            "畫面上會顯示成沒有資料。本工作階段不再重複回報。");
                }

                Volatile.Write(ref readyFlag, 1);
                return;
            }

            var now = DateTime.UtcNow;
            var revision = RetainerSalesLog.Revision;
            var neverBuilt = builtRevision < 0;

            if (!neverBuilt)
            {
                var revisionChanged = revision != builtRevision;
                var fileStale = now - lastFileReadAt >= FileRefreshEvery;

                if (!revisionChanged)
                    return;
                if (now - lastRebuildAt < RebuildThrottle && !fileStale)
                    return;
            }

            var readFile = neverBuilt || now - lastFileReadAt >= FileRefreshEvery;
            lastRebuildAt = now;

            var known = fileRows;
            job = Task.Run(() => BuildJob(known, readFile, revision));
        }

        /// <summary>🔴 執行緒池上跑：讀檔（必要時）＋折進本工作階段的列＋彙總。</summary>
        private static JobResult BuildJob(List<RetainerSaleRow> knownFileRows, bool readFile, long revision)
        {
            var rows = readFile ? RetainerSalesLog.LoadAll() : knownFileRows;

            // 追加是非同步的：剛記下的列可能還沒落地，補上這個工作階段寫過的。
            // 去重用 Format()（決定性，同一列永遠產生同一行）——與巡檢視窗那邊同一招。
            var all = new List<RetainerSaleRow>(rows);
            var session = RetainerSalesLog.SessionRows();
            if (session.Count > 0)
            {
                var seen = new HashSet<string>(rows.Count);
                foreach (var row in rows)
                    seen.Add(RetainerSalesLog.Format(row));
                foreach (var row in session)
                {
                    if (seen.Add(RetainerSalesLog.Format(row)))
                        all.Add(row);
                }
            }

            return new JobResult(SalesHistorySnapshot.Build(all), rows, readFile, revision);
        }
    }
}
