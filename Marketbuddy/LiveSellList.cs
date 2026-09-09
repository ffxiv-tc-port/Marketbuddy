using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Interface.Colors;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Marketbuddy.Common;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    /// <summary>
    /// 「即時出售品清單」——我們自己畫的雇員掛單表，貼在遊戲「出售品」視窗旁邊。
    ///
    /// 為什麼需要：批次改價是走 <c>InventoryManager.SetRetainerMarketPrice()</c> 直接寫容器的
    /// （刻意不開任何原生視窗，那才是它比手動快的原因），所以遊戲的 <c>RetainerSellList</c>
    /// **不會自己重繪**——畫面停在開窗當下的價格，剛上架的道具會一直顯示 999,999,999，
    /// 看起來像卡住。伺服器上的價格從頭到尾都是對的，這純粹是顯示問題。
    /// （2026-08-02 實測：對該 addon 呼叫 <c>OnRefresh(0, null)</c> 完全無效，那條路已經走過了。）
    ///
    /// 這裡的資料每一幀在 Framework.Update 上重新從 <c>InventoryManager</c> 讀進快照，
    /// 繪製時只吃快照，所以既永遠是最新的，也不會在繪製執行緒上呼叫任何遊戲函式。
    ///
    /// 🔴 刻意**不**照抄 DailyRoutines 的作法。DR 是把遊戲清單元件
    /// （<c>GetComponentListById(11)->OwnerNode</c>）<c>SetAlpha(0)</c> 藏起來，再把自己的
    /// ImGui 視窗蓋在原位、並每幀改寫遊戲視窗的 X/Y 去對齊自己。那需要三件我們無法離線證明的事：
    ///   (1) <c>AtkResNode.SetAlpha</c> 是**特徵碼解析的原生呼叫**，台服上解到錯的函式就是
    ///       AccessViolation，而 AVE 在 .NET Core 是 corrupted-state exception，try/catch 攔不到；
    ///   (2) 節點 ID 11 是國際服的值，台服沒有驗證過；
    ///   (3) 蓋住原生清單之後，原本點某一列改價的操作就必須由我們重新實作，一旦列高對不齊
    ///       就會「看起來點的是 A、實際改的是 B」。
    /// 換來的好處只是「版面更整齊」。所以這裡採取零原生寫入的版本：只讀 addon 的
    /// X / Y / RootNode 尺寸（全是已建模的純欄位，沒有任何特徵碼呼叫）來決定貼在哪裡，
    /// 把表畫在原生視窗**旁邊**而不是上面。原生視窗一切照舊可以點、可以拖，
    /// 這個面板跟著它跑；關掉設定就完全回到原本的行為。
    /// </summary>
    internal sealed unsafe class LiveSellList : IDisposable
    {
        /// <summary>Retainer market containers hold 20 slots; every index is bounds-checked against both ends.</summary>
        private const int MaxMarketSlots = 20;

        /// <summary>How long a repriced row keeps its "just changed" highlight.</summary>
        private static readonly TimeSpan ChangeHighlightFor = TimeSpan.FromMinutes(10);

        /// <summary>
        /// 「正在處理這一件」的列底色（半透明琥珀）。
        ///
        /// 刻意跟「已改價」那個綠色**分屬不同視覺通道**：進行中是整列底色，已改價是
        /// 單價欄的綠字。兩者可以同時出現在同一列（剛改完價、下一輪又輪到它）而不打架，
        /// 而且顏色本身也分得開——琥珀＝正在做，綠＝做完了。
        /// </summary>
        private static readonly Vector4 InProgressRowColor = new(1.00f, 0.72f, 0.15f, 0.22f);

        private readonly struct Row(short slot, string name, uint quantity, uint unitPrice)
        {
            public readonly short Slot = slot;
            public readonly string Name = name;
            public readonly uint Quantity = quantity;
            public readonly uint UnitPrice = unitPrice;

            /// <summary>這一件的「歷史最近賣出價」目前處在什麼狀態（查詢中／查不到／有值）。</summary>
            public LastSoldState SoldState { get; init; }

            /// <summary>成交單價；只有 <see cref="HasSold"/> 為 true 時才有意義。</summary>
            public long SoldUnitPrice { get; init; }

            public DateTime SoldAtUtc { get; init; }

            /// <summary>那一筆成交本身是優質品還是普通品（可能與這一格掛的品質不同——那正是「忽略優質」的意思）。</summary>
            public bool SoldHq { get; init; }

            /// <summary>可為 null：<c>default(Row)</c> 與「回應裡沒帶世界」都會落在這裡，畫面上一律畫成 <c>?</c>。</summary>
            public string? SoldWorld { get; init; }

            /// <summary>
            /// 真的取到了一筆成交紀錄。
            /// ⚠️ 與 <c>SoldState == Ready</c> <b>不等價</b>：Universalis 有這件的資料，但在
            /// 「不忽略優質」的設定下這個品質剛好沒有成交紀錄時，狀態是 Ready 而這裡是 false。
            /// </summary>
            public bool HasSold { get; init; }

            /// <summary>按下重掛會掛出去的價格（成交價無條件捨去到百位）。</summary>
            public uint RelistPrice { get; init; }

            /// <summary>本世界目前的最低掛售價；<b>null＝查不到，不是 0</b>。</summary>
            public MarketPricePoint? MinWorld { get; init; }

            /// <summary>整個資料中心目前的最低掛售價；<b>null＝查不到，不是 0</b>。</summary>
            public MarketPricePoint? MinDc { get; init; }

            /// <summary>這一批東西掛在架上多久了；<b>null＝完全沒有紀錄</b>（不是 0，也不是「剛剛」）。</summary>
            public ListingAge? Age { get; init; }

            /// <summary>
            /// 這件道具（<b>這個品質</b>）在我們自己的僱員銷售紀錄裡的彙總。
            /// ⚠️ <c>IsEmpty</c> 只代表「沒有事件」，<b>不代表「沒有資料」</b>——後者由
            /// <see cref="LiveSellList.historyState"/> 決定，兩者在畫面上必須分得出來。
            /// </summary>
            public ItemSaleHistory History { get; init; }

            /// <summary>同一個道具的<b>另一個品質</b>有沒有紀錄（只給滑鼠提示補一句用）。</summary>
            public bool HistoryOtherQuality { get; init; }
        }

        /// <summary>「賣出/下架」那一欄的資料基礎處在什麼狀態。四種在畫面上都要分得出來。</summary>
        private enum SalesHistoryState
        {
            /// <summary>記錄功能是關著的 ⇒ 沒有資料，而且不會有。</summary>
            Disabled,

            /// <summary>紀錄還在讀 ⇒ 畫「…」，不是「沒有」。</summary>
            Loading,

            /// <summary>從來沒看過這位僱員的出售品清單 ⇒ 畫「?」，<b>絕不是 0</b>。</summary>
            NoBaseline,

            /// <summary>有觀察基礎；數字可以相信，但觀察期仍然要在滑鼠提示裡講出來。</summary>
            Ready,
        }

        /// <summary>道具 id 的暫存集合，給「歷史最近賣出價」預取用；重用，不每幀配置。</summary>
        private readonly HashSet<uint> prefetchIds = [];

        /// <summary>上一次替出售品視窗裡的道具排查詢的時間。預取不必每幀做。</summary>
        private DateTime lastPrefetchAt = DateTime.MinValue;

        private static readonly TimeSpan PrefetchEvery = TimeSpan.FromSeconds(1);

        private readonly MarketGuiEventHandler gui;
        private readonly BatchReprice engine;

        // Snapshot built on the framework thread, consumed by Draw(). Nothing in the
        // draw path calls a game function or dereferences a game pointer other than the
        // addon's own plain position/size fields.
        private readonly List<Row> rows = [];
        private Vector2 anchor;
        private float nativeHeight;
        private bool haveAnchor;
        private bool containerReady;
        private ulong snapshotRetainerId;

        /// <summary>「這是哪一位僱員的掛單」——名稱＋鈴清單上的序號（2026-08-24 使用者需求）。
        /// 在 framework 執行緒隨快照一起算好，Draw 只讀字串。</summary>
        private string retainerLabel = string.Empty;

        /// <summary>「賣出/下架」那一欄的資料基礎；在 framework 執行緒隨快照算好，Draw 只讀。</summary>
        private SalesHistoryState historyState = SalesHistoryState.Loading;

        /// <summary>我們是從什麼時候開始看僱員清單的（觀察起點的下界）；MinValue＝不知道。</summary>
        private DateTime historySinceUtc = DateTime.MinValue;

        private Configuration conf => Configuration.GetOrLoad();

        public LiveSellList(MarketGuiEventHandler gui, BatchReprice engine)
        {
            this.gui = gui;
            this.engine = engine;
            Framework.Update += OnFrameworkUpdate;
        }

        public void Dispose()
        {
            Framework.Update -= OnFrameworkUpdate;
        }

        private void OnFrameworkUpdate(IFramework framework)
        {
            haveAnchor = false;

            // 「歷史最近賣出價」的預取。刻意排在下面那道顯示開關**之前**：重掛引擎自己也要用
            // 這份資料，它不該取決於使用者有沒有開這個面板。
            // 🔴 這只是一個對 Universalis 的唯讀 HTTP 查詢，不送任何遊戲內市場查詢、不改任何價格。
            //    定價方式關著（預設）時整條路徑一個位元組都不會動。
            if (conf.RelistUseLastSoldPrice && gui.IsRetainerSellListOpen)
                PrefetchLastSoldPrices();

            // 「這件賣掉過嗎」的彙總：讀檔與統計全部在執行緒池上，這裡只收工作。
            // 🔴 繪製路徑一個位元組的 I/O 都不做，而且只在那一欄真的會被畫出來時才推進。
            if (conf.LiveSellListOverlay && conf.LiveSellListSalesHistoryColumn &&
                conf.RetainerSalesLogEnabled && gui.IsRetainerSellListOpen)
                RetainerSalesHistory.Pump();

            if (!conf.LiveSellListOverlay || !gui.IsRetainerSellListOpen)
            {
                rows.Clear();
                return;
            }

            // Re-resolve the addon every tick; a native pointer is never kept across frames.
            var addon = Commons.GetUnitBase("RetainerSellList");
            if (addon == null || !addon->IsVisible || addon->RootNode == null ||
                addon->UldManager.LoadedState != AtkLoadState.Loaded)
            {
                rows.Clear();
                return;
            }

            // Geometry from plain modelled fields only - deliberately NOT GetScaledWidth()/
            // GetScaledHeight(), which are signature-resolved native calls we cannot verify
            // offline on the Taiwan client.
            var scale = addon->Scale;
            if (scale <= 0f || float.IsNaN(scale))
                scale = 1f;

            anchor = new Vector2(
                addon->X + addon->RootNode->Width * scale + conf.LiveSellListOffset.X,
                addon->Y + conf.LiveSellListOffset.Y);
            nativeHeight = addon->RootNode->Height * scale;
            haveAnchor = true;

            RebuildRows();
        }

        /// <summary>
        /// 把出售品視窗裡這一位僱員的道具排進「歷史最近賣出價」的查詢佇列。
        /// 🔴 只在 framework 執行緒上跑（它讀市場容器與 <c>PlayerState</c>）；
        /// 真正的 HTTP 在執行緒池上，見 <see cref="LastSoldPriceSource"/>。
        /// 每秒最多一次——已經有夠新答案的道具在那一側就會被濾掉，所以這裡只是省下容器掃描。
        /// </summary>
        private void PrefetchLastSoldPrices()
        {
            var now = DateTime.UtcNow;
            if (now - lastPrefetchAt < PrefetchEvery)
                return;
            lastPrefetchAt = now;

            var inventoryManager = InventoryManager.Instance();
            var container = inventoryManager == null
                ? null
                : inventoryManager->GetInventoryContainer(InventoryType.RetainerMarket);
            if (container == null || !container->IsLoaded)
                return;

            prefetchIds.Clear();
            var slotCount = Math.Min((int)container->Size, MaxMarketSlots);
            for (var i = 0; i < slotCount; i++)
            {
                var slot = inventoryManager->GetInventorySlot(InventoryType.RetainerMarket, i);
                if (slot == null || slot->ItemId == 0)
                    continue;
                prefetchIds.Add(slot->ItemId);
            }

            if (prefetchIds.Count == 0)
                return;

            var worldId = PlayerState.ContentId == 0 ? 0u : PlayerState.CurrentWorld.RowId;
            LastSoldPriceSource.Request(worldId, prefetchIds);
        }

        private void RebuildRows()
        {
            rows.Clear();
            snapshotRetainerId = BatchReprice.ActiveRetainerId();
            retainerLabel = string.Empty;

            // 名稱＋序號：序號用鈴清單的排序（GetRetainerBySortedIndex），與玩家看到的順序一致。
            // 面板只在出售品視窗開著時存在，此時 LastSelectedRetainerId 就是眼前這一位，沒有陳舊問題。
            var retainerManager = FFXIVClientStructs.FFXIV.Client.Game.RetainerManager.Instance();
            if (snapshotRetainerId != 0 && retainerManager != null && retainerManager->IsReady)
            {
                var count = retainerManager->GetRetainerCount();
                for (var i = 0u; i < count; i++)
                {
                    var retainer = retainerManager->GetRetainerBySortedIndex(i);
                    if (retainer != null && retainer->RetainerId == snapshotRetainerId)
                    {
                        retainerLabel = "?? (No.??)".Loc(retainer->NameString, i + 1);
                        break;
                    }
                }
            }

            // 「賣出/下架」那一欄的資料基礎。🔑 這幾個判斷的唯一目的是讓
            // 「我們沒看過」與「看過但沒賣掉」在畫面上分得出來——兩者長得一模一樣，
            // 而把前者畫成 0 會讓使用者做出相反的決定。
            var showHistory = conf.LiveSellListSalesHistoryColumn;
            var history = SalesHistorySnapshot.Empty;
            historySinceUtc = DateTime.MinValue;
            if (!showHistory || !conf.RetainerSalesLogEnabled)
            {
                historyState = SalesHistoryState.Disabled;
            }
            else if (!RetainerSalesHistory.Ready || !RetainerListingAge.Loaded)
            {
                historyState = SalesHistoryState.Loading;
            }
            else
            {
                history = RetainerSalesHistory.Current;
                historySinceUtc = RetainerListingAge.EarliestSeen() ?? DateTime.MinValue;
                historyState = RetainerListingAge.FirstSeen(snapshotRetainerId) == null
                    ? SalesHistoryState.NoBaseline
                    : SalesHistoryState.Ready;
            }

            var inventoryManager = InventoryManager.Instance();
            var container = inventoryManager == null
                ? null
                : inventoryManager->GetInventoryContainer(InventoryType.RetainerMarket);
            containerReady = container != null && container->IsLoaded;
            if (!containerReady)
                return;

            // Bounds: container->Size is the authority, but never index past the 20 slots
            // GetRetainerMarketPrice() is defined for - both ends are checked.
            var slotCount = Math.Min((int)container->Size, MaxMarketSlots);
            var itemSheet = DataManager.GetExcelSheet<Item>();

            for (var i = 0; i < slotCount; i++)
            {
                var slot = inventoryManager->GetInventorySlot(InventoryType.RetainerMarket, i);
                if (slot == null || slot->ItemId == 0)
                    continue;

                var name = $"#{slot->ItemId}";
                if (itemSheet != null && itemSheet.TryGetRow(slot->ItemId, out var row))
                    name = row.Name.ExtractText();
                if ((slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0)
                    name += $" {(char)SeIconChar.HighQuality}";

                var isHq = (slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
                var soldState = LastSoldState.Unknown;
                var hasSold = false;
                LastSoldEntry sold = default;
                uint relistPrice = 0;
                MarketPricePoint? minWorld = null;
                MarketPricePoint? minDc = null;
                if (conf.RelistUseLastSoldPrice || conf.LiveSellListMarketColumns)
                {
                    soldState = LastSoldPriceSource.StateOf(slot->ItemId);
                    hasSold = LastSoldPriceSource.TryGet(slot->ItemId, isHq,
                        conf.RelistLastSoldIgnoreQuality, out sold);
                    if (hasSold)
                        relistPrice = LastSoldPriceSource.RoundDownToHundred(sold.UnitPrice);
                    LastSoldPriceSource.TryGetMinPrices(slot->ItemId, isHq, out minWorld, out minDc);
                }

                // 這個品質自己的紀錄；另一個品質只用來在滑鼠提示裡補一句
                // （優質與普通在市場上是兩件不同的商品，數字刻意不合併）。
                var itemHistory = default(ItemSaleHistory);
                var otherQuality = false;
                if (showHistory && historyState != SalesHistoryState.Disabled)
                {
                    itemHistory = history.Get(slot->ItemId, isHq);
                    var anyQuality = history.GetAnyQuality(slot->ItemId);
                    otherQuality = anyQuality.SoldEvents > itemHistory.SoldEvents ||
                                   anyQuality.OffBoardEvents > itemHistory.OffBoardEvents;
                }

                rows.Add(new Row(
                    (short)i,
                    name,
                    (uint)Math.Max(0, slot->Quantity),
                    (uint)inventoryManager->GetRetainerMarketPrice((short)i))
                {
                    SoldState = soldState,
                    HasSold = hasSold,
                    SoldUnitPrice = hasSold ? sold.UnitPrice : 0,
                    SoldAtUtc = hasSold ? sold.SoldAtUtc : DateTime.MinValue,
                    SoldHq = hasSold && sold.Hq,
                    SoldWorld = hasSold ? sold.World : string.Empty,
                    RelistPrice = relistPrice,
                    MinWorld = minWorld,
                    MinDc = minDc,
                    Age = conf.LiveSellListMarketColumns
                        ? RetainerListingAge.Get(snapshotRetainerId, slot->ItemId, isHq)
                        : null,
                    History = itemHistory,
                    HistoryOtherQuality = otherQuality,
                });
            }
        }

        /// <summary>
        /// 這個面板貼在原生出售品視窗的右邊，而重掛面板（同一欄、在它上面）高度是會變的，
        /// 所以起點不能寫死：<paramref name="stackUnderY"/> 是重掛面板**這一幀量到的下緣**，
        /// 由 <see cref="PluginUI.Draw"/> 在畫完它之後立刻傳進來。傳 null 代表重掛面板
        /// 這一幀沒出現，此時退回原本的「貼齊原生視窗右上角」。
        /// </summary>
        /// <param name="stackUnderY">重掛面板下緣的螢幕 Y 座標，或 null。</param>
        public void Draw(float? stackUnderY = null)
        {
            if (!conf.LiveSellListOverlay || !haveAnchor)
                return;

            const float stackGap = 4f;
            var position = anchor;
            if (stackUnderY is { } top)
                position.Y = top + stackGap;

            // 高度上限扣掉被上面那塊吃掉的垂直空間，這樣整欄加起來還是大致貼齊原生視窗；
            // 200 px 的地板保留不動，否則重掛面板很高時這裡會被壓到看不見東西。
            var availableHeight = nativeHeight - (position.Y - anchor.Y);

            ImGui.SetNextWindowPos(position);
            ImGui.SetNextWindowSizeConstraints(
                new Vector2(220, 80),
                new Vector2(float.MaxValue, Math.Max(200f, availableHeight)));

            if (!ImGui.Begin("Marketbuddy_livesellist",
                    ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                    ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize |
                    ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoSavedSettings))
            {
                ImGui.End();
                return;
            }

            DrawHeader();
            DrawTable();

            ImGui.End();
        }

        private void DrawHeader()
        {
            // 「（永遠是最新的）」是**說明**不是標題：掛在標題後面會把這個小面板的
            // 標題撐成兩行，白白吃掉本來要留給清單的垂直空間。移進滑鼠提示。
            ImGui.TextUnformatted("Live listings".Loc());
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Re-read every frame - always shows the current prices".Loc());
            if (retainerLabel.Length > 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(ImGuiColors.DalamudOrange, retainerLabel);
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("x##mblivesellistclose"))
            {
                conf.LiveSellListOverlay = false;
                conf.Save();
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Hide this panel (re-enable it in /mbuddy)".Loc());
        }

        private void DrawTable()
        {
            if (!containerReady)
            {
                ImGui.TextUnformatted("Waiting for the retainer's data...".Loc());
                return;
            }

            if (rows.Count == 0)
            {
                ImGui.TextUnformatted("Nothing listed.".Loc());
                return;
            }

            var changes = engine.RecentChanges;
            var changesApply = snapshotRetainerId != 0 && engine.RecentChangesRetainerId == snapshotRetainerId;

            // 「此刻正在被處理的那一格」。批次重掛與快速上架的單件定價走的是同一個引擎，
            // 所以這一個判斷就同時涵蓋兩條路徑（見 BatchReprice.CurrentSlot）。
            // ⚠️ 用**格號**比對而不是道具名：同款道具拆成好幾格掛是常態，比名字會一次亮好幾列。
            var activeSlot = engine.IsRunning && snapshotRetainerId != 0 &&
                             engine.CurrentBatchRetainerId == snapshotRetainerId
                ? engine.CurrentSlot
                : (short)-1;

            // 「重掛後的金額」只有在那個定價方式開著時才有意義，關著時不占版面。
            var showRelist = conf.RelistUseLastSoldPrice;
            var showMarket = conf.LiveSellListMarketColumns;
            var showHistory = conf.LiveSellListSalesHistoryColumn;
            var columns = 4 + (showRelist ? 1 : 0) + (showMarket ? 4 : 0) + (showHistory ? 1 : 0);

            if (!ImGui.BeginTable("##mblivesellisttable", columns,
                    ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
                return;

            ImGui.TableSetupColumn("Item".Loc(), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Qty".Loc());
            ImGui.TableSetupColumn("Unit price".Loc());
            ImGui.TableSetupColumn("Total".Loc());
            if (showMarket)
            {
                ImGui.TableSetupColumn("Lowest here".Loc());
                ImGui.TableSetupColumn("Lowest on DC".Loc());
                ImGui.TableSetupColumn("Last sale".Loc());
                ImGui.TableSetupColumn("Listed for".Loc());
            }

            // 刻意緊接在「已掛售多久」後面：「掛了 12 天／賣出 0 次」要一起讀才有意義。
            if (showHistory)
                ImGui.TableSetupColumn("Sold / off the board".Loc());

            if (showRelist)
                ImGui.TableSetupColumn("Relist at".Loc());
            ImGui.TableHeadersRow();

            foreach (var row in rows)
            {
                ImGui.TableNextRow();

                // 進行中的那一列：整列底色 + 道具名改成琥珀色。
                // 目前處理中的道具**不在**這張表裡時（例如快速上架還沒把它掛上去、
                // 或引擎的目標格已經空掉）就什麼都不亮——刻意不退而求其次去比對名稱，
                // 硬找一列亮起來只會亮到錯的那一件。
                var inProgress = row.Slot == activeSlot;
                if (inProgress)
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1,
                        ImGui.ColorConvertFloat4ToU32(InProgressRowColor));

                ImGui.TableNextColumn();
                if (inProgress)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudYellow);
                    ImGui.TextUnformatted(row.Name);
                    ImGui.PopStyleColor();
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Working on this one right now".Loc());
                }
                else
                {
                    ImGui.TextUnformatted(row.Name);
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Quantity.ToString("N0"));

                ImGui.TableNextColumn();
                var changed = changesApply &&
                              changes.TryGetValue(row.Slot, out var change) &&
                              change.NewPrice == row.UnitPrice &&
                              DateTime.UtcNow - change.At <= ChangeHighlightFor;
                if (changed)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.HealerGreen);
                    ImGui.TextUnformatted(row.UnitPrice.ToString("N0"));
                    ImGui.PopStyleColor();
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Repriced by Marketbuddy: ?? → ??".Loc(
                            changes[row.Slot].OldPrice.ToString("N0"), row.UnitPrice.ToString("N0")));
                }
                else
                {
                    ImGui.TextUnformatted(row.UnitPrice.ToString("N0"));
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(((ulong)row.UnitPrice * row.Quantity).ToString("N0"));

                if (showMarket)
                {
                    ImGui.TableNextColumn();
                    DrawMinPriceCell(row, row.MinWorld, forDatacentre: false);

                    ImGui.TableNextColumn();
                    DrawMinPriceCell(row, row.MinDc, forDatacentre: true);

                    ImGui.TableNextColumn();
                    DrawLastSaleCell(row);

                    ImGui.TableNextColumn();
                    DrawAgeCell(row);
                }

                if (showHistory)
                {
                    ImGui.TableNextColumn();
                    DrawHistoryCell(row);
                }

                if (showRelist)
                {
                    ImGui.TableNextColumn();
                    DrawRelistCell(row);
                }
            }

            ImGui.EndTable();
        }

        /// <summary>
        /// 「按下重掛會掛出去的金額」那一格。
        ///
        /// <para>
        /// 🔴 「查詢中」與「查不到」是<b>兩種不同的狀態，而且都要在列上看得見</b>：
        /// 任何一種畫成 0 或空白都會被讀成「這件賣過 0 gil」，而讀不到當成 0 在艦隊裡
        /// 已經害過一次。所以這裡一律用灰色的 <c>…</c> 與 <c>?</c>，理由放滑鼠提示。
        /// </para>
        /// <para>
        /// 📌 純顯示：這一格不會去改任何價格，改價一律是使用者自己按重掛按鈕。
        /// </para>
        /// </summary>
        private static void DrawRelistCell(in Row row)
        {
            if (row.HasSold)
            {
                ImGui.TextUnformatted(row.RelistPrice.ToString("N0"));
                if (!ImGui.IsItemHovered())
                    return;

                var world = string.IsNullOrEmpty(row.SoldWorld) ? "?" : row.SoldWorld;
                ImGui.SetTooltip(
                    "Last sold for ?? gil on ?? (??, ??) - rounded down to ?? gil".Loc(
                        row.SoldUnitPrice.ToString("N0"),
                        BatchReprice.FormatSoldAt(row.SoldAtUtc),
                        row.SoldHq ? "HQ".Loc() : "NQ".Loc(),
                        world,
                        row.RelistPrice.ToString("N0")));
                return;
            }

            switch (row.SoldState)
            {
                case LastSoldState.Unknown:
                case LastSoldState.Loading:
                    Grey("...");
                    Tooltip("Asking Universalis for the most recent sale...".Loc());
                    break;

                case LastSoldState.Failed:
                    Grey("!");
                    Tooltip("Universalis could not be reached. This item keeps its usual pricing until the lookup succeeds.".Loc());
                    break;

                default:
                    // Ready 但取不到 = 「不忽略優質」而這個品質沒有成交紀錄；NoData = 完全沒有紀錄。
                    Grey("?");
                    Tooltip(
                        "No sale on record for this item on the data centre, so relisting leaves it on the usual pricing (lowest listing minus your undercut)."
                            .Loc());
                    break;
            }
        }

        /// <summary>
        /// 「目前最低價」那一格（本世界／整個資料中心各一欄）。
        ///
        /// <para>
        /// 🔴 沒有資料一律畫成灰色的 <c>?</c>（查詢中是 <c>…</c>）——<b>絕不畫成 0</b>。
        /// 一個 0 gil 的最低價是合法但荒謬的數字，使用者會照它去決定要不要降價。
        /// </para>
        /// <para>
        /// ⚠️ 這一格照的是<b>這一格自己的品質</b>，與「忽略優質狀態」那個選項無關。
        /// </para>
        /// </summary>
        private static void DrawMinPriceCell(in Row row, MarketPricePoint? point, bool forDatacentre)
        {
            if (point is { } value)
            {
                ImGui.TextUnformatted(value.UnitPrice.ToString("N0"));
                if (!ImGui.IsItemHovered())
                    return;

                var scope = forDatacentre
                    ? "the whole data centre".Loc()
                    : "this world".Loc();
                var where = string.IsNullOrEmpty(value.World) ? "?" : value.World;
                ImGui.SetTooltip(
                    "Lowest listing on ?? for this quality: ?? gil (??). Universalis data - only as fresh as the last upload."
                        .Loc(scope, value.UnitPrice.ToString("N0"), where));
                return;
            }

            DrawMissing(row.SoldState,
                "Nobody has this listed there right now, as far as Universalis knows.".Loc());
        }

        /// <summary>
        /// 「最近一次成交」那一格：價格 ＋ 灰色的日期。
        ///
        /// <para>
        /// 🔑 日期畫在<b>列上</b>而不是滑鼠提示：使用者要的就是「什麼時候賣掉的」，
        /// 一筆三個月前的成交價和昨天的成交價意義完全不同，而那件事不能藏起來。
        /// </para>
        /// <para>
        /// 📌 這個時間是 Universalis 記錄的<b>成交時間</b>（由玩家端上傳），
        /// 不是我們自己觀察到的「它從架上消失」的時間。
        /// </para>
        /// </summary>
        private static void DrawLastSaleCell(in Row row)
        {
            if (!row.HasSold)
            {
                DrawMissing(row.SoldState, "No sale on record for this item.".Loc());
                return;
            }

            ImGui.TextUnformatted(row.SoldUnitPrice.ToString("N0"));
            ImGui.SameLine();
            Grey(row.SoldAtUtc == DateTime.MinValue
                ? "?"
                : row.SoldAtUtc.ToLocalTime().ToString("MM-dd"));
            if (!ImGui.IsItemHovered())
                return;

            var world = string.IsNullOrEmpty(row.SoldWorld) ? "?" : row.SoldWorld;
            ImGui.SetTooltip(
                "Sold for ?? gil on ?? (??, ??). This is the sale time Universalis recorded, not the moment we noticed it."
                    .Loc(row.SoldUnitPrice.ToString("N0"), BatchReprice.FormatSoldAt(row.SoldAtUtc),
                        row.SoldHq ? "HQ".Loc() : "NQ".Loc(), world));
        }

        /// <summary>
        /// 「已經掛多久了」那一格。
        ///
        /// <para>
        /// 🔴 <b>精確值與暫定值必須看得出差別</b>：暫定值前面加上 <c>~</c>，理由寫在滑鼠提示裡。
        /// 遊戲不會告訴我們一筆掛售是什麼時候掛上去的，所以只有「我們親眼看著它出現」那一種
        /// 才是精確的；其餘是拿外掛載入時間暫定的。兩者長得一樣的話，使用者無從判斷該不該相信它。
        /// </para>
        /// <para>
        /// 🔴 完全沒有紀錄時是灰色的 <c>?</c>，<b>不是 0 也不是「剛剛」</b>。
        /// </para>
        /// </summary>
        private static void DrawAgeCell(in Row row)
        {
            if (row.Age is not { } age)
            {
                Grey("?");
                Tooltip(
                    "Not recorded yet. Marketbuddy starts the clock the first time it sees this retainer's sell list."
                        .Loc());
                return;
            }

            var span = DateTime.UtcNow - age.SinceUtc;
            if (span < TimeSpan.Zero)
                span = TimeSpan.Zero;

            var text = FormatSpan(span);
            if (age.Exact)
            {
                ImGui.TextUnformatted(text);
                Tooltip("Listed since ?? (Marketbuddy watched it go up).".Loc(
                    age.SinceUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")));
                return;
            }

            Grey("~" + text);
            Tooltip(
                "Approximate: it was already listed when Marketbuddy first looked, so the clock starts from when the plugin loaded (??), not from when you actually listed it."
                    .Loc(age.SinceUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")));
        }

        /// <summary>
        /// 「這件東西以前賣掉過嗎」那一格：<c>賣出次數 / 沒賣掉就下架的次數</c>。
        ///
        /// <para>
        /// 🔴 <b>四種「沒有數字」必須互相分得出來，而且一個都不可以畫成 0</b>：
        /// <list type="bullet">
        ///   <item><c>?</c>（記錄功能關著）＝沒有資料，而且不會有。</item>
        ///   <item><c>…</c>（還在讀紀錄）＝等一下就有。</item>
        ///   <item><c>?</c>（從沒看過這位僱員）＝<b>我們沒看過</b>，不是「賣不掉」。</item>
        ///   <item><c>—</c>（看過，但這件從來沒有異動）＝它就一直掛在那裡沒動過。</item>
        /// </list>
        /// 把前三種畫成 0 會讓使用者讀成「這件賣不掉」而做出相反的決定——
        /// 「讀不到當成 0」在艦隊裡已經害過一次。
        /// </para>
        ///
        /// <para>
        /// 🔴 第二個數字裡<b>絕不含賣出</b>：它是「下架＋消失但對不上金幣」。信心不足的那些
        /// （<see cref="RetainerSaleConfidence.Unknown"/>）一律不算賣出，理由寫在滑鼠提示裡。
        /// </para>
        ///
        /// <para>
        /// 📌 純顯示：這一格不會因為「賣不掉」自動下架、自動降價或做任何事。
        /// </para>
        /// </summary>
        private void DrawHistoryCell(in Row row)
        {
            switch (historyState)
            {
                case SalesHistoryState.Disabled:
                    Grey("?");
                    Tooltip(
                        "\"Record what disappears from your retainers' listings\" is off, so there is nothing to count. This means \"not watched\", not \"never sold\"."
                            .Loc());
                    return;

                case SalesHistoryState.Loading:
                    Grey("...");
                    Tooltip("Reading the sales history...".Loc());
                    return;
            }

            var history = row.History;
            if (history.IsEmpty)
            {
                if (historyState == SalesHistoryState.NoBaseline)
                {
                    Grey("?");
                    Tooltip(
                        "Marketbuddy has never had this retainer's sell list open before, so nothing is known about this item yet. That is not the same as \"it never sells\"."
                            .Loc());
                    return;
                }

                Grey("—");
                Tooltip(
                    "Nothing has happened to this item in the records kept since ??: it has just been sitting on the board."
                        .Loc(FormatHistoryCoverage()));
                return;
            }

            ImGui.BeginGroup();
            if (history.SoldEvents > 0)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.HealerGreen);
                ImGui.TextUnformatted(history.SoldEvents.ToString("N0"));
                ImGui.PopStyleColor();
            }
            else
            {
                Grey("0");
            }

            ImGui.SameLine(0, 0);
            Grey(" / " + history.OffBoardEvents.ToString("N0"));
            ImGui.EndGroup();
            Tooltip(BuildHistoryTooltip(row));
        }

        /// <summary>「賣出/下架」那一格的滑鼠提示。長說明放這裡，列上只留掃視得出來的兩個數字。</summary>
        private string BuildHistoryTooltip(in Row row)
        {
            var history = row.History;
            var lines = new List<string>
            {
                "Records from all your retainers since ??: sold ?? time(s), ?? item(s), ?? gil received."
                    .Loc(FormatHistoryCoverage(), history.SoldEvents, history.SoldQuantity,
                        history.SoldGil.ToString("N0")),
            };

            if (history.SoldGilIncomplete)
                lines.Add("Some of those sales have no amount, so the total is a lower bound.".Loc());

            if (history.LastSoldUtc != DateTime.MinValue)
                lines.Add("Last sold ??.".Loc(BatchReprice.FormatSoldAt(history.LastSoldUtc)));

            lines.Add(
                "Came off the board without a sale ?? time(s): ?? delisted by Marketbuddy, ?? that just went with no matching gil (deliberately never counted as sales)."
                    .Loc(history.OffBoardEvents, history.DelistedEvents, history.UnknownEvents));

            if (row.HistoryOtherQuality)
                lines.Add("The other quality of this item has records of its own; these numbers are for this quality only.".Loc());

            return string.Join("\n", lines);
        }

        /// <summary>觀察期：從什麼時候起算、到現在多久。🔑 少了它，「賣出 0 次」看不出可不可信。</summary>
        private string FormatHistoryCoverage()
        {
            if (historySinceUtc == DateTime.MinValue)
                return "?";

            var span = DateTime.UtcNow - historySinceUtc;
            if (span < TimeSpan.Zero)
                span = TimeSpan.Zero;

            var length = span.TotalDays >= 1
                ? "?? day(s)".Loc((int)span.TotalDays)
                : span.TotalHours >= 1
                    ? "?? hour(s)".Loc((int)span.TotalHours)
                    : "less than an hour".Loc();

            return "?? (??)".Loc(historySinceUtc.ToLocalTime().ToString("yyyy-MM-dd"), length);
        }

        /// <summary>把一段時間寫成「3天」「5小時」「12分」。刻意不進位到「1個月」——那太模糊。</summary>
        private static string FormatSpan(TimeSpan span)
        {
            if (span.TotalDays >= 1)
                return "??d".Loc((int)span.TotalDays);
            if (span.TotalHours >= 1)
                return "??h".Loc((int)span.TotalHours);
            return "??m".Loc((int)span.TotalMinutes);
        }

        /// <summary>「查詢中」「連不上」「沒有資料」三種缺席狀態的統一畫法——三種都必須分得出來。</summary>
        private static void DrawMissing(LastSoldState state, string noDataTooltip)
        {
            switch (state)
            {
                case LastSoldState.Unknown:
                case LastSoldState.Loading:
                    Grey("...");
                    Tooltip("Asking Universalis for the most recent sale...".Loc());
                    break;

                case LastSoldState.Failed:
                    Grey("!");
                    Tooltip("Universalis could not be reached. This item keeps its usual pricing until the lookup succeeds.".Loc());
                    break;

                default:
                    Grey("?");
                    Tooltip(noDataTooltip);
                    break;
            }
        }

        private static void Grey(string text)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextUnformatted(text);
            ImGui.PopStyleColor();
        }

        private static void Tooltip(string text)
        {
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(text);
        }
    }
}
