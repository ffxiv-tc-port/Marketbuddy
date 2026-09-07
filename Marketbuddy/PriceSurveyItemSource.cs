using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.Game;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    /// <summary>巡檢清單裡的一件：道具＋品質，以及我方在這個品質上的最低掛售單價（-1＝不知道）。</summary>
    internal readonly record struct PriceSurveyItem(uint ItemId, bool Hq, long OurPrice);

    /// <summary>「我現在有哪些東西掛在市場上」的一份快照，以及它是從哪裡來的。</summary>
    internal sealed class PriceSurveyItemList
    {
        /// <summary>來源代號，寫進診斷用：<c>sell-list</c>／<c>allagantools</c>／<c>csv</c>。</summary>
        public required string SourceKey;

        /// <summary>給使用者看的來源說明（已在地化）。</summary>
        public required string SourceLabel;

        /// <summary>去重後的 (道具, 品質) 清單。</summary>
        public required List<PriceSurveyItem> Items;

        /// <summary>
        /// 這些掛單屬於哪些僱員。用來判斷「該世界的最低價是不是我自己掛的」。
        /// 空集合代表不知道 —— 那時 <c>lowestIsOurs</c> 一律寫 -1，不可以寫 0。
        /// </summary>
        public required HashSet<ulong> OwnRetainerIds;

        /// <summary>要在畫面上提醒使用者的一句話（例如「這個來源只涵蓋眼前這一位僱員」）。</summary>
        public string? Note;

        /// <summary>要查幾次伺服器（同一件道具的 HQ／NQ 共用一次查詢）。</summary>
        public int DistinctItemCount
        {
            get
            {
                var seen = new HashSet<uint>();
                foreach (var item in Items)
                    seen.Add(item.ItemId);
                return seen.Count;
            }
        }
    }

    /// <summary>
    /// 巡檢清單的三個來源。
    ///
    /// <para>
    /// 🔴 <b>三個都是唯讀的</b>：讀遊戲的僱員市場容器、讀 AllaganTools 的 IPC、讀
    /// InventoryTools 自己的記錄檔。沒有任何一條路徑會寫入遊戲、寫入別的外掛的檔案，
    /// 或改變任何掛售狀態。
    /// </para>
    ///
    /// <para>
    /// 執行緒：<see cref="TryFromSellList"/> 與 <see cref="TryFromAllaganTools"/>
    /// <b>只能在 framework 執行緒上呼叫</b>（前者解遊戲的原生指標，後者的 IPC 實作
    /// 跑在呼叫端的執行緒上、而對方會去讀遊戲狀態）。<see cref="TryFromInventoryToolsCsv"/>
    /// 是純檔案讀取，<b>只能在執行緒池上呼叫</b>——那個檔案有數十萬 bytes。
    /// </para>
    /// </summary>
    internal static unsafe class PriceSurveyItemSource
    {
        /// <summary>僱員的市場容器（＝出售中的東西）。</summary>
        private const uint RetainerMarketInventoryType = (uint)InventoryType.RetainerMarket;

        /// <summary>僱員市場容器的格數上限；索引兩端都要夾。</summary>
        private const int MaxMarketSlots = 20;

        /// <summary><c>InventoryItem.ToNumeric()</c> 的欄位數（見 CriticalCommonLib）。</summary>
        private const int NumericFieldCount = 25;

        // ToNumeric()／ToCsv() 的欄位位置。兩者前 25 欄逐字相同（CriticalCommonLib
        // 的 InventoryItem.cs，2026-09-07 實讀），所以 IPC 與 CSV 兩條路共用這組常數。
        private const int FieldContainer = 0;
        private const int FieldSlot = 1;
        private const int FieldItemId = 2;
        private const int FieldFlags = 6;
        private const int FieldSortedContainer = 20;
        private const int FieldSortedCategory = 21;
        private const int FieldRetainerId = 23;
        private const int FieldRetainerMarketPrice = 24;

        /// <summary>CSV 的總欄數（前 25 欄＋GearSets＋GearSetNames）。</summary>
        private const int CsvFieldCount = 27;

        /// <summary>僱員市場列在 CSV 裡的 <c>SortedCategory</c>；只當**版面校驗**用，不當過濾條件。</summary>
        private const string ExpectedSortedCategory = "9";

        /// <summary>優質品旗標（<c>InventoryItem.ItemFlags.HighQuality</c>）。</summary>
        private const ulong HighQualityFlag = 1;

        /// <summary>
        /// 來源①：遊戲自己的「出售品」視窗開著時，直接讀眼前這一位僱員的市場容器。
        /// 這是唯一「保證與伺服器同步」的來源，但**只涵蓋眼前這一位僱員**。
        /// 視窗沒開就回 null。
        /// 🔴 framework 執行緒限定。
        /// </summary>
        internal static PriceSurveyItemList? TryFromSellList(MarketGuiEventHandler gui)
        {
            if (!gui.IsRetainerSellListOpen)
                return null;

            var inventoryManager = InventoryManager.Instance();
            if (inventoryManager == null)
                return null;

            var container = inventoryManager->GetInventoryContainer(InventoryType.RetainerMarket);
            if (container == null || !container->IsLoaded)
                return null;

            var retainerId = BatchReprice.ActiveRetainerId();
            var builder = new Builder();
            var slotCount = Math.Min((int)container->Size, MaxMarketSlots);
            for (var i = 0; i < slotCount; i++)
            {
                var slot = inventoryManager->GetInventorySlot(InventoryType.RetainerMarket, i);
                if (slot == null || slot->ItemId == 0)
                    continue;
                var hq = (slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
                builder.Add(slot->ItemId, hq, (long)inventoryManager->GetRetainerMarketPrice((short)i), retainerId);
            }

            if (builder.Count == 0)
                return null;

            return builder.Build(
                "sell-list",
                "Retainer sell list (this retainer only)".Loc(),
                "This source only sees the retainer in front of you. Close the sell list before starting, to survey every retainer at once.".Loc());
        }

        /// <summary>
        /// 來源②：AllaganTools（InventoryTools）的 IPC，涵蓋**目前角色**的所有僱員。
        /// 沒裝、端點不存在、或對方內部擲例外時回 null。
        /// 🔴 framework 執行緒限定（IPC 的實作跑在我們這條執行緒上，而它會去讀遊戲狀態）。
        /// </summary>
        /// <remarks>
        /// 🔴 <c>GetCharacterItemsByType</c> 在 InventoryTools 那側是
        /// <c>_inventoryMonitor.Inventories.First(pair =&gt; pair.Key == characterId)</c>：
        /// 找不到就擲 <see cref="InvalidOperationException"/>，而且那個字典由對方的背景工作重建，
        /// 我們是在列舉一個別人可能正在改的集合。所以**每一位僱員各自 try/catch**、
        /// 失敗就當那位僱員沒東西，全部都失敗才判定這條路不通。
        /// </remarks>
        internal static PriceSurveyItemList? TryFromAllaganTools()
        {
            HashSet<ulong>? owned;
            try
            {
                owned = PluginInterface
                    .GetIpcSubscriber<bool, HashSet<ulong>>("AllaganTools.GetCharactersOwnedByActive")
                    .InvokeFunc(false);
            }
            catch
            {
                // 沒裝 InventoryTools、或 IPC 還沒好。
                return null;
            }

            if (owned == null || owned.Count == 0)
                return null;

            var builder = new Builder();
            var attempted = 0;
            var failed = 0;
            foreach (var characterId in owned)
            {
                attempted++;
                try
                {
                    var rows = PluginInterface
                        .GetIpcSubscriber<ulong, uint, HashSet<ulong[]>>("AllaganTools.GetCharacterItemsByType")
                        .InvokeFunc(characterId, RetainerMarketInventoryType);
                    if (rows == null)
                        continue;

                    foreach (var row in rows)
                    {
                        if (row == null || row.Length < NumericFieldCount)
                            continue;
                        if (row[FieldContainer] != RetainerMarketInventoryType)
                            continue;
                        var itemId = row[FieldItemId];
                        if (itemId == 0 || itemId > uint.MaxValue)
                            continue;
                        var hq = (row[FieldFlags] & HighQualityFlag) != 0;
                        builder.Add((uint)itemId, hq, (long)row[FieldRetainerMarketPrice], row[FieldRetainerId]);
                    }
                }
                catch
                {
                    // 這一位僱員拿不到就跳過；不要讓一位僱員擋掉整條來源。
                    failed++;
                }
            }

            if (failed > 0)
                Log.Information(
                    $"[Marketbuddy] 巡檢清單：AllaganTools 有 {failed}/{attempted} 位僱員讀取失敗，已略過那幾位。");

            if (builder.Count == 0)
                return null;

            return builder.Build(
                "allagantools",
                "AllaganTools (every retainer of the current character)".Loc(),
                null);
        }

        /// <summary>
        /// InventoryTools 的庫存記錄檔位置。設定目錄是同一層的兄弟資料夾，
        /// 所以由我們自己的設定目錄往上一層推得，不寫死任何使用者路徑。
        /// 推不出來時回 null。
        /// </summary>
        internal static string? InventoryToolsCsvPath()
        {
            try
            {
                var parent = PluginInterface.ConfigDirectory.Parent;
                if (parent == null)
                    return null;
                return Path.Combine(parent.FullName, "InventoryTools", "inventories.csv");
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 來源③：InventoryTools 的 <c>inventories.csv</c>，涵蓋**所有角色**的所有僱員。
        /// 這是唯一能一次看到全部掛單的來源，但它的新鮮度取決於 InventoryTools 上次寫檔的時間。
        /// 🔴 執行緒池限定（檔案有數十萬 bytes）。🔴 只讀不寫。
        /// </summary>
        internal static PriceSurveyItemList? TryFromInventoryToolsCsv(string? path)
        {
            if (string.IsNullOrEmpty(path))
                return null;

            var builder = new Builder();
            var layoutConfirmed = false;
            var containerRows = 0;

            try
            {
                if (!File.Exists(path))
                    return null;

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                while (reader.ReadLine() is { } line)
                {
                    if (line.Length == 0)
                        continue;
                    var fields = line.Split(',');
                    if (fields.Length != CsvFieldCount)
                        continue;
                    if (fields[FieldContainer] != "12002")
                        continue;

                    containerRows++;

                    // 版面校驗：只要有任何一列同時滿足這兩個條件，就確認欄位位置沒有換過。
                    // 🔴 刻意**不**拿它們當過濾條件——那樣一旦上游改了排序分類，
                    //    結果會變成「靜默 0 件」而不是「看得見的錯誤」。
                    if (fields[FieldSortedContainer] == "12002" && fields[FieldSortedCategory] == ExpectedSortedCategory)
                        layoutConfirmed = true;

                    if (!uint.TryParse(fields[FieldItemId], NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out var itemId) || itemId == 0)
                        continue;
                    if (!ulong.TryParse(fields[FieldFlags], NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out var flags))
                        flags = 0;
                    if (!ulong.TryParse(fields[FieldRetainerId], NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out var retainerId))
                        retainerId = 0;
                    if (!long.TryParse(fields[FieldRetainerMarketPrice], NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out var price))
                        price = -1;

                    builder.Add(itemId, (flags & HighQualityFlag) != 0, price, retainerId);
                }
            }
            catch (Exception e)
            {
                Log.Information(e, "[Marketbuddy] 巡檢清單：讀取 InventoryTools 的 inventories.csv 失敗。");
                return null;
            }

            if (containerRows > 0 && !layoutConfirmed)
            {
                // 有僱員市場列、但欄位版面對不上我們認得的那一版 ⇒ 寧可說「不認得」也不要
                // 拿可能錯位的欄位去查價（錯位的失敗形式是安靜地查一堆無關的道具）。
                Log.Information(
                    "[Marketbuddy] 巡檢清單：InventoryTools 的 inventories.csv 欄位版面與預期不符，已忽略這個來源。");
                return null;
            }

            if (builder.Count == 0)
                return null;

            return builder.Build(
                "csv",
                "InventoryTools log file (every character's retainers)".Loc(),
                "This list comes from whatever InventoryTools last wrote to disk, it is not live.".Loc());
        }


        /// <summary>
        /// 掛單的實際位置：哪一位僱員的哪一格，掛多少錢。
        /// 巡檢清單本身刻意去重成 (道具, 品質)，因為它只需要查一次伺服器；
        /// 但「待處理清單」要能指著某一格說「就是這個」，所以另外收一份不去重的位置表。
        /// </summary>
        /// <param name="Price">掛售單價；-1＝不知道。</param>
        internal readonly record struct PriceSurveyPlacement(
            uint ItemId, bool Hq, ulong RetainerId, short Slot, long Price);

        /// <summary>
        /// 眼前這一位僱員的掛單位置。🔴 framework 執行緒限定（解遊戲的原生指標）。
        /// 出售品視窗沒開時回空清單。
        /// </summary>
        internal static List<PriceSurveyPlacement> ReadPlacementsFromSellList(MarketGuiEventHandler gui)
        {
            var result = new List<PriceSurveyPlacement>();
            if (!gui.IsRetainerSellListOpen)
                return result;

            var inventoryManager = InventoryManager.Instance();
            if (inventoryManager == null)
                return result;

            var container = inventoryManager->GetInventoryContainer(InventoryType.RetainerMarket);
            if (container == null || !container->IsLoaded)
                return result;

            var retainerId = BatchReprice.ActiveRetainerId();
            if (retainerId == 0)
                return result;

            var slotCount = Math.Min((int)container->Size, MaxMarketSlots);
            for (var i = 0; i < slotCount; i++)
            {
                var slot = inventoryManager->GetInventorySlot(InventoryType.RetainerMarket, i);
                if (slot == null || slot->ItemId == 0)
                    continue;
                var hq = (slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
                result.Add(new PriceSurveyPlacement(slot->ItemId, hq, retainerId, (short)i,
                    (long)inventoryManager->GetRetainerMarketPrice((short)i)));
            }

            return result;
        }

        /// <summary>
        /// 目前角色所有僱員的掛單位置，經由 AllaganTools 的 IPC。
        /// 🔴 framework 執行緒限定（IPC 的實作跑在我們這條執行緒上，而它會去讀遊戲狀態）。
        /// 沒裝、端點不存在、或對方內部擲例外時回空清單。
        /// </summary>
        internal static List<PriceSurveyPlacement> ReadPlacementsFromAllaganTools()
        {
            var result = new List<PriceSurveyPlacement>();
            HashSet<ulong>? owned;
            try
            {
                owned = PluginInterface
                    .GetIpcSubscriber<bool, HashSet<ulong>>("AllaganTools.GetCharactersOwnedByActive")
                    .InvokeFunc(false);
            }
            catch
            {
                return result;
            }

            if (owned == null)
                return result;

            foreach (var characterId in owned)
            {
                try
                {
                    var rows = PluginInterface
                        .GetIpcSubscriber<ulong, uint, HashSet<ulong[]>>("AllaganTools.GetCharacterItemsByType")
                        .InvokeFunc(characterId, RetainerMarketInventoryType);
                    if (rows == null)
                        continue;

                    foreach (var row in rows)
                    {
                        if (row == null || row.Length < NumericFieldCount)
                            continue;
                        if (row[FieldContainer] != RetainerMarketInventoryType)
                            continue;
                        var itemId = row[FieldItemId];
                        if (itemId == 0 || itemId > uint.MaxValue)
                            continue;
                        var slotIndex = row[FieldSlot];
                        if (slotIndex >= MaxMarketSlots)
                            continue;
                        result.Add(new PriceSurveyPlacement((uint)itemId,
                            (row[FieldFlags] & HighQualityFlag) != 0, row[FieldRetainerId], (short)slotIndex,
                            (long)row[FieldRetainerMarketPrice]));
                    }
                }
                catch
                {
                    // 這一位僱員拿不到就跳過；不要讓一位僱員擋掉整條來源。
                }
            }

            return result;
        }

        /// <summary>
        /// 所有角色所有僱員的掛單位置，來自 InventoryTools 的 <c>inventories.csv</c>。
        /// 🔴 執行緒池限定（檔案有數十萬 bytes）。🔴 只讀不寫。
        /// </summary>
        internal static List<PriceSurveyPlacement> ReadPlacementsFromCsv(string? path)
        {
            var result = new List<PriceSurveyPlacement>();
            if (string.IsNullOrEmpty(path))
                return result;

            try
            {
                if (!File.Exists(path))
                    return result;

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                while (reader.ReadLine() is { } line)
                {
                    if (line.Length == 0)
                        continue;
                    var fields = line.Split(',');
                    if (fields.Length != CsvFieldCount)
                        continue;
                    if (fields[FieldContainer] != "12002")
                        continue;
                    if (!uint.TryParse(fields[FieldItemId], NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out var itemId) || itemId == 0)
                        continue;
                    if (!int.TryParse(fields[FieldSlot], NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out var slotIndex) || slotIndex < 0 || slotIndex >= MaxMarketSlots)
                        continue;
                    if (!ulong.TryParse(fields[FieldFlags], NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out var flags))
                        flags = 0;
                    if (!ulong.TryParse(fields[FieldRetainerId], NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out var retainerId))
                        continue;
                    if (!long.TryParse(fields[FieldRetainerMarketPrice], NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out var price))
                        price = -1;

                    result.Add(new PriceSurveyPlacement(itemId, (flags & HighQualityFlag) != 0, retainerId,
                        (short)slotIndex, price));
                }
            }
            catch (Exception e)
            {
                Log.Information(e, "[Marketbuddy] 待處理清單：讀取 InventoryTools 的 inventories.csv 位置資料失敗。");
                return [];
            }

            return result;
        }

        /// <summary>(道具, 品質) 去重＋取我方最低掛售價的小工具。</summary>
        private sealed class Builder
        {
            private readonly Dictionary<(uint ItemId, bool Hq), long> prices = new();
            private readonly HashSet<ulong> retainers = [];

            public int Count => prices.Count;

            public void Add(uint itemId, bool hq, long price, ulong retainerId)
            {
                if (itemId == 0)
                    return;
                if (retainerId != 0)
                    retainers.Add(retainerId);

                var key = (itemId, hq);
                // 同一件道具可能掛在好幾位僱員身上、價格不同。取**最低**的那個當「我方價」：
                // 「有沒有被壓價」問的是我方最便宜的那一筆有沒有被壓過去。
                if (!prices.TryGetValue(key, out var existing) || existing < 0 || (price >= 0 && price < existing))
                    prices[key] = price;
            }

            public PriceSurveyItemList Build(string sourceKey, string sourceLabel, string? note)
            {
                var items = new List<PriceSurveyItem>(prices.Count);
                foreach (var ((itemId, hq), price) in prices)
                    items.Add(new PriceSurveyItem(itemId, hq, price));
                // 同一件道具的兩種品質排在一起，這樣查詢順序＝道具順序，只查一次伺服器。
                items.Sort((a, b) =>
                {
                    var c = a.ItemId.CompareTo(b.ItemId);
                    return c != 0 ? c : a.Hq.CompareTo(b.Hq);
                });

                return new PriceSurveyItemList
                {
                    SourceKey = sourceKey,
                    SourceLabel = sourceLabel,
                    Items = items,
                    OwnRetainerIds = retainers,
                    Note = note,
                };
            }
        }
    }
}
