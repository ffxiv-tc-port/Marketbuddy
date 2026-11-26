using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Marketbuddy;

// 情境測試：巡檢區網連線，全部走 loopback 真 socket。
// 執行：dotnet run -c Release --project tests/SurveyLinkTests/SurveyLinkTests.csproj
// 回傳碼：0＝全部通過；1＝有情境失敗。

internal static class Program
{
    private static int failures;
    private static int passes;
    private static string root = string.Empty;

    private static readonly DateTime T0 = new(2026, 11, 26, 8, 0, 0, DateTimeKind.Utc);

    private static int Main()
    {
        SurveyLinkTiming.HandshakeTimeout = TimeSpan.FromSeconds(1);
        SurveyLinkTiming.RetryMin = TimeSpan.FromMilliseconds(200);
        SurveyLinkTiming.RetryMax = TimeSpan.FromMilliseconds(400);
        SurveyLinkTiming.StatusMinInterval = TimeSpan.FromMilliseconds(50);
        SurveyLinkTiming.PairingWait = TimeSpan.FromSeconds(5);
        SurveyLinkTiming.NotReadyRetry = TimeSpan.FromMilliseconds(100);

        root = Path.Combine(Path.GetTempPath(), "mb-surveylink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Marketbuddy.Common.Dalamud.PluginInterface.ConfigDirectory = new DirectoryInfo(root);
        try
        {
            Run(nameof(PairThenConnect), PairThenConnect);
            Run(nameof(WrongKeyIsRefusedBothWays), WrongKeyIsRefusedBothWays);
            Run(nameof(ReplayedFrameIsRejected), ReplayedFrameIsRejected);
            Run(nameof(OversizeAndHalfFramesAreDropped), OversizeAndHalfFramesAreDropped);
            Run(nameof(RevokeDisconnectsAndRefuses), RevokeDisconnectsAndRefuses);
            Run(nameof(RemoteStartRefusalsTravelBack), RemoteStartRefusalsTravelBack);
            Run(nameof(RemoteStartGateOrder), RemoteStartGateOrder);
            Run(nameof(ListArrivesInChunks), ListArrivesInChunks);
            Run(nameof(ObservationsSyncIdempotentAndEachSideComputesOwn), ObservationsSyncIdempotentAndEachSideComputesOwn);
            Run(nameof(HostDropsMidRoundClientCarriesOnAndBackfills), HostDropsMidRoundClientCarriesOnAndBackfills);
            Run(nameof(ClientRestartKeepsQueuedObservations), ClientRestartKeepsQueuedObservations);
            Run(nameof(MergedWorldIsNotOtherCharacter), MergedWorldIsNotOtherCharacter);
            Run(nameof(BadObservationsAreRejected), BadObservationsAreRejected);
            Run(nameof(DisposeLeavesNothingListening), DisposeLeavesNothingListening);
            Run(nameof(DigestWritesOneLinePerWindow), DigestWritesOneLinePerWindow);
            Run(nameof(ObservationLogCompactsKeepsLatestPendingAndCursor), ObservationLogCompactsKeepsLatestPendingAndCursor);
            Run(nameof(ObservationLogCompactsOnThreshold), ObservationLogCompactsOnThreshold);
            Run(nameof(OldObservationFileStillLoads), OldObservationFileStillLoads);
            Run(nameof(SalesTravelOptionallyAndOldShapesStillParse), SalesTravelOptionallyAndOldShapesStillParse);
            Run(nameof(OldStatusWithoutNewFieldsReadsAsUnknown), OldStatusWithoutNewFieldsReadsAsUnknown);
            Run(nameof(RoundProgressSurvivesRestart), RoundProgressSurvivesRestart);
            Run(nameof(FullMarketOrderingAndSkips), FullMarketOrderingAndSkips);
            Run(nameof(FullMarketFallsBackToLocalTimes), FullMarketFallsBackToLocalTimes);
            Run(nameof(UniversalisAggregatedParse), UniversalisAggregatedParse);
            Run(nameof(WorldChoiceIsBatchedPerWorld), WorldChoiceIsBatchedPerWorld);
            Run(nameof(FullStartNeedsCapabilityAndUnknownKindNeverRuns), FullStartNeedsCapabilityAndUnknownKindNeverRuns);
            Run(nameof(HostDropsDuringFullScanBackfillKeepsSales), HostDropsDuringFullScanBackfillKeepsSales);
            Run(nameof(TravelWatchSpotsAStalledTeleport), TravelWatchSpotsAStalledTeleport);
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
                // 暫存目錄清不掉不影響結論。
            }
        }

        Console.WriteLine($"passed {passes}, failed {failures}");
        return failures == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
        }
        catch (Exception e)
        {
            Check(name + " threw", false, e.ToString());
        }
    }

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok)
        {
            passes++;
            return;
        }

        failures++;
        Console.WriteLine($"FAIL {name} {detail}");
    }

    private static bool WaitFor(Func<bool> condition, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (condition())
                return true;
            Thread.Sleep(20);
        }

        return condition();
    }

    private static string NewDir()
    {
        var dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static MarketObservation Obs(int i, uint world = 4028, long lowNq = 1000, ulong nqRetainer = 77, string verdict = "ok")
        => new(T0.AddSeconds(i), world, "伊弗利特", (uint)(5000 + i), lowNq, nqRetainer, -1, 0, 3, "live", 2000, verdict);

    /// <summary>兩個角色共用的假端點：觀測走真的 <see cref="SurveyObservationLog"/>，指令與清單交給可替換的處理器。</summary>
    private sealed class FakeEndpoint(string dir) : ISurveyLinkHostEndpoint, ISurveyLinkClientEndpoint
    {
        public readonly SurveyObservationLog Log = Load(dir);
        public readonly List<string> Lines = [];
        public readonly List<SharedList> Lists = [];
        public readonly List<MarketObservation> Accepted = [];
        public readonly List<SurveyWorldRow> Worlds = [];
        public readonly List<SurveyWorldRow> OutWorlds = [];
        public (string KeyId, byte[] Key)? Key;
        public Func<RemoteCommand, string?> OnCommand = _ => null;
        public ClientStatus Status = ClientStatus.Empty;
        public int Duplicates;

        private static SurveyObservationLog Load(string dir)
        {
            var log = new SurveyObservationLog(dir, _ => { });
            log.BeginLoad().Wait();
            return log;
        }

        void ISurveyLinkEndpoint.Log(string message)
        {
            lock (Lines)
                Lines.Add(message);
        }

        public bool Logged(string fragment)
        {
            lock (Lines)
                return Lines.Exists(l => l.Contains(fragment, StringComparison.Ordinal));
        }

        public Func<uint, bool>? KnownItem => id => id < 900_000;
        public Func<uint, bool>? KnownWorld => id => id is >= 4028 and <= 4035;
        public (string KeyId, byte[] Key)? PairedKey() => Key;
        public void SavePairedKey(string keyId, byte[] key) => Key = (keyId, (byte[])key.Clone());
        public void ClearPairedKey() => Key = null;

        public OutgoingBatch? TakeOutgoing(int max)
        {
            var peek = Log.PeekOutbox(max);
            List<SurveyWorldRow> worlds;
            lock (OutWorlds)
                worlds = new List<SurveyWorldRow>(OutWorlds);
            if (peek.Count == 0 && worlds.Count == 0)
                return null;
            var rows = new List<MarketObservation>();
            foreach (var (_, o) in peek)
                rows.Add(o);
            return new OutgoingBatch(peek.Count > 0 ? peek[^1].Seq : 0, rows, worlds);
        }

        public void Acked(OutgoingBatch batch)
        {
            if (batch.Rows.Count > 0)
                Log.Ack(batch.UpToSeq);
            lock (OutWorlds)
                OutWorlds.RemoveAll(w => batch.Worlds.Contains(w));
        }

        public int? Receive(IReadOnlyList<MarketObservation> rows, IReadOnlyList<SurveyWorldRow> worlds)
        {
            var n = 0;
            lock (Accepted)
            {
                foreach (var row in rows)
                {
                    if (Log.AcceptRemote(row) == SurveyObservationLog.Intake.Accepted)
                    {
                        Accepted.Add(row);
                        n++;
                    }
                    else
                    {
                        Duplicates++;
                    }
                }

                Worlds.AddRange(worlds);
            }

            return n;
        }

        public int AcceptedCount
        {
            get
            {
                lock (Accepted)
                    return Accepted.Count;
            }
        }

        public ClientStatus CurrentStatus() => Status;

        public Task<string?> HandleList(SharedList list)
        {
            lock (Lists)
                Lists.Add(list);
            return Task.FromResult<string?>(null);
        }

        public Task<string?> HandleCommand(RemoteCommand command) => Task.FromResult(OnCommand(command));
    }

    private static (SurveyLinkHost Host, FakeEndpoint HostEp, SurveyLinkClient Client, FakeEndpoint ClientEp) Paired()
    {
        var hostEp = new FakeEndpoint(NewDir());
        var clientEp = new FakeEndpoint(NewDir());
        var host = new SurveyLinkHost(hostEp);
        host.Start(0);
        var client = new SurveyLinkClient(clientEp, new IPEndPoint(IPAddress.Loopback, host.Port));
        client.Start();
        client.RequestPairing();
        WaitFor(() => host.PendingPairing != null);
        host.ResolvePairing(true);
        WaitFor(() => client.IsConnected && host.IsConnected);
        return (host, hostEp, client, clientEp);
    }

    // =====================================================================

    private static void PairThenConnect()
    {
        var hostEp = new FakeEndpoint(NewDir());
        var clientEp = new FakeEndpoint(NewDir());
        using var host = new SurveyLinkHost(hostEp);
        host.Start(0);
        using var client = new SurveyLinkClient(clientEp, new IPEndPoint(IPAddress.Loopback, host.Port));
        client.Start();
        Check("unpaired client waits", WaitFor(() => client.State == ClientLinkState.NeedsPairing));
        client.RequestPairing();
        Check("host sees pairing", WaitFor(() => host.PendingPairing != null));
        Check("client shows code", WaitFor(() => client.State == ClientLinkState.Pairing && client.PairCode.Length == 6));
        Check("codes match", host.PendingPairing?.Code == client.PairCode, $"{host.PendingPairing?.Code} vs {client.PairCode}");
        host.ResolvePairing(true);
        Check("connected after pairing", WaitFor(() => client.IsConnected && host.IsConnected));
        Check("same key both sides",
            hostEp.Key is { } hk && clientEp.Key is { } ck && hk.KeyId == ck.KeyId &&
            Convert.ToBase64String(hk.Key) == Convert.ToBase64String(ck.Key));
        Check("key never logged", !hostEp.Logged(Convert.ToBase64String(hostEp.Key!.Value.Key)) &&
                                  !clientEp.Logged(Convert.ToBase64String(hostEp.Key!.Value.Key)));
        clientEp.Status = ClientStatus.Empty with { LoggedIn = true, WorldName = "伊弗利特", State = "running", Processed = 3, Total = 9 };
        Check("status reaches host", WaitFor(() => host.LatestStatus is { State: "running", Processed: 3 }));
    }

    private static void WrongKeyIsRefusedBothWays()
    {
        var (host, hostEp, client, clientEp) = Paired();
        using (host)
        {
            client.Dispose();
            var keyId = clientEp.Key!.Value.KeyId;

            // 對方拿錯的金鑰：主機的證明對不上，連線端先拒絕主機。
            var wrongEp = new FakeEndpoint(NewDir()) { Key = (keyId, new byte[32]) };
            using var wrong = new SurveyLinkClient(wrongEp, new IPEndPoint(IPAddress.Loopback, host.Port));
            wrong.Start();
            Check("client rejects host proof", WaitFor(() => wrong.State == ClientLinkState.Rejected && wrong.Detail == "bad-host-proof"),
                wrong.State + " " + wrong.Detail);
            Check("never connected", !host.IsConnected);

            // 主機那側：hello 對、證明錯 ⇒ 拒絕並記原因。
            using var raw = new TcpClient();
            raw.Connect(IPAddress.Loopback, host.Port);
            var s = raw.GetStream();
            Write(s, LinkJson.Build("hello", w =>
            {
                w.WriteNumber("v", 1);
                w.WriteString("keyId", keyId);
                w.WriteBase64String("cn", new byte[16]);
            }));
            var challenge = LinkMessage.Parse(Read(s));
            Check("challenge sent", challenge.Type == "challenge");
            Write(s, LinkJson.Build("auth", w => w.WriteBase64String("cp", new byte[32])));
            var reject = LinkMessage.Parse(Read(s));
            Check("host rejects bad proof", reject.Type == "reject" && reject.Str("why") == "bad-proof");
            Check("host logged auth failure", WaitFor(() => hostEp.Logged("證明不符")));

            // 不認得的金鑰代號。
            using var raw2 = new TcpClient();
            raw2.Connect(IPAddress.Loopback, host.Port);
            var s2 = raw2.GetStream();
            Write(s2, LinkJson.Build("hello", w =>
            {
                w.WriteNumber("v", 1);
                w.WriteString("keyId", "deadbeef");
                w.WriteBase64String("cn", new byte[16]);
            }));
            var reject2 = LinkMessage.Parse(Read(s2));
            Check("unknown key id rejected", reject2.Type == "reject" && reject2.Str("why") == "unknown-key");
        }
    }

    private static void ReplayedFrameIsRejected()
    {
        var key = new byte[32];
        key[0] = 7;
        var cn = new byte[16];
        var hn = new byte[16];
        hn[0] = 1;

        // 用記憶體串流當「線」錄下一則合法的訊框。
        var captured = new MemoryStream();
        var sender = new LinkSession(captured, LinkSession.SessionKey(key, cn, hn), false);
        sender.SendAsync(LinkJson.Build("status", w => LinkJson.WriteStatus(w, ClientStatus.Empty)), CancellationToken.None).Wait();
        var frame = captured.ToArray();
        var payload = frame.AsSpan(4).ToArray();

        var receiver = new LinkSession(new MemoryStream(), LinkSession.SessionKey(key, cn, hn), true);
        Check("first copy accepted", receiver.Open(payload).Type == "status");
        var replayRejected = false;
        try
        {
            receiver.Open(payload);
        }
        catch (LinkProtocolException e)
        {
            replayRejected = e.Code == "bad-mac";
        }

        Check("replay rejected", replayRejected);

        var other = new LinkSession(new MemoryStream(), LinkSession.SessionKey(key, cn, new byte[16]), true);
        var otherSessionRejected = false;
        try
        {
            other.Open(payload);
        }
        catch (LinkProtocolException)
        {
            otherSessionRejected = true;
        }

        Check("frame from another session rejected", otherSessionRejected);

        var tampered = (byte[])payload.Clone();
        tampered[^2] ^= 1;
        var fresh = new LinkSession(new MemoryStream(), LinkSession.SessionKey(key, cn, hn), true);
        var tamperRejected = false;
        try
        {
            fresh.Open(tampered);
        }
        catch (LinkProtocolException)
        {
            tamperRejected = true;
        }

        Check("tampered frame rejected", tamperRejected);
    }

    private static void OversizeAndHalfFramesAreDropped()
    {
        var hostEp = new FakeEndpoint(NewDir());
        using var host = new SurveyLinkHost(hostEp);
        host.Start(0);

        using (var big = new TcpClient())
        {
            big.Connect(IPAddress.Loopback, host.Port);
            var header = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(header, 5 * 1024 * 1024);
            big.GetStream().Write(header);
            Check("oversize closed", ClosedWithin(big, 3000));
            Check("oversize logged", WaitFor(() => hostEp.Logged("too-large")));
        }

        using (var half = new TcpClient())
        {
            half.Connect(IPAddress.Loopback, host.Port);
            var header = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(header, 100);
            half.GetStream().Write(header);
            half.GetStream().Write(new byte[10]);
            var started = DateTime.UtcNow;
            Check("half frame times out", ClosedWithin(half, 4000) && DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(800));
        }

        using (var garbage = new TcpClient())
        {
            garbage.Connect(IPAddress.Loopback, host.Port);
            Write(garbage.GetStream(), Encoding.UTF8.GetBytes("{\"t\":\"$type\",\"v\":1}"));
            Check("unknown type closed", ClosedWithin(garbage, 3000));
        }
    }

    private static void RevokeDisconnectsAndRefuses()
    {
        var (host, _, client, _) = Paired();
        using (host)
        using (client)
        {
            Check("connected", host.IsConnected);
            host.Revoke();
            Check("client told it is unknown", WaitFor(() => client.State == ClientLinkState.Rejected && client.Detail == "unknown-key"),
                client.State + " " + client.Detail);
            Check("host has no session", !host.IsConnected);
        }
    }

    private static void RemoteStartRefusalsTravelBack()
    {
        var (host, _, client, clientEp) = Paired();
        using (host)
        using (client)
        {
            var consent = false;
            var busy = (string?)null;
            var canStart = (string?)null;
            clientEp.OnCommand = c => c.Kind != RemoteCommandKind.Start
                ? null
                : SurveyShare.RemoteStartBlock(consent, c.ListId == "L1", busy, null, null, canStart);

            host.SendCommand(RemoteCommandKind.Start, "L1", []);
            Check("no consent refused", WaitFor(() => host.LastCommandResult is { Ok: false, Why: "consent" }),
                host.LastCommandResult?.Why ?? "-");

            consent = true;
            busy = "the character is in a duty";
            host.SendCommand(RemoteCommandKind.Start, "L1", []);
            Check("busy refused", WaitFor(() => host.LastCommandResult is { Ok: false, Why: "the character is in a duty" }));

            busy = null;
            canStart = "A relist is running";
            host.SendCommand(RemoteCommandKind.Start, "L1", []);
            Check("CanStart refused", WaitFor(() => host.LastCommandResult is { Ok: false, Why: "A relist is running" }));

            canStart = null;
            host.SendCommand(RemoteCommandKind.Start, "OTHER", []);
            Check("wrong list refused", WaitFor(() => host.LastCommandResult is { Ok: false, Why: "list" }));

            var id = host.SendCommand(RemoteCommandKind.Start, "L1", [4029u, 4030u]);
            Check("accepted", WaitFor(() => host.LastCommandResult is { Ok: true } r && r.Id == id));
        }
    }

    private static void RemoteStartGateOrder()
    {
        Check("consent first", SurveyShare.RemoteStartBlock(false, false, "busy", "x", "y", "z") == "consent");
        Check("list second", SurveyShare.RemoteStartBlock(true, false, "busy", null, null, null) == "list");
        Check("busy before CanStart", SurveyShare.RemoteStartBlock(true, true, "busy", null, null, "z") == "busy");
        Check("all clear", SurveyShare.RemoteStartBlock(true, true, null, null, null, null) == null);
    }

    private static void ListArrivesInChunks()
    {
        var (host, _, client, clientEp) = Paired();
        using (host)
        using (client)
        {
            var items = new List<SharedListItem>();
            for (uint i = 1; i <= 16_412; i++)
                items.Add(new SharedListItem(i, i % 3 == 0));
            items.Add(new SharedListItem(950_000, false));
            var list = new SharedList
            {
                ListId = "20261126080000-abcd",
                ExportedUtc = T0,
                SourceKey = "csv",
                Items = items,
                ExcludedWorlds = [4034],
                HostWorlds = [new SurveyWorldRow(T0, 4029, "迦樓羅", 999, "csv", 10, 10, true)],
            };
            Check("sent", host.SendList(list));
            Check("arrived", WaitFor(() =>
            {
                lock (clientEp.Lists)
                    return clientEp.Lists.Count == 1;
            }));
            SharedList got;
            lock (clientEp.Lists)
                got = clientEp.Lists[0];
            Check("all valid items, unknown one skipped", got.Items.Count == 16_412, got.Items.Count.ToString());
            Check("excluded carried", got.ExcludedWorlds.Contains(4034));
            Check("host world carried with ContentId 0", got.HostWorlds.Count == 1 && got.HostWorlds[0].ContentId == 0);
            Check("host saw result", WaitFor(() => host.LastListResult == "ok:16412"), host.LastListResult);
        }
    }

    private static void ObservationsSyncIdempotentAndEachSideComputesOwn()
    {
        var (host, hostEp, client, clientEp) = Paired();
        using (host)
        using (client)
        {
            // 這台（代掃）查到 10 筆：最低價那筆是僱員 77。
            for (var i = 0; i < 10; i++)
                clientEp.Log.RecordLocal(Obs(i, nqRetainer: 77));
            Check("host received 10", WaitFor(() => hostEp.AcceptedCount == 10), hostEp.AcceptedCount.ToString());
            Check("client outbox drained", WaitFor(() => clientEp.Log.OutboxCount == 0));

            // 再收一次同一批：一筆都不算新的。
            var again = ((ISurveyLinkEndpoint)hostEp).Receive([Obs(0, nqRetainer: 77), Obs(1, nqRetainer: 77)], []);
            Check("same batch twice counts once", again == 0 && hostEp.AcceptedCount == 10);

            // 各自用自己的清單與僱員算「我方」：77 是主機的僱員、不是代掃那台的。
            var list = new Dictionary<uint, List<(bool, long)>> { [5000] = [(false, 1200L)] };
            var hostRows = SurveyShare.ToRows([Obs(0, nqRetainer: 77)], list, new HashSet<ulong> { 77 });
            var clientRows = SurveyShare.ToRows([Obs(0, nqRetainer: 77)], list, new HashSet<ulong> { 88 });
            var unknownRows = SurveyShare.ToRows([Obs(0, nqRetainer: 77)], list, new HashSet<ulong>());
            Check("host sees its own lowest", hostRows.Count == 1 && hostRows[0].LowestIsOurs == 1 && hostRows[0].OurPrice == 1200);
            Check("helper sees a competitor", clientRows.Count == 1 && clientRows[0].LowestIsOurs == 0 && clientRows[0].UndercutBy == 200);
            Check("no retainers known writes -1", unknownRows[0].LowestIsOurs == -1 && unknownRows[0].UndercutBy == -1);
            Check("item not on the list makes no row", SurveyShare.ToRows([Obs(3)], list, new HashSet<ulong>()).Count == 0);

            // 反方向：主機掃到的也送過去；收到的不再彈回來。
            hostEp.Log.RecordLocal(Obs(100, world: 4029));
            Check("host to helper", WaitFor(() => clientEp.AcceptedCount == 1));
            Thread.Sleep(400);
            Check("no echo back", hostEp.AcceptedCount == 10 && clientEp.Log.OutboxCount == 0);
        }
    }

    private static void HostDropsMidRoundClientCarriesOnAndBackfills()
    {
        var hostDir = NewDir();
        var hostEp = new FakeEndpoint(hostDir);
        var clientEp = new FakeEndpoint(NewDir());
        var host = new SurveyLinkHost(hostEp);
        host.Start(0);
        var port = host.Port;
        using var client = new SurveyLinkClient(clientEp, new IPEndPoint(IPAddress.Loopback, port));
        client.Start();
        client.RequestPairing();
        WaitFor(() => host.PendingPairing != null);
        host.ResolvePairing(true);
        WaitFor(() => client.IsConnected && host.IsConnected);

        // 假引擎：一輪三個世界、每個世界 20 件，邊掃邊記；第一個世界掃完後主機遊戲關掉。
        uint[] worlds = [4028, 4029, 4030];
        var n = 0;
        for (var k = 0; k < 20; k++)
            clientEp.Log.RecordLocal(Obs(n++, world: worlds[0]));
        lock (clientEp.OutWorlds)
            clientEp.OutWorlds.Add(new SurveyWorldRow(T0, worlds[0], "伊弗利特", 0, "imported", 20, 20, true));
        Check("first world synced", WaitFor(() => hostEp.AcceptedCount == 20));
        var savedKey = hostEp.Key;

        host.Dispose();
        Check("client notices", WaitFor(() => !client.IsConnected));
        var whileDown = 0;
        foreach (var world in worlds[1..])
        {
            for (var k = 0; k < 20; k++)
            {
                clientEp.Log.RecordLocal(Obs(n++, world: world));
                whileDown++;
            }

            lock (clientEp.OutWorlds)
                clientEp.OutWorlds.Add(new SurveyWorldRow(T0.AddMinutes(1), world, "w", 0, "imported", 20, 20, true));
        }

        Check("queued while down", clientEp.Log.OutboxCount == whileDown, clientEp.Log.OutboxCount.ToString());
        Check("client keeps retrying", WaitFor(() => client.State == ClientLinkState.WaitingRetry));

        // 主機重開：同一個記錄檔、同一把金鑰、同一個埠。
        var hostEp2 = new FakeEndpoint(hostDir) { Key = savedKey };
        using var host2 = new SurveyLinkHost(hostEp2);
        host2.Start(port);
        Check("reconnected", WaitFor(() => client.IsConnected && host2.IsConnected, 8000));
        Check("backfilled exactly the rows scanned while down", WaitFor(() => hostEp2.AcceptedCount == whileDown) &&
                                                                hostEp2.Duplicates == 0,
            $"{hostEp2.AcceptedCount} dup {hostEp2.Duplicates}");
        Check("world progress delivered", WaitFor(() =>
        {
            lock (hostEp2.Accepted)
                return hostEp2.Worlds.Count >= 2;
        }));
        Check("outbox empty", WaitFor(() => clientEp.Log.OutboxCount == 0));
    }

    private static void ClientRestartKeepsQueuedObservations()
    {
        var clientDir = NewDir();
        var first = new FakeEndpoint(clientDir);
        for (var i = 0; i < 15; i++)
            first.Log.RecordLocal(Obs(i));
        Check("written", WaitFor(() => first.Log.PendingWrites == 0));

        var hostEp = new FakeEndpoint(NewDir());
        using var host = new SurveyLinkHost(hostEp);
        host.Start(0);
        var reborn = new FakeEndpoint(clientDir);
        Check("outbox survives restart", reborn.Log.OutboxCount == 15, reborn.Log.OutboxCount.ToString());
        using var client = new SurveyLinkClient(reborn, new IPEndPoint(IPAddress.Loopback, host.Port));
        client.Start();
        client.RequestPairing();
        WaitFor(() => host.PendingPairing != null);
        host.ResolvePairing(true);
        Check("sent after restart", WaitFor(() => hostEp.AcceptedCount == 15));
        Check("ack cursor persisted", WaitFor(() => File.Exists(Path.Combine(clientDir, SurveyObservationLog.AckFileName))));
        client.Dispose();
        Thread.Sleep(300);
        var third = new FakeEndpoint(clientDir);
        Check("nothing re-queued after ack", third.Log.OutboxCount == 0, third.Log.OutboxCount.ToString());
        third.Log.RecordLocal(Obs(99));
        Check("new seq continues", third.Log.OutboxCount == 1);
    }

    private static void MergedWorldIsNotOtherCharacter()
    {
        var merged = new SurveyWorldRow(DateTime.UtcNow, 4031, "x", 0, "imported", 700, 700, true);
        Check("merge newer", PriceSurveyWorldLog.MergeNewer(merged));
        Check("older does not replace", !PriceSurveyWorldLog.MergeNewer(merged with { AtUtc = merged.AtUtc.AddHours(-1), Completed = false }));
        PriceSurveyWorldLog.TryGet(4031, out var row);
        var freshness = PriceSurveyWorldLog.Classify(row, 6, 0x1234_5678UL, DateTime.UtcNow);
        Check("merged is Complete, not OtherCharacter", freshness == SurveyWorldFreshness.Complete, freshness.ToString());
    }

    private static void BadObservationsAreRejected()
    {
        var now = T0.AddHours(1);
        Func<uint, bool> item = id => id < 900_000;
        Func<uint, bool> world = id => id is >= 4028 and <= 4035;
        Check("good", SurveyShare.Validate(Obs(1), item, world, now) == null);
        Check("price too big", SurveyShare.Validate(Obs(1, lowNq: 1_000_000_000), item, world, now) == "bad-price");
        Check("negative price", SurveyShare.Validate(Obs(1, lowNq: -5), item, world, now) == "bad-price");
        Check("unknown world", SurveyShare.Validate(Obs(1, world: 1), item, world, now) == "unknown-world");
        Check("unknown item", SurveyShare.Validate(Obs(1) with { ItemId = 950_000 }, item, world, now) == "unknown-item");
        Check("bad verdict", SurveyShare.Validate(Obs(1, verdict: "rm -rf"), item, world, now) == "bad-verdict");
        Check("future", SurveyShare.Validate(Obs(1) with { AtUtc = now.AddDays(3) }, item, world, now) == "bad-time");
        Check("control chars", SurveyShare.Validate(Obs(1) with { WorldName = "a\nb" }, item, world, now) == "bad-name");
        Check("public address refused", !SurveyShare.IsPrivateAddress(IPAddress.Parse("8.8.8.8")));
        Check("rfc1918 ok", SurveyShare.IsPrivateAddress(IPAddress.Parse("192.168.1.194"))
                            && SurveyShare.IsPrivateAddress(IPAddress.Parse("172.20.0.1"))
                            && !SurveyShare.IsPrivateAddress(IPAddress.Parse("172.32.0.1")));
        Check("mapped v4", SurveyShare.IsPrivateAddress(IPAddress.Parse("::ffff:10.0.0.5")));
        Check("endpoint parse", SurveyShare.TryParseEndpoint("192.168.1.10:5000", 47835, out var ep) && ep.Port == 5000
                                && !SurveyShare.TryParseEndpoint("example.com", 47835, out _));
    }

    private static void DisposeLeavesNothingListening()
    {
        var (host, hostEp, client, _) = Paired();
        var port = host.Port;
        client.Dispose();
        Check("host notices client gone", WaitFor(() => !host.IsConnected));
        host.Dispose();
        Check("stop logged", hostEp.Logged("停止 listen"));
        var refused = false;
        try
        {
            using var probe = new TcpClient();
            probe.Connect(IPAddress.Loopback, port);
        }
        catch (SocketException)
        {
            refused = true;
        }

        Check("port closed after dispose", refused);
    }

    // =====================================================================
    //  第二階段
    // =====================================================================

    private static SaleSummary Sales(int recent = 2, int page = 5)
        => new(true, 900, T0.AddDays(-1), -1, DateTime.MinValue, recent, page);

    private static void DigestWritesOneLinePerWindow()
    {
        var lines = new List<string>();
        var digest = new SurveyLinkDigest(TimeSpan.FromMinutes(5), lines.Add, T0);
        for (var i = 0; i < 150; i++)
        {
            digest.NoteReceived(1, 1);
            digest.NoteSent(1);
            digest.NoteLocal(i % 50 == 0 ? "refused" : "ok");
            Check("no line before the window ends", !digest.Tick(T0.AddSeconds(2 * i), 0) && lines.Count == 0);
        }

        Check("one line at the end of the window", digest.Tick(T0.AddMinutes(5), 7) && lines.Count == 1, lines.Count.ToString());
        Check("line carries the totals", lines.Count == 1 && lines[0].Contains("收到 150 筆", StringComparison.Ordinal)
                                         && lines[0].Contains("被拒 3", StringComparison.Ordinal)
                                         && lines[0].Contains("還排著 7 筆", StringComparison.Ordinal), lines.Count > 0 ? lines[0] : "-");
        Check("quiet window writes nothing", !digest.Tick(T0.AddMinutes(11), 0) && lines.Count == 1);
        digest.NoteLocal("ok");
        Check("forced flush on unload", digest.Tick(T0.AddMinutes(12), 0, force: true) && lines.Count == 2);
    }

    private static void ObservationLogCompactsKeepsLatestPendingAndCursor()
    {
        var dir = NewDir();
        var log = new SurveyObservationLog(dir, _ => { });
        log.BeginLoad().Wait();
        var key = Obs(0) with { ItemId = 7000 };
        log.RecordLocal(key);                                                   // seq 1
        log.RecordLocal(key with { AtUtc = T0.AddMinutes(1), LowestNq = 900 }); // seq 2
        log.RecordLocal(key with { AtUtc = T0.AddMinutes(2), LowestNq = 800, Sales = Sales() }); // seq 3（最新的答案）
        log.RecordLocal(key with { AtUtc = T0.AddMinutes(3), Verdict = "refused", LowestNq = -1 }); // seq 4（比答案新的沒問到）
        for (var i = 0; i < 5; i++)
            log.AcceptRemote(Obs(i, world: 4029) with { ItemId = 7100, AtUtc = T0.AddMinutes(i) });
        log.Ack(2);
        Check("written before compaction", WaitFor(() => log.IsSettled));
        File.WriteAllText(Path.Combine(dir, SurveyObservationLog.AckFileName), "2");
        var ackBefore = File.ReadAllText(Path.Combine(dir, SurveyObservationLog.AckFileName));

        log.RequestCompact();
        Check("compacted", WaitFor(() => log.Size.Compactions == 1 && log.IsSettled));
        var dataLines = File.ReadAllLines(Path.Combine(dir, SurveyObservationLog.FileName)).Length - 1;
        // 7000：答案 seq3（待送）＋沒問到 seq4（待送）；7100：最新一筆遠端。
        Check("file holds only latest per key plus pending", dataLines == 3, dataLines.ToString());
        Check("cursor untouched", File.ReadAllText(Path.Combine(dir, SurveyObservationLog.AckFileName)) == ackBefore);

        var reborn = new SurveyObservationLog(dir, _ => { });
        reborn.BeginLoad().Wait();
        Check("pending survives compaction", reborn.OutboxCount == 2, reborn.OutboxCount.ToString());
        var peek = reborn.PeekOutbox(10);
        Check("pending keeps its sequence numbers", peek.Count == 2 && peek[0].Seq == 3 && peek[1].Seq == 4);
        Check("latest answer kept with sales", reborn.TryGetLatest(4028, 7000, out var latest)
                                               && latest.LowestNq == 800 && latest.Sales == Sales());
        Check("remote latest kept", reborn.TryGetLatest(4029, 7100, out var remote) && remote.AtUtc == T0.AddMinutes(4));
        Check("older duplicate still recognised", reborn.AcceptRemote(Obs(0, world: 4029) with { ItemId = 7100, AtUtc = T0.AddMinutes(4) })
                                                  == SurveyObservationLog.Intake.Duplicate);
        reborn.RecordLocal(Obs(9) with { ItemId = 7200 });
        Check("new seq continues after compaction", reborn.PeekOutbox(10)[^1].Seq == 5);
        reborn.Ack(5);
        Check("ack after compaction drains", reborn.OutboxCount == 0);
    }

    private static void ObservationLogCompactsOnThreshold()
    {
        var saved = SurveyObservationLog.CompactSlack;
        SurveyObservationLog.CompactSlack = 20;
        try
        {
            var dir = NewDir();
            var log = new SurveyObservationLog(dir, _ => { });
            log.BeginLoad().Wait();
            for (var i = 0; i < 300; i++)
                log.AcceptRemote(Obs(0) with { ItemId = 7300 + (uint)(i % 3), AtUtc = T0.AddSeconds(i) });
            Check("settles", WaitFor(() => log.IsSettled));
            var dataLines = File.ReadAllLines(Path.Combine(dir, SurveyObservationLog.FileName)).Length - 1;
            Check("file stays bounded", log.Size.Compactions > 0 && dataLines <= 3 * 2 + 20 + 3, $"{dataLines} lines, {log.Size.Compactions} compactions");

            // 讀檔時就超過門檻：讀完立刻重寫。
            var big = NewDir();
            var path = Path.Combine(big, SurveyObservationLog.FileName);
            var text = new StringBuilder("seq,origin,timestampUtc,worldId,worldName,itemId,lowestNq,lowestNqRetainer,lowestHq,lowestHqRetainer,listingCount,source,gateIntervalMs,verdict\r\n");
            for (var i = 0; i < 200; i++)
                text.Append($"0,R,2026-11-26 08:{i / 60:00}:{i % 60:00},4028,伊弗利特,7400,{1000 + i},0,-1,0,1,live,2000,ok\r\n");
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(true));
            var loaded = new SurveyObservationLog(big, _ => { });
            loaded.BeginLoad().Wait();
            Check("compacts on load", WaitFor(() => loaded.Size.Compactions == 1 && loaded.IsSettled)
                                      && File.ReadAllLines(path).Length == 2, File.ReadAllLines(path).Length.ToString());
        }
        finally
        {
            SurveyObservationLog.CompactSlack = saved;
        }
    }

    private static void OldObservationFileStillLoads()
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, SurveyObservationLog.FileName),
            "seq,origin,timestampUtc,worldId,worldName,itemId,lowestNq,lowestNqRetainer,lowestHq,lowestHqRetainer,listingCount,source,gateIntervalMs,verdict\r\n" +
            "1,L,2026-11-26 08:00:00,4028,伊弗利特,7500,1000,77,-1,0,3,live,2000,ok\r\n",
            new UTF8Encoding(true));
        var log = new SurveyObservationLog(dir, _ => { });
        log.BeginLoad().Wait();
        Check("old 14-column row loads", log.TryGetLatest(4028, 7500, out var o) && o.LowestNq == 1000);
        Check("its sales are unknown, not zero", !o.Sales.Known);
        Check("old row is still pending", log.OutboxCount == 1);
        log.RecordLocal(Obs(1) with { ItemId = 7501, Sales = Sales(page: 0, recent: 0) with { LastNq = -1, LastNqUtc = DateTime.MinValue } });
        Check("mixed file written", WaitFor(() => log.IsSettled));
        var again = new SurveyObservationLog(dir, _ => { });
        again.BeginLoad().Wait();
        Check("new row with sales reads back", again.TryGetLatest(4028, 7501, out var n) && n.Sales.Known && n.Sales.PageCount == 0);
        Check("both rows pending", again.OutboxCount == 2);
        var brief = again.WorldBrief(4028);
        Check("brief marks verdict ok with empty page as not nothing-at-all", brief.TryGetValue(7501, out var b) && !b.NothingAtAll);
    }

    private static void SalesTravelOptionallyAndOldShapesStillParse()
    {
        // 新寫法送出的觀測：本身仍是 12 欄（舊對端的讀法照樣讀得懂），成交在平行的 sales 陣列。
        var obs = Obs(1) with { Sales = Sales() };
        var json = LinkJson.Build("obs", w =>
        {
            w.WriteNumber("batch", 1);
            w.WriteStartArray("rows");
            LinkJson.WriteObservation(w, obs);
            w.WriteEndArray();
            w.WriteStartArray("sales");
            LinkJson.WriteSales(w, obs.Sales);
            w.WriteEndArray();
            w.WriteStartArray("worlds");
            w.WriteEndArray();
        });
        var m = LinkMessage.Parse(json);
        var row = m.Array("rows", 10).EnumerateArray().GetEnumerator();
        row.MoveNext();
        Check("old reader still reads the row", LinkJson.TryReadObservation(row.Current, out var plain) && !plain.Sales.Known);
        var sale = m.Array("sales", 10).EnumerateArray().GetEnumerator();
        sale.MoveNext();
        Check("sales round trip", LinkJson.ReadSales(sale.Current) == obs.Sales);
        Check("unknown sales travel as null", LinkJson.Build("x", w =>
        {
            w.WriteStartArray("s");
            LinkJson.WriteSales(w, default);
            w.WriteEndArray();
        }).AsSpan().IndexOf("[null]"u8) >= 0);
        Check("bad sales are rejected", SurveyShare.Validate(Sales() with { RecentCount = 9, PageCount = 5 }, T0) == "bad-sales"
                                        && SurveyShare.Validate(Sales() with { LastNqUtc = DateTime.MinValue }, T0) == "bad-sales"
                                        && SurveyShare.Validate(default(SaleSummary), T0) == null);

        // 實線：有成交的與沒成交的（舊對端＝不送 sales）都收得下。
        var (host, hostEp, client, clientEp) = Paired();
        using (host)
        using (client)
        {
            clientEp.Log.RecordLocal(Obs(2) with { ItemId = 7600, Sales = Sales() });
            clientEp.Log.RecordLocal(Obs(3) with { ItemId = 7601 });
            Check("both arrive", WaitFor(() => hostEp.AcceptedCount == 2), hostEp.AcceptedCount.ToString());
            Check("sales arrive intact", hostEp.Log.TryGetLatest(4028, 7600, out var a) && a.Sales == Sales());
            Check("missing sales arrive as unknown", hostEp.Log.TryGetLatest(4028, 7601, out var b) && !b.Sales.Known);
        }
    }

    private static void OldStatusWithoutNewFieldsReadsAsUnknown()
    {
        // 舊版 WriteStatus 只寫到 max。
        var old = LinkJson.Build("status", w =>
        {
            w.WriteBoolean("loggedIn", true);
            w.WriteNumber("worldId", 4028);
            w.WriteString("world", "伊弗利特");
            w.WriteString("state", "idle");
            w.WriteNumber("processed", 0);
            w.WriteNumber("total", 0);
            w.WriteBoolean("armed", false);
            w.WriteBoolean("remote", false);
            w.WriteString("text", "");
            w.WriteString("tour", "");
            w.WriteString("block", "");
            w.WriteBoolean("consent", true);
            w.WriteString("listId", "");
            w.WriteNumber("outbox", 0);
            w.WriteNumber("step", 0);
            w.WriteNumber("max", 0);
        });
        var s = LinkJson.ReadStatus(LinkMessage.Parse(old));
        Check("old peer cannot do full market", !s.SupportsFullMarket && s.Caps == 0);
        Check("unknowns stay unknown", s.FullPending == -1 && s.Gil == -1 && s.Mode.Length == 0 && s.Halt.Length == 0);
        var fresh = ClientStatus.Empty with { Caps = ClientStatus.CapFullMarket, Mode = "full", FullWorld = 2, FullWorlds = 8, FullPending = 1234, Gil = 5, Halt = "x" };
        var back = LinkJson.ReadStatus(LinkMessage.Parse(LinkJson.Build("status", w => LinkJson.WriteStatus(w, fresh))));
        Check("new fields round trip", back == fresh, back.ToString());
    }

    private static void RoundProgressSurvivesRestart()
    {
        var dir = NewDir();
        var p = new SurveyRoundProgress(dir, _ => { });
        p.BeginLoad().Wait();
        var key = SurveyRoundProgress.ListKeyOf([new SharedListItem(5, true), new SharedListItem(3, false)]);
        Check("list key ignores order", key == SurveyRoundProgress.ListKeyOf([new SharedListItem(3, false), new SharedListItem(5, true)])
                                        && key != SurveyRoundProgress.ListKeyOf([new SharedListItem(3, false)]));
        var proxyRow = new SurveyWorldRow(T0, 4029, "迦樓羅", 0, "imported", 642, 642, true);
        var fullRow = new SurveyWorldRow(T0.AddHours(1), 4030, "泰坦", 0, "full-market", 16412, 900, false);
        p.Record(SurveyRoundProgress.ModeProxy, key, proxyRow);
        p.Record(SurveyRoundProgress.ModeFull, string.Empty, fullRow);
        Check("written", WaitFor(() => File.Exists(Path.Combine(dir, SurveyRoundProgress.FileName))
                                       && File.ReadAllLines(Path.Combine(dir, SurveyRoundProgress.FileName)).Length == 3));
        var again = new SurveyRoundProgress(dir, _ => { });
        again.BeginLoad().Wait();
        Check("proxy row back", again.TryGet(SurveyRoundProgress.ModeProxy, key, 4029, out var a) && a.Surveyed == 642 && a.Completed);
        Check("full row back", again.TryGet(SurveyRoundProgress.ModeFull, string.Empty, 4030, out var b) && b.Surveyed == 900 && !b.Completed
                               && b.AtUtc == fullRow.AtUtc);
        Check("other list sees nothing", again.Rows(SurveyRoundProgress.ModeProxy, "deadbeef").Count == 0);
    }

    private static readonly DateTime Now2 = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);

    private static void FullMarketOrderingAndSkips()
    {
        uint[] items = [1, 2, 3, 4, 5, 6];
        var uploads = new UploadTimes();
        uploads.Set(4028, 1, Now2.AddDays(-10));
        uploads.Set(4028, 2, Now2.AddHours(-1));   // 有人一小時前更新過 ⇒ 跳過
        uploads.Set(4028, 4, Now2.AddDays(-20));
        uploads.Set(4028, 6, Now2.AddDays(-9));
        var local = new Dictionary<uint, ItemBrief>
        {
            [4] = new(Now2.AddHours(-2), false),     // 兩台之一兩小時前問過 ⇒ 跳過
            [5] = new(Now2.AddDays(-2), true),       // 哪裡都沒有、兩天前問過 ⇒ 降頻跳過
            [6] = new(Now2.AddDays(-10), true),      // 哪裡都沒有、十天前問過 ⇒ 重掃
        };
        var nothing = new HashSet<uint> { 5, 6 };
        var plan = FullMarketPlanner.PlanWorld(4028, items, local, nothing, uploads, Now2, 24, 7);
        Check("stalest first, never-uploaded at the front", string.Join(",", plan.Queue) == "3,1,6", string.Join(",", plan.Queue));
        Check("fresh skipped", plan.SkippedFresh == 2, plan.SkippedFresh.ToString());
        Check("dead items rechecked less often", plan.SkippedNoData == 1, plan.SkippedNoData.ToString());
        var noSkip = FullMarketPlanner.PlanWorld(4028, items, local, nothing, uploads, Now2, 0, 0);
        Check("0 turns both skips off", noSkip.Queue.Count == 6 && noSkip.SkippedFresh == 0 && noSkip.SkippedNoData == 0);

        // 「每個世界都沒有」：任何一個世界沒問過或有成交就不算。
        var w1 = new Dictionary<uint, ItemBrief> { [5] = new(Now2, true), [6] = new(Now2, true), [7] = new(Now2, true) };
        var w2 = new Dictionary<uint, ItemBrief> { [5] = new(Now2, true), [6] = new(Now2, false) };
        var dead = FullMarketPlanner.NothingAnywhere([w1, w2]);
        Check("dead only when dead on every world", dead.SetEquals([5]), string.Join(",", dead));
    }

    private static void FullMarketFallsBackToLocalTimes()
    {
        uint[] items = [10, 11, 12];
        var local = new Dictionary<uint, ItemBrief>
        {
            [10] = new(Now2.AddDays(-1.5), false),
            [11] = new(Now2.AddDays(-3), false),
        };
        var plan = FullMarketPlanner.PlanWorld(4028, items, local, new HashSet<uint>(), null, Now2, 24, 7);
        Check("Universalis down: never-asked first, then by local time", string.Join(",", plan.Queue) == "12,11,10", string.Join(",", plan.Queue));
        var withUni = new UploadTimes();
        withUni.Set(4028, 12, Now2.AddHours(-3));
        var plan2 = FullMarketPlanner.PlanWorld(4028, items, local, new HashSet<uint>(), withUni, Now2, 24, 7);
        Check("Universalis time is used when there is one", string.Join(",", plan2.Queue) == "11,10" && plan2.SkippedFresh == 1,
            string.Join(",", plan2.Queue));
    }

    private static void UniversalisAggregatedParse()
    {
        // 形狀照 Universalis v2 aggregated 的文件：results[].itemId、results[].worldUploadTimes[].{worldId,timestamp(ms)}、failedItems。
        var json = """
                   {"results":[
                     {"itemId":5057,"nq":{"minListing":{"world":{"price":10}}},"hq":{},
                      "worldUploadTimes":[{"worldId":4028,"timestamp":1764140400000},{"worldId":4029,"timestamp":1764054000000}]},
                     {"itemId":5058,"nq":{},"hq":{},"worldUploadTimes":[]},
                     {"itemId":"bad"}],
                    "failedItems":[99999]}
                   """u8;
        var times = new UploadTimes();
        var n = times.AddAggregated(json);
        Check("items counted", n == 2, n.ToString());
        Check("upload time per world", times.TryGet(4028, 5057, out var t) && t == DateTimeOffset.FromUnixTimeMilliseconds(1764140400000).UtcDateTime);
        Check("no time recorded for an item nobody uploaded", !times.TryGet(4028, 5058, out _) && times.Count == 2);
        var threw = false;
        try
        {
            new UploadTimes().AddAggregated("error code: 520"u8);
        }
        catch (System.Text.Json.JsonException)
        {
            threw = true;
        }

        Check("non-JSON error page is a failure, not an empty answer", threw);
    }

    private static void WorldChoiceIsBatchedPerWorld()
    {
        Check("most pending wins", FullMarketPlanner.ChooseWorld([(4028, 10, DateTime.MinValue), (4029, 30, Now2)]) == 4029);
        Check("tie goes to the stalest", FullMarketPlanner.ChooseWorld([(4028, 30, Now2), (4029, 30, Now2.AddDays(-1))]) == 4029);
        Check("nothing pending is 0", FullMarketPlanner.ChooseWorld([(4028, 0, DateTime.MinValue)]) == 0);

        // 模擬一輪：挑世界、把那個世界的待掃整份掃完、再挑。換世界次數＝有待掃的世界數，不是件數。
        uint[] worlds = [4028, 4029, 4030];
        var items = new List<uint>();
        for (uint i = 1; i <= 50; i++)
            items.Add(i);
        var briefs = new Dictionary<uint, Dictionary<uint, ItemBrief>>();
        foreach (var w in worlds)
            briefs[w] = new Dictionary<uint, ItemBrief>();
        for (uint i = 1; i <= 40; i++)
            briefs[4030][i] = new ItemBrief(Now2.AddHours(-1), false); // 4030 大部分剛掃過
        var visited = new HashSet<uint>();
        var order = new List<uint>();
        var switches = 0;
        while (true)
        {
            var choices = new List<(uint, int, DateTime)>();
            foreach (var w in worlds)
            {
                if (visited.Contains(w))
                    continue;
                var plan = FullMarketPlanner.PlanWorld(w, items, briefs[w], new HashSet<uint>(), null, Now2, 24, 7);
                choices.Add((w, plan.Queue.Count, DateTime.MinValue));
            }

            var best = FullMarketPlanner.ChooseWorld(choices);
            if (best == 0)
                break;
            visited.Add(best);
            order.Add(best);
            switches++;
            foreach (var item in FullMarketPlanner.PlanWorld(best, items, briefs[best], new HashSet<uint>(), null, Now2, 24, 7).Queue)
                briefs[best][item] = new ItemBrief(Now2, false);
        }

        Check("each world once, emptiest last", string.Join(",", order) == "4028,4029,4030" && switches == 3, string.Join(",", order));
    }

    private static void FullStartNeedsCapabilityAndUnknownKindNeverRuns()
    {
        var (host, _, client, clientEp) = Paired();
        using (host)
        using (client)
        {
            var commands = new List<RemoteCommandKind>();
            clientEp.OnCommand = c =>
            {
                lock (commands)
                    commands.Add(c.Kind);
                return null;
            };

            // 對端回報的狀態沒有 caps（舊版）：主機根本不送。
            clientEp.Status = ClientStatus.Empty with { LoggedIn = true, Processed = 1 };
            WaitFor(() => host.LatestStatus is { Processed: 1 });
            Check("not sent to an old peer", host.SendCommand(RemoteCommandKind.FullStart, string.Empty, []) == -2);
            Thread.Sleep(300);
            lock (commands)
                Check("old peer saw nothing", commands.Count == 0);

            clientEp.Status = ClientStatus.Empty with { LoggedIn = true, Processed = 2, Caps = ClientStatus.CapFullMarket };
            WaitFor(() => host.LatestStatus is { Processed: 2 });
            var id = host.SendCommand(RemoteCommandKind.FullStart, string.Empty, []);
            Check("new peer runs it", WaitFor(() => host.LastCommandResult is { Ok: true } r && r.Id == id && r.Kind == RemoteCommandKind.FullStart));
            lock (commands)
                Check("arrived as full start", commands.Count == 1 && commands[0] == RemoteCommandKind.FullStart);

            // 舊版對端收到不認得的指令名＝這裡收到 "zzz" 的同一條路：擲 bad-field 斷線，什麼都不照做（不是「停止」）。
            var connection = typeof(SurveyLinkHost).GetField("current", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(host) as LinkConnection;
            connection!.Post(LinkJson.Build("cmd", w =>
            {
                w.WriteNumber("id", 99);
                w.WriteString("kind", "zzz");
                w.WriteString("listId", string.Empty);
                w.WriteStartArray("worlds");
                w.WriteEndArray();
            }));
            Check("unknown kind drops the link", WaitFor(() => clientEp.Logged("disconnected:bad-field")));
            lock (commands)
                Check("unknown kind ran nothing", commands.Count == 1);
            Check("and it comes back", WaitFor(() => client.IsConnected && host.IsConnected, 8000));
        }
    }

    private static void HostDropsDuringFullScanBackfillKeepsSales()
    {
        var hostDir = NewDir();
        var hostEp = new FakeEndpoint(hostDir);
        var clientEp = new FakeEndpoint(NewDir());
        var host = new SurveyLinkHost(hostEp);
        host.Start(0);
        var port = host.Port;
        using var client = new SurveyLinkClient(clientEp, new IPEndPoint(IPAddress.Loopback, port));
        client.Start();
        client.RequestPairing();
        WaitFor(() => host.PendingPairing != null);
        host.ResolvePairing(true);
        WaitFor(() => client.IsConnected && host.IsConnected);
        var savedKey = hostEp.Key;
        host.Dispose();
        Check("down", WaitFor(() => !client.IsConnected));

        for (var i = 0; i < 2500; i++)
            clientEp.Log.RecordLocal(Obs(i, world: (uint)(4028 + (i % 3))) with { ItemId = (uint)(10_000 + i), Sales = Sales() });
        var hostEp2 = new FakeEndpoint(hostDir) { Key = savedKey };
        using var host2 = new SurveyLinkHost(hostEp2);
        host2.Start(port);
        Check("backfilled in batches", WaitFor(() => hostEp2.AcceptedCount == 2500, 15000), hostEp2.AcceptedCount.ToString());
        Check("sales came along", hostEp2.Log.TryGetLatest(4029, 10_001, out var o) && o.Sales == Sales());
        var again = ((ISurveyLinkEndpoint)hostEp2).Receive([Obs(5, world: 4030) with { ItemId = 10_005, Sales = Sales() }], []);
        Check("replay is a no-op", again == 0 && hostEp2.AcceptedCount == 2500);
        Check("outbox drained", WaitFor(() => clientEp.Log.OutboxCount == 0));
    }

    private static void TravelWatchSpotsAStalledTeleport()
    {
        var w = new TravelWatch();
        w.Start(T0, 4030);
        Check("grace", w.Tick(T0.AddSeconds(1), true, 4029, false, false) == TravelWatch.Result.Waiting);
        Check("busy keeps waiting", w.Tick(T0.AddSeconds(20), true, 4029, true, false) == TravelWatch.Result.Waiting);
        Check("loading screen keeps waiting", w.Tick(T0.AddSeconds(25), true, 4029, false, true) == TravelWatch.Result.Waiting);
        Check("idle but not long enough", w.Tick(T0.AddSeconds(27), true, 4029, false, false) == TravelWatch.Result.Waiting);
        Check("idle long enough after Lifestream gave up", w.Tick(T0.AddSeconds(32.5), true, 4029, false, false) == TravelWatch.Result.NotStarted);
        w.Start(T0, 4030);
        Check("arrived wins", w.Tick(T0.AddSeconds(60), true, 4030, false, false) == TravelWatch.Result.Arrived);
        w.Start(T0, 4030);
        w.Tick(T0.AddSeconds(1), true, 4029, false, false);
        Check("never busy still fails fast", w.Tick(T0.AddSeconds(9), true, 4029, false, false) == TravelWatch.Result.NotStarted);
    }

    // =====================================================================

    private static void Write(Stream s, byte[] json) => LinkFrames.WriteAsync(s, json, CancellationToken.None).Wait();

    private static byte[] Read(Stream s)
        => LinkFrames.ReadAsync(s, LinkFrames.MaxFrame, TimeSpan.FromSeconds(3), CancellationToken.None).GetAwaiter().GetResult();

    private static bool ClosedWithin(TcpClient c, int ms)
    {
        c.ReceiveTimeout = ms;
        try
        {
            var buffer = new byte[256];
            while (true)
            {
                var read = c.GetStream().Read(buffer, 0, buffer.Length);
                if (read == 0)
                    return true;
            }
        }
        catch (IOException e)
        {
            // 逾時＝對方沒關；被重設＝對方關了。
            return e.InnerException is SocketException { SocketErrorCode: not SocketError.TimedOut };
        }
    }
}
