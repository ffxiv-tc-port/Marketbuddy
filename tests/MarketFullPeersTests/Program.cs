using System;
using System.Collections.Generic;
using System.IO;
using Marketbuddy;

// 情境測試：快速上架撞到「市場已滿」時附在同一則訊息後的其他僱員空位。
// 先用英文鍵（未載入 ini）驗來源選擇，再載入出貨的繁中 ini 驗譯文都有命中（沒命中會留下英文）。
// 執行：dotnet run -c Release --project tests/MarketFullPeersTests/MarketFullPeersTests.csproj
// 回傳碼：0＝全部通過；1＝有情境失敗。

internal static class Program
{
    private static int failures;
    private static int passes;

    private static readonly DateTime Now = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);

    private static int Main()
    {
        Localization.Init(null);
        SnapshotPreferredOverCounter();
        CounterWhenNoSnapshot();
        UnknownWhenNeither();
        NotLoadedIsUnknown();
        NoPeersIsEmpty();
        FreeNeverNegative();
        AgeBuckets();
        TraditionalChineseHits();

        Console.WriteLine($"MARKETFULLPEERS_DONE pass={passes} fail={failures}");
        return failures == 0 ? 0 : 1;
    }

    private static string One(PeerRetainer peer) => MarketFullPeers.Describe([peer], 20, Now);

    private static void SnapshotPreferredOverCounter()
        => Check("有紀錄就用紀錄", One(new PeerRetainer("A", 17, Now.AddMinutes(-12), 5)) ==
                                   " Other retainers' free slots: A: 3 free (recorded 12 minute(s) ago)");

    private static void CounterWhenNoSnapshot()
        => Check("沒紀錄用僱員清單計數", One(new PeerRetainer("B", null, null, 15)) ==
                                       " Other retainers' free slots: B: about 5 free (retainer list count)");

    private static void UnknownWhenNeither()
        => Check("兩者都沒有＝未知", One(new PeerRetainer("C", null, null, null)) == " Other retainers' free slots: C: unknown");

    private static void NotLoadedIsUnknown()
        => Check("僱員資料沒載入＝整段未知", MarketFullPeers.Describe(null, 20, Now) == " Other retainers' free slots: unknown");

    private static void NoPeersIsEmpty()
        => Check("沒有其他僱員＝不附", MarketFullPeers.Describe(new List<PeerRetainer>(), 20, Now) == string.Empty);

    private static void FreeNeverNegative()
        => Check("空位不為負", One(new PeerRetainer("D", 25, Now, null)).Contains("D: 0 free"));

    private static void AgeBuckets()
    {
        Check("一分鐘內＝剛剛", One(new PeerRetainer("E", 0, Now.AddSeconds(-30), null)).Contains("recorded just now"));
        Check("小時", One(new PeerRetainer("E", 0, Now.AddHours(-3), null)).Contains("recorded 3 hour(s) ago"));
        Check("天", One(new PeerRetainer("E", 0, Now.AddDays(-2), null)).Contains("recorded 2 day(s) ago"));
        var two = MarketFullPeers.Describe([new PeerRetainer("F", 20, Now, null), new PeerRetainer("G", null, null, 19)], 20, Now);
        Check("多名僱員依序以 / 分隔", two == " Other retainers' free slots: F: 0 free (recorded just now) / G: about 1 free (retainer list count)");
    }

    private static void TraditionalChineseHits()
    {
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../Marketbuddy"));
        Localization.Init(dir);
        var text = MarketFullPeers.Describe(
            [new PeerRetainer("甲", 17, Now.AddMinutes(-12), 5), new PeerRetainer("乙", null, null, 15), new PeerRetainer("丙", null, null, null)],
            20, Now);
        Check($"繁中譯文命中（{text}）", text == " 其他僱員空位：甲：空 3 格（12 分鐘前的紀錄） / 乙：約空 5 格（僱員清單計數） / 丙：未知");
        Check("繁中：沒載入", MarketFullPeers.Describe(null, 20, Now) == " 其他僱員空位：未知");
    }

    private static void Check(string name, bool ok)
    {
        if (ok)
        {
            passes++;
            return;
        }

        failures++;
        Console.WriteLine($"FAIL {name}");
    }
}
