using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Marketbuddy
{
    // 本檔契約：不引用 Dalamud。socket I/O 全是執行緒池上的 async＋CancellationToken，不自建執行緒、沒有 async void；
    // 每個背景 Task 整段 try/catch。碰遊戲或巡檢引擎的事一律交給端點（ISurveyLink*Endpoint）自己排回 framework 執行緒。
    // 金鑰不進任何 log 字串。

    /// <summary>一批要送出去的觀測與世界進度；對方確認後原物交回 <see cref="ISurveyLinkEndpoint.Acked"/>。</summary>
    internal sealed record OutgoingBatch(long UpToSeq, List<MarketObservation> Rows, List<SurveyWorldRow> Worlds);

    internal interface ISurveyLinkEndpoint
    {
        /// <summary>診斷輸出（Information）。任何執行緒都會呼叫。</summary>
        void Log(string message);

        Func<uint, bool>? KnownItem { get; }
        Func<uint, bool>? KnownWorld { get; }

        /// <summary>配對過的金鑰；沒有就回 null。任何執行緒。</summary>
        (string KeyId, byte[] Key)? PairedKey();

        /// <summary>存下新配對的金鑰（取代舊的）。任何執行緒。</summary>
        void SavePairedKey(string keyId, byte[] key);

        OutgoingBatch? TakeOutgoing(int max);
        void Acked(OutgoingBatch batch);

        /// <summary>收下一批（已驗證）。回 null＝還沒準備好（不確認，對方稍後重送）；否則是新收的筆數。</summary>
        int? Receive(IReadOnlyList<MarketObservation> rows, IReadOnlyList<SurveyWorldRow> worlds);
    }

    internal interface ISurveyLinkHostEndpoint : ISurveyLinkEndpoint
    {
        void ClearPairedKey();
    }

    internal interface ISurveyLinkClientEndpoint : ISurveyLinkEndpoint
    {
        ClientStatus CurrentStatus();

        /// <summary>收到一份完整清單。回 null＝收下；否則是要傳回主機的原因。</summary>
        Task<string?> HandleList(SharedList list);

        /// <summary>收到一則指令。回 null＝已照做；否則是要傳回主機的拒絕原因。</summary>
        Task<string?> HandleCommand(RemoteCommand command);
    }

    internal static class SurveyLinkTiming
    {
        internal static TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
        internal static TimeSpan PairingWait = TimeSpan.FromSeconds(120);
        internal static TimeSpan IdleTimeout = TimeSpan.FromSeconds(45);
        internal static TimeSpan Heartbeat = TimeSpan.FromSeconds(15);
        internal static TimeSpan StatusMinInterval = TimeSpan.FromSeconds(2);
        internal static TimeSpan AckTimeout = TimeSpan.FromSeconds(90);
        internal static TimeSpan NotReadyRetry = TimeSpan.FromSeconds(5);
        internal static TimeSpan RetryMin = TimeSpan.FromSeconds(10);
        internal static TimeSpan RetryMax = TimeSpan.FromMinutes(5);
        internal static TimeSpan DisposeWait = TimeSpan.FromSeconds(2);
    }

    /// <summary>認證後的一條連線：一個讀迴圈、一個寫迴圈，觀測一次只有一批在路上。</summary>
    internal sealed class LinkConnection : IDisposable
    {
        private readonly LinkSession session;
        private readonly TcpClient client;
        private readonly bool isHost;
        private readonly ISurveyLinkEndpoint endpoint;
        private readonly Func<LinkMessage, Task<bool>> roleHandler;
        private readonly Func<byte[]?>? roleTick;
        private readonly CancellationTokenSource cts;
        private readonly ConcurrentQueue<byte[]> outgoing = new();
        private readonly SemaphoreSlim wake = new(0);
        private readonly object batchGate = new();
        private OutgoingBatch? inFlight;
        private long inFlightId;
        private DateTime inFlightSentUtc;
        private DateTime retryAfterUtc;
        private long nextBatchId;
        private DateTime lastSentUtc = DateTime.UtcNow;
        private long lastReceivedTicks = DateTime.UtcNow.Ticks;
        private int disposed;

        internal LinkConnection(LinkSession session, TcpClient client, bool isHost, ISurveyLinkEndpoint endpoint,
            Func<LinkMessage, Task<bool>> roleHandler, Func<byte[]?>? roleTick, CancellationToken outer)
        {
            this.session = session;
            this.client = client;
            this.isHost = isHost;
            this.endpoint = endpoint;
            this.roleHandler = roleHandler;
            this.roleTick = roleTick;
            cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        }

        internal DateTime LastReceivedUtc => new(Interlocked.Read(ref lastReceivedTicks), DateTimeKind.Utc);

        internal bool IsOpen => Volatile.Read(ref disposed) == 0 && !cts.IsCancellationRequested;

        internal string CloseReason { get; private set; } = string.Empty;

        /// <summary>排一則出去（依序送）。任何執行緒。</summary>
        internal void Post(byte[] json)
        {
            if (!IsOpen)
                return;
            outgoing.Enqueue(json);
            wake.Release();
        }

        internal async Task RunAsync()
        {
            var reader = ReadLoop();
            var writer = WriteLoop();
            await Task.WhenAny(reader, writer).ConfigureAwait(false);
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 已經被 Dispose 收掉了。
            }

            try
            {
                await Task.WhenAll(reader, writer).ConfigureAwait(false);
            }
            catch
            {
                // 兩個迴圈各自記了原因。
            }

            Dispose();
        }

        private async Task ReadLoop()
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var message = await session.ReceiveAsync(SurveyLinkTiming.IdleTimeout, cts.Token).ConfigureAwait(false);
                    Interlocked.Exchange(ref lastReceivedTicks, DateTime.UtcNow.Ticks);
                    switch (message.Type)
                    {
                        case "ping":
                        case "welcome":
                            continue;
                        case "obs":
                            HandleObservations(message);
                            continue;
                        case "obs-ack":
                            HandleAck(message);
                            continue;
                    }

                    if (!await roleHandler(message).ConfigureAwait(false))
                        throw new LinkProtocolException("unexpected-" + message.Type);
                }
            }
            catch (LinkProtocolException e)
            {
                CloseReason = e.Code;
            }
            catch (OperationCanceledException)
            {
                CloseReason = CloseReason.Length > 0 ? CloseReason : "closed";
            }
            catch (Exception e)
            {
                CloseReason = e.GetType().Name;
            }
        }

        private async Task WriteLoop()
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    await wake.WaitAsync(TimeSpan.FromSeconds(1), cts.Token).ConfigureAwait(false);
                    while (outgoing.TryDequeue(out var json))
                        await Send(json).ConfigureAwait(false);

                    var now = DateTime.UtcNow;
                    byte[]? batchMessage = null;
                    bool canSend;
                    lock (batchGate)
                    {
                        if (inFlight != null && now - inFlightSentUtc > SurveyLinkTiming.AckTimeout)
                            throw new LinkProtocolException("ack-timeout");
                        canSend = inFlight == null && now >= retryAfterUtc;
                    }

                    // 只有這個寫迴圈會把 inFlight 設成非 null，所以鎖外取批次不會撞車；端點可能寫 log，不能在鎖內叫。
                    var batch = canSend ? endpoint.TakeOutgoing(SurveyShare.MaxObservationBatch) : null;
                    lock (batchGate)
                    {
                        if (batch != null && (batch.Rows.Count > 0 || batch.Worlds.Count > 0))
                        {
                            inFlight = batch;
                            inFlightId = ++nextBatchId;
                            inFlightSentUtc = now;
                            var id = inFlightId;
                            batchMessage = LinkJson.Build("obs", w =>
                            {
                                w.WriteNumber("batch", id);
                                w.WriteStartArray("rows");
                                foreach (var row in batch.Rows)
                                    LinkJson.WriteObservation(w, row);
                                w.WriteEndArray();
                                w.WriteStartArray("sales");
                                foreach (var row in batch.Rows)
                                    LinkJson.WriteSales(w, row.Sales);
                                w.WriteEndArray();
                                w.WriteStartArray("worlds");
                                foreach (var world in batch.Worlds)
                                    LinkJson.WriteWorld(w, world);
                                w.WriteEndArray();
                            });
                        }
                    }

                    if (batchMessage != null)
                        await Send(batchMessage).ConfigureAwait(false);

                    if (roleTick?.Invoke() is { } tick)
                        await Send(tick).ConfigureAwait(false);
                    else if (isHost && DateTime.UtcNow - lastSentUtc > SurveyLinkTiming.Heartbeat)
                        await Send(LinkJson.Build("ping")).ConfigureAwait(false);
                }
            }
            catch (LinkProtocolException e)
            {
                CloseReason = e.Code;
            }
            catch (OperationCanceledException)
            {
                // 讀迴圈或 Dispose 收的。
            }
            catch (Exception e)
            {
                CloseReason = CloseReason.Length > 0 ? CloseReason : e.GetType().Name;
            }
        }

        private async Task Send(byte[] json)
        {
            await session.SendAsync(json, cts.Token).ConfigureAwait(false);
            lastSentUtc = DateTime.UtcNow;
        }

        private void HandleObservations(LinkMessage message)
        {
            var batch = message.I64("batch");
            var rowsElement = message.Array("rows", SurveyShare.MaxObservationBatch);
            var worldsElement = message.Array("worlds", SurveyShare.MaxWorldsPerMessage);
            // 可選：舊對端不送；長度對不上就整份當「不知道」，不拖累觀測本身。
            System.Text.Json.JsonElement[]? sales = null;
            if (message.Has("sales"))
            {
                var salesElement = message.Array("sales", SurveyShare.MaxObservationBatch);
                if (salesElement.GetArrayLength() == rowsElement.GetArrayLength())
                    sales = [.. salesElement.EnumerateArray()];
            }

            var now = DateTime.UtcNow;
            var rows = new List<MarketObservation>(rowsElement.GetArrayLength());
            var worlds = new List<SurveyWorldRow>();
            var skipped = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var index = 0;
            foreach (var element in rowsElement.EnumerateArray())
            {
                string? why;
                var saleElement = sales?[index];
                index++;
                if (!LinkJson.TryReadObservation(element, out var obs))
                    why = "bad-field";
                else
                {
                    if (saleElement is { } se)
                    {
                        var summary = LinkJson.ReadSales(se);
                        obs = obs with { Sales = SurveyShare.Validate(summary, now) == null ? summary : default };
                    }

                    why = SurveyShare.Validate(obs, endpoint.KnownItem, endpoint.KnownWorld, now);
                }
                if (why == null)
                    rows.Add(obs);
                else
                    skipped[why] = skipped.TryGetValue(why, out var n) ? n + 1 : 1;
            }

            foreach (var element in worldsElement.EnumerateArray())
            {
                string? why;
                if (!LinkJson.TryReadWorld(element, "imported", out var world))
                    why = "bad-world-row";
                else
                    why = SurveyShare.Validate(world, endpoint.KnownWorld, now);
                if (why == null)
                    worlds.Add(world);
                else
                    skipped[why] = skipped.TryGetValue(why, out var n) ? n + 1 : 1;
            }

            if (skipped.Count > 0)
            {
                var text = new StringBuilder();
                foreach (var (why, count) in skipped)
                    text.Append(text.Length > 0 ? ", " : string.Empty).Append(why).Append('=').Append(count);
                endpoint.Log($"收到的觀測有 {text} 筆不合格，已跳過。");
            }

            var accepted = endpoint.Receive(rows, worlds);
            Post(LinkJson.Build("obs-ack", w =>
            {
                w.WriteNumber("batch", batch);
                w.WriteBoolean("ok", accepted != null);
            }));
        }

        private void HandleAck(LinkMessage message)
        {
            var batch = message.I64("batch");
            var ok = message.Bool("ok");
            OutgoingBatch? done = null;
            lock (batchGate)
            {
                if (inFlight == null || batch != inFlightId)
                    return;
                if (ok)
                    done = inFlight;
                else
                    retryAfterUtc = DateTime.UtcNow + SurveyLinkTiming.NotReadyRetry;
                inFlight = null;
            }

            if (done != null)
                endpoint.Acked(done);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 已收。
            }

            session.Dispose();
            try
            {
                client.Dispose();
            }
            catch
            {
                // 關閉時的例外沒有後續動作可做。
            }
        }
    }

    /// <summary>配對請求：主機畫面顯示位址與代碼，等使用者按允許／拒絕。</summary>
    internal sealed record PairingRequest(string Address, string Code, DateTime ExpiresUtc);

    /// <summary>
    /// 主機：只收私有位址、同時最多一個已認證的連線（新的取代舊的）、認證前的握手最多 4 條。
    /// 🔴 只在使用者打開時 listen；收到的東西只有觀測、狀態、清單／指令的回覆——主機這側沒有任何會被對方觸發的動作。
    /// </summary>
    internal sealed class SurveyLinkHost : IDisposable
    {
        private const int MaxHandshakes = 4;

        private readonly ISurveyLinkHostEndpoint endpoint;
        private readonly CancellationTokenSource cts = new();
        private readonly ConcurrentDictionary<Task, byte> tasks = new();
        private readonly object gate = new();
        private TcpListener? listener;
        private LinkConnection? current;
        private TaskCompletionSource<bool>? pendingDecision;
        private PairingRequest? pending;
        private int handshakes;
        private int nextCommandId;
        private int disposed;

        internal SurveyLinkHost(ISurveyLinkHostEndpoint endpoint) => this.endpoint = endpoint;

        internal int Port { get; private set; }
        internal bool IsListening => listener != null && Volatile.Read(ref disposed) == 0;
        internal PairingRequest? PendingPairing => pending;
        internal string ClientAddress { get; private set; } = string.Empty;
        internal DateTime LastConnectedUtc { get; private set; } = DateTime.MinValue;
        internal DateTime LastDisconnectedUtc { get; private set; } = DateTime.MinValue;
        internal ClientStatus? LatestStatus { get; private set; }
        internal string LastListResult { get; private set; } = string.Empty;
        internal (int Id, RemoteCommandKind Kind, bool Ok, string Why, DateTime AtUtc)? LastCommandResult { get; private set; }

        internal bool IsConnected
        {
            get
            {
                lock (gate)
                {
                    return current is { IsOpen: true };
                }
            }
        }

        internal DateTime LastSeenUtc
        {
            get
            {
                lock (gate)
                {
                    return current?.LastReceivedUtc ?? LastDisconnectedUtc;
                }
            }
        }

        /// <summary>開始 listen。埠被占用等錯誤照常擲（呼叫端轉成畫面上的狀態）。</summary>
        internal void Start(int port)
        {
            var l = new TcpListener(IPAddress.Any, port);
            l.Start(4);
            listener = l;
            Port = ((IPEndPoint)l.LocalEndpoint).Port;
            Track(AcceptLoop(l));
            endpoint.Log($"開始 listen，埠 {Port}。");
        }

        private void Track(Task task)
        {
            tasks[task] = 0;
            task.ContinueWith(t => tasks.TryRemove(t, out _), TaskScheduler.Default);
        }

        private async Task AcceptLoop(TcpListener l)
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var socket = await l.AcceptTcpClientAsync(cts.Token).ConfigureAwait(false);
                    var remote = (socket.Client.RemoteEndPoint as IPEndPoint)?.Address;
                    if (!SurveyShare.IsPrivateAddress(remote))
                    {
                        endpoint.Log($"拒絕連線：{remote} 不是私有位址。");
                        socket.Dispose();
                        continue;
                    }

                    if (Interlocked.Increment(ref handshakes) > MaxHandshakes)
                    {
                        Interlocked.Decrement(ref handshakes);
                        socket.Dispose();
                        continue;
                    }

                    Track(HandleIncoming(socket, remote!.ToString()));
                }
            }
            catch (OperationCanceledException)
            {
                // 停止 listen。
            }
            catch (ObjectDisposedException)
            {
                // 停止 listen。
            }
            catch (Exception e)
            {
                endpoint.Log($"listen 中斷：{e.GetType().Name}。");
            }
        }

        private async Task HandleIncoming(TcpClient socket, string address)
        {
            var handedOver = false;
            var counted = true;
            try
            {
                socket.NoDelay = true;
                var stream = socket.GetStream();
                var first = LinkMessage.Parse(await LinkFrames.ReadAsync(stream, LinkFrames.MaxPreAuthFrame,
                    SurveyLinkTiming.HandshakeTimeout, cts.Token).ConfigureAwait(false));
                if (first.I32("v") != 1)
                {
                    await Reject(stream, "version").ConfigureAwait(false);
                    return;
                }

                if (first.Type == "pair")
                {
                    await HandlePairing(stream, first, address).ConfigureAwait(false);
                    return;
                }

                if (first.Type != "hello")
                    return;

                var keyId = first.Str("keyId", SurveyShare.MaxIdLength);
                var clientNonce = first.Bytes("cn", LinkCrypto.NonceBytes, LinkCrypto.NonceBytes);
                var paired = endpoint.PairedKey();
                if (paired is not { } pk || !string.Equals(pk.KeyId, keyId, StringComparison.Ordinal))
                {
                    endpoint.Log($"認證失敗：{address} 用的金鑰代號不是目前配對的那一個（可能已撤銷）。");
                    await Reject(stream, "unknown-key").ConfigureAwait(false);
                    return;
                }

                var hostNonce = LinkCrypto.Random(LinkCrypto.NonceBytes);
                var hostProof = LinkCrypto.Mac(pk.Key, "host-proof", clientNonce, hostNonce);
                await SendPlain(stream, LinkJson.Build("challenge", w =>
                {
                    w.WriteBase64String("hn", hostNonce);
                    w.WriteBase64String("hp", hostProof);
                })).ConfigureAwait(false);

                var auth = LinkMessage.Parse(await LinkFrames.ReadAsync(stream, LinkFrames.MaxPreAuthFrame,
                    SurveyLinkTiming.HandshakeTimeout, cts.Token).ConfigureAwait(false));
                if (auth.Type != "auth"
                    || !LinkCrypto.Equal(auth.Bytes("cp", 32, 32), LinkCrypto.Mac(pk.Key, "client-proof", clientNonce, hostNonce)))
                {
                    endpoint.Log($"認證失敗：{address} 的證明不符。");
                    await Reject(stream, "bad-proof").ConfigureAwait(false);
                    return;
                }

                var session = new LinkSession(stream, LinkSession.SessionKey(pk.Key, clientNonce, hostNonce), true);
                var connection = new LinkConnection(session, socket, true, endpoint, HandleClientMessage, null, cts.Token);
                LinkConnection? replaced;
                lock (gate)
                {
                    replaced = current;
                    current = connection;
                }

                replaced?.Dispose();
                handedOver = true;
                Interlocked.Decrement(ref handshakes);
                counted = false;
                ClientAddress = address;
                LastConnectedUtc = DateTime.UtcNow;
                LatestStatus = null;
                await session.SendAsync(LinkJson.Build("welcome"), cts.Token).ConfigureAwait(false);
                endpoint.Log($"另一台已連上：{address}{(replaced != null ? "（取代先前那條連線）" : string.Empty)}。");

                await connection.RunAsync().ConfigureAwait(false);
                LastDisconnectedUtc = DateTime.UtcNow;
                lock (gate)
                {
                    if (current == connection)
                        current = null;
                }

                endpoint.Log($"另一台斷線：{address}（{connection.CloseReason}）。");
            }
            catch (LinkProtocolException e)
            {
                endpoint.Log($"握手失敗：{address}（{e.Code}）。");
            }
            catch (OperationCanceledException)
            {
                // 主機收掉了。
            }
            catch (Exception e)
            {
                endpoint.Log($"連線處理失敗：{address}（{e.GetType().Name}）。");
            }
            finally
            {
                if (counted)
                    Interlocked.Decrement(ref handshakes);
                if (!handedOver)
                    socket.Dispose();
            }
        }

        private async Task HandlePairing(System.IO.Stream stream, LinkMessage first, string address)
        {
            var clientPublic = first.Bytes("pub", 32, 256);
            var clientNonce = first.Bytes("cn", LinkCrypto.NonceBytes, LinkCrypto.NonceBytes);
            TaskCompletionSource<bool>? decision = null;
            lock (gate)
            {
                if (pendingDecision == null)
                    decision = pendingDecision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            if (decision == null)
            {
                await Reject(stream, "busy").ConfigureAwait(false);
                return;
            }

            using var ecdh = LinkCrypto.NewEphemeral();
            try
            {
                var hostPublic = ecdh.ExportSubjectPublicKeyInfo();
                var hostNonce = LinkCrypto.Random(LinkCrypto.NonceBytes);
                byte[] key;
                try
                {
                    key = LinkCrypto.DerivePairingKey(ecdh, clientPublic, clientNonce, hostNonce);
                }
                catch (CryptographicException)
                {
                    throw new LinkProtocolException("bad-key");
                }

                var code = LinkCrypto.PairingCode(clientPublic, hostPublic, clientNonce, hostNonce);
                lock (gate)
                {
                    pending = new PairingRequest(address, code, DateTime.UtcNow + SurveyLinkTiming.PairingWait);
                }

                await SendPlain(stream, LinkJson.Build("pair-wait", w =>
                {
                    w.WriteBase64String("pub", hostPublic);
                    w.WriteBase64String("hn", hostNonce);
                })).ConfigureAwait(false);
                endpoint.Log($"{address} 要求配對，代碼 {code}，等主機上按允許或拒絕。");

                bool allowed;
                try
                {
                    allowed = await decision.Task.WaitAsync(SurveyLinkTiming.PairingWait, cts.Token).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    allowed = false;
                }

                if (!allowed)
                {
                    await SendPlain(stream, LinkJson.Build("pair-denied")).ConfigureAwait(false);
                    endpoint.Log($"拒絕了 {address} 的配對（使用者按拒絕或逾時）。");
                    return;
                }

                var keyId = Convert.ToHexString(LinkCrypto.Random(8)).ToLowerInvariant();
                endpoint.SavePairedKey(keyId, key);
                LinkConnection? old;
                lock (gate)
                {
                    old = current;
                    current = null;
                }

                old?.Dispose();
                var mac = LinkCrypto.Mac(key, "pair-ok", Encoding.ASCII.GetBytes(keyId), clientNonce, hostNonce);
                await SendPlain(stream, LinkJson.Build("pair-ok", w =>
                {
                    w.WriteString("keyId", keyId);
                    w.WriteBase64String("mac", mac);
                })).ConfigureAwait(false);
                CryptographicOperations.ZeroMemory(key);
                endpoint.Log($"已和 {address} 配對完成（舊的配對同時作廢）。");
            }
            finally
            {
                lock (gate)
                {
                    pending = null;
                    pendingDecision = null;
                }
            }
        }

        /// <summary>主機畫面上按「允許」或「拒絕」。任何執行緒。</summary>
        internal void ResolvePairing(bool allow)
        {
            TaskCompletionSource<bool>? decision;
            lock (gate)
            {
                decision = pendingDecision;
            }

            decision?.TrySetResult(allow);
        }

        /// <summary>撤銷配對：清掉金鑰並斷開目前連線；對方下次連線會被拒。</summary>
        internal void Revoke()
        {
            endpoint.ClearPairedKey();
            LinkConnection? old;
            lock (gate)
            {
                old = current;
                current = null;
            }

            old?.Dispose();
            LatestStatus = null;
            endpoint.Log("已撤銷配對並斷開連線。");
        }

        private Task<bool> HandleClientMessage(LinkMessage message)
        {
            switch (message.Type)
            {
                case "status":
                    LatestStatus = LinkJson.ReadStatus(message);
                    return Task.FromResult(true);
                case "list-result":
                {
                    var ok = message.Bool("ok");
                    var why = SurveyShare.CleanText(message.Str("why", 2048));
                    var count = message.I32("count");
                    LastListResult = ok ? $"ok:{count}" : "fail:" + why;
                    endpoint.Log(ok ? $"另一台收下清單（{count} 件）。" : $"另一台拒收清單：{why}");
                    return Task.FromResult(true);
                }
                case "cmd-result":
                {
                    var id = message.I32("id");
                    if (!LinkJson.TryKind(message.Str("kind", 16), out var kind))
                        throw new LinkProtocolException("bad-field");
                    var ok = message.Bool("ok");
                    var why = SurveyShare.CleanText(message.Str("why", 2048));
                    LastCommandResult = (id, kind, ok, why, DateTime.UtcNow);
                    endpoint.Log(ok
                        ? $"遠端指令 #{id} {LinkJson.KindName(kind)}：另一台已照做。"
                        : $"遠端指令 #{id} {LinkJson.KindName(kind)}：另一台拒絕（{why}）。");
                    return Task.FromResult(true);
                }
                default:
                    return Task.FromResult(false);
            }
        }

        /// <summary>把清單分批推過去。沒連上時回 false。任何執行緒。</summary>
        internal bool SendList(SharedList list)
        {
            LinkConnection? connection;
            lock (gate)
            {
                connection = current;
            }

            if (connection is not { IsOpen: true })
                return false;

            var chunks = (list.Items.Count + SurveyShare.ListChunkItems - 1) / SurveyShare.ListChunkItems;
            connection.Post(LinkJson.Build("list-begin", w =>
            {
                w.WriteString("listId", list.ListId);
                w.WriteNumber("exportedUtc", new DateTimeOffset(list.ExportedUtc).ToUnixTimeSeconds());
                w.WriteString("sourceKey", SurveyShare.NormaliseSourceKey(list.SourceKey));
                w.WriteNumber("total", list.Items.Count);
                w.WriteNumber("chunks", chunks);
                w.WriteStartArray("excluded");
                foreach (var world in list.ExcludedWorlds)
                    w.WriteNumberValue(world);
                w.WriteEndArray();
                w.WriteStartArray("worlds");
                foreach (var row in list.HostWorlds)
                    LinkJson.WriteWorld(w, row);
                w.WriteEndArray();
            }));
            for (var i = 0; i < chunks; i++)
            {
                var index = i;
                connection.Post(LinkJson.Build("list-chunk", w =>
                {
                    w.WriteString("listId", list.ListId);
                    w.WriteNumber("index", index);
                    w.WriteStartArray("items");
                    var end = Math.Min(list.Items.Count, (index + 1) * SurveyShare.ListChunkItems);
                    for (var j = index * SurveyShare.ListChunkItems; j < end; j++)
                    {
                        w.WriteStartArray();
                        w.WriteNumberValue(list.Items[j].ItemId);
                        w.WriteNumberValue(list.Items[j].Hq ? 1 : 0);
                        w.WriteEndArray();
                    }

                    w.WriteEndArray();
                }));
            }

            connection.Post(LinkJson.Build("list-end", w =>
            {
                w.WriteString("listId", list.ListId);
                w.WriteNumber("chunks", chunks);
            }));
            return true;
        }

        /// <summary>
        /// 送一則指令；回指令編號，沒連上回 -1，對端沒回報認得這個指令（舊版）回 -2、什麼都不送。
        /// 🔴 只由主機畫面上使用者按的按鈕呼叫。
        /// </summary>
        internal int SendCommand(RemoteCommandKind kind, string listId, IReadOnlyCollection<uint> worlds)
        {
            if (kind == RemoteCommandKind.FullStart && LatestStatus is not { SupportsFullMarket: true })
                return -2;

            LinkConnection? connection;
            lock (gate)
            {
                connection = current;
            }

            if (connection is not { IsOpen: true })
                return -1;
            var id = Interlocked.Increment(ref nextCommandId);
            connection.Post(LinkJson.Build("cmd", w =>
            {
                w.WriteNumber("id", id);
                w.WriteString("kind", LinkJson.KindName(kind));
                w.WriteString("listId", listId);
                w.WriteStartArray("worlds");
                foreach (var world in worlds)
                    w.WriteNumberValue(world);
                w.WriteEndArray();
            }));
            endpoint.Log($"送出遠端指令 #{id} {LinkJson.KindName(kind)}（清單 {(listId.Length > 0 ? listId : "-")}，指定世界 {worlds.Count} 個）。");
            return id;
        }

        private static Task SendPlain(System.IO.Stream stream, byte[] json)
            => LinkFrames.WriteAsync(stream, json, CancellationToken.None);

        private static async Task Reject(System.IO.Stream stream, string why)
        {
            try
            {
                await SendPlain(stream, LinkJson.Build("reject", w => w.WriteString("why", why))).ConfigureAwait(false);
            }
            catch
            {
                // 對方已經斷了。
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            cts.Cancel();
            try
            {
                listener?.Stop();
            }
            catch
            {
                // 停 listen 失敗沒有後續動作。
            }

            ResolvePairing(false);
            LinkConnection? old;
            lock (gate)
            {
                old = current;
                current = null;
            }

            old?.Dispose();
            try
            {
                Task.WaitAll([.. tasks.Keys], SurveyLinkTiming.DisposeWait);
            }
            catch
            {
                // 收尾中的例外各自已記。
            }

            endpoint.Log($"停止 listen，埠 {Port}。");
        }
    }

    internal enum ClientLinkState
    {
        Idle = 0,
        Connecting = 1,
        Connected = 2,
        WaitingRetry = 3,
        NeedsPairing = 4,
        Pairing = 5,
        Rejected = 6,
    }

    /// <summary>
    /// 代掃那一台：主動連主機。連不上就退避重試（10 秒起、上限 5 分鐘）；被主機拒絕（撤銷配對、證明不符）就停下等使用者。
    /// 🔴 連線建立本身不會讓任何事開始：指令只在主機那邊有人按了按鈕才會來，而且到了這台還要過同意與遊戲狀態的閘門。
    /// </summary>
    internal sealed class SurveyLinkClient : IDisposable
    {
        private readonly ISurveyLinkClientEndpoint endpoint;
        private readonly IPEndPoint hostEndpoint;
        private readonly CancellationTokenSource cts = new();
        private readonly SemaphoreSlim wake = new(0);
        private readonly object gate = new();
        private Task? loop;
        private LinkConnection? current;
        private volatile bool pairRequested;
        private int disposed;
        private ClientStatus? lastSentStatus;
        private DateTime lastStatusSentUtc = DateTime.MinValue;

        // 正在組的清單（只在讀迴圈上碰）。
        private SharedList? assembling;
        private int assemblingTotal;
        private int assemblingNextChunk;
        private HashSet<(uint, bool)>? assemblingSeen;
        private int assemblingSkipped;

        internal SurveyLinkClient(ISurveyLinkClientEndpoint endpoint, IPEndPoint hostEndpoint)
        {
            this.endpoint = endpoint;
            this.hostEndpoint = hostEndpoint;
        }

        internal ClientLinkState State { get; private set; }
        internal string Detail { get; private set; } = string.Empty;
        internal string PairCode { get; private set; } = string.Empty;
        internal DateTime NextRetryUtc { get; private set; } = DateTime.MinValue;
        internal DateTime LastConnectedUtc { get; private set; } = DateTime.MinValue;
        internal IPEndPoint Host => hostEndpoint;

        internal void Start() => loop = Task.Run(RunLoop);

        /// <summary>下一次連線改做配對。任何執行緒。</summary>
        internal void RequestPairing()
        {
            pairRequested = true;
            Wake();
        }

        /// <summary>被拒或等待中時立刻再試一次。</summary>
        internal void Wake() => wake.Release();

        private void SetState(ClientLinkState state, string detail)
        {
            var changed = state != State || detail != Detail;
            State = state;
            Detail = detail;
            if (changed && state != ClientLinkState.Connecting)
                endpoint.Log($"連線狀態：{state}{(detail.Length > 0 ? "（" + detail + "）" : string.Empty)}。");
        }

        private async Task RunLoop()
        {
            var backoff = SurveyLinkTiming.RetryMin;
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    if (pairRequested)
                    {
                        pairRequested = false;
                        if (await PairOnce().ConfigureAwait(false))
                        {
                            backoff = SurveyLinkTiming.RetryMin;
                            continue;
                        }

                        await WaitForWake(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                        continue;
                    }

                    if (endpoint.PairedKey() is not { } key)
                    {
                        SetState(ClientLinkState.NeedsPairing, string.Empty);
                        await WaitForWake(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                        continue;
                    }

                    var outcome = await ConnectOnce(key).ConfigureAwait(false);
                    switch (outcome)
                    {
                        case ConnectOutcome.Ran:
                            backoff = SurveyLinkTiming.RetryMin;
                            continue;
                        case ConnectOutcome.Rejected:
                            await WaitForWake(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                            continue;
                        default:
                            NextRetryUtc = DateTime.UtcNow + backoff;
                            SetState(ClientLinkState.WaitingRetry, "no-response");
                            await WaitForWake(backoff).ConfigureAwait(false);
                            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, SurveyLinkTiming.RetryMax.Ticks));
                            continue;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 收掉了。
            }
            catch (Exception e)
            {
                endpoint.Log($"連線迴圈意外結束：{e.GetType().Name}。");
            }
        }

        private async Task WaitForWake(TimeSpan timeout)
        {
            await wake.WaitAsync(timeout, cts.Token).ConfigureAwait(false);
            while (wake.CurrentCount > 0)
                await wake.WaitAsync(0).ConfigureAwait(false);
        }

        private enum ConnectOutcome
        {
            Unreachable,
            Ran,
            Rejected,
        }

        private async Task<TcpClient?> Open()
        {
            var socket = new TcpClient(System.Net.Sockets.AddressFamily.InterNetwork) { NoDelay = true };
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                timeout.CancelAfter(SurveyLinkTiming.HandshakeTimeout);
                await socket.ConnectAsync(hostEndpoint, timeout.Token).ConfigureAwait(false);
                return socket;
            }
            catch (Exception) when (!cts.IsCancellationRequested)
            {
                socket.Dispose();
                return null;
            }
        }

        private async Task<ConnectOutcome> ConnectOnce((string KeyId, byte[] Key) key)
        {
            SetState(ClientLinkState.Connecting, string.Empty);
            var socket = await Open().ConfigureAwait(false);
            if (socket == null)
                return ConnectOutcome.Unreachable;

            var handedOver = false;
            try
            {
                var stream = socket.GetStream();
                var clientNonce = LinkCrypto.Random(LinkCrypto.NonceBytes);
                await LinkFrames.WriteAsync(stream, LinkJson.Build("hello", w =>
                {
                    w.WriteNumber("v", 1);
                    w.WriteString("keyId", key.KeyId);
                    w.WriteBase64String("cn", clientNonce);
                }), cts.Token).ConfigureAwait(false);

                var reply = LinkMessage.Parse(await LinkFrames.ReadAsync(stream, LinkFrames.MaxPreAuthFrame,
                    SurveyLinkTiming.HandshakeTimeout, cts.Token).ConfigureAwait(false));
                if (reply.Type == "reject")
                {
                    SetState(ClientLinkState.Rejected, SurveyShare.CleanText(reply.Str("why", 32)));
                    return ConnectOutcome.Rejected;
                }

                if (reply.Type != "challenge")
                    return ConnectOutcome.Unreachable;
                var hostNonce = reply.Bytes("hn", LinkCrypto.NonceBytes, LinkCrypto.NonceBytes);
                if (!LinkCrypto.Equal(reply.Bytes("hp", 32, 32), LinkCrypto.Mac(key.Key, "host-proof", clientNonce, hostNonce)))
                {
                    endpoint.Log("認證失敗：主機的證明不符（可能不是原本配對的那一台）。");
                    SetState(ClientLinkState.Rejected, "bad-host-proof");
                    return ConnectOutcome.Rejected;
                }

                await LinkFrames.WriteAsync(stream, LinkJson.Build("auth",
                        w => w.WriteBase64String("cp", LinkCrypto.Mac(key.Key, "client-proof", clientNonce, hostNonce))),
                    cts.Token).ConfigureAwait(false);

                var session = new LinkSession(stream, LinkSession.SessionKey(key.Key, clientNonce, hostNonce), false);
                var firstFrame = await LinkFrames.ReadAsync(stream, LinkFrames.MaxFrame, SurveyLinkTiming.HandshakeTimeout,
                    cts.Token).ConfigureAwait(false);
                if (TryPlainReject(firstFrame, out var rejectWhy))
                {
                    SetState(ClientLinkState.Rejected, rejectWhy);
                    session.Dispose();
                    return ConnectOutcome.Rejected;
                }

                if (session.Open(firstFrame).Type != "welcome")
                    throw new LinkProtocolException("no-welcome");

                var connection = new LinkConnection(session, socket, false, endpoint, HandleHostMessage, StatusTick, cts.Token);
                lock (gate)
                {
                    current = connection;
                }

                handedOver = true;
                lastSentStatus = null;
                assembling = null;
                LastConnectedUtc = DateTime.UtcNow;
                SetState(ClientLinkState.Connected, string.Empty);
                await connection.RunAsync().ConfigureAwait(false);
                lock (gate)
                {
                    if (current == connection)
                        current = null;
                }

                SetState(ClientLinkState.WaitingRetry, "disconnected:" + connection.CloseReason);
                return ConnectOutcome.Ran;
            }
            catch (LinkProtocolException e)
            {
                endpoint.Log($"握手失敗（{e.Code}）。");
                return ConnectOutcome.Unreachable;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return ConnectOutcome.Unreachable;
            }
            finally
            {
                if (!handedOver)
                    socket.Dispose();
            }
        }

        /// <summary>認證後主機若改送一則未加 MAC 的拒絕。MAC 的第一個位元組也可能剛好是 '{'，所以解析失敗就當不是。</summary>
        private static bool TryPlainReject(byte[] frame, out string why)
        {
            why = string.Empty;
            if (frame.Length == 0 || frame[0] != (byte)'{')
                return false;
            try
            {
                var plain = LinkMessage.Parse(frame);
                if (plain.Type != "reject")
                    return false;
                why = SurveyShare.CleanText(plain.Str("why", 32));
                return true;
            }
            catch (LinkProtocolException)
            {
                return false;
            }
        }

        private async Task<bool> PairOnce()
        {
            SetState(ClientLinkState.Connecting, "pairing");
            var socket = await Open().ConfigureAwait(false);
            if (socket == null)
            {
                SetState(ClientLinkState.WaitingRetry, "no-response");
                return false;
            }

            try
            {
                using var ecdh = LinkCrypto.NewEphemeral();
                var stream = socket.GetStream();
                var clientPublic = ecdh.ExportSubjectPublicKeyInfo();
                var clientNonce = LinkCrypto.Random(LinkCrypto.NonceBytes);
                await LinkFrames.WriteAsync(stream, LinkJson.Build("pair", w =>
                {
                    w.WriteNumber("v", 1);
                    w.WriteBase64String("pub", clientPublic);
                    w.WriteBase64String("cn", clientNonce);
                }), cts.Token).ConfigureAwait(false);

                var wait = LinkMessage.Parse(await LinkFrames.ReadAsync(stream, LinkFrames.MaxPreAuthFrame,
                    SurveyLinkTiming.HandshakeTimeout, cts.Token).ConfigureAwait(false));
                if (wait.Type != "pair-wait")
                {
                    SetState(ClientLinkState.Rejected,
                        wait.Type == "reject" ? SurveyShare.CleanText(wait.Str("why", 32)) : wait.Type);
                    return false;
                }

                var hostPublic = wait.Bytes("pub", 32, 256);
                var hostNonce = wait.Bytes("hn", LinkCrypto.NonceBytes, LinkCrypto.NonceBytes);
                byte[] key;
                try
                {
                    key = LinkCrypto.DerivePairingKey(ecdh, hostPublic, clientNonce, hostNonce);
                }
                catch (CryptographicException)
                {
                    throw new LinkProtocolException("bad-key");
                }

                PairCode = LinkCrypto.PairingCode(clientPublic, hostPublic, clientNonce, hostNonce);
                SetState(ClientLinkState.Pairing, string.Empty);

                var answer = LinkMessage.Parse(await LinkFrames.ReadAsync(stream, LinkFrames.MaxPreAuthFrame,
                    SurveyLinkTiming.PairingWait + SurveyLinkTiming.HandshakeTimeout, cts.Token).ConfigureAwait(false));
                PairCode = string.Empty;
                if (answer.Type != "pair-ok")
                {
                    SetState(ClientLinkState.Rejected, "pair-denied");
                    return false;
                }

                var keyId = answer.Str("keyId", SurveyShare.MaxIdLength);
                if (!SurveyShare.IsSafeId(keyId)
                    || !LinkCrypto.Equal(answer.Bytes("mac", 32, 32),
                        LinkCrypto.Mac(key, "pair-ok", Encoding.ASCII.GetBytes(keyId), clientNonce, hostNonce)))
                {
                    endpoint.Log("配對失敗：主機的確認不符（可能有人在中間）。");
                    SetState(ClientLinkState.Rejected, "pair-mac");
                    return false;
                }

                endpoint.SavePairedKey(keyId, key);
                CryptographicOperations.ZeroMemory(key);
                endpoint.Log("配對完成，接著用新金鑰連線。");
                return true;
            }
            catch (LinkProtocolException e)
            {
                PairCode = string.Empty;
                SetState(ClientLinkState.Rejected, "pair-" + e.Code);
                return false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                PairCode = string.Empty;
                SetState(ClientLinkState.WaitingRetry, e.GetType().Name);
                return false;
            }
            finally
            {
                socket.Dispose();
            }
        }

        private byte[]? StatusTick()
        {
            var status = endpoint.CurrentStatus();
            var now = DateTime.UtcNow;
            var due = now - lastStatusSentUtc > SurveyLinkTiming.Heartbeat
                      || (status != lastSentStatus && now - lastStatusSentUtc > SurveyLinkTiming.StatusMinInterval);
            if (!due)
                return null;
            lastSentStatus = status;
            lastStatusSentUtc = now;
            return LinkJson.Build("status", w => LinkJson.WriteStatus(w, status));
        }

        private async Task<bool> HandleHostMessage(LinkMessage message)
        {
            switch (message.Type)
            {
                case "list-begin":
                    BeginList(message);
                    return true;
                case "list-chunk":
                    AddChunk(message);
                    return true;
                case "list-end":
                    await EndList(message).ConfigureAwait(false);
                    return true;
                case "cmd":
                    await HandleCommand(message).ConfigureAwait(false);
                    return true;
                default:
                    return false;
            }
        }

        private void BeginList(LinkMessage m)
        {
            var listId = m.Str("listId", SurveyShare.MaxIdLength);
            var total = m.I32("total");
            var exported = m.I64("exportedUtc");
            if (!SurveyShare.IsSafeId(listId) || total < 0 || total > SurveyShare.MaxListItems
                || exported < 0 || exported > 4_102_444_800)
                throw new LinkProtocolException("bad-list");
            var excluded = new HashSet<uint>();
            foreach (var e in m.Array("excluded", SurveyShare.MaxWorldsPerMessage).EnumerateArray())
            {
                if (!e.TryGetUInt32(out var id) || id == 0)
                    throw new LinkProtocolException("bad-list");
                excluded.Add(id);
            }

            var worlds = new List<SurveyWorldRow>();
            var now = DateTime.UtcNow;
            foreach (var e in m.Array("worlds", SurveyShare.MaxWorldsPerMessage).EnumerateArray())
            {
                if (LinkJson.TryReadWorld(e, "imported", out var row) && SurveyShare.Validate(row, endpoint.KnownWorld, now) == null)
                    worlds.Add(row);
            }

            assembling = new SharedList
            {
                ListId = listId,
                ExportedUtc = DateTimeOffset.FromUnixTimeSeconds(exported).UtcDateTime,
                SourceKey = SurveyShare.NormaliseSourceKey(m.Str("sourceKey", 32)),
                Items = new List<SharedListItem>(Math.Min(total, SurveyShare.MaxListItems)),
                ExcludedWorlds = excluded,
                HostWorlds = worlds,
            };
            assemblingTotal = total;
            assemblingNextChunk = 0;
            assemblingSeen = [];
            assemblingSkipped = 0;
        }

        private void AddChunk(LinkMessage m)
        {
            var list = assembling ?? throw new LinkProtocolException("bad-list");
            if (m.Str("listId", SurveyShare.MaxIdLength) != list.ListId || m.I32("index") != assemblingNextChunk)
                throw new LinkProtocolException("bad-list");
            assemblingNextChunk++;
            foreach (var e in m.Array("items", SurveyShare.ListChunkItems).EnumerateArray())
            {
                if (list.Items.Count + assemblingSkipped >= assemblingTotal)
                    throw new LinkProtocolException("bad-list");
                if (e.ValueKind != System.Text.Json.JsonValueKind.Array || e.GetArrayLength() != 2)
                {
                    assemblingSkipped++;
                    continue;
                }

                var pair = e.EnumerateArray();
                pair.MoveNext();
                var idOk = pair.Current.TryGetUInt32(out var itemId);
                pair.MoveNext();
                var hqOk = pair.Current.TryGetInt32(out var hq) && hq is 0 or 1;
                if (!idOk || !hqOk || itemId == 0 || (endpoint.KnownItem != null && !endpoint.KnownItem(itemId))
                    || !assemblingSeen!.Add((itemId, hq == 1)))
                {
                    assemblingSkipped++;
                    continue;
                }

                list.Items.Add(new SharedListItem(itemId, hq == 1));
            }
        }

        private async Task EndList(LinkMessage m)
        {
            var list = assembling ?? throw new LinkProtocolException("bad-list");
            assembling = null;
            if (m.Str("listId", SurveyShare.MaxIdLength) != list.ListId || m.I32("chunks") != assemblingNextChunk)
                throw new LinkProtocolException("bad-list");

            string? why;
            if (list.Items.Count == 0)
                why = "empty-list";
            else
            {
                if (assemblingSkipped > 0)
                    endpoint.Log($"收到的清單 {list.ListId} 有 {assemblingSkipped} 件不合格，已跳過。");
                why = await endpoint.HandleList(list).ConfigureAwait(false);
            }

            Post(LinkJson.Build("list-result", w =>
            {
                w.WriteString("listId", list.ListId);
                w.WriteBoolean("ok", why == null);
                w.WriteString("why", why ?? string.Empty);
                w.WriteNumber("count", list.Items.Count);
            }));
        }

        private async Task HandleCommand(LinkMessage m)
        {
            var id = m.I32("id");
            if (!LinkJson.TryKind(m.Str("kind", 16), out var kind))
                throw new LinkProtocolException("bad-field");
            var listId = m.Str("listId", SurveyShare.MaxIdLength);
            if (listId.Length > 0 && !SurveyShare.IsSafeId(listId))
                throw new LinkProtocolException("bad-field");
            var worlds = new List<uint>();
            foreach (var e in m.Array("worlds", SurveyShare.MaxWorldsPerMessage).EnumerateArray())
            {
                if (!e.TryGetUInt32(out var world) || world == 0)
                    throw new LinkProtocolException("bad-field");
                worlds.Add(world);
            }

            var command = new RemoteCommand(id, kind, listId, worlds);
            endpoint.Log($"收到遠端指令 #{id} {LinkJson.KindName(kind)}。");
            var why = await endpoint.HandleCommand(command).ConfigureAwait(false);
            endpoint.Log(why == null
                ? $"遠端指令 #{id} {LinkJson.KindName(kind)}：已照做。"
                : $"遠端指令 #{id} {LinkJson.KindName(kind)}：拒絕（{why}）。");
            Post(LinkJson.Build("cmd-result", w =>
            {
                w.WriteNumber("id", id);
                w.WriteString("kind", LinkJson.KindName(kind));
                w.WriteBoolean("ok", why == null);
                w.WriteString("why", why ?? string.Empty);
            }));
        }

        private void Post(byte[] json)
        {
            LinkConnection? connection;
            lock (gate)
            {
                connection = current;
            }

            connection?.Post(json);
        }

        internal bool IsConnected
        {
            get
            {
                lock (gate)
                {
                    return current is { IsOpen: true };
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            cts.Cancel();
            LinkConnection? old;
            lock (gate)
            {
                old = current;
                current = null;
            }

            old?.Dispose();
            try
            {
                loop?.Wait(SurveyLinkTiming.DisposeWait);
            }
            catch
            {
                // 收尾中的例外各自已記。
            }
        }
    }
}
