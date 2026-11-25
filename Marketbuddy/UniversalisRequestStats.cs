using System;
using System.Collections.Generic;
using System.Threading;

namespace Marketbuddy
{
    /// <summary>對 Universalis 的請求量計數，每小時吐一行彙總（整小時沒有任何動靜就不吐）。</summary>
    /// <remarks>
    /// 契約：任何執行緒都可以呼叫（查詢迴圈在執行緒池、彙總在 framework 執行緒），全部進同一把鎖。
    /// 「查價」＝查詢迴圈送出的一批；成功／失敗也按批算，取消（卸載）兩邊都不算。
    /// 只用 System，離線測試直接編這一個檔。
    /// </remarks>
    internal sealed class UniversalisRequestStats
    {
        public const long IntervalMs = 3_600_000;

        private readonly string logPrefix;
        private readonly object gate = new();
        private readonly HashSet<uint> items = [];
        private long windowStartMs;
        private int queries, requests, retries, successes, failures;

        public UniversalisRequestStats(string logPrefix, long nowMs)
        {
            this.logPrefix = logPrefix;
            windowStartMs = nowMs;
        }

        /// <summary>重新開始計時並清空（重新載入時用）。</summary>
        public void Reset(long nowMs)
        {
            lock (gate)
            {
                Volatile.Write(ref windowStartMs, nowMs);
                items.Clear();
                queries = requests = retries = successes = failures = 0;
            }
        }

        public void Query(IEnumerable<uint> itemIds)
        {
            lock (gate)
            {
                queries++;
                foreach (var itemId in itemIds)
                    items.Add(itemId);
            }
        }

        /// <param name="attempt">0＝第一次送，之後是重試。</param>
        public void Attempt(int attempt)
        {
            lock (gate)
            {
                requests++;
                if (attempt > 0)
                    retries++;
            }
        }

        public void Succeeded()
        {
            lock (gate)
                successes++;
        }

        public void Failed()
        {
            lock (gate)
                failures++;
        }

        /// <summary>滿一小時（或 <paramref name="force"/>）就回傳彙總行並歸零；否則回 null。</summary>
        public string? TryFlush(long nowMs, bool force = false)
        {
            // 每幀都會呼叫：沒到時間就不進鎖。
            if (!force && nowMs - Volatile.Read(ref windowStartMs) < IntervalMs)
                return null;

            lock (gate)
            {
                var elapsed = nowMs - windowStartMs;
                if (!force && elapsed < IntervalMs)
                    return null;

                Volatile.Write(ref windowStartMs, nowMs);
                var q = queries;
                var itemCount = items.Count;
                var req = requests;
                var retry = retries;
                var ok = successes;
                var fail = failures;
                items.Clear();
                queries = requests = retries = successes = failures = 0;
                if (q + req + ok + fail == 0)
                    return null;

                var span = elapsed >= IntervalMs ? "本小時" : $"近 {Math.Max(1, elapsed / 60_000)} 分";
                return $"{logPrefix} Universalis {span}：查價 {q} 次（道具 {itemCount} 件）、HTTP 請求 {req} 次（其中重試 {retry} 次）、成功 {ok}、失敗 {fail}";
            }
        }
    }
}
