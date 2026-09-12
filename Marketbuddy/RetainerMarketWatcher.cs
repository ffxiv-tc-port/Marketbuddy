using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    /// <summary>僱員市場容器裡的一筆掛單（快照用，<b>不帶格號</b>，理由見快照類別）。</summary>
    internal readonly record struct RetainerListing(uint ItemId, bool Hq, int Quantity, long UnitPrice);

    /// <summary>
    /// 某一位僱員的市場容器在某一刻的樣子。
    /// 🔴 <b>刻意不記格號。</b>一件道具被取回或賣掉之後，市場容器裡剩下的格子有沒有被壓縮，
    /// 我們無法離線證明（<see cref="BatchDelist"/> 的 <c>DelistJob</c> 註解逐字寫著同一件事，
    /// 它也因此改成每次現找第一個有東西的格子）。只要它會壓縮，用格號去對就會整排錯位，
    /// 而錯位的結果是「賣掉了 A」被記成「賣掉了 B」——一個自信的錯誤比沒有記錄更糟。
    /// 所以比對一律用「(道具, 品質) → 各價位各幾件」這個<b>與位置無關</b>的形狀。
    /// 🔑 同一個形狀順帶解決了重掛：改價只是把某個價位的件數搬到另一個價位，
    /// <b>(道具, 品質) 的總件數不變</b>，所以批次重掛不會生出任何假的「賣出」事件。
    /// </summary>
    internal sealed class RetainerMarketSnapshot
    {
        public ulong RetainerId;
        public string RetainerName = string.Empty;
        public DateTime AtUtc;

        /// <summary>快照當下僱員錢包裡的金幣；<c>-1</c>＝讀不到（不是 0）。</summary>
        public long Gil = -1;

        public List<RetainerListing> Listings = [];

        /// <summary>兩份快照的掛單內容一不一樣（與順序無關）。</summary>
        internal static bool SameListings(RetainerMarketSnapshot a, RetainerMarketSnapshot b)
        {
            if (a.Listings.Count != b.Listings.Count)
                return false;

            var counts = new Dictionary<RetainerListing, int>();
            foreach (var listing in a.Listings)
            {
                counts.TryGetValue(listing, out var n);
                counts[listing] = n + 1;
            }

            foreach (var listing in b.Listings)
            {
                if (!counts.TryGetValue(listing, out var n) || n == 0)
                    return false;
                counts[listing] = n - 1;
            }

            return true;
        }
    }

    /// <summary>
    /// 「這件東西是 Marketbuddy 自己下架的」的短期帳。
    /// 🔴 沒有這本帳，我方的批次下架會在下一次快照時長成一批「消失」事件，
    /// 而那些事件會去跟金幣比對——下架不會讓錢包增加，所以它們會全部落到
    /// 「低信心：可能手動下架」，把真正需要注意的那幾列淹掉。
    /// 存活時間刻意短（<see cref="TtlMinutes"/> 分鐘）：這本帳是用來解釋<b>剛剛</b>發生的事，
    /// 留太久只會讓一次很久以前的下架去認領一筆真正的賣出。過期沒被認領的條目自然消失，
    /// 失敗形式是「那一列變成低信心」——不會產生錯誤的高信心結論。
    /// 執行緒：所有呼叫點都在 framework 執行緒上（兩個下架引擎與觀察者都掛在
    /// <c>Framework.Update</c> 上），但仍然上鎖——這種清單一旦哪天被別的路徑碰到，
    /// 裸 <c>List</c> 的失敗形式是清單本身壞掉，不是拿到舊值。
    /// </summary>
    internal static class RetainerDelistLedger
    {
        private const int TtlMinutes = 30;

        private sealed class Entry
        {
            public ulong RetainerId;
            public uint ItemId;
            public bool Hq;
            public int Quantity;
            public DateTime AtUtc;
        }

        private static readonly List<Entry> Entries = [];

        private static readonly object Gate = new();

        /// <summary>
        /// 記下一次「我方剛剛把某件東西從市場容器取回」。
        /// 🔴 只記錄，沒有任何副作用；記漏了最壞的結果是那一列變成低信心。
        /// </summary>
        internal static void Note(ulong retainerId, uint itemId, bool hq, int quantity)
        {
            if (retainerId == 0 || itemId == 0 || quantity <= 0)
                return;

            lock (Gate)
            {
                Prune();
                Entries.Add(new Entry
                {
                    RetainerId = retainerId,
                    ItemId = itemId,
                    Hq = hq,
                    Quantity = quantity,
                    AtUtc = DateTime.UtcNow,
                });
            }
        }

        /// <summary>
        /// 認領最多 <paramref name="wanted"/> 件。回傳真的被這本帳解釋掉的件數。
        /// 認領掉的部分會從帳上扣除，不會被第二次快照重複認領。
        /// </summary>
        internal static int Consume(ulong retainerId, uint itemId, bool hq, int wanted)
        {
            if (retainerId == 0 || itemId == 0 || wanted <= 0)
                return 0;

            var taken = 0;
            lock (Gate)
            {
                Prune();
                for (var i = 0; i < Entries.Count && taken < wanted; i++)
                {
                    var entry = Entries[i];
                    if (entry.RetainerId != retainerId || entry.ItemId != itemId || entry.Hq != hq)
                        continue;

                    var use = Math.Min(entry.Quantity, wanted - taken);
                    entry.Quantity -= use;
                    taken += use;
                }

                Entries.RemoveAll(e => e.Quantity <= 0);
            }

            return taken;
        }

        /// <summary>🔴 只在持有 <see cref="Gate"/> 時呼叫。</summary>
        private static void Prune()
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-TtlMinutes);
            Entries.RemoveAll(e => e.AtUtc < cutoff || e.Quantity <= 0);
        }
    }

    /// <summary>
    /// 每一位僱員「上一次看到的市場容器」的持久基準線，寫在 Marketbuddy 自己的設定目錄底下的
    /// <c>retainer_market_state.csv</c>。
    /// 🔴 <b>為什麼一定要落地</b>：差異事件是「上一次開僱員」與「這一次開僱員」之間的比較，
    /// 而這兩件事跨得過重開遊戲。只放記憶體的話，每個工作階段第一次開每一位僱員都只能
    /// 「先記一份基準」什麼都算不出來——也就是最常見的使用方式下這個功能等於不存在。
    /// ⚠️ 這個檔是<b>整份重寫</b>（僱員會被解僱、清單會縮短，追加寫不出「某一列不見了」），
    /// 走「暫存檔 → <see cref="File.Move(string,string,bool)"/>」，所以寫到一半崩潰不會留下
    /// 半份基準線。形狀與存檔紀律逐字比照 <see cref="PendingActions"/>。
    /// </summary>
    internal static class RetainerMarketState
    {
        internal const string FileName = "retainer_market_state.csv";

        private const string Header = "retainerId,retainerName,atUtc,gil,itemId,hq,quantity,unitPrice";

        private const int FieldCount = 8;

        private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";

        private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

        /// <summary>🔴 只在持有 <see cref="Gate"/> 時碰它。</summary>
        private static readonly Dictionary<ulong, RetainerMarketSnapshot> Snapshots = new();

        /// <summary>🔴 鎖內只碰 <see cref="Snapshots"/> 與那兩個版號。</summary>
        private static readonly object Gate = new();

        private static long revision;

        private static long savedRevision;

        private static int saving;

        private static int loadStarted;

        private static int writeFailureLogged;

        private static volatile Task? loadTask;

        /// <summary>基準線檔讀完了沒。🔴 <b>沒讀完之前一列差異都不可以算</b>——那時每一位僱員
        /// 看起來都是「第一次見到」，而真正的基準還躺在檔案裡。</summary>
        internal static bool Loaded { get; private set; }

        internal static string FilePath
            => cachedFilePath ??= Path.Combine(PluginInterface.ConfigDirectory.FullName, FileName);

        private static string? cachedFilePath;

        /// <summary>第一次呼叫時在執行緒池上把檔案讀回來；之後是 no-op。</summary>
        internal static void BeginLoad()
        {
            if (Interlocked.CompareExchange(ref loadStarted, 1, 0) != 0)
                return;
            loadTask = Task.Run(LoadCore);
        }

        /// <summary>讀檔工作跑完了就收掉。framework 執行緒每幀呼叫。</summary>
        internal static void PumpLoad()
        {
            var task = loadTask;
            if (task == null || !task.IsCompleted)
                return;
            loadTask = null;
            Loaded = true;
        }

        private static void LoadCore()
        {
            var fromFile = ReadAll();
            lock (Gate)
            {
                foreach (var (id, snapshot) in fromFile)
                    Snapshots.TryAdd(id, snapshot);
            }
        }

        /// <summary>某一位僱員上一次的基準線；沒有就回 null（＝還沒見過這位僱員）。</summary>
        internal static RetainerMarketSnapshot? Get(ulong retainerId)
        {
            lock (Gate)
            {
                return Snapshots.GetValueOrDefault(retainerId);
            }
        }

        /// <summary>換掉某一位僱員的基準線。</summary>
        internal static void Set(RetainerMarketSnapshot snapshot)
        {
            if (snapshot.RetainerId == 0)
                return;

            lock (Gate)
            {
                Snapshots[snapshot.RetainerId] = snapshot;
                revision++;
            }

            KickSave();
        }

        /// <summary>已經有基準線的僱員數（UI 顯示用）。</summary>
        internal static int Count
        {
            get
            {
                lock (Gate)
                {
                    return Snapshots.Count;
                }
            }
        }

        private static void KickSave()
        {
            if (Interlocked.CompareExchange(ref saving, 1, 0) != 0)
                return;
            Task.Run(SaveLoop);
        }

        private static void SaveLoop()
        {
            try
            {
                while (true)
                {
                    List<RetainerMarketSnapshot> batch;
                    long at;
                    lock (Gate)
                    {
                        if (revision == savedRevision)
                            break;
                        at = revision;
                        batch = new List<RetainerMarketSnapshot>(Snapshots.Values);
                    }

                    // 🔴 I/O 在鎖外做。
                    WriteAll(batch);

                    lock (Gate)
                    {
                        savedRevision = at;
                    }
                }
            }
            catch (Exception e)
            {
                ReportWriteFailure(e);

                // 🔴 放棄這一代，不要重試：寫不進去的常見原因不會在下一毫秒消失，
                //    而 finally 裡的「還有新東西就再踢一次」會把失敗變成燒 CPU 的死迴圈。
                lock (Gate)
                {
                    savedRevision = revision;
                }
            }
            finally
            {
                Volatile.Write(ref saving, 0);

                var again = false;
                lock (Gate)
                {
                    again = revision != savedRevision;
                }

                if (again)
                    KickSave();
            }
        }

        private static void WriteAll(List<RetainerMarketSnapshot> snapshots)
        {
            var path = FilePath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var text = new StringBuilder();
            text.Append(Header).Append("\r\n");
            foreach (var snapshot in snapshots)
            {
                // 每一位僱員先寫一列「表頭」（itemId 0）：那是唯一寫得下「這位僱員一件都沒掛」
                // 以及快照時間與錢包的地方。
                text.Append(FormatHeaderRow(snapshot)).Append("\r\n");
                foreach (var listing in snapshot.Listings)
                    text.Append(FormatListingRow(snapshot, listing)).Append("\r\n");
            }

            var bytes = new UTF8Encoding(false).GetBytes(text.ToString());

            var temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(Utf8Bom, 0, Utf8Bom.Length);
                stream.Write(bytes, 0, bytes.Length);
            }

            File.Move(temp, path, true);
        }

        private static string FormatHeaderRow(RetainerMarketSnapshot s)
            => string.Join(",",
                s.RetainerId.ToString(CultureInfo.InvariantCulture),
                PriceSurveyLog.Escape(s.RetainerName),
                s.AtUtc.ToString(TimestampFormat, CultureInfo.InvariantCulture),
                s.Gil.ToString(CultureInfo.InvariantCulture),
                "0", "0", "0", "-1");

        private static string FormatListingRow(RetainerMarketSnapshot s, RetainerListing listing)
            => string.Join(",",
                s.RetainerId.ToString(CultureInfo.InvariantCulture),
                PriceSurveyLog.Escape(s.RetainerName),
                s.AtUtc.ToString(TimestampFormat, CultureInfo.InvariantCulture),
                s.Gil.ToString(CultureInfo.InvariantCulture),
                listing.ItemId.ToString(CultureInfo.InvariantCulture),
                listing.Hq ? "1" : "0",
                listing.Quantity.ToString(CultureInfo.InvariantCulture),
                listing.UnitPrice.ToString(CultureInfo.InvariantCulture));

        private static void ReportWriteFailure(Exception e)
        {
            if (Interlocked.Exchange(ref writeFailureLogged, 1) != 0)
                return;

            try
            {
                Log.Information(
                    e,
                    $"[Marketbuddy] 僱員掛售基準線寫入失敗（{FileName}）；下一次開遊戲會從頭建立基準，" +
                    "那段期間算不出差異事件。本工作階段不再重複回報這個錯誤。");
            }
            catch
            {
                // 記錄失敗絕不能中斷任何流程。
            }
        }

        /// <summary>整份讀回來。🔴 <b>只從執行緒池呼叫。</b></summary>
        private static Dictionary<ulong, RetainerMarketSnapshot> ReadAll()
        {
            var result = new Dictionary<ulong, RetainerMarketSnapshot>();
            string path;
            try
            {
                path = FilePath;
            }
            catch
            {
                return result;
            }

            try
            {
                if (!File.Exists(path))
                    return result;

                // 🔴 FileShare.Delete 是必要的：存檔走的是「暫存檔 → File.Move 覆蓋」。
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                while (reader.ReadLine() is { } line)
                {
                    if (line.Length == 0)
                        continue;
                    ParseInto(line, result);
                }
            }
            catch (Exception e)
            {
                Log.Information(e,
                    $"[Marketbuddy] 僱員掛售基準線讀取失敗（{FileName}），差異事件會從這一次開僱員重新開始算。");
            }

            return result;
        }

        /// <summary>解析一列。標題列與任何壞掉的列都靜默略過。</summary>
        private static void ParseInto(string line, Dictionary<ulong, RetainerMarketSnapshot> into)
        {
            var fields = PriceSurveyLog.SplitCsv(line);
            if (fields.Count < FieldCount)
                return;

            if (!ulong.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var retainerId)
                || retainerId == 0)
                return;
            if (!DateTime.TryParseExact(fields[2], TimestampFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
                return;
            if (!long.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var gil))
                return;
            if (!uint.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemId))
                return;

            if (!into.TryGetValue(retainerId, out var snapshot))
            {
                snapshot = new RetainerMarketSnapshot
                {
                    RetainerId = retainerId,
                    RetainerName = fields[1],
                    AtUtc = at,
                    Gil = gil,
                };
                into[retainerId] = snapshot;
            }

            if (itemId == 0)
                return;

            if (!int.TryParse(fields[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hq))
                return;
            if (!int.TryParse(fields[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var quantity)
                || quantity <= 0)
                return;
            if (!long.TryParse(fields[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var unitPrice))
                return;

            snapshot.Listings.Add(new RetainerListing(itemId, hq != 0, quantity, unitPrice));
        }
    }

    /// <summary>
    /// 僱員市場容器的差異觀察者：把「兩次看到之間少掉的東西」記成一張帶信心標記的事件清單。
    /// 🔴 <b>純唯讀，零自動化。</b>只讀 <c>InventoryManager</c> 的僱員市場容器與
    /// <c>RetainerManager</c> 的錢包欄位，不送封包、不掛 hook、不寫任何記憶體，
    /// 也不會因為看到什麼而去下架、改價或掛單。市場紅線：純記錄。
    /// 🔑 <b>為什麼只在「出售品」視窗開著時取樣。</b>市場容器是全域的一份，而
    /// <c>GetActiveRetainer()</c> 換人的時機與容器被伺服器填好的時機不保證同步；
    /// 兩者錯開的那幾幀會讓僱員 B 的容器內容被拿去跟僱員 A 的基準線比對，
    /// 結果是<b>一整批假的「賣出」</b>。出售品視窗開著的期間，容器屬於誰是確定的
    /// 🔑 <b>兩次相同的讀取才算數。</b>視窗剛開的那幾幀容器可能還沒填好，
    /// 「還沒填好」與「東西全賣光了」長得一模一樣。所以先拍成候選，隔一段時間再讀一次，
    /// 內容（含錢包）完全相同才承認它是一份快照。代價只是慢半秒。
    /// 執行緒：全部在 framework 執行緒上（<c>Framework.Update</c>）。落地一律交給
    /// <see cref="RetainerSalesLog"/> 與 <see cref="RetainerMarketState"/>，那兩支自己
    /// 把 I/O 丟到執行緒池，鎖內不做 I/O。
    /// </summary>
    internal sealed unsafe class RetainerMarketWatcher : IDisposable
    {
        /// <summary>僱員市場容器的格數上限；索引兩端都要夾。</summary>
        private const int MaxMarketSlots = 20;

        /// <summary>兩次取樣之間至少隔多久（也就是「兩次相同才算數」的最短觀察窗）。</summary>
        private const int SampleIntervalMs = 500;

        private readonly MarketGuiEventHandler gui;

        private readonly BatchReprice reprice;

        private Configuration conf => Configuration.GetOrLoad();

        /// <summary>還沒被確認的那一份讀取；null＝這一輪還沒讀過。</summary>
        private RetainerMarketSnapshot? candidate;

        private DateTime lastSampleAt = DateTime.MinValue;

        /// <summary>上一次取樣被擋下來的原因（已在地化）。空＝沒有被擋。</summary>
        internal string LastSkipReason { get; private set; } = string.Empty;

        /// <summary>這個工作階段承認過幾份快照（UI 拿它分辨「沒有事件」與「根本沒取到樣」）。</summary>
        internal int SnapshotsTaken { get; private set; }

        internal DateTime LastSnapshotAt { get; private set; } = DateTime.MinValue;

        public RetainerMarketWatcher(MarketGuiEventHandler gui, BatchReprice reprice)
        {
            this.gui = gui;
            this.reprice = reprice;
            Framework.Update += OnFrameworkUpdate;
        }

        public void Dispose()
        {
            Framework.Update -= OnFrameworkUpdate;
        }

        private void OnFrameworkUpdate(IFramework framework)
        {
            if (!conf.RetainerSalesLogEnabled)
            {
                candidate = null;
                return;
            }

            // 基準線檔要先讀回來，否則每一位僱員都會被當成「第一次見到」。
            RetainerMarketState.BeginLoad();
            RetainerMarketState.PumpLoad();
            // 掛售年齡的紀錄同理：沒讀回來就寫暫定值會把既有的起點蓋成今天，
            // 而那個壞法是靜默的（永遠顯示「剛掛上去」）。見 RetainerListingAge。
            RetainerListingAge.BeginLoad();
            RetainerListingAge.PumpLoad();
            if (!RetainerMarketState.Loaded)
                return;

            if (!gui.IsRetainerSellListOpen)
            {
                candidate = null;
                return;
            }

            // 別的外掛把 Marketbuddy 整個鎖住時什麼都不做。
            if (IPCManager.IsLocked)
                return;

            var now = DateTime.UtcNow;
            if ((now - lastSampleAt).TotalMilliseconds < SampleIntervalMs)
                return;
            lastSampleAt = now;

            var current = ReadSnapshot(now);
            if (current == null)
            {
                candidate = null;
                return;
            }

            // 第一次讀、或換了一位僱員、或內容還在動：留成候選，下一次再看。
            if (candidate == null || candidate.RetainerId != current.RetainerId
                || candidate.Gil != current.Gil
                || !RetainerMarketSnapshot.SameListings(candidate, current))
            {
                candidate = current;
                return;
            }

            // 連續兩次讀到完全一樣的東西 ＝ 這份快照可以相信了。
            candidate = current;

            // 掛售年齡：與差異事件完全無關的另一份紀錄，所以刻意放在 Commit **外面**
            // ——Commit 在「跟基準線一模一樣」時會提早返回，而那正是我們需要替
            // 「這個功能開始看之前就已經在架上」的東西寫暫定值的那一刻。
            RetainerListingAge.Observe(current);

            Commit(current);
        }

        /// <summary>
        /// 讀一份快照。🔴 framework 執行緒限定（解遊戲的原生指標）。
        /// 讀不成時回 null 並把原因寫進 <see cref="LastSkipReason"/>——
        /// 「取不到樣」必須在畫面上看得見，否則這個功能沒作用時是完全靜默的。
        /// </summary>
        private RetainerMarketSnapshot? ReadSnapshot(DateTime now)
        {
            var retainerManager = RetainerManager.Instance();
            var active = retainerManager == null ? null : retainerManager->GetActiveRetainer();
            if (active == null || active->RetainerId == 0)
            {
                LastSkipReason = "no retainer is active".Loc();
                return null;
            }

            var inventoryManager = InventoryManager.Instance();
            if (inventoryManager == null)
            {
                LastSkipReason = "the inventory manager is not available".Loc();
                return null;
            }

            var container = inventoryManager->GetInventoryContainer(InventoryType.RetainerMarket);
            if (container == null || !container->IsLoaded)
            {
                LastSkipReason = "the retainer's market container is not loaded yet".Loc();
                return null;
            }

            var snapshot = new RetainerMarketSnapshot
            {
                RetainerId = active->RetainerId,
                RetainerName = active->NameString,
                AtUtc = now,
                Gil = active->Gil,
            };

            var slotCount = Math.Min((int)container->Size, MaxMarketSlots);
            for (var i = 0; i < slotCount; i++)
            {
                var slot = inventoryManager->GetInventorySlot(InventoryType.RetainerMarket, i);
                if (slot == null || slot->ItemId == 0)
                    continue;
                var hq = (slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
                snapshot.Listings.Add(new RetainerListing(
                    slot->ItemId, hq, Math.Max(1, slot->Quantity),
                    (long)inventoryManager->GetRetainerMarketPrice((short)i)));
            }

            LastSkipReason = string.Empty;
            return snapshot;
        }

        private void Commit(RetainerMarketSnapshot current)
        {
            var previous = RetainerMarketState.Get(current.RetainerId);

            // 完全沒變（含錢包）就什麼都不用做——不換基準線，也不用寫檔。
            if (previous != null && previous.Gil == current.Gil
                && RetainerMarketSnapshot.SameListings(previous, current))
                return;

            SnapshotsTaken++;
            LastSnapshotAt = current.AtUtc;
            RetainerMarketState.Set(current);

            if (previous == null)
            {
                Log.Information(
                    $"[Marketbuddy] 僱員銷售：第一次記下「{current.RetainerName}」的掛售基準線" +
                    $"（{current.Listings.Count} 筆掛單）。從下一次開這位僱員起才算得出差異事件。");
                return;
            }

            var rows = Diff(previous, current, TaxPercent());
            foreach (var row in rows)
                RetainerSalesLog.Append(row);

            if (rows.Count > 0)
                Log.Information(
                    $"[Marketbuddy] 僱員銷售：「{current.RetainerName}」記下 {rows.Count} 件差異事件" +
                    $"（錢包 {(previous.Gil < 0 || current.Gil < 0 ? "?" : (current.Gil - previous.Gil).ToString(CultureInfo.InvariantCulture))} gil）。");
        }

        /// <summary>
        /// 目前這位僱員所在城市的市場稅率（%）。
        /// 拿不到即時稅率時 <see cref="BatchReprice"/> 會退回使用者設定的常數，所以永遠有值。
        /// </summary>
        private int TaxPercent()
        {
            try
            {
                return (int)reprice.CurrentTaxPercent();
            }
            catch
            {
                return Math.Clamp(conf.MarketTaxPercent, 0, 25);
            }
        }

        // =====================================================================
        //  差異計算（純函式，沒有任何遊戲狀態；上面那些前提都已經處理完了）
        // =====================================================================

        /// <summary>一件候選「賣出」：某個 (道具, 品質) 少掉了幾件、原本掛多少錢。</summary>
        private readonly record struct Candidate(uint ItemId, bool Hq, int Quantity, long UnitPrice);

        /// <summary>
        /// 比較兩份快照，產出差異事件。
        /// 步驟：①以 (道具, 品質) 為鍵算出「少了幾件」——<b>與格號、與價格都無關</b>，
        /// 所以壓縮容器與批次重掛都不會生出假事件。②先讓
        /// <see cref="RetainerDelistLedger">我方下架的帳</see>認領掉它解釋得了的部分。
        /// ③剩下的拿僱員錢包的增量去對：對得上＝高信心賣出，對不上＝低信心「消失」。
        /// 🔑 <b>稅由誰付這件事不用先賭。</b>扣稅（<c>net</c>）與未扣稅（<c>gross</c>）兩種
        /// 都試著對，對上哪一種就記哪一種。台服遊戲文案（Addon #943「出售道具時需要繳交 N% 的稅金」、
        /// #6945「利用服裝模特出售道具無需繳交稅金」）指向<b>賣方付稅</b>，所以先試 <c>net</c>；
        /// 但那是文案推定不是實測，所以另一種也留著，而且哪一種對上會逐列寫進記錄檔。
        /// </summary>
        internal static List<RetainerSaleRow> Diff(RetainerMarketSnapshot previous,
            RetainerMarketSnapshot current, int taxPercent)
        {
            var rows = new List<RetainerSaleRow>();
            var before = Group(previous.Listings);
            var after = Group(current.Listings);

            var gilDelta = previous.Gil < 0 || current.Gil < 0
                ? long.MinValue
                : current.Gil - previous.Gil;

            var candidates = new List<Candidate>();

            foreach (var (key, beforePrices) in before)
            {
                var beforeTotal = 0;
                foreach (var n in beforePrices.Values)
                    beforeTotal += n;

                after.TryGetValue(key, out var afterPrices);
                var afterTotal = 0;
                if (afterPrices != null)
                {
                    foreach (var n in afterPrices.Values)
                        afterTotal += n;
                }

                var gone = beforeTotal - afterTotal;
                if (gone <= 0)
                    continue;

                // 單價只有在「少掉的量剛好全部落在同一個價位上」時才算得出來；
                // 否則寫 -1（＝不知道），UI 會畫成灰色的 ?。絕不挑一個價位充數。
                var unitPrice = -1L;
                var pricesTouched = 0;
                var onlyPrice = -1L;
                var removedAtOnlyPrice = 0;
                foreach (var (price, count) in beforePrices)
                {
                    var stillThere = 0;
                    afterPrices?.TryGetValue(price, out stillThere);
                    var removed = count - stillThere;
                    if (removed <= 0)
                        continue;
                    pricesTouched++;
                    onlyPrice = price;
                    removedAtOnlyPrice = removed;
                }

                if (pricesTouched == 1 && removedAtOnlyPrice == gone)
                    unitPrice = onlyPrice;

                // 我方自己下架的部分先扣掉：它本來就不是賣出，也不會讓錢包增加。
                var delisted = RetainerDelistLedger.Consume(previous.RetainerId, key.ItemId, key.Hq, gone);
                if (delisted > 0)
                {
                    rows.Add(new RetainerSaleRow(current.AtUtc, previous.AtUtc, previous.RetainerId,
                        current.RetainerName, key.ItemId, key.Hq, delisted, unitPrice, -1,
                        RetainerSaleConfidence.Delisted, string.Empty, taxPercent, gilDelta));
                }

                var remaining = gone - delisted;
                if (remaining > 0)
                    candidates.Add(new Candidate(key.ItemId, key.Hq, remaining, unitPrice));
            }

            if (candidates.Count == 0)
                return rows;

            var basis = MatchGil(candidates, gilDelta, taxPercent);

            foreach (var candidate in candidates)
            {
                long received;
                RetainerSaleConfidence confidence;
                if (basis.Length == 0)
                {
                    received = -1;
                    confidence = RetainerSaleConfidence.Unknown;
                }
                else
                {
                    confidence = RetainerSaleConfidence.Sold;

                    // 只有一件候選時，實收就是實際觀察到的錢包增量（最精確的那個數字）。
                    // 多件時錢包只給得出總額，硬拆給每一列會是編造的，所以各列寫自己算得出的
                    // 期望值，而總額對得上是這一批被判成高信心的理由。
                    received = candidates.Count == 1
                        ? gilDelta
                        : Expected(candidate, taxPercent, basis);
                }

                rows.Add(new RetainerSaleRow(current.AtUtc, previous.AtUtc, previous.RetainerId,
                    current.RetainerName, candidate.ItemId, candidate.Hq, candidate.Quantity,
                    candidate.UnitPrice, received, confidence, basis, taxPercent, gilDelta));
            }

            return rows;
        }

        /// <summary>
        /// 錢包的增量對得上哪一種算法：<c>net</c>／<c>gross</c>／空（都對不上）。
        /// 任何一件候選算不出單價，就整批不判——半份資料湊出來的總額沒有意義。
        /// </summary>
        private static string MatchGil(List<Candidate> candidates, long gilDelta, int taxPercent)
        {
            if (gilDelta == long.MinValue || gilDelta <= 0)
                return string.Empty;

            long net = 0;
            long gross = 0;
            foreach (var candidate in candidates)
            {
                if (candidate.UnitPrice < 0)
                    return string.Empty;
                net += Expected(candidate, taxPercent, "net");
                gross += Expected(candidate, taxPercent, "gross");
            }

            // 容差：每一筆的扣稅都是整數除法，逐筆最多各差 1 gil。
            var tolerance = candidates.Count + 1;
            if (Math.Abs(gilDelta - net) <= tolerance)
                return "net";
            if (Math.Abs(gilDelta - gross) <= tolerance)
                return "gross";
            return string.Empty;
        }

        private static long Expected(Candidate candidate, int taxPercent, string basis)
        {
            var gross = candidate.UnitPrice * candidate.Quantity;
            if (basis != "net")
                return gross;
            var tax = Math.Clamp(taxPercent, 0, 25);
            return gross * (100 - tax) / 100;
        }

        /// <summary>把掛單清單整理成 (道具, 品質) → 各價位各幾件。</summary>
        private static Dictionary<(uint ItemId, bool Hq), Dictionary<long, int>> Group(
            List<RetainerListing> listings)
        {
            var result = new Dictionary<(uint, bool), Dictionary<long, int>>();
            foreach (var listing in listings)
            {
                var key = (listing.ItemId, listing.Hq);
                if (!result.TryGetValue(key, out var byPrice))
                {
                    byPrice = new Dictionary<long, int>();
                    result[key] = byPrice;
                }

                byPrice.TryGetValue(listing.UnitPrice, out var n);
                byPrice[listing.UnitPrice] = n + listing.Quantity;
            }

            return result;
        }
    }
}
