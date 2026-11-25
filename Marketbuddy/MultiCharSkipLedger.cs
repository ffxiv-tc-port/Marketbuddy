using System;
using System.Collections.Generic;

namespace Marketbuddy;

/// <summary>
/// 多角色重掛一輪裡被跳過的角色與原因。純邏輯、不碰 Dalamud，給離線測試用。
/// 契約：只放記憶體（重載插件就清空）；<see cref="BeginRound"/> 只在武裝成功時呼叫，
/// 武裝失敗不清掉上一輪的清單；同一角色在一輪內只留最後一個原因；
/// <see cref="TryTakeSummary"/> 每輪最多回 true 一次。只在框架執行緒上用。
/// </summary>
internal sealed class MultiCharSkipLedger
{
    internal readonly record struct Entry(ulong Cid, string Name, string Reason);

    private readonly List<Entry> entries = [];
    private bool summaryTaken;

    public IReadOnlyList<Entry> Entries => entries;

    public int Count => entries.Count;

    /// <summary>每次內容變動加一；畫面用它判斷快取的文字要不要重組。</summary>
    public int Version { get; private set; }

    public void BeginRound()
    {
        entries.Clear();
        summaryTaken = false;
        Version++;
    }

    public void Record(ulong cid, string name, string reason)
    {
        var entry = new Entry(cid, name, reason);
        var index = entries.FindIndex(e => e.Cid == cid);
        if (index >= 0)
            entries[index] = entry;
        else
            entries.Add(entry);
        Version++;
    }

    /// <summary>「只重跑這幾角」的目標；空清單回 null。</summary>
    public HashSet<ulong>? RerunTargets()
    {
        if (entries.Count == 0)
            return null;
        var set = new HashSet<ulong>();
        foreach (var e in entries)
            set.Add(e.Cid);
        return set;
    }

    /// <summary>這一輪結束時拿一次清單；清單空或已經拿過就回 false。</summary>
    public bool TryTakeSummary(out IReadOnlyList<Entry> summary)
    {
        summary = entries;
        if (summaryTaken || entries.Count == 0)
            return false;
        summaryTaken = true;
        return true;
    }

    /// <summary>「甲（原因）、乙（原因）」；<paramref name="format"/> 收（名字, 原因）。</summary>
    public string Join(string separator, Func<string, string, string> format)
    {
        var parts = new string[entries.Count];
        for (var i = 0; i < entries.Count; i++)
            parts[i] = format(entries[i].Name, entries[i].Reason);
        return string.Join(separator, parts);
    }
}
