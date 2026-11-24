using System.Collections.Generic;

namespace Marketbuddy
{
    /// <summary>
    /// 最近成交價的查詢佇列：先排先送，一次取一批。
    /// 「優先」＝使用者按了重掛／上架、正在等答案的道具：排在背景預取（出售品清單、背包整理）前面，
    /// 每批最多 <see cref="PriorityBatchSize"/> 件；背景的照舊每批最多 <see cref="NormalBatchSize"/> 件。
    /// 🔴 不是執行緒安全的：呼叫端持有 <c>LastSoldPriceSource.Gate</c> 才碰。純邏輯，離線測試在 tests/LastSoldWaitTests。
    /// </summary>
    internal sealed class LastSoldRequestQueue
    {
        /// <summary>aggregated 端點一次最多帶幾件；背景預取沿用。</summary>
        public const int NormalBatchSize = 60;

        /// <summary>
        /// 優先的那幾件一批最多幾件：與整位僱員重掛同大小（實機跑過的範圍）。
        /// 同一批也是近期成交、別的世界掛單兩條請求的大小，一批太大時它們更容易逾時、整批失去共識價。
        /// </summary>
        public const int PriorityBatchSize = 20;

        private readonly HashSet<uint> members = [];
        private readonly List<uint> priority = [];
        private readonly List<uint> normal = [];

        public int Count => members.Count;

        /// <summary>排進佇列尾端。已經在佇列裡回 false（優先的請求會順便把它提到優先那一列，見 <see cref="Promote"/>）。</summary>
        public bool Add(uint itemId, bool urgent)
        {
            if (!members.Add(itemId))
            {
                if (urgent)
                    Promote(itemId);
                return false;
            }

            (urgent ? priority : normal).Add(itemId);
            return true;
        }

        /// <summary>還沒送出的背景項目改排到優先那一列的尾端；不在佇列裡（已送出或沒排過）就不動。</summary>
        public void Promote(uint itemId)
        {
            if (members.Contains(itemId) && normal.Remove(itemId))
                priority.Add(itemId);
        }

        /// <summary>取下一批（照排入順序）；佇列空了回 null。優先的那一列先取完，兩種不混在同一批。</summary>
        public List<uint>? TakeBatch()
        {
            var (source, size) = priority.Count > 0 ? (priority, PriorityBatchSize) : (normal, NormalBatchSize);
            if (source.Count == 0)
                return null;
            var n = source.Count < size ? source.Count : size;
            var batch = source.GetRange(0, n);
            source.RemoveRange(0, n);
            foreach (var itemId in batch)
                members.Remove(itemId);
            return batch;
        }

        public void Clear()
        {
            members.Clear();
            priority.Clear();
            normal.Clear();
        }
    }
}
