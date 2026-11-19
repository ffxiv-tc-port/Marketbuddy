using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    /// <summary>
    /// 「最近成交價是自家僱員掛過的價」的查詢端（<see cref="Configuration.RelistSkipOwnSalePrice"/>）。
    /// 資料來自既有的僱員掛售基準線（<see cref="RetainerMarketState"/>）與僱員銷售紀錄（<see cref="RetainerSalesLog"/>），
    /// 本身不寫任何檔案。
    /// </summary>
    /// <remarks>
    /// 執行緒：<see cref="Contains"/>／<see cref="Refresh"/> 任何執行緒可呼叫，只讀兩個版號與一個不可變索引；
    /// 重建在執行緒池上（讀銷售紀錄檔），同一時間最多一個。索引還沒建好時一律回 false（＝照舊採用成交價）。
    /// </remarks>
    internal static class OwnPriceEcho
    {
        private static OwnPriceIndex index = OwnPriceIndex.Empty;

        private static volatile bool ready;

        private static int building;

        private static long builtShelfRevision = -1;
        private static long builtSalesRevision = -1;
        private static volatile bool builtShelfLoaded;
        private static long builtAtTicks;

        private static int failureLogged;

        // 只在重建工作裡碰（building 保證同時只有一個）。
        private static List<(uint ItemId, bool Hq, long UnitPrice, DateTime AtUtc)> history = [];
        private static long historyRevision = -1;
        private static DateTime historyAtUtc = DateTime.MinValue;

        /// <summary>第一次建好索引了沒。</summary>
        internal static bool Ready => ready;

        /// <summary>目前記得幾個自家價（道具＋品質＋單價）。</summary>
        internal static int Count => Volatile.Read(ref index).Count;

        /// <summary>這個成交單價是自家僱員掛過的嗎；索引還沒建好時回 false。</summary>
        internal static bool Contains(uint itemId, bool hq, long unitPrice)
        {
            Refresh();
            return Volatile.Read(ref index).Contains(itemId, hq, unitPrice);
        }

        /// <summary>來源有變動（或超過一小時、讓銷售紀錄滑出 7 天窗）就排一次重建；很便宜，可以每幀呼叫。</summary>
        internal static void Refresh()
        {
            var shelfRevision = RetainerMarketState.Revision;
            var shelfLoaded = RetainerMarketState.Loaded;
            var salesRevision = RetainerSalesLog.Revision;
            var expired = DateTime.UtcNow.Ticks - Interlocked.Read(ref builtAtTicks) > TimeSpan.TicksPerHour;
            if (!expired
                && shelfRevision == Interlocked.Read(ref builtShelfRevision)
                && salesRevision == Interlocked.Read(ref builtSalesRevision)
                && shelfLoaded == builtShelfLoaded)
                return;

            if (Interlocked.CompareExchange(ref building, 1, 0) != 0)
                return;
            Task.Run(() => Rebuild(shelfRevision, shelfLoaded, salesRevision));
        }

        private static void Rebuild(long shelfRevision, bool shelfLoaded, long salesRevision)
        {
            try
            {
                var now = DateTime.UtcNow;
                if (salesRevision != historyRevision || now - historyAtUtc > TimeSpan.FromHours(1))
                {
                    var rows = RetainerSalesLog.LoadAll();
                    rows.AddRange(RetainerSalesLog.SessionRowsMissingFrom(rows));
                    var kept = new List<(uint, bool, long, DateTime)>();
                    var cutoff = now.AddDays(-OwnPriceIndex.WindowDays);
                    foreach (var row in rows)
                    {
                        if (row.UnitPrice > 0 && row.AtUtc >= cutoff)
                            kept.Add((row.ItemId, row.Hq, row.UnitPrice, row.AtUtc));
                    }

                    history = kept;
                    historyRevision = salesRevision;
                    historyAtUtc = now;
                }

                var shelf = new List<(uint, bool, long)>();
                RetainerMarketState.CopyListingsTo(shelf);
                Volatile.Write(ref index, OwnPriceIndex.Build(shelf, history, now));
                ready = true;
            }
            catch (Exception e)
            {
                // 失敗時留著舊索引；下面照樣記下版號，等來源再變動或一小時後才重試，不會每幀重排。
                if (Interlocked.Exchange(ref failureLogged, 1) == 0)
                    Log.Information(e, "[Marketbuddy] 自家掛價索引建立失敗；「成交價是自家掛的價時不採用」暫時不作用。本工作階段不再重複回報。");
            }
            finally
            {
                Interlocked.Exchange(ref builtShelfRevision, shelfRevision);
                Interlocked.Exchange(ref builtSalesRevision, salesRevision);
                builtShelfLoaded = shelfLoaded;
                Interlocked.Exchange(ref builtAtTicks, DateTime.UtcNow.Ticks);
                Volatile.Write(ref building, 0);
            }
        }
    }
}
