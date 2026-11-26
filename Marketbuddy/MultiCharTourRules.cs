namespace Marketbuddy
{
    // 本檔只有純資料與純函式：不讀遊戲記憶體、不碰 Dalamud，離線測試直接編這個檔。

    /// <summary>巡迴到每個僱員身上要做的事。導航完全相同，只有中間那一段不一樣。</summary>
    internal enum TourMode
    {
        /// <summary>全僱員重掛（<see cref="BatchReprice"/>）。</summary>
        Reprice,

        /// <summary>全僱員下架（<see cref="BatchDelist"/>）。</summary>
        Delist,

        /// <summary>
        /// 全僱員補上架：每名僱員先重掛，再從他自己的物品欄補上架（<see cref="BatchLister"/>，只補使用者替他勾過的道具）。
        /// 🔴 只由使用者按鈕開始；不掛進 <see cref="MultiCharacterTour"/>。
        /// </summary>
        Replenish,

        /// <summary>
        /// 「這個角色整理一輪」：每名僱員 重掛 → 剛下架回背包的交給僱員賣給商店（<see cref="VendorSeller"/>）→ 補上架。
        /// 🔴 只由使用者按鈕（兩段確認）開始；不掛進 <see cref="MultiCharacterTour"/>，沒有事件接手。
        /// </summary>
        Tidy,

        /// <summary>
        /// 背包上架到有空位的僱員：逐名僱員從「出售（玩家所持物品）」進去，把玩家背包裡在批次上架面板勾過的道具上架，
        /// 直到背包沒有可上的或僱員都走過；不重掛。依紀錄確知已滿的僱員不去。
        /// 🔴 使用者按鈕開始；或多角色巡迴在使用者選了「背包上架＋重掛」並手動武裝的那一輪、每角接手的背包段（見 <see cref="MultiCharTourRules"/>）。
        /// </summary>
        BagList,
    }

    /// <summary>多角色巡迴每一角要做什麼。執行期用；設定檔存的是 <see cref="Configuration.MultiCharTourListFromBags"/>。</summary>
    internal enum MultiCharTourMode
    {
        /// <summary>只重掛（預設，既有行為）。</summary>
        RelistOnly,

        /// <summary>先把背包裡預先勾選命中的道具上架到有空位的僱員，再重掛。🔴 不賣給商店。</summary>
        BagListThenRelist,
    }

    /// <summary>背包段結束後這一角接著做什麼。</summary>
    internal enum LegAfterBag
    {
        Relist,
        Close,
    }

    /// <summary>武裝「背包上架＋重掛」前要先開著的東西；<see cref="None"/>＝可以武裝。</summary>
    internal enum BagModeArmBlock
    {
        None,
        BagListTourOff,
        PreTickOff,
        MenuHookUnavailable,
    }

    internal static class MultiCharTourRules
    {
        internal static MultiCharTourMode ModeOf(bool listFromBags)
            => listFromBags ? MultiCharTourMode.BagListThenRelist : MultiCharTourMode.RelistOnly;

        /// <summary>這一角要不要先跑背包段。</summary>
        internal static bool HasBagStage(MultiCharTourMode mode) => mode == MultiCharTourMode.BagListThenRelist;

        /// <summary>
        /// AutoRetainer 接手期間這種巡迴准不准起跑。背包上架只准多角色巡迴自己這一角的背包段；
        /// 補上架與整理一輪一律不准；重掛與下架不在這裡擋（照舊）。
        /// </summary>
        internal static bool AllowedDuringHandover(TourMode mode, bool fromMultiCharLeg) => mode switch
        {
            TourMode.BagList => fromMultiCharLeg,
            TourMode.Replenish or TourMode.Tidy => false,
            _ => true,
        };

        /// <summary>背包段正常跑完而且這一輪仍武裝著才接著重掛；中止或已解除武裝就收尾歸還控制權。</summary>
        internal static LegAfterBag AfterBagStage(bool bagCompleted, bool stillArmed)
            => bagCompleted && stillArmed ? LegAfterBag.Relist : LegAfterBag.Close;

        /// <summary>武裝背包模式的前置條件，依序回第一個沒滿足的。只重掛模式一律可以武裝。</summary>
        internal static BagModeArmBlock ArmBlock(MultiCharTourMode mode, bool bagListTourEnabled, bool preTickEnabled,
            bool menuHookAvailable)
        {
            if (!HasBagStage(mode))
                return BagModeArmBlock.None;
            if (!bagListTourEnabled)
                return BagModeArmBlock.BagListTourOff;
            if (!preTickEnabled)
                return BagModeArmBlock.PreTickOff;
            return menuHookAvailable ? BagModeArmBlock.None : BagModeArmBlock.MenuHookUnavailable;
        }
    }
}
