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
        }

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

        private void RebuildRows()
        {
            rows.Clear();
            snapshotRetainerId = BatchReprice.ActiveRetainerId();

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

                rows.Add(new Row(
                    (short)i,
                    name,
                    (uint)Math.Max(0, slot->Quantity),
                    (uint)inventoryManager->GetRetainerMarketPrice((short)i)));
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

            if (!ImGui.BeginTable("##mblivesellisttable", 4,
                    ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
                return;

            ImGui.TableSetupColumn("Item".Loc(), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Qty".Loc());
            ImGui.TableSetupColumn("Unit price".Loc());
            ImGui.TableSetupColumn("Total".Loc());
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
            }

            ImGui.EndTable();
        }
    }
}
