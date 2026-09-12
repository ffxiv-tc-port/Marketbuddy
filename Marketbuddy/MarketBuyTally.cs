using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Interface.Colors;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Marketbuddy.Common;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    /// <summary>
    /// 「這一趟買到哪了」——貼在市場板搜尋結果視窗旁邊的計數面板。
    /// 🔴 <b>純顯示，而且刻意沒有「一鍵買到 N 件」。</b>那會是「按一下帶出一串市場操作」，
    /// 風險完全不同：改錯價可以改回來，買錯買不回來。所以這裡一件東西都不買、一列都不點，
    /// 只把使用者本來就要自己算的兩個數字算給他看。
    /// ⚠️ <c>Listings</c> 的布局在台服 7.20 沒有被證實過，所以只讀純整數欄位、<b>絕不</b>去解那個指標欄
    /// （解錯的指標是 AccessViolation，而 AVE 在 .NET Core 上 try/catch 攔不到）、索引一律夾在 0..100，
    /// 並且每一列的 <c>ItemId</c> 必須等於 <c>SearchItemId</c> ⇒ 偏移只要錯一格就整塊不顯示，而不是遊戲崩掉。
    /// </summary>
    internal sealed unsafe class MarketBuyTally : IDisposable
    {
        /// <summary>
        /// <c>InfoProxyItemSearch._listings</c> 是 <c>FixedSizeArray100</c>。
        /// 🔴 這是硬上限，不是建議值：<c>ListingCount</c> 一律夾到這裡面。
        /// </summary>
        private const int MaxListings = 100;

        /// <summary>合理的單價上限（＝遊戲自己的掛售價上限）。超出就當作「讀到的不是單價」。</summary>
        private const uint SaneUnitPrice = 999_999_999;

        /// <summary>合理的件數上限。一格最多 999 個，這裡放寬一位數當雜訊裕度。</summary>
        private const uint SaneQuantity = 9_999;

        /// <summary>離開市場板多久之後，「這一趟」自己歸零。</summary>
        private static readonly TimeSpan ResetAfterAway = TimeSpan.FromMinutes(5);

        /// <summary>累計表裡「湊夠了」的那一列的底色（半透明綠）。</summary>
        private static readonly Vector4 TargetRowColor = new(0.20f, 0.80f, 0.35f, 0.22f);

        /// <summary>這一頁的掛單明細處在什麼狀態。四種在畫面上都要分得出來。</summary>
        private enum PreviewState
        {
            /// <summary>還沒查過任何東西，或 proxy 還沒準備好 ⇒ 畫「等結果」，不是「沒有」。</summary>
            Unavailable,

            /// <summary>
            /// 讀到的東西通不過自我校驗 ⇒ 這個客戶端上對不齊，畫灰字說明。
            /// 🔴 絕不退而求其次把讀到的數字畫出來：一個自信的錯誤金額比沒有金額更糟。
            /// </summary>
            Untrusted,

            /// <summary>這件東西現在市場上一張掛單都沒有。</summary>
            Empty,

            /// <summary>可以相信。</summary>
            Ready,
        }

        private readonly struct PreviewRow(uint unitPrice, uint quantity, bool hq)
        {
            public readonly uint UnitPrice = unitPrice;
            public readonly uint Quantity = quantity;
            public readonly bool Hq = hq;

            public long Total => (long)UnitPrice * Quantity;
        }

        /// <summary>某一件道具在這一趟裡買了多少。</summary>
        private sealed class ItemTally
        {
            public long Quantity;
            public long Gil;
            public int Purchases;
        }

        private static readonly Comparison<PreviewRow> ByUnitPrice =
            static (a, b) => a.UnitPrice.CompareTo(b.UnitPrice);

        private readonly MarketGuiEventHandler gui;

        // ---- 本趟累計（只在 framework 執行緒上寫，Draw 只讀）----

        private readonly Dictionary<uint, ItemTally> perItem = [];
        private long tripQuantity;
        private long tripGil;
        private int tripPurchases;
        private DateTime tripStartedUtc = DateTime.MinValue;

        /// <summary>市場板關掉之後過了多久；<c>MinValue</c> ＝ 現在還開著（或還沒買過東西）。</summary>
        private DateTime awaySinceUtc = DateTime.MinValue;

        // ---- 這一頁的快照（framework 執行緒建，Draw 只讀）----

        private readonly List<PreviewRow> preview = [];
        private PreviewState previewState = PreviewState.Unavailable;
        private uint previewItemId;
        private string previewItemName = string.Empty;

        private Vector2 anchor;
        private float nativeHeight;
        private bool haveAnchor;

        /// <summary>
        /// 使用者要湊幾件（0 ＝ 不設）。刻意<b>不存檔</b>：那是「我現在想買幾個」，
        /// 不是一個設定，下次進遊戲不該還記著上次的數字。
        /// </summary>
        private int target;

        /// <summary>自我校驗失敗只記一次，免得每幀洗 log。</summary>
        private bool untrustedLogged;

        private Configuration conf => Configuration.GetOrLoad();

        public MarketBuyTally(MarketGuiEventHandler gui)
        {
            this.gui = gui;
            Framework.Update += OnFrameworkUpdate;
        }

        public void Dispose()
        {
            Framework.Update -= OnFrameworkUpdate;
        }

        /// <summary>
        /// 記下一筆真的成交的市場購買。
        /// 🔴 只由 <see cref="MarketPurchaseWatcher"/> 在 framework 執行緒上呼叫——
        /// 這裡刻意<b>不自己輪詢</b>：兩套偵測器遲早會對不起來，而「買了幾件」對不起來
        /// 就是這個面板唯一的價值消失。
        /// </summary>
        internal void Record(uint itemId, uint quantity, uint unitPrice)
        {
            // 讀到明顯不是購買的東西就不要記。寧可少記一筆，也不要把總額弄成天文數字。
            if (itemId == 0 || quantity == 0 || quantity > SaneQuantity ||
                unitPrice == 0 || unitPrice > SaneUnitPrice)
                return;

            if (tripPurchases == 0)
                tripStartedUtc = DateTime.UtcNow;

            var gil = (long)unitPrice * quantity;
            tripQuantity += quantity;
            tripGil += gil;
            tripPurchases++;

            if (!perItem.TryGetValue(itemId, out var tally))
            {
                tally = new ItemTally();
                perItem[itemId] = tally;
            }

            tally.Quantity += quantity;
            tally.Gil += gil;
            tally.Purchases++;

            awaySinceUtc = DateTime.MinValue;
        }

        private void Reset()
        {
            perItem.Clear();
            tripQuantity = 0;
            tripGil = 0;
            tripPurchases = 0;
            tripStartedUtc = DateTime.MinValue;
            awaySinceUtc = DateTime.MinValue;
            target = 0;
        }

        private void OnFrameworkUpdate(IFramework framework)
        {
            haveAnchor = false;

            TickTripExpiry();

            if (!conf.MarketBuyTallyOverlay || !gui.IsItemSearchResultOpen)
            {
                preview.Clear();
                previewState = PreviewState.Unavailable;
                return;
            }

            // 🔴 每一幀重新解析 addon；原生指標一格都不跨幀保存。
            var addon = Commons.GetUnitBase("ItemSearchResult");
            if (addon == null || !addon->IsVisible || addon->RootNode == null ||
                addon->UldManager.LoadedState != AtkLoadState.Loaded)
            {
                preview.Clear();
                previewState = PreviewState.Unavailable;
                return;
            }

            // 幾何只用已建模的純欄位，刻意**不**用 GetScaledWidth()/GetScaledHeight()——
            // 那兩個是特徵碼解析的原生呼叫，台服上解到錯的函式就是 AccessViolation。
            var scale = addon->Scale;
            if (scale <= 0f || float.IsNaN(scale))
                scale = 1f;

            anchor = new Vector2(
                addon->X + addon->RootNode->Width * scale + conf.MarketBuyPanelOffset.X,
                addon->Y + conf.MarketBuyPanelOffset.Y);
            nativeHeight = addon->RootNode->Height * scale;
            haveAnchor = true;

            RebuildPreview();
        }

        /// <summary>
        /// 「這一趟」的到期判斷。人離開市場板一段時間就算是另一趟了，否則下次回來看到的
        /// 是上次的總額——那比沒有數字更糟。
        /// </summary>
        private void TickTripExpiry()
        {
            if (tripPurchases == 0)
            {
                awaySinceUtc = DateTime.MinValue;
                return;
            }

            // 市場板本體或搜尋結果任一個開著，就還在這一趟裡。
            var boardOpen = gui.IsItemSearchResultOpen || Commons.GetUnitBase("ItemSearch") != null;
            if (boardOpen)
            {
                awaySinceUtc = DateTime.MinValue;
                return;
            }

            var now = DateTime.UtcNow;
            if (awaySinceUtc == DateTime.MinValue)
            {
                awaySinceUtc = now;
                return;
            }

            if (now - awaySinceUtc >= ResetAfterAway)
                Reset();
        }

        /// <summary>
        /// 把這一頁的掛單抄成自己的快照。
        /// 🔴 全程唯讀，而且每一列都要通過自我校驗才收；任何一列不過就整塊放棄
        /// （見類別說明的第 3 點）。
        /// </summary>
        private void RebuildPreview()
        {
            preview.Clear();
            previewState = PreviewState.Unavailable;
            previewItemId = 0;
            previewItemName = string.Empty;

            var infoModule = InfoModule.Instance();
            if (infoModule == null)
                return;

            var proxy = (InfoProxyItemSearch*)infoModule->GetInfoProxyById(InfoProxyId.ItemSearch);
            if (proxy == null)
                return;

            var searchItemId = proxy->SearchItemId;
            if (searchItemId == 0)
                return;

            // 查不到名字就整塊不顯示：一張沒有標題的金額表會被讀成「上一件的價格」。
            var name = ItemName(searchItemId);
            if (name == null)
                return;

            previewItemId = searchItemId;
            previewItemName = name;

            var rawCount = proxy->ListingCount;
            if (rawCount > MaxListings)
            {
                // 這個數字本身就不合理 ⇒ 這一段偏移對不齊，不要拿它的內容去算錢。
                MarkUntrusted(searchItemId, rawCount, -1);
                return;
            }

            var count = (int)rawCount;
            if (count == 0)
            {
                previewState = PreviewState.Empty;
                return;
            }

            for (var i = 0; i < count; i++)
            {
                // 🔴 只讀純整數欄位；那個結構裡唯一的指標欄一個位元組都不碰。
                ref var listing = ref proxy->Listings[i];
                var itemId = listing.ItemId;
                var unitPrice = listing.UnitPrice;
                var quantity = listing.Quantity;

                if (itemId != searchItemId ||
                    unitPrice == 0 || unitPrice > SaneUnitPrice ||
                    quantity == 0 || quantity > SaneQuantity)
                {
                    preview.Clear();
                    MarkUntrusted(searchItemId, rawCount, i);
                    return;
                }

                preview.Add(new PreviewRow(unitPrice, quantity, listing.IsHqItem));
            }

            // 自己排序，就不必假設陣列順序等於畫面上的順序：這張表回答的是
            //「最便宜的前幾列加起來是多少」，那本來就是使用者會去點的那幾列。
            preview.Sort(ByUnitPrice);
            previewState = PreviewState.Ready;
        }

        /// <summary>
        /// 記下「這個客戶端上對不齊」。
        /// 📌 寫 <c>Information</c>：這是要請使用者回報的東西，而且一個 session 只寫一次。
        /// </summary>
        private void MarkUntrusted(uint searchItemId, uint rawCount, int failedRow)
        {
            previewState = PreviewState.Untrusted;
            if (untrustedLogged)
                return;

            untrustedLogged = true;
            Log.Information(
                "Marketbuddy: 市場板掛單明細通不過自我校驗，累計表停用（其餘功能不受影響）。" +
                $"searchItemId={searchItemId} listingCount={rawCount} failedRow={failedRow}");
        }

        // ---------------------------------------------------------------- 繪製

        public void Draw()
        {
            if (!conf.MarketBuyTallyOverlay || !haveAnchor)
                return;

            ImGui.SetNextWindowPos(anchor);
            ImGui.SetNextWindowSizeConstraints(
                new Vector2(240, 60),
                new Vector2(float.MaxValue, Math.Max(220f, nativeHeight)));

            if (!ImGui.Begin("Marketbuddy_buytally",
                    ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                    ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize |
                    ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoSavedSettings))
            {
                ImGui.End();
                return;
            }

            DrawHeader();
            DrawTripTotals();
            ImGui.Separator();
            DrawPreview();

            ImGui.End();
        }

        private void DrawHeader()
        {
            ImGui.TextUnformatted("This trip".Loc());
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Everything you have bought from the market board since you walked up to it. Item price only - the board's fee is not counted."
                        .Loc());

            ImGui.SameLine();
            if (ImGui.SmallButton("x##mbbuytallyclose"))
            {
                conf.MarketBuyTallyOverlay = false;
                conf.Save();
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Hide this panel (re-enable it in /mbuddy)".Loc());
        }

        private void DrawTripTotals()
        {
            if (tripPurchases == 0)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
                ImGui.TextUnformatted("Nothing bought yet on this trip.".Loc());
                ImGui.PopStyleColor();
                return;
            }

            ImGui.TextUnformatted("?? items for ?? gil".Loc(
                tripQuantity.ToString("N0"), tripGil.ToString("N0")));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("?? separate purchases, starting ??. Resets on its own five minutes after you leave the market board."
                    .Loc(tripPurchases.ToString(),
                        tripStartedUtc == DateTime.MinValue
                            ? "?"
                            : tripStartedUtc.ToLocalTime().ToString("HH:mm")));

            ImGui.SameLine();
            if (ImGui.SmallButton("Reset".Loc() + "##mbbuytallyreset"))
                Reset();

            // 這一件在本趟買了多少。使用者的買法是「一件商品點很多列」，所以這一行
            // 才是他真正在追的數字；總額是拿來看錢包的。
            if (previewItemId != 0 && perItem.TryGetValue(previewItemId, out var mine))
            {
                ImGui.TextColored(ImGuiColors.DalamudOrange,
                    "?? : ?? bought, ?? gil".Loc(
                        previewItemName, mine.Quantity.ToString("N0"), mine.Gil.ToString("N0")));
            }
        }

        private void DrawPreview()
        {
            switch (previewState)
            {
                case PreviewState.Unavailable:
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
                    ImGui.TextUnformatted("Waiting for the board's results...".Loc());
                    ImGui.PopStyleColor();
                    return;

                case PreviewState.Untrusted:
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
                    ImGui.TextUnformatted("Listings not readable here".Loc());
                    ImGui.PopStyleColor();
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(
                            "The plugin could not line up this game version's listing table, so it will not guess at the running cost - a confident wrong number is worse than none. What you have already bought (above) is unaffected."
                                .Loc());
                    return;

                case PreviewState.Empty:
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
                    ImGui.TextUnformatted("Nothing listed for this item.".Loc());
                    ImGui.PopStyleColor();
                    return;
            }

            DrawTargetRow(out var remaining);
            DrawCumulativeTable(remaining);
        }

        /// <summary>
        /// 「我要湊幾件」那一列。<paramref name="remaining"/> 是扣掉本趟已買之後<b>還差幾件</b>
        /// （0 ＝ 沒設目標，或已經夠了）。
        /// </summary>
        private void DrawTargetRow(out long remaining)
        {
            remaining = 0;

            ImGui.TextUnformatted("I want".Loc());
            ImGui.SameLine();
            ImGui.SetNextItemWidth(90);
            if (ImGui.InputInt("items in total".Loc() + "##mbbuytallytarget", ref target, 0))
                target = Math.Clamp(target, 0, 999_999);

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "0 = off. What you have already bought on this trip is taken off, so the row to stop at moves up as you buy."
                        .Loc());

            if (target <= 0)
                return;

            var already = previewItemId != 0 && perItem.TryGetValue(previewItemId, out var mine)
                ? mine.Quantity
                : 0;
            remaining = target - already;

            if (remaining <= 0)
            {
                remaining = 0;
                ImGui.TextColored(ImGuiColors.HealerGreen,
                    "Target reached: ?? of ??".Loc(already.ToString("N0"), target.ToString("N0")));
                return;
            }

            ImGui.TextUnformatted("Still need ?? of ??".Loc(
                remaining.ToString("N0"), target.ToString("N0")));
        }

        /// <summary>
        /// 累計表：最便宜的前 K 列加起來是幾件、多少錢。
        /// 🔴 每一列都只是數字，<b>沒有任何一格是按鈕</b>——這個面板不會替使用者買東西。
        /// </summary>
        private void DrawCumulativeTable(long remaining)
        {
            var maxRows = Math.Clamp(conf.MarketBuyPreviewRows, 3, 20);
            var shown = Math.Min(preview.Count, maxRows);

            // 「湊夠了」是第幾列（1 起算）；0 ＝ 沒設目標或這一頁湊不到。
            var targetRow = 0;
            if (remaining > 0)
            {
                long running = 0;
                for (var i = 0; i < preview.Count; i++)
                {
                    running += preview[i].Quantity;
                    if (running < remaining)
                        continue;
                    targetRow = i + 1;
                    break;
                }
            }

            // 目標落在預覽範圍外時把表拉長到剛好蓋住它——不然使用者看得到「還差 N 件」
            // 卻看不到要買到第幾列，那等於沒回答。
            if (targetRow > shown)
                shown = Math.Min(preview.Count, targetRow);

            if (!ImGui.BeginTable("##mbbuytallytable", 5,
                    ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
                return;

            ImGui.TableSetupColumn("Row".Loc());
            ImGui.TableSetupColumn("Unit price".Loc());
            ImGui.TableSetupColumn("Qty".Loc());
            ImGui.TableSetupColumn("Running qty".Loc());
            ImGui.TableSetupColumn("Running gil".Loc());
            ImGui.TableHeadersRow();

            long runningQty = 0;
            long runningGil = 0;
            for (var i = 0; i < shown; i++)
            {
                var row = preview[i];
                runningQty += row.Quantity;
                runningGil += row.Total;

                ImGui.TableNextRow();
                if (i + 1 == targetRow)
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1,
                        ImGui.ColorConvertFloat4ToU32(TargetRowColor));

                ImGui.TableNextColumn();
                ImGui.TextUnformatted((i + 1).ToString());

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Hq
                    ? $"{row.UnitPrice:N0} {(char)SeIconChar.HighQuality}"
                    : row.UnitPrice.ToString("N0"));

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Quantity.ToString("N0"));

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(runningQty.ToString("N0"));

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(runningGil.ToString("N0"));
            }

            ImGui.EndTable();

            DrawPreviewFooter(remaining, targetRow, shown);
        }

        private void DrawPreviewFooter(long remaining, int targetRow, int shown)
        {
            if (targetRow > 0)
            {
                long qty = 0;
                long gil = 0;
                for (var i = 0; i < targetRow; i++)
                {
                    qty += preview[i].Quantity;
                    gil += preview[i].Total;
                }

                ImGui.TextColored(ImGuiColors.HealerGreen,
                    "Buy the cheapest ?? listings: ?? items, ?? gil".Loc(
                        targetRow.ToString(), qty.ToString("N0"), gil.ToString("N0")));
            }
            else if (remaining > 0)
            {
                long qty = 0;
                long gil = 0;
                foreach (var row in preview)
                {
                    qty += row.Quantity;
                    gil += row.Total;
                }

                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
                ImGui.TextUnformatted("Every listing here only adds up to ?? items (?? gil)".Loc(
                    qty.ToString("N0"), gil.ToString("N0")));
                ImGui.PopStyleColor();
            }

            if (preview.Count > shown)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
                ImGui.TextUnformatted("?? more listings not shown".Loc((preview.Count - shown).ToString()));
                ImGui.PopStyleColor();
            }

            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextUnformatted("Cheapest first - fee not included".Loc());
            ImGui.PopStyleColor();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "A listing you have just bought stays on this table until the board sends a fresh result, so give it a moment after each purchase."
                        .Loc());
        }

        /// <summary>查不到就回 null——<b>刻意不回 <c>#12345</c> 這種代用名</b>：這個面板整塊的前提
        /// 是「知道自己在算哪一件」，不知道就不要畫。</summary>
        private static string? ItemName(uint itemId)
        {
            try
            {
                var sheet = DataManager.GetExcelSheet<Item>();
                if (sheet != null && sheet.TryGetRow(itemId, out var row))
                {
                    var text = row.Name.ExtractText();
                    if (text.Length > 0)
                        return text;
                }
            }
            catch
            {
                // 查表失敗只表示這一塊不顯示。
            }

            return null;
        }
    }
}
