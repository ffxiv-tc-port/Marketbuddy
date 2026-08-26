using System;
using System.Collections.Generic;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy.Common
{
    internal enum TickTaskResult
    {
        /// <summary>Task is not finished; run it again on the next tick.</summary>
        Continue,

        /// <summary>Task finished; proceed to the next queued task.</summary>
        Done,

        /// <summary>Unrecoverable problem; abort the whole queue.</summary>
        AbortQueue
    }

    /// <summary>
    /// Minimal sequential task queue pumped from Framework.Update (owner calls
    /// <see cref="Update"/> once per tick). Runs one task at a time; a task is a
    /// delegate invoked every tick until it reports Done.
    ///
    /// Soft timeouts, retries and backoff are the responsibility of the tasks
    /// themselves (they know how to re-issue their work); the per-task watchdog
    /// here is a last-resort safety net so a stuck task can never hang the queue
    /// silently. No hooks, no code patches, no busy-waiting.
    /// </summary>
    internal sealed class TickTaskQueue
    {
        private sealed class Entry
        {
            public required string Name;
            public required Func<TickTaskResult> Tick;
            public required TimeSpan Watchdog;
            public DateTime StartedAt;
        }

        private readonly Queue<Entry> pending = new();
        private Entry? current;

        public bool IsRunning => current != null || pending.Count > 0;
        public int PendingCount => pending.Count + (current != null ? 1 : 0);
        public string? CurrentName => current?.Name;

        /// <summary>Fired when the queue aborts (explicit Abort, watchdog timeout or task exception).</summary>
        public event Action<string>? Aborted;

        /// <summary>Fired when the last queued task completes normally.</summary>
        public event Action? Completed;

        public void Enqueue(string name, TimeSpan watchdogTimeout, Func<TickTaskResult> tick)
        {
            pending.Enqueue(new Entry { Name = name, Tick = tick, Watchdog = watchdogTimeout });
        }

        public void Abort(string reason)
        {
            if (!IsRunning)
                return;
            current = null;
            pending.Clear();
            Aborted?.Invoke(reason);
        }

        /// <summary>Call once per Framework.Update tick. Cheap no-op while idle.</summary>
        public void Update()
        {
            if (current == null)
            {
                if (pending.Count == 0)
                    return;
                current = pending.Dequeue();
                current.StartedAt = DateTime.UtcNow;
            }

            if (DateTime.UtcNow - current.StartedAt > current.Watchdog)
            {
                Log.Warning($"TickTaskQueue: task '{current.Name}' exceeded its {current.Watchdog.TotalSeconds:0}s watchdog, aborting queue");
                Abort($"task '{current.Name}' timed out");
                return;
            }

            TickTaskResult result;
            try
            {
                result = current.Tick();
            }
            catch (Exception e)
            {
                Log.Error(e, $"TickTaskQueue: task '{current.Name}' threw an exception");
                Abort($"task '{current.Name}' failed: {e.Message}");
                return;
            }

            switch (result)
            {
                case TickTaskResult.Continue:
                    break;
                case TickTaskResult.Done:
                    current = null;
                    if (pending.Count == 0)
                        Completed?.Invoke();
                    break;
                case TickTaskResult.AbortQueue:
                    Abort($"task '{current.Name}' requested abort");
                    break;
            }
        }
    }
}
