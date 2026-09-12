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

        /// <summary>
        /// 台服的拉姆（4034）。<b>出廠就在排除清單裡</b>：那個世界已經停止營運，
        /// Lifestream 送不過去。
        /// </summary>
        /// <remarks>
        /// 🔴 這個常數只被 <see cref="SeedClosedWorldExclusions"/> 用來<b>塞一次</b>初始值，
        /// <b>不是</b>寫死的判斷式：判「要不要排除」一律讀
        /// <see cref="PriceSurveyExcludedWorlds"/>，所以日後台服再關／再開別的世界，
        /// 使用者自己在畫面上勾一下就好，不必等改版。
        /// </remarks>
        [NonSerialized] public const uint CLOSED_WORLD_RAMUH = 4034;

        /// <summary>一輪自動續跑最多換幾個世界的硬上限（台服只有八個世界）。</summary>
        [NonSerialized] public const int MAX_TOUR_WORLDS = 8;

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
        /// 浮動列」相對**左上角**的位移；重掛介面改成獨立視窗之後，位置改由
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

        /// <summary>
        /// 異常低價保護：別人手滑少打一個 0 的時候，不要跟著把自己的東西降到那個價。
        /// <b>預設開啟。</b>
        /// </summary>
        /// <remarks>
        /// 🔴 它擋下來的是「跟著一個看起來打錯的價降價」，<b>不是</b>「降價」本身：
        /// 判準是<b>離群</b>（最低價比下一位賣家便宜太多倍）而不是<b>變低</b>，
        /// 所以整個市場一起崩盤時它不會作用。完整的取捨寫在
        /// <see cref="PriceAnomalyGuard"/> 的類別註解裡，那裡是唯一真值來源。
        /// </remarks>
        public bool AnomalyGuardEnabled = true;

        /// <summary>
        /// 異常低價保護的<b>金額門檻</b>：只有「正常價」達到這個數字，這道保護才會作用。
        /// <b>0 = 停用</b>。
        /// </summary>
        /// <remarks>
        /// ⚠️ 比的是<b>正常價</b>（下一位賣家的價，或退而求其次時自己的現價），
        /// 不是那個可疑的低價——可疑的那個本來就低，拿它當門檻等於門檻永遠不會過。
        /// </remarks>
        public int AnomalyGuardMinNormalPrice = 100000;

        /// <summary>
        /// 異常低價保護的<b>倍數門檻</b>：正常價是可疑價的幾倍（含）以上算異常。
        /// </summary>
        /// <remarks>
        /// 合理範圍 <see cref="PriceAnomalyGuard.MinRatio"/>..<see cref="PriceAnomalyGuard.MaxRatio"/>，
        /// 超出範圍時保護<b>整個不作用</b>（寧可不擋，也不要擋住正常的降價）。
        /// </remarks>
        public int AnomalyGuardRatio = 5;
        public int MarketTaxPercent = 5;
        public bool DelistToRetainerInventory = false;
        public int QuickListKeyCode = 0;

        /// <summary>
        /// 「全部下架」時**只**下架單價高於這個值的掛單；<b>0 = 停用</b>，也就是維持
        /// 一直以來的行為（全部下架）。預設 0，既有使用者不會被動改到任何東西。
        /// 🔑 **這是單價（每一件的掛售價），不是整堆的總價。**
        /// UI 上必須把「單價」講明白，否則使用者會照總價去設門檻。
        /// 這個門檻作用在 <see cref="BatchDelist"/>——也就是「本僱員全下架」與
        /// 「全僱員下架」兩顆按鈕共用的那一個引擎。**刻意不作用在**改價流程裡的自動下架
        /// （<see cref="BatchDelistBelowVendor"/> 與 <see cref="BatchMinPrice"/>）：那兩項的
        /// 用途正好相反，是「太便宜就撤掉」，套上「只下架貴的」會直接把它們抵銷掉。
        /// </summary>
        public int DelistAboveUnitPrice = 0;

        /// <summary>
        /// 「全僱員下架」巡迴要**整個跳過**的僱員（語意：這些僱員留著賣高價品）。
        /// 🔑 存的是 <c>RetainerId</c> 不是名字：名字會被改、跨角色還可能重複，
        /// 拿名字當識別會在改名那一刻靜默失效（而且失效方向是「照樣下架」＝最糟）。
        /// ⚠️ **只作用在下架巡迴，不作用在重掛巡迴。** 重掛也跳過的話，被保護的僱員
        /// 手上那些高價品就再也不會跟著市場調價，等於為了保護它們反而讓它們永遠掛在
        /// 過時的價格上——那是意外傷害，不是使用者要的。
        /// </summary>
        public List<ulong> DelistTourSkipRetainers = [];

        /// <summary>
        /// 市場資料快取的新鮮度上限（秒）。0 = 停用，每一格都重新向伺服器查一次。
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
        /// </summary>
        public Vector2 LiveSellListOffset = new Vector2(4, 0);

        /// <summary>
        /// ⚠️ **已停用，保留只為了不動到既有設定檔。** 位置改由 <see cref="LiveSellListOffset"/> 決定，改這個欄位不會有任何效果。
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

        /// <summary>
        /// 跨世界價格巡檢<b>正常掃完一個世界</b>時，透過 IPC 請「塔塔露誇獎」(TataruPraise) 念一句。
        /// </summary>
        /// <remarks>
        /// 📌 純通知：不觸發任何自動化、不改巡檢的任何行為，也不會多送一次市場查詢。
        /// ⚠️ 只有「這個世界的清單整份掃到最後」才響：使用者自己按停、讓路給重掛或下架、
        /// 第一件連續沒回應而放棄、清單根本建不起來，這些通通<b>不</b>響——那些不是跑完，
        /// 響了會讓人以為資料已經收齊。
        /// 📌 跟 <see cref="TataruPraiseOnRelistDone"/> 分成兩個開關是刻意的：巡檢是唯讀的、
        /// 一個世界按一次，重掛是會改價的整輪流程；想只聽其中一種的人要關得掉另一種。
        /// </remarks>
        public bool TataruPraiseOnSurveyDone = true;

        /// <summary>
        /// 把高頻的 <c>[MBDIAG]</c> 診斷行從 <c>Debug</c> 提升到 <c>Information</c>。
        /// ⚠️ <c>REFUSED</c>／<c>TIMEOUT</c>／<c>MKTRESULT-ERR</c>／快取清除這些低頻、
        /// 代表真的出事的行<b>不受這個開關影響</b>，一律維持原等級。
        /// </summary>
        public bool VerboseMarketDiagnostics = false;

        /// <summary>
        /// 「跨世界價格巡檢」：帶著自己掛售中的清單，在每一個世界的市場前面按一下，
        /// 把每一件的行情逐一問過並記進 <c>price_survey.csv</c>。
        /// </summary>
        /// <remarks>
        /// 🔴 <b>預設關閉</b>，而且開著也只是「顯示這個功能的視窗」——真的要跑一定要
        /// 使用者自己在巡檢視窗上按「掃描這個世界」。它不掛任何自動觸發：
        /// 沒有 AutoRetainer 事件、沒有 addon 事件、掃完一個世界就停。
        /// <para>🔴 整條路徑只讀不寫：只送遊戲自己的市場查詢，不改任何價格、不掛售、不下架。</para>
        /// </remarks>
        public bool PriceSurveyEnabled = false;

        /// <summary>
        /// 巡檢時可以直接採用的市場快取新鮮度（秒）。<b>0 = 每一件都重新向伺服器查</b>。
        /// </summary>
        /// <remarks>
        /// 🔑 預設刻意是 0，與重掛的 <see cref="MarketDataCacheSeconds"/> 不同：巡檢的產出
        /// 是一份「這個世界此刻的行情」記錄，混進幾分鐘前的舊值會讓事後比價得出錯的結論。
        /// </remarks>
        public int PriceSurveyCacheSeconds = 0;

        /// <summary>
        /// 續掃用：同一個世界在這麼多小時內已經問到答案的道具就跳過。<b>0 = 不跳過</b>。
        /// </summary>
        /// <remarks>
        /// ⚠️ 被拒絕與逾時的那幾件**不算掃過**，下一輪會重新問。
        /// </remarks>
        public int PriceSurveySkipHours = 6;

        /// <summary>
        /// 只巡檢「上一輪資料顯示已經被別人壓價」的道具。
        /// </summary>
        /// <remarks>
        /// ⚠️ 需要 <c>price_survey.csv</c> 裡已經有資料；完全沒有資料時開著它會篩掉全部道具，
        /// 畫面會直說「沒有符合條件的道具」而不是靜靜地什麼都不做。
        /// </remarks>
        public bool PriceSurveyOnlyUndercut = false;

        /// <summary>
        /// 巡檢清單直接取用 InventoryTools 的 <c>inventories.csv</c>（涵蓋**所有角色**的僱員），
        /// 不走「出售品視窗 → AllaganTools IPC」那條只看得到目前角色的優先序。
        /// </summary>
        /// <remarks>
        /// false ＝ 走優先序：出售品視窗開著時只看眼前那一位僱員，否則問 AllaganTools
        /// （目前角色的全部僱員），再不行才讀記錄檔。
        /// ⚠️ 記錄檔的新鮮度取決於 InventoryTools 上次寫檔的時間，不是即時的。
        /// ⚠️ 預設 true 時，沒裝 InventoryTools 會直接說「找不到任何掛售中的道具」，
        /// <b>不會</b>自己退回上面那條優先序——把畫面上那個核取方塊取消勾選即可
        /// （來源選擇的邏輯刻意沒有改動，這次只翻了預設值）。
        /// </remarks>
        public bool PriceSurveyAllCharacters = true;

        /// <summary>
        /// 巡檢每一個世界的時候，順便把「採購清單」（<c>shopping_list.txt</c>）上的東西也問一遍行情，
        /// 結果寫進 <c>shopping_survey.csv</c>，在巡檢視窗的「採購」分頁上一件一列、每個世界一欄。
        /// </summary>
        /// <remarks>
        /// 🔴 <b>預設關閉，而且必須維持關閉當預設。</b>清單上每多一件，每一個世界就多一次市場查詢，所以「多花多久」是使用者要自己決定的事。
        /// 🔴 打開它<b>不會</b>讓任何事情自己開始跑：它只是讓已經在跑的那一輪多查幾件，
        /// 不會新增任何觸發來源，也不會讓巡檢自己換世界
        /// （那要另外打開 <see cref="PriceSurveyAutoTour"/> 並親手武裝）。
        /// 🔴 <b>純查價，不買。</b>這條路徑只送遊戲自己的市場查詢，沒有任何購買動作、
        /// 沒有任何封包偽造、也不碰任何掛售狀態。
        /// </remarks>
        public bool PriceSurveyShoppingList = false;

        /// <summary>
        /// 不要碰的世界（世界 id）。<b>所有用途共用這一份，只有這一份</b>：
        /// 換世界選單不列它、自動續跑不選它、站在它上面也不准開始掃描、
        /// 待處理清單不採用它的行情、<b>「最近成交價重掛」也不採用它的成交紀錄</b>。
        /// </summary>
        /// <remarks>
        /// 🔴 <b>初始式刻意是空的</b>：Dalamud 的設定反序列化是把 JSON 的內容<b>加進</b>現有的集合，寫在初始式裡的東西使用者勾不掉。出廠要排除的世界一律走 <see cref="SeedClosedWorldExclusions"/> 那條一次性的路。
        /// 🔴 <b>定價路徑上有一個例外</b>：掛售發生的那個世界（家世界）永遠不會被排除，
        /// 見 <see cref="BuildPricingExclusions()"/>。勾了它仍然會讓那個世界從換世界選單、
        /// 自動續跑、掃描與兩張表上消失，只是「拿哪裡的價來定價」不受影響。
        /// </remarks>
        public List<uint> PriceSurveyExcludedWorlds = [];

        /// <summary>
        /// 出廠排除清單套用過了沒。🔴 <b>一次性</b>：套用過就把這個旗標存起來，
        /// 使用者之後把那個世界勾掉就永遠不會再被塞回去。
        /// </summary>
        public bool PriceSurveyExcludedWorldsSeeded = false;

        /// <summary>
        /// 允許「一個世界掃完之後自動切到資料最舊的世界接著掃」。
        /// </summary>
        /// <remarks>
        /// 🔴 <b>預設關。</b>而且打開它<b>也不會</b>讓任何事情自己開始跑：它只是讓
        /// 巡檢視窗上那顆「武裝一輪」按鈕可以按。真正的連鎖一定要使用者按下武裝，
        /// 而武裝狀態<b>刻意不存檔</b>——重開遊戲、重載外掛一律回到解除狀態。
        /// 🔴 整條鏈仍然只讀不寫：換世界走 Lifestream 的具名 IPC 端點（<b>絕不</b>用
        /// 空參數的 <c>/li</c> 聊天指令，那等於跨世界傳送），到了之後送的還是
        /// 遊戲自己的市場查詢，不改價、不掛售、不下架。
        /// </remarks>
        public bool PriceSurveyAutoTour = false;

        /// <summary>一輪武裝最多換幾個世界。</summary>
        /// <remarks>
        /// 🔴 這是防無限迴圈的兩道閘之一（另一道是「同一輪裡每個世界最多去一次」）。
        /// 夾在 1..<see cref="MAX_TOUR_WORLDS"/>。
        /// </remarks>
        public int PriceSurveyAutoTourMaxWorlds = MAX_TOUR_WORLDS;

        /// <summary>
        /// 排除清單改過幾次（<b>只在記憶體裡</b>，不存檔）。
        /// 巡檢與畫面拿它當「這份清單變了，該重建了」的訊號——少了它，
        /// 勾掉一個世界之後換世界選單會維持舊內容直到下一次換世界。
        /// </summary>
        [NonSerialized] public int WorldExclusionRevision;

        /// <summary>
        /// 跨世界價格巡檢正常跑完一個世界之後，自動重新計算「待處理」清單。
        /// </summary>
        /// <remarks>
        /// <b>它不會改任何價格、不會下架任何東西、也不會多送一次市場查詢。</b>
        /// 改價一律仍然要使用者在待處理分頁上一列一列按。
        /// 預設 <b>true</b>：剛掃完的資料就是最新的，這時候不重算等於讓使用者對著舊清單做決定。
        /// ⚠️ 只有「這個世界的清單整份掃到最後」才觸發（與塔塔露那個通知同一道閘）：
        /// 自己按停、讓路給重掛或下架、清單根本建不起來，通通不重算——那些時候的資料是半份的。
        /// </remarks>
        public bool PendingRecomputeAfterSurvey = true;

        /// <summary>
        /// 記錄「僱員的掛售清單少了什麼」——也就是僱員銷售紀錄。預設開。
        /// 🔴 這是<b>純觀察</b>：只在「出售品」視窗開著時讀僱員的市場容器與錢包，
        /// 不送封包、不掛 hook、不改任何價格、不下架任何東西。關掉它只會停止記錄。
        /// ⚠️ 預設開是因為它沒有任何會改變遊戲狀態的路徑，而且不記就永遠補不回來
        /// （兩次快照之間發生的事沒有第二個來源）。
        /// </summary>
        public bool RetainerSalesLogEnabled = true;

        /// <summary>
        /// 重掛時改用「這件道具在整個資料中心最近一次實際成交的價格，無條件捨去到百位」定價，
        /// 而不是既有的「最低掛售價再降一點」。
        /// </summary>
        /// <remarks>
        /// 🔴 <b>預設關閉</b>，而且只有使用者自己按下重掛按鈕時才生效：它是一種<b>定價方式</b>，
        /// 不是新的觸發來源，沒有任何自動接手鏈。
        /// 🔴 價格來源是 <b>Universalis 的 HTTP API</b>（<see cref="LastSoldPriceSource"/>），
        /// <b>不送遊戲內的市場查詢</b>：不呼叫 <c>InfoProxyItemSearch.RequestData()</c>、
        /// 不開任何原生視窗、零封包。所以它也不會撞到台服「查詢被拒絕時完全靜默」那個問題。
        /// </remarks>
        public bool RelistUseLastSoldPrice = false;

        /// <summary>
        /// 查歷史最近賣出價時忽略優質狀態：同一個 itemId 的優質與普通成交視為同一件道具，
        /// 取<b>時間最近</b>的那一筆。
        /// </summary>
        /// <remarks>
        /// ⚠️ 只影響<b>查價</b>。僱員銷售紀錄那邊的比對鍵仍然含品質，那是另一件事：
        /// 少了品質它就配不出「少掉的是哪一批」。
        /// </remarks>
        public bool RelistLastSoldIgnoreQuality = true;

        /// <summary>
        /// 成交紀錄的<b>新鮮度窗（天）</b>：只有這麼多天之內的成交價才拿來定價。
        /// <b>0 = 不限</b>（＝保留舊行為的路）。
        /// </summary>
        /// <remarks>
        /// 🔴 <b>時間戳不知道的成交紀錄一律當成過期</b>（見
        /// <see cref="RelistPricing.IsSaleStale"/>）：Universalis 的回應不保證帶時間，
        /// 把「不知道年代」當成「剛剛」正是「掛在賣不掉的價」的成因。
        /// ⚠️ 過期<b>不是失敗</b>：那一格改用板上（家世界）別人的最低掛售價，也就是這個外掛
        /// 一直以來的定價方式。
        /// </remarks>
        public int RelistLastSoldMaxAgeDays = 7;

        /// <summary>
        /// 遊戲內市場查詢被拒絕／逾時之後，改用<b>跨世界價格巡檢</b>記錄裡家世界那一列
        /// 當「板上最低價」的時限（<b>小時</b>）。<b>0 = 不使用巡檢補位</b>。
        /// </summary>
        /// <remarks>
        /// 🔴 <b>刻意不重用 <see cref="PriceSurveySkipHours"/></b>：那一個的語意是
        /// 「續掃時同一個世界幾小時內掃過的就跳過」，而且它的 <b>0 是「不跳過」</b>——
        /// 拿它當新鮮度窗的話，0 會變成「不限年代」，剛好是最危險的方向。兩個數字的
        /// 用途與安全方向都相反，共用一個欄位遲早會被其中一邊的調整弄壞另一邊。
        /// 🔴 <b>只補家世界</b>。別的世界的掛單永遠不會變成定價依據——別人在別的世界比我便宜，
        /// 不代表我在自己的市場上吃虧。
        /// </remarks>
        public int RelistSurveyFallbackHours = 24;

        /// <summary>
        /// 在「即時出售品清單」面板上多畫三欄：本世界目前最低價、整個資料中心目前最低價、
        /// 以及最近一次成交（價格＋時間）。
        /// </summary>
        /// <remarks>
        /// 🔴 <b>預設關閉</b>，而且是<b>純顯示</b>：不會因為任何數字自動降價或自動下架。
        /// 🔴 <b>但「不會多送」的另一面是「自己也不會去送」</b>：通往
        /// <c>LastSoldPriceSource.Request()</c> 的每一條路徑都在
        /// <see cref="RelistUseLastSoldPrice"/> 底下，所以那個開關關著時，前三欄
        /// <b>永遠</b>不會有資料。面板把那種情況畫成灰色的 <c>?</c> 並在滑鼠提示裡說要開哪一項；<b>不可以</b>畫成「正在查…」。
        /// ⚠️ 最低價一律照該格自己的品質取，<b>不受</b> <see cref="RelistLastSoldIgnoreQuality"/> 影響：
        /// 拿自己的優質品去跟普通品的最低價比是錯的比較。
        /// </remarks>
        public bool LiveSellListMarketColumns = false;

        /// <summary>
        /// 在「即時出售品清單」面板上多畫一欄：這件道具<b>在我們自己的紀錄裡</b>賣出過幾次、
        /// 被 Marketbuddy 下架過幾次。
        /// </summary>
        /// <remarks>
        /// 資料完全來自本機既有的僱員銷售紀錄檔（<see cref="RetainerSalesLog"/>）：
        /// <b>零網路、零遊戲內查詢、零封包</b>，也不會多寫任何檔案。
        /// 🔴 <b>純顯示</b>：不會因為「賣不掉」自動下架、自動降價或自動做任何事。
        /// 🔴 「沒有資料」與「賣出 0 次」在畫面上<b>一定要分得出來</b>：沒有觀察基礎時
        /// 畫灰色的 <c>?</c>，絕不畫成 0。判準是
        /// <see cref="RetainerListingAge.FirstSeen"/>——沒看過那位僱員就沒有根據。
        /// </remarks>
        public bool LiveSellListSalesHistoryColumn = true;

        /// <summary>
        /// 「這一趟買到哪了」：在市場板搜尋結果視窗旁畫一塊面板，顯示本趟已買的件數與金額，
        /// 以及「最便宜的前 K 列加起來是幾件、多少錢」。
        /// </summary>
        /// <remarks>
        /// 🔴 這<b>不是</b>市場自動化，所以不受「市場功能一律預設關」那條的規範：
        /// 它一件東西都不買、一列都不點、一個查詢都不送。資料全部來自遊戲自己已經填好的欄位
        /// （<c>InfoProxyItemSearch</c>），零封包、零 hook、零記憶體寫入。
        /// 🔴 刻意<b>沒有</b>「一鍵買到 N 件」。那會是「按一下帶出一串市場操作」，而且花的是
        /// 不可逆的 gil；要做那個必須另外開一次決策，不會夾在這個顯示功能裡。
        /// </remarks>
        public bool MarketBuyTallyOverlay = true;

        /// <summary>
        /// 上面那塊面板的累計表要列幾列（3～20，使用範圍外的值會在使用時夾住）。
        /// </summary>
        /// <remarks>
        /// ⚠️ 設了目標件數而「湊夠了」的那一列落在這個範圍之外時，表會<b>自動拉長到蓋住它</b>——
        /// 看得到「還差 N 件」卻看不到要買到第幾列，等於沒回答。
        /// </remarks>
        public int MarketBuyPreviewRows = 8;

        /// <summary>
        /// 「這一趟買到哪了」面板相對於市場板搜尋結果視窗「右上角」的位移。
        /// 與出售品視窗旁那一欄（<see cref="LiveSellListOffset"/>）分開存：兩扇視窗可能同時開著，
        /// 位置互不相干。
        /// </summary>
        public Vector2 MarketBuyPanelOffset = new Vector2(4, 0);

        public int Version { get; set; } = 0;

        // the below exist just to make saving/loading less cumbersome
        [NonSerialized] private static Configuration? _cachedConfig;

        /// <summary>這個世界在排除清單上嗎。</summary>
        public bool IsWorldExcluded(uint worldId)
            => worldId != 0 && PriceSurveyExcludedWorlds.Contains(worldId);

        /// <summary>
        /// 掛售發生的那個世界，也就是家世界。0＝還沒登入或讀不到。
        /// </summary>
        /// <remarks>
        /// 🔑 <b>為什麼是家世界而不是目前所在的世界</b>：僱員綁在家世界上，東西一律掛在那裡賣，
        /// 跑到別的世界只是在看別人的市場。參考價可以來自很多世界，<b>定價的市場只有一個</b>。
        /// 🔴 只從 framework／繪製執行緒呼叫：它讀的是遊戲自己的玩家狀態。
        /// </remarks>
        public static uint SellingWorldId()
            => PlayerState.ContentId == 0 ? 0u : PlayerState.HomeWorld.RowId;

        /// <summary>
        /// 拿來過濾「參考價」的排除清單：設定裡那一份，<b>扣掉掛售所在的那個世界</b>。
        /// </summary>
        /// <remarks>
        /// 🔴 <b>這是「自己掛售的世界永不排除」唯一的實作點。</b>待處理清單的建議價
        /// （<c>PendingActionsBuilder</c>）與最近成交價重掛（<c>LastSoldPriceSource</c>）
        /// 都從這裡拿清單，不各自補一個 if —— 那種寫法漏掉一處就是靜默的語意不一致，
        /// 而且兩條路各自看起來都是對的。
        /// 🔴 只從 framework／繪製執行緒呼叫：它讀那個裸 <c>List</c>，而勾選框是在
        /// 繪製執行緒上改它的。
        /// </remarks>
        public HashSet<uint> BuildPricingExclusions()
            => BuildPricingExclusions(SellingWorldId());

        /// <summary>
        /// 同上，但由呼叫端指定掛售所在的世界。
        /// 🔑 存在的理由：呼叫端若要把「這份清單是扣掉哪個世界算的」一起記下來，
        /// 兩次分開讀玩家狀態可能讀到不同的答案（換角色），那會產生一份自相矛盾的快照。
        /// </summary>
        public HashSet<uint> BuildPricingExclusions(uint sellingWorldId)
        {
            var set = new HashSet<uint>(PriceSurveyExcludedWorlds);
            if (sellingWorldId != 0)
                set.Remove(sellingWorldId);
            return set;
        }

        /// <summary>把一個世界加進／移出排除清單並存檔。沒有真的改到就什麼都不做。</summary>
        public void SetWorldExcluded(uint worldId, bool excluded)
        {
            if (worldId == 0)
                return;

            bool changed;
            if (excluded)
            {
                changed = !PriceSurveyExcludedWorlds.Contains(worldId);
                if (changed)
                    PriceSurveyExcludedWorlds.Add(worldId);
            }
            else
            {
                changed = PriceSurveyExcludedWorlds.Remove(worldId);
            }

            if (!changed)
                return;

            WorldExclusionRevision++;
            Save();
        }

        /// <summary>
        /// 出廠就該排除的世界：台服的拉姆（<see cref="CLOSED_WORLD_RAMUH"/>）已經停止營運，切不過去。
        /// </summary>
        /// <remarks>
        /// 🔑 <b>為什麼走這條一次性的路，而不是寫進欄位初始式</b>：Dalamud 的設定反序列化會把
        /// JSON 的內容<b>加進</b>初始式建好的集合（Newtonsoft 預設的
        /// <c>ObjectCreationHandling.Auto</c>），所以寫在初始式裡的東西使用者拿不掉。
        /// 🔴 <b>離線資料表證明不了「哪個世界還在營運」</b>：<c>World</c> 表裡 4034 仍然存在
        /// （<c>TcRamuh</c>／「拉姆」），而台服每一個世界的 <c>IsPublic</c> 都是 false，
        /// 照它篩會得到空清單。這一筆是實機試過去不了才知道的事實，所以只能寫成
        /// 「出廠預設」，不能寫成「從資料表推出來的判斷」。
        /// </remarks>
        private void SeedClosedWorldExclusions()
        {
            if (PriceSurveyExcludedWorldsSeeded)
                return;

            PriceSurveyExcludedWorldsSeeded = true;
            var added = !PriceSurveyExcludedWorlds.Contains(CLOSED_WORLD_RAMUH);
            if (added)
            {
                PriceSurveyExcludedWorlds.Add(CLOSED_WORLD_RAMUH);
                WorldExclusionRevision++;
            }

            Save();

            // 要使用者回報的診斷一律寫 Information。
            Log.Information(
                $"[Marketbuddy] 巡檢：套用出廠世界排除清單（這次有沒有新增={added}）——" +
                $"拉姆（{CLOSED_WORLD_RAMUH}）已停止營運、切不過去，所以不列進換世界選單、" +
                "不會被自動續跑選中，也不能在那裡開始掃描。這是一次性的：" +
                "在畫面上把它勾掉之後不會再被加回來。");
        }

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
                if (conf.AnomalyGuardMinNormalPrice < 0)
                    conf.AnomalyGuardMinNormalPrice = 0;
                // 🔴 刻意**不**夾到合法範圍內：被手改成範圍外的值時該發生的事是
                //    「保護不作用」（PriceAnomalyGuard.Evaluate 自己會放行），
                //    而不是被我們悄悄改成 2 或 1000 —— 那等於替使用者做了他沒要的決定。
                if (conf.DelistAboveUnitPrice < 0)
                    conf.DelistAboveUnitPrice = 0;
                // 舊設定檔沒有這個鍵時欄位初始值會留著；只有檔案裡明寫 null 才會變成 null。
                // 這一行是為了後者——少了它，之後每一個 Contains/Count 都會 NRE。
                conf.DelistTourSkipRetainers ??= [];
                conf.MarketTaxPercent = Math.Clamp(conf.MarketTaxPercent, 0, 25);
                conf.MarketDataCacheSeconds = Math.Clamp(conf.MarketDataCacheSeconds, 0, 3600);
                conf.PriceSurveyCacheSeconds = Math.Clamp(conf.PriceSurveyCacheSeconds, 0, 3600);
                conf.PriceSurveySkipHours = Math.Clamp(conf.PriceSurveySkipHours, 0, 168);
                // 🔑 兩者的 0 都是「關閉」而不是「不限」，所以夾到 0 是安全方向
                //    （見各自的欄位說明）。上界只是避免設定檔被手改成荒謬值。
                conf.RelistLastSoldMaxAgeDays = Math.Clamp(conf.RelistLastSoldMaxAgeDays, 0, 3650);
                conf.RelistSurveyFallbackHours = Math.Clamp(conf.RelistSurveyFallbackHours, 0, 8760);
                // 舊設定檔沒有這個鍵時欄位初始值（空清單）會留著；只有檔案裡明寫 null
                // 才會變成 null，而那之後每一個 Contains 都會 NRE。
                conf.PriceSurveyExcludedWorlds ??= [];
                conf.PriceSurveyAutoTourMaxWorlds =
                    Math.Clamp(conf.PriceSurveyAutoTourMaxWorlds, 1, MAX_TOUR_WORLDS);
            }

            // 🔴 兩條路（全新設定檔／既有設定檔）都要經過，而且只會真的動一次。
            conf.SeedClosedWorldExclusions();

            _cachedConfig = conf;
            return _cachedConfig;
        }
    }
}