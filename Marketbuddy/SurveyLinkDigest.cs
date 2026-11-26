using System;
using System.Text;
using System.Threading;

namespace Marketbuddy
{
    /// <summary>
    /// 巡檢連線的定時彙總：逐筆、逐批的事（收到幾筆、送出幾筆、本機查到幾筆、被拒幾筆、合併幾筆）只累計，
    /// 每個區間最多寫一行；那個區間什麼都沒發生就不寫。狀態改變（連線、斷線、配對、遠端指令、斷線後的大批補傳、世界收場）
    /// 不走這裡，照常立刻寫。
    /// <para>契約：不引用 Dalamud（離線測試直接編它）；任何執行緒都可以累計，<see cref="Tick"/> 由同一條執行緒呼叫。</para>
    /// </summary>
    internal sealed class SurveyLinkDigest
    {
        private readonly TimeSpan interval;
        private readonly Action<string> log;
        private DateTime windowStart;
        private long received, receivedNew, sent, local, refused, timeouts, merged, mergedRows;

        internal SurveyLinkDigest(TimeSpan interval, Action<string> log, DateTime nowUtc)
        {
            this.interval = interval;
            this.log = log;
            windowStart = nowUtc;
        }

        internal void NoteReceived(int rows, int fresh)
        {
            Interlocked.Add(ref received, rows);
            Interlocked.Add(ref receivedNew, fresh);
        }

        internal void NoteSent(int rows) => Interlocked.Add(ref sent, rows);

        internal void NoteLocal(string verdict)
        {
            Interlocked.Increment(ref local);
            if (verdict == "refused")
                Interlocked.Increment(ref refused);
            else if (verdict == "timeout")
                Interlocked.Increment(ref timeouts);
        }

        internal void NoteMerged(int observations, int rows)
        {
            Interlocked.Add(ref merged, observations);
            Interlocked.Add(ref mergedRows, rows);
        }

        /// <summary>到了區間尾端就寫一行（有事才寫）並開新區間。<paramref name="force"/>＝不管區間到了沒（卸載時）。</summary>
        /// <returns>這次有沒有寫。</returns>
        internal bool Tick(DateTime nowUtc, int outbox, bool force = false)
        {
            if (!force && nowUtc - windowStart < interval)
                return false;
            var minutes = Math.Max(1, (int)Math.Round((nowUtc - windowStart).TotalMinutes));
            windowStart = nowUtc;
            var r = Interlocked.Exchange(ref received, 0);
            var rn = Interlocked.Exchange(ref receivedNew, 0);
            var s = Interlocked.Exchange(ref sent, 0);
            var l = Interlocked.Exchange(ref local, 0);
            var rf = Interlocked.Exchange(ref refused, 0);
            var to = Interlocked.Exchange(ref timeouts, 0);
            var m = Interlocked.Exchange(ref merged, 0);
            var mr = Interlocked.Exchange(ref mergedRows, 0);
            if (r == 0 && s == 0 && l == 0 && m == 0)
                return false;

            var text = new StringBuilder();
            text.Append("近 ").Append(minutes).Append(" 分：");
            text.Append("本機查價 ").Append(l).Append(" 件（被拒 ").Append(rf).Append("、逾時 ").Append(to).Append("）");
            text.Append("，收到 ").Append(r).Append(" 筆觀測（新 ").Append(rn).Append(" 筆）");
            text.Append("，對方確認收下 ").Append(s).Append(" 筆");
            if (m > 0)
                text.Append("，合併 ").Append(m).Append(" 筆成這台清單上的 ").Append(mr).Append(" 列");
            text.Append("，還排著 ").Append(outbox).Append(" 筆待送。");
            log(text.ToString());
            return true;
        }
    }
}
