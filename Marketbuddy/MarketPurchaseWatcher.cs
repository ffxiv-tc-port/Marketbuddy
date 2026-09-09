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
    ///
    /// <para>
    /// 🔴 <b>純唯讀輪詢，零封包、零 hook、零記憶體寫入。</b>看的是
    /// <c>InfoProxyItemSearch.LastPurchasedMarketboardItem</c> —— 那是<b>遊戲自己填好</b>的
    /// 欄位（<c>SetLastPurchasedItem</c> 在送出購買請求前把選中的掛單抄進去）。
    /// 我們<b>只讀它</b>：不呼叫 <c>SetLastPurchasedItem</c>、不呼叫
    /// <c>SendPurchaseRequestPacket</c>、不掛任何 hook，也不攔任何封包。
    /// </para>
    ///
    /// <para>
    /// 🔑 <b>為什麼是這個欄位，而不是「點了某一列」。</b>市場板上點一列只代表「在看」，
    /// 使用者還可以取消；而且瀏覽時會連點好幾列，對方的提示佇列是
    /// <b>先進先出取第一個相符的</b>（<c>GilHintStore.TryConsume</c> 從最舊的開始掃），
    /// 於是「點 A、點 B、買 B」會被記成買了 A ——<b>一個自信的錯誤註記，比沒有註記更糟</b>。
    /// 這個欄位只在真的要買的時候才變，所以沒有這個問題。
    /// </para>
    ///
    /// <para>
    /// 🔑 <b>我們加的是「買了什麼」，不是「這是不是市場購買」。</b>GilDelta 自己的
    /// <c>MarketBoardBuyRule</c> 只要 <c>ItemSearch</c> 這個視窗開著就會把自己錢包的減少
    /// 判成市場購買，那本來就會答對。它<b>結構上不可能知道</b>的是道具名、數量與單價——
    /// 那才是這個提示的價值，也是使用者在流水帳上真正想看到的東西。
    /// </para>
    ///
    /// <para>
    /// 🔴 <b>為什麼沒有「僱員賣出」的提示</b>（2026-09-08 逐行讀 GilDelta 後的決定）：
    /// <list type="number">
    ///   <item>僱員錢包那一側對方<b>已經是無歧義的</b>——<c>RetainerSaleRule.cs:40-47</c>
    ///         對任何 <c>WalletKind.Retainer</c> 的增加直接判 <c>RetainerSale</c>，
    ///         註解逐字寫著「needs no corroboration」。我們幫不上忙。</item>
    ///   <item>玩家錢包那一側，對方的佐證清單（<c>RetainerSaleRule.cs:30-31</c>）
    ///         <b>已經包含 <c>RetainerSellList</c> 與 <c>RetainerList</c></b>。
    ///         「開了僱員出售品視窗」這個弱訊號對方自己看得到，我們再送一次是重複的。</item>
    ///   <item>而且<b>不是無害的重複</b>：<c>HintRule</c> 排在所有以視窗推斷的規則<b>之前</b>
    ///         （<c>Plugin.cs:146-156</c>），所以一個「開視窗就送」的 <c>RetainerSale</c> 提示
    ///         會在它的有效期內壓過 <c>NpcShopSellRule</c>／<c>RepairRule</c> 這些規則。
    ///         僱員傳喚鈴旁邊就是商店與修理——把賣給 NPC 的錢記成僱員賣出，是實實在在的退步。</item>
    ///   <item>時間上也來不及：對方每一幀讀 <c>RetainerManager.Retainers[i].Gil</c>
    ///         （<c>WalletReader.TryReadRetainers</c>），而那份資料是<b>開傳喚鈴</b>時就填好的，
    ///         比我們進得了某一位僱員、能去比對他的市場容器還早。等我們算得出「哪一件賣掉了」，
    ///         對方早就已經（正確地）記完那筆帳了。</item>
    /// </list>
    /// ⇒ 這個桶留白是刻意的。要送就得先有一個「對方推不出來、而我們確定知道」的時刻，
    /// 目前沒有。
    /// </para>
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
