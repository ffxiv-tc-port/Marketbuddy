using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Gui.Dtr;
using Marketbuddy.Common;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    /// <summary>
    /// 巡檢區網連線的插件側：照設定開關主機／連線端、把網路收到的東西排回 framework 執行緒、
    /// 把收到的觀測用<b>本地</b>清單換成本地記錄列、在伺服器資訊欄顯示狀態。
    /// <para>🔴 網路只會帶來四種巡檢指令（開始一輪、暫停、繼續、停止）、清單、狀態與觀測；沒有任何路徑會觸發
    /// 上架、改價、下架、賣商店、重掛或多角色輪。遠端開始一律是主機上有人按了按鈕，到了這台還要過同意與遊戲狀態閘門。</para>
    /// <para>🔴 <see cref="gate"/> 內只碰欄位：不做 I/O、不寫 log、不呼叫別的插件。遊戲狀態只在 <see cref="Pump"/>（framework 執行緒）碰。</para>
    /// </summary>
    internal sealed class SurveyLink : ISurveyLinkHostEndpoint, ISurveyLinkClientEndpoint, IDisposable
    {
        private const string LogTag = "[巡檢連線]";
        private static readonly TimeSpan FrameworkCallTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan MergeInterval = TimeSpan.FromSeconds(30);
        private const int MaxMergeBatch = 50_000;

        private readonly PriceSurvey survey;
        private readonly MarketGuiEventHandler gui;
        private readonly SurveyObservationLog observations;
        private readonly ConcurrentQueue<Action> frameworkQueue = new();
        private readonly object gate = new();

        // ---- gate 內 ----
        private readonly List<MarketObservation> pendingMerge = [];
        private readonly List<SurveyWorldRow> pendingHostWorlds = [];
        private readonly Dictionary<uint, SurveyWorldRow> outgoingWorlds = new();
        private (string KeyId, byte[] Key)? hostKey;
        private (string KeyId, byte[] Key)? clientKey;

        private SurveyLinkHost? host;
        private SurveyLinkClient? client;
        // 上一次照哪一組設定開的（每幀比對，不組字串）。
        private int hostPortInUse = -1;
        private bool clientOn;
        private string clientAddressInUse = string.Empty;
        private int clientPortInUse = -1;
        private string clientKeyIdInUse = string.Empty;
        private volatile ClientStatus status = ClientStatus.Empty;
        private Func<uint, bool>? knownItem;
        private Func<uint, bool>? knownWorld;
        private DateTime nextStatusAt = DateTime.MinValue;
        private DateTime nextMergeAt = DateTime.MinValue;
        private int merging;
        private int disposed;
        private IDtrBarEntry? dtr;
        private string dtrText = string.Empty;
        private int dataSerial;

        internal SurveyLink(PriceSurvey survey, MarketGuiEventHandler gui)
        {
            this.survey = survey;
            this.gui = gui;
            observations = new SurveyObservationLog(PluginInterface.ConfigDirectory.FullName, m => Log.Information($"{LogTag} {m}"));
        }

        /// <summary>伺服器資訊欄左鍵要做的事（打開巡檢視窗）；插件建好 UI 之後掛上。</summary>
        internal Action? OpenWindow { get; set; }

        private Configuration conf => Configuration.GetOrLoad();

        internal SurveyLinkHost? Host => host;
        internal SurveyLinkClient? Client => client;
        internal int OutboxCount => observations.IsLoaded ? observations.OutboxCount : -1;
        internal string HostStartError { get; private set; } = string.Empty;
        internal IReadOnlyList<string> LocalAddresses { get; private set; } = [];

        /// <summary>每併入一批就 +1；巡檢視窗拿它當「記錄檔變了，重讀」的訊號。</summary>
        internal int DataSerial => Volatile.Read(ref dataSerial);

        /// <summary>上一次合併的結果（畫面用）。</summary>
        internal string MergeStatus { get; private set; } = string.Empty;

        /// <summary>這台開著連線（任一方向）。觀測只在這時記錄，沒用這個功能的人不會多一個檔。</summary>
        internal bool Active => conf.SurveyLinkHostEnabled || conf.SurveyLinkClientEnabled;

        // =====================================================================
        //  framework 執行緒
        // =====================================================================

        /// <summary>每幀由 <see cref="PriceSurvey"/> 的幀時鐘呼叫。</summary>
        internal void Pump()
        {
            if (Volatile.Read(ref disposed) != 0)
                return;

            ReconcileRoles();
            while (frameworkQueue.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    Log.Information(e, $"{LogTag} 排回 framework 執行緒的工作擲例外。");
                }
            }

            var now = DateTime.UtcNow;
            if (now >= nextStatusAt)
            {
                nextStatusAt = now.AddSeconds(1);
                if (client != null)
                    status = survey.BuildLinkStatus(observations.IsLoaded ? observations.OutboxCount : 0);
                UpdateDtr();
            }

            ApplyHostWorlds();
            KickMerge(now);
        }

        /// <summary>設定（開關、埠、位址、金鑰）變了就重開對應的那一側。</summary>
        private void ReconcileRoles()
        {
            var c = conf;
            var wantHostPort = c.SurveyLinkHostEnabled ? c.SurveyLinkPort : -1;
            if (wantHostPort != hostPortInUse)
            {
                hostPortInUse = wantHostPort;
                host?.Dispose();
                host = null;
                HostStartError = string.Empty;
                if (wantHostPort > 0)
                    StartHost(c);
            }

            var wantClient = c.SurveyLinkClientEnabled;
            if (wantClient != clientOn
                || (wantClient && (c.SurveyLinkPort != clientPortInUse
                                   || !string.Equals(c.SurveyLinkHostAddress, clientAddressInUse, StringComparison.Ordinal)
                                   || !string.Equals(c.SurveyLinkClientKeyId, clientKeyIdInUse, StringComparison.Ordinal))))
            {
                clientOn = wantClient;
                clientPortInUse = c.SurveyLinkPort;
                clientAddressInUse = c.SurveyLinkHostAddress;
                clientKeyIdInUse = c.SurveyLinkClientKeyId;
                client?.Dispose();
                client = null;
                if (wantClient)
                    StartClient(c);
            }
        }

        private void EnsureCommon(Configuration c)
        {
            knownItem ??= PriceSurveyItemSourceHelpers.KnownItem();
            knownWorld ??= PriceSurveyItemSourceHelpers.KnownWorld();
            _ = observations.BeginLoad();
            lock (gate)
            {
                hostKey = DecodeKey(c.SurveyLinkHostKeyId, c.SurveyLinkHostKey);
                clientKey = DecodeKey(c.SurveyLinkClientKeyId, c.SurveyLinkClientKey);
            }
        }

        private void StartHost(Configuration c)
        {
            EnsureCommon(c);
            var h = new SurveyLinkHost(this);
            // 先掛上再 listen：第一條連線可能在 Start 回來之前就進來，那時端點要已經知道自己是主機。
            host = h;
            try
            {
                h.Start(c.SurveyLinkPort);
                Task.Run(() =>
                {
                    try
                    {
                        LocalAddresses = FindLocalAddresses();
                    }
                    catch
                    {
                        LocalAddresses = [];
                    }
                });
            }
            catch (Exception e)
            {
                host = null;
                h.Dispose();
                HostStartError = e is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }
                    ? "That port is already in use by another program.".Loc()
                    : e.GetType().Name;
                Log.Information($"{LogTag} 無法 listen 埠 {c.SurveyLinkPort}：{e.GetType().Name}。");
            }
        }

        private void StartClient(Configuration c)
        {
            EnsureCommon(c);
            if (!SurveyShare.TryParseEndpoint(c.SurveyLinkHostAddress, c.SurveyLinkPort, out var endpoint)
                || !SurveyShare.IsPrivateAddress(endpoint.Address))
                return;
            client = new SurveyLinkClient(this, endpoint);
            client.Start();
        }

        /// <summary>這台的區網 IPv4（只列私有位址），給使用者抄到另一台。執行緒池上呼叫。</summary>
        private static List<string> FindLocalAddresses()
        {
            var result = new List<string>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                {
                    var ip = address.Address;
                    if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip)
                        && SurveyShare.IsPrivateAddress(ip))
                        result.Add(ip.ToString());
                }
            }

            return result;
        }

        private static (string, byte[])? DecodeKey(string keyId, string key)
        {
            if (!SurveyShare.IsSafeId(keyId) || string.IsNullOrEmpty(key))
                return null;
            try
            {
                var bytes = Convert.FromBase64String(key);
                return bytes.Length == LinkCrypto.KeyBytes ? (keyId, bytes) : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        /// <summary>收到的世界進度（主機那側）：等世界進度檔讀完再併，較舊的不蓋較新的。</summary>
        private void ApplyHostWorlds()
        {
            if (!PriceSurveyWorldLog.Loaded)
                return;
            List<SurveyWorldRow> worlds;
            lock (gate)
            {
                if (pendingHostWorlds.Count == 0)
                    return;
                worlds = new List<SurveyWorldRow>(pendingHostWorlds);
                pendingHostWorlds.Clear();
            }

            var merged = 0;
            foreach (var world in worlds)
            {
                if (PriceSurveyWorldLog.MergeNewer(world))
                    merged++;
            }

            if (merged > 0)
            {
                Interlocked.Increment(ref dataSerial);
                Log.Information($"{LogTag} 併入另一台代掃的世界進度 {merged}/{worlds.Count} 個。");
            }
        }

        /// <summary>
        /// 把收到的（與這台代掃的）觀測換成本地記錄列。清單用<b>跟掃描完全相同的來源優先序</b>，
        /// 所以「我方價」與「最低價是不是我的」一律是這台自己的視角。
        /// </summary>
        private void KickMerge(DateTime now)
        {
            if (now < nextMergeAt || Volatile.Read(ref merging) != 0)
                return;
            List<MarketObservation> batch;
            lock (gate)
            {
                if (pendingMerge.Count == 0)
                    return;
                var take = Math.Min(pendingMerge.Count, MaxMergeBatch);
                batch = pendingMerge.GetRange(0, take);
                pendingMerge.RemoveRange(0, take);
            }

            nextMergeAt = now + MergeInterval;
            Interlocked.Exchange(ref merging, 1);
            var c = conf;
            var live = c.PriceSurveyAllCharacters
                ? null
                : PriceSurveyItemSource.TryFromSellList(gui) ?? PriceSurveyItemSource.TryFromAllaganTools();
            var csvPath = live == null ? PriceSurveyItemSource.InventoryToolsCsvPath() : null;
            Task.Run(() =>
            {
                try
                {
                    var list = live ?? PriceSurveyItemSource.TryFromInventoryToolsCsv(csvPath);
                    if (list == null || list.Items.Count == 0)
                    {
                        MergeStatus = "Shared prices were kept, but none of them is an item you are selling right now.".Loc();
                        Log.Information($"{LogTag} 合併：這台找不到自己的掛售清單，{batch.Count} 筆觀測只留在觀測記錄檔。");
                        return;
                    }

                    var mine = new Dictionary<uint, List<(bool, long)>>();
                    foreach (var item in list.Items)
                    {
                        if (!mine.TryGetValue(item.ItemId, out var entries))
                            mine[item.ItemId] = entries = [];
                        entries.Add((item.Hq, item.OurPrice));
                    }

                    var rows = SurveyShare.ToRows(batch, mine, list.OwnRetainerIds);
                    foreach (var row in rows)
                        PriceSurveyLog.Append(row);
                    if (rows.Count > 0)
                        Interlocked.Increment(ref dataSerial);
                    MergeStatus = "Merged ?? shared price(s) into ?? row(s) of your own list.".Loc(batch.Count, rows.Count);
                    Log.Information(
                        $"{LogTag} 合併：{batch.Count} 筆觀測 → 這台清單上的 {rows.Count} 列（來源={list.SourceKey}，" +
                        $"我方僱員 {list.OwnRetainerIds.Count} 位）。");
                }
                catch (Exception e)
                {
                    Log.Information($"{LogTag} 合併失敗：{e.GetType().Name}。");
                }
                finally
                {
                    Volatile.Write(ref merging, 0);
                }
            });
        }

        // =====================================================================
        //  巡檢引擎呼叫進來的（framework 執行緒）
        // =====================================================================

        /// <summary>
        /// 這台查完一件。開著連線才記：記錄檔＋待送。代掃的那一件另外排進合併，換成這台自己視角的列。
        /// </summary>
        internal void NoteObservation(MarketObservation obs, bool proxy)
        {
            if (!Active)
                return;
            _ = observations.BeginLoad();
            observations.RecordLocal(obs);
            if (!proxy)
                return;
            lock (gate)
            {
                pendingMerge.Add(obs);
            }
        }

        /// <summary>代掃的一個世界收場：進度排進下一批送給主機（斷線時留著，連上再送）。</summary>
        internal void NoteProxyWorld(SurveyWorldRow row)
        {
            lock (gate)
            {
                outgoingWorlds[row.WorldId] = row;
            }
        }

        /// <summary>代掃挑世界時，看兩邊合起來（這台的紀錄＋主機的觀測）在保留時間內問過哪些道具。執行緒池上呼叫。</summary>
        internal HashSet<uint> AnsweredSince(uint worldId, DateTime cutoffUtc)
            => observations.IsLoaded ? observations.AnsweredSince(worldId, cutoffUtc) : [];

        // =====================================================================
        //  主機畫面上的按鈕（framework／繪製執行緒）
        // =====================================================================

        /// <summary>
        /// 推清單（必要時接著送「開始一輪」）。🔴 只由主機畫面上使用者按的按鈕呼叫。
        /// 清單用跟掃描完全相同的來源優先序在這裡建；記錄檔那一條在執行緒池上讀。
        /// </summary>
        internal void PushList(bool thenStart) => frameworkQueue.Enqueue(() => PushListCore(thenStart));

        /// <summary>framework 執行緒：出售品視窗與 AllaganTools 只能在這裡讀。</summary>
        private void PushListCore(bool thenStart)
        {
            var h = host;
            if (h is not { IsConnected: true })
                return;
            var c = conf;
            var live = c.PriceSurveyAllCharacters
                ? null
                : PriceSurveyItemSource.TryFromSellList(gui) ?? PriceSurveyItemSource.TryFromAllaganTools();
            var csvPath = live == null ? PriceSurveyItemSource.InventoryToolsCsvPath() : null;
            var excluded = new HashSet<uint>(c.PriceSurveyExcludedWorlds);
            var hostWorlds = new List<SurveyWorldRow>();
            foreach (var (worldId, _) in survey.DataCentreWorlds)
            {
                if (PriceSurveyWorldLog.TryGet(worldId, out var row))
                    hostWorlds.Add(row with { ContentId = 0 });
            }

            LastPushStatus = "Building the list...".Loc();
            Task.Run(() =>
            {
                try
                {
                    var list = live ?? PriceSurveyItemSource.TryFromInventoryToolsCsv(csvPath);
                    if (list == null || list.Items.Count == 0)
                    {
                        LastPushStatus = "No listed items found (needs InventoryTools/AllaganTools, or open a retainer's sell list first).".Loc();
                        return;
                    }

                    var items = new List<SharedListItem>(list.Items.Count);
                    foreach (var item in list.Items)
                        items.Add(new SharedListItem(item.ItemId, item.Hq));
                    var now = DateTime.UtcNow;
                    var shared = new SharedList
                    {
                        ListId = SurveyShare.NewListId(now, Random.Shared.Next()),
                        ExportedUtc = now,
                        SourceKey = list.SourceKey,
                        Items = items,
                        ExcludedWorlds = excluded,
                        HostWorlds = hostWorlds,
                    };
                    if (!h.SendList(shared))
                    {
                        LastPushStatus = "Not connected.".Loc();
                        return;
                    }

                    LastPushedListId = shared.ListId;
                    LastPushStatus = "Sent a list of ?? item(s).".Loc(items.Count);
                    Log.Information(
                        $"{LogTag} 推送清單 {shared.ListId}：{items.Count} 件（來源={list.SourceKey}），" +
                        $"排除世界 {excluded.Count} 個，附主機世界進度 {hostWorlds.Count} 個。");
                    if (thenStart)
                        h.SendCommand(RemoteCommandKind.Start, shared.ListId, []);
                }
                catch (Exception e)
                {
                    LastPushStatus = e.GetType().Name;
                    Log.Information($"{LogTag} 推送清單失敗：{e.GetType().Name}。");
                }
            });
        }

        internal string LastPushStatus { get; private set; } = string.Empty;
        internal string LastPushedListId { get; private set; } = string.Empty;

        /// <summary>🔴 只由主機畫面上使用者按的按鈕呼叫。</summary>
        internal void SendCommand(RemoteCommandKind kind) => host?.SendCommand(kind, LastPushedListId, []);

        /// <summary>主機畫面：撤銷配對。</summary>
        internal void Revoke() => host?.Revoke();

        /// <summary>代掃那台的畫面：清掉這台記得的配對。</summary>
        internal void ForgetPairing()
        {
            lock (gate)
            {
                clientKey = null;
            }

            var c = conf;
            c.SurveyLinkClientKeyId = string.Empty;
            c.SurveyLinkClientKey = string.Empty;
            c.Save();
            Log.Information($"{LogTag} 這台忘記了和主機的配對。");
        }

        // =====================================================================
        //  端點（網路那側呼叫；任何執行緒）
        // =====================================================================

        void ISurveyLinkEndpoint.Log(string message) => Log.Information($"{LogTag} {message}");

        Func<uint, bool>? ISurveyLinkEndpoint.KnownItem => knownItem ?? (static _ => false);
        Func<uint, bool>? ISurveyLinkEndpoint.KnownWorld => knownWorld ?? (static _ => false);

        (string KeyId, byte[] Key)? ISurveyLinkEndpoint.PairedKey()
        {
            lock (gate)
            {
                // 兩個角色不會同時開（設定頁互斥），各自讀自己那一份。
                return host != null ? hostKey : clientKey;
            }
        }

        void ISurveyLinkEndpoint.SavePairedKey(string keyId, byte[] key)
        {
            var copy = (byte[])key.Clone();
            var asHost = host != null;
            lock (gate)
            {
                if (asHost)
                    hostKey = (keyId, copy);
                else
                    clientKey = (keyId, copy);
            }

            var encoded = Convert.ToBase64String(copy);
            frameworkQueue.Enqueue(() =>
            {
                var c = conf;
                if (asHost)
                {
                    c.SurveyLinkHostKeyId = keyId;
                    c.SurveyLinkHostKey = encoded;
                }
                else
                {
                    c.SurveyLinkClientKeyId = keyId;
                    c.SurveyLinkClientKey = encoded;
                    // 金鑰代號是連線端設定鍵的一部分；先記下新值，免得下一幀把剛配對好的連線拆掉重來。
                    clientKeyIdInUse = keyId;
                }

                c.Save();
            });
        }

        void ISurveyLinkHostEndpoint.ClearPairedKey()
        {
            lock (gate)
            {
                hostKey = null;
            }

            frameworkQueue.Enqueue(() =>
            {
                var c = conf;
                c.SurveyLinkHostKeyId = string.Empty;
                c.SurveyLinkHostKey = string.Empty;
                c.Save();
            });
        }

        OutgoingBatch? ISurveyLinkEndpoint.TakeOutgoing(int max)
        {
            if (!observations.IsLoaded)
                return null;
            var peek = observations.PeekOutbox(max);
            List<SurveyWorldRow> worlds;
            lock (gate)
            {
                worlds = client != null ? new List<SurveyWorldRow>(outgoingWorlds.Values) : [];
            }

            if (peek.Count == 0 && worlds.Count == 0)
                return null;
            var rows = new List<MarketObservation>(peek.Count);
            foreach (var (_, obs) in peek)
                rows.Add(obs);
            return new OutgoingBatch(peek.Count > 0 ? peek[^1].Seq : 0, rows, worlds);
        }

        void ISurveyLinkEndpoint.Acked(OutgoingBatch batch)
        {
            if (batch.Rows.Count > 0)
                observations.Ack(batch.UpToSeq);
            lock (gate)
            {
                foreach (var world in batch.Worlds)
                {
                    if (outgoingWorlds.TryGetValue(world.WorldId, out var now) && now == world)
                        outgoingWorlds.Remove(world.WorldId);
                }
            }

            if (batch.Rows.Count > 0 || batch.Worlds.Count > 0)
                Log.Information($"{LogTag} 補傳完成：{batch.Rows.Count} 筆觀測、{batch.Worlds.Count} 個世界進度，還排著 {observations.OutboxCount} 筆。");
        }

        int? ISurveyLinkEndpoint.Receive(IReadOnlyList<MarketObservation> rows, IReadOnlyList<SurveyWorldRow> worlds)
        {
            if (!observations.IsLoaded)
            {
                _ = observations.BeginLoad();
                return null;
            }

            var accepted = new List<MarketObservation>();
            foreach (var row in rows)
            {
                if (observations.AcceptRemote(row) == SurveyObservationLog.Intake.Accepted)
                    accepted.Add(row);
            }

            var asHost = host != null;
            lock (gate)
            {
                pendingMerge.AddRange(accepted);
                if (asHost)
                    pendingHostWorlds.AddRange(worlds);
            }

            Log.Information(
                $"{LogTag} 收到 {rows.Count} 筆觀測（新 {accepted.Count} 筆、重複 {rows.Count - accepted.Count} 筆）" +
                $"{(worlds.Count > 0 ? $"、{worlds.Count} 個世界進度" : string.Empty)}。");
            return accepted.Count;
        }

        ClientStatus ISurveyLinkClientEndpoint.CurrentStatus() => status;

        Task<string?> ISurveyLinkClientEndpoint.HandleList(SharedList list)
            => OnFramework(() => survey.ApplyRemoteList(list));

        Task<string?> ISurveyLinkClientEndpoint.HandleCommand(RemoteCommand command)
            => OnFramework(() => survey.ExecuteRemote(command));

        /// <summary>排回 framework 執行緒做，並等它回答（有上限：遊戲卡住時回「逾時」而不是一直掛著）。</summary>
        private async Task<string?> OnFramework(Func<string?> work)
        {
            var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            frameworkQueue.Enqueue(() =>
            {
                try
                {
                    done.TrySetResult(work());
                }
                catch (Exception e)
                {
                    done.TrySetResult(e.GetType().Name);
                }
            });
            try
            {
                return await done.Task.WaitAsync(FrameworkCallTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return "timeout";
            }
        }

        // =====================================================================
        //  伺服器資訊欄（視窗關著也看得到有沒有在跑、有沒有斷線）
        // =====================================================================

        private void UpdateDtr()
        {
            var text = string.Empty;
            var tooltip = string.Empty;
            var h = host;
            if (h != null && (h.IsConnected || conf.SurveyLinkHostKeyId.Length > 0))
            {
                var s = h.LatestStatus;
                if (!h.IsConnected)
                    text = "Survey: helper not connected".Loc();
                else if (s is { RemoteControlled: true })
                    text = s.TourMax > 0
                        ? "Survey: helper world ?? / ??".Loc(Math.Max(1, s.TourStep), s.TourMax)
                        : "Survey: helper scanning".Loc();
                else
                    text = "Survey: helper idle".Loc();
                tooltip = s == null
                    ? "Click to open the price survey window.".Loc()
                    : $"{(s.WorldName.Length > 0 ? s.WorldName : "?")}  {s.Processed} / {s.Total}\n" +
                      $"{(s.TourStatus.Length > 0 ? s.TourStatus : s.StatusText)}\n" +
                      "Click to open the price survey window.".Loc();
            }
            else if (client != null && survey.IsRemoteControlled)
            {
                text = "Survey: run by the host".Loc();
                tooltip = (survey.TourStatus.Length > 0 ? survey.TourStatus : survey.StatusText) + "\n" +
                          "Click to open the price survey window.".Loc();
            }

            if (text.Length == 0)
            {
                if (dtr != null)
                    dtr.Shown = false;
                return;
            }

            if (dtr == null)
            {
                dtr = DtrBar.Get("Marketbuddy survey link");
                dtr.OnClick = _ => OpenWindow?.Invoke();
            }

            dtr.Shown = true;
            if (text != dtrText)
            {
                dtrText = text;
                dtr.Text = text;
            }

            dtr.Tooltip = tooltip;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            try
            {
                host?.Dispose();
            }
            catch
            {
                // 收尾失敗不擋卸載。
            }

            try
            {
                client?.Dispose();
            }
            catch
            {
                // 收尾失敗不擋卸載。
            }

            host = null;
            client = null;
            try
            {
                dtr?.Remove();
            }
            catch
            {
                // 收尾失敗不擋卸載。
            }

            dtr = null;
            while (frameworkQueue.TryDequeue(out _))
            {
            }
        }
    }

    /// <summary>道具／世界表的存在判斷式。只在 framework 執行緒取表；判斷式可帶去執行緒池用。</summary>
    internal static class PriceSurveyItemSourceHelpers
    {
        internal static Func<uint, bool> KnownItem()
        {
            var sheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
            return sheet == null ? static _ => false : id => sheet.TryGetRow(id, out _);
        }

        internal static Func<uint, bool> KnownWorld()
        {
            var sheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.World>();
            return sheet == null ? static _ => false : id => sheet.TryGetRow(id, out _);
        }
    }
}
