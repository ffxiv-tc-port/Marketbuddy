using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Marketbuddy
{
    internal static partial class LastSoldPriceSource
    {
        /// <summary>aggregated 端點一次最多 100 件（Universalis 文件）。</summary>
        private const int UploadTimesBatch = 100;

        /// <summary>整份上傳時間最多抓這麼久；超過就放棄，呼叫端退回本機時間排序。</summary>
        private static readonly TimeSpan UploadTimesBudget = TimeSpan.FromMinutes(10);

        /// <summary>等道具清單建好再抓（清單在執行緒池上建）；整段在執行緒池上跑。</summary>
        internal static Task<(UploadTimes? Times, string Why)> FetchUploadTimesAfterAsync(
            uint anyWorldId, Task<List<uint>> items, Action<int, int>? progress, CancellationToken token)
            => Task.Run(async () =>
            {
                var list = await items.ConfigureAwait(false);
                return await FetchUploadTimesAsync(anyWorldId, list, progress, token).ConfigureAwait(false);
            });

        /// <summary>
        /// 全市場掃描一輪開始時：批次抓這個資料中心各 (世界, 道具) 在 Universalis 的最後上傳時間。
        /// 🔴 只在使用者按了開始的那一輪抓一次、結果只活在那一輪；每批 100 件、兩批之間至少 <see cref="MinRequestGapMs"/>
        /// （與最近成交價查詢共用同一個間隔與退避），任一批失敗或逾時就整份放棄（回 Times＝null），不重試到底。
        /// 執行緒池上呼叫；不碰遊戲狀態。
        /// </summary>
        /// <param name="anyWorldId">這個資料中心的任一世界（用來向 Universalis 問資料中心名稱）。</param>
        internal static async Task<(UploadTimes? Times, string Why)> FetchUploadTimesAsync(
            uint anyWorldId, IReadOnlyList<uint> items, Action<int, int>? progress, CancellationToken token)
        {
            var client = http;
            CancellationToken unload;
            try
            {
                unload = cts?.Token ?? CancellationToken.None;
            }
            catch (ObjectDisposedException)
            {
                return (null, "unloading");
            }

            if (client == null)
                return (null, "http client not ready");

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, unload);
            linked.CancelAfter(UploadTimesBudget);
            var times = new UploadTimes();
            var batches = (items.Count + UploadTimesBatch - 1) / UploadTimesBatch;
            try
            {
                var worlds = await GetDataCenterWorldsAsync(client, anyWorldId, linked.Token).ConfigureAwait(false);
                var dc = dcName;
                if (worlds.Length == 0 || dc.Length == 0)
                    return (null, "Universalis does not know this data centre");

                for (var b = 0; b < batches; b++)
                {
                    var delay = DelayBeforeNextRequest();
                    if (delay > TimeSpan.Zero)
                        await Task.Delay(delay, linked.Token).ConfigureAwait(false);

                    var start = b * UploadTimesBatch;
                    var batch = new List<uint>(UploadTimesBatch);
                    for (var i = start; i < Math.Min(items.Count, start + UploadTimesBatch); i++)
                        batch.Add(items[i]);

                    Interlocked.Exchange(ref lastRequestTicks, DateTime.UtcNow.Ticks);
                    Stats.Query(batch);
                    times.Requests++;
                    var url = "https://universalis.app/api/v2/aggregated/" + Uri.EscapeDataString(dc) + "/" + string.Join(",", batch);
                    using var response = await SendWithRetryAsync(client, url, linked.Token).ConfigureAwait(false);

                    // 400＝這一批一件都不在 Universalis 的可上市清單裡：沒有時間可記，不是失敗。
                    if (response.StatusCode == HttpStatusCode.BadRequest)
                    {
                        Stats.Succeeded();
                        progress?.Invoke(b + 1, batches);
                        continue;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        Stats.Failed();
                        return (null, $"HTTP {(int)response.StatusCode}");
                    }

                    var bytes = await response.Content.ReadAsByteArrayAsync(linked.Token).ConfigureAwait(false);
                    times.AddAggregated(bytes);
                    Stats.Succeeded();
                    progress?.Invoke(b + 1, batches);
                }

                return (times, string.Empty);
            }
            catch (OperationCanceledException)
            {
                Stats.Failed();
                return (null, unload.IsCancellationRequested ? "unloading" : token.IsCancellationRequested ? "cancelled" : "timeout");
            }
            catch (Exception e)
            {
                Stats.Failed();
                return (null, e.GetType().Name);
            }
        }
    }
}
