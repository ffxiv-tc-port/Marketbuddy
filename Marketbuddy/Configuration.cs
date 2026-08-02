using System;
using System.Numerics;
using Dalamud.Configuration;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    [Serializable]
    public class Configuration : IPluginConfiguration
    {
        [NonSerialized] public const int MIN_PRICE = 1;
        [NonSerialized] public const int MAX_PRICE = 999999999;

        public bool HoldShiftToStop = true;
        public bool AutoOpenComparePrices = true;
        public bool AutoOpenHistory = true;
        public bool AutoRequeryOnThrottle = true;

        public bool SaveToClipboard = true;
        public bool AutoInputNewPrice = true;
        public bool AutoConfirmNewPrice = true;
        public bool HoldCtrlToPaste = true;
        public bool HoldAltHistoryHandling = false;

        public bool AdjustMaxStackSizeInSellList = true;
        public Vector2 AdjustMaxStackSizeInSellListOffset = new Vector2(77, 10);
        public bool UseMaxStackSize = false;
        public int MaximumStackSize = 99;
        public int UndercutPrice = 1;
        public bool UndercutUsePercent = false;
        public int UndercutPercent = 1;

        public bool BatchRepriceEnabled = true;
        public bool BatchCompareHqOnly = true;
        public bool BatchDelistBelowVendor = false;
        public int BatchMinPrice = 0;
        public int MarketTaxPercent = 5;
        public bool DelistToRetainerInventory = false;
        public int QuickListKeyCode = 0;

        /// <summary>
        /// 市場資料快取的新鮮度上限（秒）。0 = 停用，每一格都重新向伺服器查一次。
        ///
        /// 預設 120 秒的理由（2026-08-02 實機 log，n=463 次首度查詢）：把門檻從 30 秒
        /// 一路放寬到 30 分鐘，命中率完全不動（27/463 → 29/463），也就是說**放長根本
        /// 換不到速度**，只會換到越來越舊的價格。另一方面「全雇員巡迴」是共用同一份快取
        /// 的：一個雇員 20 格在 .16 的節奏下大約要 40～60 秒，所以 60 秒會在巡迴途中就
        /// 過期。120 秒剛好蓋得住跨雇員重複道具（拆堆疊放在不同雇員是很常見的擺法，而且
        /// 那正是快取最可能命中、又最不可能算錯價的情況——競爭對手清單是同一份）。
        /// 舊版寫死 30 分鐘，這裡是**大幅收緊**，不是放寬。
        /// </summary>
        public int MarketDataCacheSeconds = 120;

        /// <summary>
        /// 「即時出售品清單」：在遊戲的出售品視窗旁邊，由外掛自己畫一份永遠最新的掛單表。
        /// 純顯示、每幀重讀容器，不做任何遊戲操作、不改任何原生節點（見 LiveSellList 的說明）。
        /// 依市場紅線一律預設關閉；批次改價完成時會提示一次它的存在。
        /// </summary>
        public bool LiveSellListOverlay = false;

        /// <summary>面板相對於出售品視窗「右上角」的位移。</summary>
        public Vector2 LiveSellListOffset = new Vector2(4, 0);

        public int Version { get; set; } = 0;

        // the below exist just to make saving/loading less cumbersome
        [NonSerialized] private static Configuration? _cachedConfig;

        public void Save()
        {
            PluginInterface.SavePluginConfig(this);
        }

        public static Configuration GetOrLoad()
        {
            if (_cachedConfig != null)
                return _cachedConfig;

            if (PluginInterface.GetPluginConfig() is not Configuration conf)
            {
                conf = new Configuration();
                conf.Save();
            }
            else
            {
                if (conf.MaximumStackSize > 9999)
                    conf.MaximumStackSize = 9999;
                if (!conf.AutoInputNewPrice)
                    conf.AutoConfirmNewPrice = false;
                //if (!conf.AutoOpenComparePrices)
                //    conf.HoldShiftToStop = false;
                if (conf.UndercutPrice < 0)
                    conf.UndercutPrice = 0;
                if (conf.BatchMinPrice < 0)
                    conf.BatchMinPrice = 0;
                conf.MarketTaxPercent = Math.Clamp(conf.MarketTaxPercent, 0, 25);
                conf.MarketDataCacheSeconds = Math.Clamp(conf.MarketDataCacheSeconds, 0, 3600);
            }

            _cachedConfig = conf;
            return _cachedConfig;
        }
    }
}