using System;
using System.Collections.Generic;
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

        /// <summary>
        /// ⚠️ **已停用，保留只為了不動到既有設定檔。** 這是舊的「貼在出售品視窗標題列上的
        /// 浮動列」相對**左上角**的位移；2026-08-03 重掛介面改成獨立視窗之後，位置改由
        /// <see cref="RepriceWindowOffset"/> 決定。改這個欄位不會有任何效果。
        /// </summary>
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
        /// 「全部下架」時**只**下架單價高於這個值的掛單；<b>0 = 停用</b>，也就是維持
        /// 一直以來的行為（全部下架）。預設 0，既有使用者不會被動改到任何東西。
        ///
        /// 🔑 **這是單價（每一件的掛售價），不是整堆的總價。**
        /// 依據：批次重掛寫進容器的是 <c>listing.PricePerUnit</c>（<see cref="MarketDataCache"/>
        /// 收到的市場報價本來就是每件單價），寫入走 <c>SetRetainerMarketPrice()</c>，
        /// 而 <c>GetRetainerMarketPrice()</c> 讀回來的值 <see cref="BatchReprice"/> 是拿去
        /// 跟同一個 newPrice 做等值比較的（「already at ?? gil」那條捷徑）——如果讀回來的是
        /// 總價，凡是數量 &gt; 1 的堆疊那個比較就永遠不會相等，那條捷徑等於死碼。
        /// 即時掛單面板也是拿它當「單價」欄、另外乘上數量才得到「總價」欄。
        /// 三處互相印證，所以這個門檻的單位是**每件 gil**。
        ///
        /// ⚠️ 因此一堆 5 件、每件 1,000 gil 的掛單算 1,000 而不是 5,000。
        /// UI 上必須把「單價」講明白，否則使用者會照總價去設門檻。
        ///
        /// 這個門檻作用在 <see cref="BatchDelist"/>——也就是「本僱員全下架」與
        /// 「全僱員下架」兩顆按鈕共用的那一個引擎。**刻意不作用在**改價流程裡的自動下架
        /// （<see cref="BatchDelistBelowVendor"/> 與 <see cref="BatchMinPrice"/>）：那兩項的
        /// 用途正好相反，是「太便宜就撤掉」，套上「只下架貴的」會直接把它們抵銷掉。
        /// </summary>
        public int DelistAboveUnitPrice = 0;

        /// <summary>
        /// 「全僱員下架」巡迴要**整個跳過**的僱員（語意：這些僱員留著賣高價品）。
        ///
        /// 🔑 存的是 <c>RetainerId</c> 不是名字：名字會被改、跨角色還可能重複，
        /// 拿名字當識別會在改名那一刻靜默失效（而且失效方向是「照樣下架」＝最糟）。
        ///
        /// ⚠️ **只作用在下架巡迴，不作用在重掛巡迴。** 重掛也跳過的話，被保護的僱員
        /// 手上那些高價品就再也不會跟著市場調價，等於為了保護它們反而讓它們永遠掛在
        /// 過時的價格上——那是意外傷害，不是使用者要的。
        ///
        /// ⚠️ 也不作用在「本僱員全下架」那顆按鈕：那是使用者站在該僱員面前、
        /// 針對這一名僱員下的明確指令，用一份「巡迴要跳過誰」的名單去推翻它會很難理解。
        ///
        /// ⚠️ 僱員屬於角色，所以多角色玩家的這份名單會混著好幾個角色的 id；
        /// 在 A 角色的設定畫面裡列不出 B 角色的僱員名字。UI 必須把「有幾筆列不出來」
        /// 講出來，不能假裝名單只有看得到的那幾筆。
        /// </summary>
        public List<ulong> DelistTourSkipRetainers = [];

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

        /// <summary>
        /// **整欄側邊面板**相對於出售品視窗「右上角」的位移。
        ///
        /// 2026-08-03 起重掛面板與即時掛單面板是同一欄（重掛在上、即時掛單接在下面），
        /// 兩塊共用這一個位移，所以拖一個滑桿就是兩塊一起動。欄位名沿用舊的
        /// <c>LiveSellListOffset</c>：對「只開即時掛單」的既有設定檔而言語意完全沒變
        /// （還是那一塊貼在右上角），所以任何人存過的值都不會被偷偷改掉。
        /// </summary>
        public Vector2 LiveSellListOffset = new Vector2(4, 0);

        /// <summary>
        /// ⚠️ **已停用，保留只為了不動到既有設定檔。** 這是 v7.20.0.21 那一版
        /// 「重掛面板貼在出售品視窗**左下角**下方」的位移；2026-08-03 重掛面板移到
        /// 即時掛單上面（＝原生視窗右上角那一欄）之後，位置改由
        /// <see cref="LiveSellListOffset"/> 決定。改這個欄位不會有任何效果。
        ///
        /// 刻意**不**把舊值搬過去：舊值是相對左下角調出來的，直接當成右上角的位移用，
        /// 等於把面板搬到一個使用者從來沒指定過的位置。
        /// </summary>
        public Vector2 RepriceWindowOffset = new Vector2(0, 4);

        /// <summary>
        /// 僱員選單旁那塊「巡迴」面板相對於僱員選單「右上角」的位移。
        /// 版面形式與出售品視窗旁那一欄相同（見 <see cref="LiveSellListOffset"/>），
        /// 但兩個視窗會同時開著、位置互不相干，所以位移分開存。
        /// </summary>
        public Vector2 RetainerPanelOffset = new Vector2(4, 0);

        /// <summary>
        /// 一整輪市場重掛跑完時，透過 IPC 請「塔塔露誇獎」(TataruPraise) 念一句。
        /// </summary>
        /// <remarks>
        /// 📌 純通知：不觸發任何自動化、不改任何重掛行為。沒裝 TataruPraise 時整條路徑是 no-op
        /// （見 <see cref="TataruPraiseIPC"/>），所以預設開著對沒裝的人完全沒有影響。
        /// <para>
        /// ⚠️ 只有「整輪跑完」才響：全僱員重掛巡迴收尾響一次，單僱員重掛（不在巡迴中）收尾響一次；
        /// 巡迴途中每個僱員各自的批次收尾<b>不</b>響，快速上架的單件定價也不響——那些會變成洗版。
        /// </para>
        /// </remarks>
        /// <summary>
        /// 多角色重掛：與 AutoRetainer 的多開模式協作，在 AR 每處理完一個角色、
        /// 準備登出換下一角之前，接手跑一輪全僱員重掛巡迴。
        /// </summary>
        /// <remarks>
        /// 🔴 這個開關只決定「要不要顯示這個功能的操作介面」。真正會動起來還需要使用者
        /// <b>手動武裝</b>，而武裝狀態<b>不存檔</b>：重開遊戲／重載外掛一律回到解除狀態，
        /// 而且一輪跑完就自己解除。預設關閉。
        /// </remarks>
        public bool MultiCharTourEnabled = false;

        public bool TataruPraiseOnRelistDone = true;

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
                if (conf.DelistAboveUnitPrice < 0)
                    conf.DelistAboveUnitPrice = 0;
                // 舊設定檔沒有這個鍵時欄位初始值會留著；只有檔案裡明寫 null 才會變成 null。
                // 這一行是為了後者——少了它，之後每一個 Contains/Count 都會 NRE。
                conf.DelistTourSkipRetainers ??= [];
                conf.MarketTaxPercent = Math.Clamp(conf.MarketTaxPercent, 0, 25);
                conf.MarketDataCacheSeconds = Math.Clamp(conf.MarketDataCacheSeconds, 0, 3600);
            }

            _cachedConfig = conf;
            return _cachedConfig;
        }
    }
}