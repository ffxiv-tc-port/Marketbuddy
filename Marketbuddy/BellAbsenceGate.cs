using System;

namespace Marketbuddy;

/// <summary>多角色重掛找鈴時，一次掃描物件表的結果。</summary>
internal enum BellScan
{
    /// <summary>這次不能下結論（表名讀不到、本人不在、換區中）。</summary>
    Unknown,

    /// <summary>物件表裡完全沒有傳喚鈴（不論距離、能不能選取）。</summary>
    NoneInTable,

    /// <summary>表裡有鈴，但都搆不到或不能選取。</summary>
    OutOfReach,

    /// <summary>已對鈴送出互動。</summary>
    Interacted,
}

/// <summary>
/// 多角色重掛接手後「這一區根本沒有傳喚鈴」的提早跳過判斷。純邏輯、不碰 Dalamud，給離線測試用。
/// 契約：連續 <see cref="RequiredAbsentScans"/> 次 <see cref="BellScan.NoneInTable"/>，
/// 而且換區後已過 <see cref="SettleAfterZoneChange"/> 才回 true；中間任何一次不是 NoneInTable 就從頭數。
/// 有鈴只是搆不到的不歸這裡管（照舊等滿逾時）。
/// </summary>
internal sealed class BellAbsenceGate
{
    public const int RequiredAbsentScans = 3;

    public static readonly TimeSpan AbsentRescanInterval = TimeSpan.FromSeconds(1);

    /// <summary>剛換區時物件（尤其房屋家具）還在陸續載入，這段期間的「沒有」不算數。</summary>
    public static readonly TimeSpan SettleAfterZoneChange = TimeSpan.FromSeconds(10);

    public int ConsecutiveAbsent { get; private set; }

    public void Reset() => ConsecutiveAbsent = 0;

    /// <param name="sinceZoneChange">距上次換區多久；本外掛沒看過換區時傳 <see cref="TimeSpan.MaxValue"/>。</param>
    /// <returns>true＝現在就該跳過這個角色。</returns>
    public bool Observe(BellScan scan, TimeSpan sinceZoneChange)
    {
        if (scan != BellScan.NoneInTable || sinceZoneChange < SettleAfterZoneChange)
        {
            ConsecutiveAbsent = 0;
            return false;
        }

        ConsecutiveAbsent++;
        return ConsecutiveAbsent >= RequiredAbsentScans;
    }

    /// <summary>上一次是「表裡沒有」就隔 1 秒再確認；其餘照呼叫端原本的節流。</summary>
    public TimeSpan NextScanInterval(TimeSpan normalInterval)
        => ConsecutiveAbsent > 0 ? AbsentRescanInterval : normalInterval;
}
