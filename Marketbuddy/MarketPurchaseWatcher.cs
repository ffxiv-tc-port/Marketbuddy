using System;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    /// <summary>
    /// 看著遊戲自己記下來的「上一筆市場板購買」，在使用者買下東西的那一刻告訴
    /// <see cref="GilDeltaHintIPC">GilDelta</see>「接下來那筆金幣減少是買了什麼」。
    /// 🔴 <b>純唯讀輪詢，零封包、零 hook、零記憶體寫入。</b>看的是
    /// <c>InfoProxyItemSearch.LastPurchasedMarketboardItem</c> —— 那是<b>遊戲自己填好</b>的
    /// 欄位（<c>SetLastPurchasedItem</c> 在送出購買請求前把選中的掛單抄進去）。
    /// 🔑 <b>為什麼是這個欄位，而不是「點了某一列」。</b>市場板上點一列只代表「在看」，
    /// 這個欄位只在真的要買的時候才變，所以沒有這個問題。
    /// ⇒ 這個桶留白是刻意的。要送就得先有一個「對方推不出來、而我們確定知道」的時刻，
    /// 目前沒有。
    /// </summary>
    internal sealed unsafe class MarketPurchaseWatcher : IDisposable
    {
        private readonly MarketGuiEventHandler gui;

        /// <summary>
        /// 「這一趟買到哪了」的計數面板。
        /// 🔑 刻意讓它<b>共用這一個偵測器</b>而不是自己再輪詢一次：兩套偵測遲早會對不起來，
        /// 而「買了幾件」對不起來就是那個面板唯一的價值消失。可為 null（面板建不起來時
        /// 這裡照舊只送 GilDelta 提示）。
        /// </summary>
        internal MarketBuyTally? Tally { get; set; }

        /// <summary>
        /// 上一次看到的那筆購買記錄。<c>ListingId</c> 為 0 代表「還沒取過基準」。
        /// </summary>
        private ulong lastListingId;

        private uint lastItemId;
        private uint lastQuantity;
        private uint lastUnitPrice;

        /// <summary>
        /// 這一輪（市場結果視窗開著的期間）有沒有取過基準。
        /// 🔴 沒有這個旗標的話，視窗一開就會把<b>上一次</b>（甚至上一次遊戲）留下來的購買記錄
        /// 當成剛剛發生的事送出去。
        /// </summary>
        private bool baselineTaken;

        public MarketPurchaseWatcher(MarketGuiEventHandler gui)
        {
            this.gui = gui;
            Framework.Update += OnFrameworkUpdate;
        }

        public void Dispose()
        {
            Framework.Update -= OnFrameworkUpdate;
        }

        private void OnFrameworkUpdate(IFramework framework)
        {
            // 只在市場結果視窗開著的時候輪詢：那是唯一買得成東西的時候，
            // 其餘時間一個指標都不解。
            if (!gui.IsItemSearchResultOpen)
            {
                baselineTaken = false;
                return;
            }

            // 別的外掛透過 IPC 把 Marketbuddy 整個鎖住時，連通知都不要送。
            if (IPCManager.IsLocked)
                return;

            var infoModule = InfoModule.Instance();
            if (infoModule == null)
                return;
            var proxy = (InfoProxyItemSearch*)infoModule->GetInfoProxyById(InfoProxyId.ItemSearch);
            if (proxy == null)
                return;

            var last = proxy->LastPurchasedMarketboardItem;
            if (last.ItemId == 0)
                return;

            if (!baselineTaken)
            {
                // 視窗剛開：這個欄位裡放的是以前留下來的東西，記下來當基準，不當成事件。
                baselineTaken = true;
                Remember(last);
                return;
            }

            if (last.ListingId == lastListingId && last.ItemId == lastItemId
                && last.Quantity == lastQuantity && last.UnitPrice == lastUnitPrice)
                return;

            Remember(last);
            // 🔴 先記帳再送提示：送提示會呼叫別的外掛的 IPC，那條路上任何一個例外都不該
            //    把「使用者自己的購買紀錄」弄丟。
            Tally?.Record(last.ItemId, last.Quantity, last.UnitPrice);
            SendHint(last);
        }

        private void Remember(LastPurchasedMarketboardItem last)
        {
            lastListingId = last.ListingId;
            lastItemId = last.ItemId;
            lastQuantity = last.Quantity;
            lastUnitPrice = last.UnitPrice;
        }

        /// <summary>
        /// 🔴 在 framework 執行緒上呼叫（<see cref="OnFrameworkUpdate"/> 的鏈上）。
        /// 送出去的是「買了什麼」，分類本身對方多半也推得出來。
        /// </summary>
        private void SendHint(LastPurchasedMarketboardItem last)
        {
            var name = ItemName(last.ItemId);
            if (last.IsHqItem)
                name += $" {(char)SeIconChar.HighQuality}";

            // 總價用單價×數量算，不用對方欄位裡的稅：使用者錢包實際少掉的是含稅總額，
            // 但稅率會變，寫一個可能不準的總額不如把兩個確定的數字寫清楚。
            var note = $"市場買入 {name} ×{last.Quantity}，單價 {last.UnitPrice:N0} gil";
            GilDeltaHintIPC.TrySend(GilDeltaHintIPC.CategoryMarketBoardBuy, note,
                GilDeltaHintIPC.MarketBoardBuyTtlMs);
        }

        private static string ItemName(uint itemId)
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
                // 查不到名字只影響說明文字。
            }

            return $"#{itemId}";
        }
    }
}
