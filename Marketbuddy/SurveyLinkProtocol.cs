using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Marketbuddy
{
    // 本檔契約：不引用 Dalamud（離線測試用 loopback 真 socket 直接編它）。
    // 線上格式：4 位元組大端長度＋內容。認證前內容是 JSON；認證後是 32 位元組 HMAC-SHA256＋JSON，
    // MAC 綁「方向＋這個方向的序號」，序號只能 +1（重放、重排、截斷都會對不上而斷線）。
    // JSON 只認明確的欄位與型別，不做多型反序列化；任何不認得的東西一律斷線。

    internal sealed class LinkProtocolException(string code) : Exception(code)
    {
        internal string Code { get; } = code;
    }

    internal static class LinkFrames
    {
        internal const int MaxPreAuthFrame = 64 * 1024;

        /// <summary>單則上限。清單分批、觀測分批，所以總量不受這個限制。</summary>
        internal const int MaxFrame = 4 * 1024 * 1024;

        internal static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            if (payload.Length > MaxFrame)
                throw new LinkProtocolException("too-large");
            var header = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
            await stream.WriteAsync(header, ct).ConfigureAwait(false);
            await stream.WriteAsync(payload, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        /// <summary>讀一則。超過 <paramref name="max"/> 立刻擲（不讀內容）；讀到一半斷線擲 <c>truncated</c>。</summary>
        internal static async Task<byte[]> ReadAsync(Stream stream, int max, TimeSpan timeout, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                var header = new byte[4];
                await ReadExactAsync(stream, header, cts.Token).ConfigureAwait(false);
                var length = BinaryPrimitives.ReadInt32BigEndian(header);
                if (length < 0 || length > max)
                    throw new LinkProtocolException("too-large");
                var payload = new byte[length];
                await ReadExactAsync(stream, payload, cts.Token).ConfigureAwait(false);
                return payload;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new LinkProtocolException("timeout");
            }
        }

        private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
                if (read <= 0)
                    throw new LinkProtocolException(offset == 0 && buffer.Length == 4 ? "closed" : "truncated");
                offset += read;
            }
        }
    }

    internal static class LinkCrypto
    {
        internal const int KeyBytes = 32;
        internal const int NonceBytes = 16;
        private static readonly byte[] KeyInfo = Encoding.ASCII.GetBytes("marketbuddy-survey-link-v1");

        internal static byte[] Random(int count) => RandomNumberGenerator.GetBytes(count);

        internal static byte[] Mac(byte[] key, string label, params byte[][] parts)
        {
            using var hmac = new HMACSHA256(key);
            var labelBytes = Encoding.ASCII.GetBytes(label);
            hmac.TransformBlock(labelBytes, 0, labelBytes.Length, null, 0);
            foreach (var part in parts)
                hmac.TransformBlock(part, 0, part.Length, null, 0);
            hmac.TransformFinalBlock([], 0, 0);
            return hmac.Hash!;
        }

        internal static bool Equal(byte[]? a, byte[]? b)
            => a != null && b != null && a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);

        /// <summary>配對時兩邊畫面上對照的 6 位數（綁兩邊的公開金鑰與亂數；中間人換掉金鑰，代碼就對不上）。</summary>
        internal static string PairingCode(byte[] clientPublic, byte[] hostPublic, byte[] clientNonce, byte[] hostNonce)
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            sha.AppendData(Encoding.ASCII.GetBytes("mb-pair-code"));
            sha.AppendData(clientPublic);
            sha.AppendData(hostPublic);
            sha.AppendData(clientNonce);
            sha.AppendData(hostNonce);
            var hash = sha.GetHashAndReset();
            var value = BinaryPrimitives.ReadUInt32BigEndian(hash) % 1_000_000u;
            return value.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>ECDH（P-256）共享祕密＋HKDF 導出長期金鑰；金鑰本身從來不上線。</summary>
        internal static byte[] DerivePairingKey(ECDiffieHellman mine, byte[] peerPublic, byte[] clientNonce, byte[] hostNonce)
        {
            using var peer = ECDiffieHellman.Create();
            peer.ImportSubjectPublicKeyInfo(peerPublic, out _);
            var shared = mine.DeriveRawSecretAgreement(peer.PublicKey);
            try
            {
                var salt = new byte[clientNonce.Length + hostNonce.Length];
                clientNonce.CopyTo(salt, 0);
                hostNonce.CopyTo(salt, clientNonce.Length);
                return HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, KeyBytes, salt, KeyInfo);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(shared);
            }
        }

        internal static ECDiffieHellman NewEphemeral() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    }

    /// <summary>主機看得到的另一台狀態（全部是顯示用；字串收進來時已清過）。</summary>
    internal sealed record ClientStatus(
        bool LoggedIn,
        uint WorldId,
        string WorldName,
        string State,
        int Processed,
        int Total,
        bool TourArmed,
        bool RemoteControlled,
        string StatusText,
        string TourStatus,
        string StartBlock,
        bool Consent,
        string ListId,
        int Outbox,
        int TourStep,
        int TourMax,
        int Caps = 0,
        string Mode = "",
        int FullWorld = 0,
        int FullWorlds = 0,
        int FullPending = -1,
        bool FullPlanned = false,
        string Halt = "",
        long Gil = -1)
    {
        /// <summary>這台認得「全市場掃描」指令。舊版不送 <c>caps</c>＝0。</summary>
        internal const int CapFullMarket = 1;

        internal bool SupportsFullMarket => (Caps & CapFullMarket) != 0;

        internal static readonly ClientStatus Empty =
            new(false, 0, string.Empty, "idle", 0, 0, false, false, string.Empty, string.Empty, string.Empty, false,
                string.Empty, 0, 0, 0);
    }

    internal enum RemoteCommandKind
    {
        Start = 0,
        Pause = 1,
        Resume = 2,
        Stop = 3,

        /// <summary>全市場掃描一輪。🔴 舊版對端不認得這個名字會擲 bad-field 斷線（不會照做），所以主機只對回報過 <see cref="ClientStatus.CapFullMarket"/> 的對端送。</summary>
        FullStart = 4,
    }

    /// <summary>主機送過來的巡檢指令。🔴 只有這五種；沒有任何上架、改價、下架、賣商店、重掛的指令存在。</summary>
    internal sealed record RemoteCommand(int Id, RemoteCommandKind Kind, string ListId, IReadOnlyList<uint> Worlds);

    /// <summary>一則已驗證過 MAC 的訊息。</summary>
    internal sealed class LinkMessage
    {
        private static readonly HashSet<string> KnownTypes = new(StringComparer.Ordinal)
        {
            "hello", "pair", "challenge", "auth", "reject", "pair-wait", "pair-ok", "pair-denied",
            "welcome", "ping", "status", "list-begin", "list-chunk", "list-end", "list-result",
            "cmd", "cmd-result", "obs", "obs-ack",
        };

        internal bool Has(string name) => Root.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null;

        public required string Type;
        public required JsonElement Root;

        internal static LinkMessage Parse(byte[] json)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 6 });
            }
            catch (Exception)
            {
                throw new LinkProtocolException("bad-json");
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.String
                    || t.GetString() is not { } type || !KnownTypes.Contains(type))
                    throw new LinkProtocolException("bad-type");
                return new LinkMessage { Type = type, Root = root.Clone() };
            }
        }

        internal string Str(string name, int maxLength = 256)
        {
            if (!Root.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
                throw new LinkProtocolException("bad-field");
            var s = v.GetString() ?? string.Empty;
            if (s.Length > maxLength)
                throw new LinkProtocolException("bad-field");
            return s;
        }

        internal long I64(string name)
        {
            if (!Root.TryGetProperty(name, out var v) || !v.TryGetInt64(out var n))
                throw new LinkProtocolException("bad-field");
            return n;
        }

        internal int I32(string name)
        {
            if (!Root.TryGetProperty(name, out var v) || !v.TryGetInt32(out var n))
                throw new LinkProtocolException("bad-field");
            return n;
        }

        internal bool Bool(string name)
        {
            if (!Root.TryGetProperty(name, out var v) || v.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new LinkProtocolException("bad-field");
            return v.GetBoolean();
        }

        internal byte[] Bytes(string name, int minLength, int maxLength)
        {
            var s = Str(name, ((maxLength + 2) / 3 * 4) + 4);
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(s);
            }
            catch (FormatException)
            {
                throw new LinkProtocolException("bad-field");
            }

            if (bytes.Length < minLength || bytes.Length > maxLength)
                throw new LinkProtocolException("bad-field");
            return bytes;
        }

        internal JsonElement Array(string name, int maxLength)
        {
            if (!Root.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array || v.GetArrayLength() > maxLength)
                throw new LinkProtocolException("bad-field");
            return v;
        }
    }

    internal static class LinkJson
    {
        internal static byte[] Build(string type, Action<Utf8JsonWriter>? body = null)
        {
            var buffer = new ArrayBufferWriter<byte>(256);
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("t", type);
                body?.Invoke(writer);
                writer.WriteEndObject();
            }

            return buffer.WrittenSpan.ToArray();
        }

        internal static void WriteObservation(Utf8JsonWriter w, MarketObservation o)
        {
            w.WriteStartArray();
            w.WriteNumberValue(new DateTimeOffset(DateTime.SpecifyKind(o.AtUtc, DateTimeKind.Utc)).ToUnixTimeSeconds());
            w.WriteNumberValue(o.WorldId);
            w.WriteStringValue(o.WorldName);
            w.WriteNumberValue(o.ItemId);
            w.WriteNumberValue(o.LowestNq);
            w.WriteNumberValue(o.LowestNqRetainer);
            w.WriteNumberValue(o.LowestHq);
            w.WriteNumberValue(o.LowestHqRetainer);
            w.WriteNumberValue(o.ListingCount);
            w.WriteStringValue(o.Source);
            w.WriteNumberValue(o.GateIntervalMs);
            w.WriteStringValue(o.Verdict);
            w.WriteEndArray();
        }

        internal static bool TryReadObservation(JsonElement e, out MarketObservation o)
        {
            o = default;
            if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() != 12)
                return false;
            var a = e.EnumerateArray().GetEnumerator();
            var v = new JsonElement[12];
            for (var i = 0; i < 12 && a.MoveNext(); i++)
                v[i] = a.Current;
            if (!v[0].TryGetInt64(out var unix) || unix < 0 || unix > 4_102_444_800
                || !v[1].TryGetUInt32(out var worldId)
                || v[2].ValueKind != JsonValueKind.String
                || !v[3].TryGetUInt32(out var itemId)
                || !v[4].TryGetInt64(out var lowNq)
                || !v[5].TryGetUInt64(out var nqRetainer)
                || !v[6].TryGetInt64(out var lowHq)
                || !v[7].TryGetUInt64(out var hqRetainer)
                || !v[8].TryGetInt32(out var count)
                || v[9].ValueKind != JsonValueKind.String
                || !v[10].TryGetInt32(out var gate)
                || v[11].ValueKind != JsonValueKind.String)
                return false;
            o = new MarketObservation(DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime, worldId,
                v[2].GetString() ?? string.Empty, itemId, lowNq, nqRetainer, lowHq, hqRetainer, count,
                v[9].GetString() ?? string.Empty, gate, v[11].GetString() ?? string.Empty);
            return true;
        }

        /// <summary>
        /// 成交摘要走觀測訊息裡另一個<b>可選</b>的平行陣列 <c>sales</c>（第 i 個對應 <c>rows</c> 第 i 個）：
        /// 觀測本身維持 12 欄，舊對端只認 12 欄、也不讀 <c>sales</c>，照樣收得下。不知道寫 null。
        /// </summary>
        internal static void WriteSales(Utf8JsonWriter w, in SaleSummary s)
        {
            if (!s.Known)
            {
                w.WriteNullValue();
                return;
            }

            w.WriteStartArray();
            w.WriteNumberValue(s.LastNq);
            w.WriteNumberValue(UnixOrZero(s.LastNqUtc));
            w.WriteNumberValue(s.LastHq);
            w.WriteNumberValue(UnixOrZero(s.LastHqUtc));
            w.WriteNumberValue(s.RecentCount);
            w.WriteNumberValue(s.PageCount);
            w.WriteEndArray();
        }

        /// <summary>讀不懂的一律當「不知道」（回 default），不拖累同一筆觀測。</summary>
        internal static SaleSummary ReadSales(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() != 6)
                return default;
            var v = new JsonElement[6];
            var i = 0;
            foreach (var item in e.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number)
                    return default;
                v[i++] = item;
            }

            if (!v[0].TryGetInt64(out var nq) || !v[1].TryGetInt64(out var nqAt) || nqAt < 0 || nqAt > 4_102_444_800
                || !v[2].TryGetInt64(out var hq) || !v[3].TryGetInt64(out var hqAt) || hqAt < 0 || hqAt > 4_102_444_800
                || !v[4].TryGetInt32(out var recent) || !v[5].TryGetInt32(out var page))
                return default;
            return new SaleSummary(true, nq, FromUnixOrMin(nqAt), hq, FromUnixOrMin(hqAt), recent, page);
        }

        internal static long UnixOrZero(DateTime utc)
            => utc == DateTime.MinValue ? 0 : new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

        internal static DateTime FromUnixOrMin(long unix)
            => unix <= 0 ? DateTime.MinValue : DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;

        internal static void WriteWorld(Utf8JsonWriter w, SurveyWorldRow row)
        {
            w.WriteStartArray();
            w.WriteNumberValue(new DateTimeOffset(DateTime.SpecifyKind(row.AtUtc, DateTimeKind.Utc)).ToUnixTimeSeconds());
            w.WriteNumberValue(row.WorldId);
            w.WriteStringValue(row.WorldName);
            w.WriteNumberValue(row.PlannedTotal);
            w.WriteNumberValue(row.Surveyed);
            w.WriteNumberValue(row.Completed ? 1 : 0);
            w.WriteEndArray();
        }

        /// <summary>🔑 ContentId 一律 0（＝不知道）：線上不帶角色身分，併進對方也就不會被判成「別的角色的紀錄」。</summary>
        internal static bool TryReadWorld(JsonElement e, string sourceKey, out SurveyWorldRow row)
        {
            row = default;
            if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() != 6)
                return false;
            var v = new JsonElement[6];
            var i = 0;
            foreach (var item in e.EnumerateArray())
                v[i++] = item;
            if (!v[0].TryGetInt64(out var unix) || unix < 0 || unix > 4_102_444_800
                || !v[1].TryGetUInt32(out var worldId)
                || v[2].ValueKind != JsonValueKind.String
                || !v[3].TryGetInt32(out var planned)
                || !v[4].TryGetInt32(out var surveyed)
                || !v[5].TryGetInt32(out var completed) || completed is not (0 or 1))
                return false;
            row = new SurveyWorldRow(DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime, worldId,
                v[2].GetString() ?? string.Empty, 0, sourceKey, planned, surveyed, completed == 1);
            return true;
        }

        internal static void WriteStatus(Utf8JsonWriter w, ClientStatus s)
        {
            w.WriteBoolean("loggedIn", s.LoggedIn);
            w.WriteNumber("worldId", s.WorldId);
            w.WriteString("world", s.WorldName);
            w.WriteString("state", s.State);
            w.WriteNumber("processed", s.Processed);
            w.WriteNumber("total", s.Total);
            w.WriteBoolean("armed", s.TourArmed);
            w.WriteBoolean("remote", s.RemoteControlled);
            w.WriteString("text", s.StatusText);
            w.WriteString("tour", s.TourStatus);
            w.WriteString("block", s.StartBlock);
            w.WriteBoolean("consent", s.Consent);
            w.WriteString("listId", s.ListId);
            w.WriteNumber("outbox", s.Outbox);
            w.WriteNumber("step", s.TourStep);
            w.WriteNumber("max", s.TourMax);
            w.WriteNumber("caps", s.Caps);
            w.WriteString("mode", s.Mode);
            w.WriteNumber("fullWorld", s.FullWorld);
            w.WriteNumber("fullWorlds", s.FullWorlds);
            w.WriteNumber("fullPending", s.FullPending);
            w.WriteBoolean("fullPlanned", s.FullPlanned);
            w.WriteString("halt", s.Halt);
            w.WriteNumber("gil", s.Gil);
        }

        internal static ClientStatus ReadStatus(LinkMessage m)
        {
            var state = m.Str("state", 16);
            if (state is not ("idle" or "preparing" or "running" or "paused"))
                throw new LinkProtocolException("bad-field");
            var listId = m.Str("listId", SurveyShare.MaxIdLength);
            if (listId.Length > 0 && !SurveyShare.IsSafeId(listId))
                throw new LinkProtocolException("bad-field");
            return new ClientStatus(
                m.Bool("loggedIn"),
                (uint)Math.Clamp(m.I64("worldId"), 0, uint.MaxValue),
                SurveyShare.CleanText(m.Str("world", SurveyShare.MaxWorldNameLength)),
                state,
                Math.Max(0, m.I32("processed")),
                Math.Max(0, m.I32("total")),
                m.Bool("armed"),
                m.Bool("remote"),
                SurveyShare.CleanText(m.Str("text", 2048)),
                SurveyShare.CleanText(m.Str("tour", 2048)),
                SurveyShare.CleanText(m.Str("block", 2048)),
                m.Bool("consent"),
                listId,
                Math.Max(0, m.I32("outbox")),
                Math.Clamp(m.I32("step"), 0, 64),
                Math.Clamp(m.I32("max"), 0, 64),
                m.Has("caps") ? m.I32("caps") : 0,
                m.Has("mode") ? ReadMode(m.Str("mode", 16)) : string.Empty,
                m.Has("fullWorld") ? Math.Clamp(m.I32("fullWorld"), 0, 64) : 0,
                m.Has("fullWorlds") ? Math.Clamp(m.I32("fullWorlds"), 0, 64) : 0,
                m.Has("fullPending") ? Math.Max(-1, m.I32("fullPending")) : -1,
                m.Has("fullPlanned") && m.Bool("fullPlanned"),
                m.Has("halt") ? SurveyShare.CleanText(m.Str("halt", 2048)) : string.Empty,
                m.Has("gil") ? Math.Max(-1, m.I64("gil")) : -1);
        }

        private static string ReadMode(string mode) => mode is "full" or "proxy" ? mode : string.Empty;

        internal static string KindName(RemoteCommandKind kind) => kind switch
        {
            RemoteCommandKind.Start => "start",
            RemoteCommandKind.Pause => "pause",
            RemoteCommandKind.Resume => "resume",
            RemoteCommandKind.FullStart => "full",
            _ => "stop",
        };

        internal static bool TryKind(string s, out RemoteCommandKind kind)
        {
            switch (s)
            {
                case "start":
                    kind = RemoteCommandKind.Start;
                    return true;
                case "pause":
                    kind = RemoteCommandKind.Pause;
                    return true;
                case "resume":
                    kind = RemoteCommandKind.Resume;
                    return true;
                case "stop":
                    kind = RemoteCommandKind.Stop;
                    return true;
                case "full":
                    kind = RemoteCommandKind.FullStart;
                    return true;
                default:
                    kind = RemoteCommandKind.Stop;
                    return false;
            }
        }
    }

    /// <summary>
    /// 認證後的一條連線：送與收各自帶序號與 MAC。送出端由呼叫端保證同時只有一個寫入者（這裡另有一道鎖兜底）。
    /// </summary>
    internal sealed class LinkSession : IDisposable
    {
        private const byte HostToClient = (byte)'H';
        private const byte ClientToHost = (byte)'C';

        private readonly Stream stream;
        private readonly byte[] sessionKey;
        private readonly byte sendDirection;
        private readonly byte receiveDirection;
        private readonly SemaphoreSlim writeLock = new(1, 1);
        private long sendSeq;
        private long receiveSeq;

        internal LinkSession(Stream stream, byte[] sessionKey, bool isHost)
        {
            this.stream = stream;
            this.sessionKey = sessionKey;
            sendDirection = isHost ? HostToClient : ClientToHost;
            receiveDirection = isHost ? ClientToHost : HostToClient;
        }

        internal static byte[] SessionKey(byte[] longTermKey, byte[] clientNonce, byte[] hostNonce)
            => LinkCrypto.Mac(longTermKey, "session", clientNonce, hostNonce);

        private byte[] FrameMac(byte direction, long seq, byte[] json)
        {
            var header = new byte[9];
            header[0] = direction;
            BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(1), seq);
            return LinkCrypto.Mac(sessionKey, "frame", header, json);
        }

        internal async Task SendAsync(byte[] json, CancellationToken ct)
        {
            await writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var seq = ++sendSeq;
                var mac = FrameMac(sendDirection, seq, json);
                var payload = new byte[mac.Length + json.Length];
                mac.CopyTo(payload, 0);
                json.CopyTo(payload, mac.Length);
                await LinkFrames.WriteAsync(stream, payload, ct).ConfigureAwait(false);
            }
            finally
            {
                writeLock.Release();
            }
        }

        internal async Task<LinkMessage> ReceiveAsync(TimeSpan timeout, CancellationToken ct)
        {
            var payload = await LinkFrames.ReadAsync(stream, LinkFrames.MaxFrame, timeout, ct).ConfigureAwait(false);
            return Open(payload);
        }

        /// <summary>驗 MAC 並拆出訊息。驗不過一律擲：呼叫端斷線。</summary>
        internal LinkMessage Open(byte[] payload)
        {
            if (payload.Length <= 32)
                throw new LinkProtocolException("bad-mac");
            var json = payload.AsSpan(32).ToArray();
            var expected = FrameMac(receiveDirection, receiveSeq + 1, json);
            if (!CryptographicOperations.FixedTimeEquals(expected, payload.AsSpan(0, 32)))
                throw new LinkProtocolException("bad-mac");
            receiveSeq++;
            return LinkMessage.Parse(json);
        }

        public void Dispose()
        {
            try
            {
                stream.Dispose();
            }
            catch
            {
                // 關閉時的例外沒有後續動作可做。
            }

            CryptographicOperations.ZeroMemory(sessionKey);
        }
    }
}
