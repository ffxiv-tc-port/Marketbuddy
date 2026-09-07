using System;
using System.Numerics;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Text;
using Dalamud.Logging;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.Attributes;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.NativeWrapper;
using Marketbuddy.Common;
using Marketbuddy.Structs;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    public unsafe class MarketGuiEventHandler : IDisposable
    {
        internal Configuration conf => Configuration.GetOrLoad();

        /// <summary>Injected after construction; shares the delist thresholds and tax logic with the batch engine.</summary>
        internal BatchReprice? BatchEngine { get; set; }

        /// <summary>Injected after construction; shares the mutual-exclusion state with the delist engine.</summary>
        internal BatchDelist? DelistEngine { get; set; }

        /// <summary>Injected after construction; the cross-world price survey (read-only, manual only).</summary>
        internal PriceSurvey? Survey { get; set; }

        /// <summary>Injected after construction; lets the quick-list flow take over RetainerSell when it opened it.</summary>
        internal QuickLister? QuickLister { get; set; }

        /// <summary>
        /// Injected after construction; the pending-actions worklist driver.
        /// 🔴 純計算與「把某一格交給既有的重掛引擎」，本身不改任何價格。
        /// </summary>
        internal PendingActionsBuilder? Pending { get; set; }

        private IntPtr AddonRetainerSellList = IntPtr.Zero;
        private IntPtr AddonRetainerList = IntPtr.Zero;
        private IntPtr AddonItemSearchResult = IntPtr.Zero;

        internal bool IsRetainerSellListOpen => AddonRetainerSellList != IntPtr.Zero;

        internal bool IsRetainerListOpen => AddonRetainerList != IntPtr.Zero;

        /// <summary>
        /// True while the market price list (ItemSearchResult) is open, be it
        /// the standalone market board search or the "compare prices" popup
        /// opened from a retainer's sell window. Used by <see cref="ManualRequery"/>
        /// to know when to watch for a stalled/throttled query.
        /// </summary>
        internal bool IsItemSearchResultOpen => AddonItemSearchResult != IntPtr.Zero;

        public MarketGuiEventHandler()
        {
            AddonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, "ItemSearchResult", OnItemSearchResultReceiveEvent);

            AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "RetainerSell", OnRetainerSellSetup);
            AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "ItemSearchResult", OnItemSearchResultSetup);
            AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "ItemSearchResult", OnItemSearchResultFinalize);
            AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "RetainerSellList", OnRetainerSellListSetup);
            AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "RetainerSellList", OnRetainerSellListFinalize);

            AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "RetainerList", OnRetainerListSetup);
            AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "RetainerList", OnRetainerListFinalize);
        }

        private void OnRetainerListSetup(AddonEvent type, AddonArgs args)
        {
            AddonRetainerList = args.Addon;
        }

        private void OnRetainerListFinalize(AddonEvent type, AddonArgs args)
        {
            AddonRetainerList = IntPtr.Zero;
        }

        /// <summary>
        /// 僱員選單目前的螢幕矩形：左上角座標與**已套用縮放**的尺寸。
        /// 巡迴面板靠它貼在原生視窗右邊並跟著它跑。
        ///
        /// 🔴 與 <see cref="AddonRetainerSellList_Frame"/> 同一套作法，只讀已建模的**純欄位**，
        /// 不呼叫任何特徵碼解析的原生函式（理由見那個方法的說明）。
        /// </summary>
        internal unsafe bool AddonRetainerList_Frame(out Vector2 topLeft, out Vector2 size)
        {
            topLeft = Vector2.Zero;
            size = Vector2.Zero;
            if (AddonRetainerList == IntPtr.Zero)
                return false;

            var addon = (AtkUnitBase*)AddonRetainerList;
            if (!addon->IsVisible || addon->RootNode == null ||
                addon->UldManager.LoadedState != AtkLoadState.Loaded)
                return false;

            var scale = addon->Scale;
            if (scale <= 0f || float.IsNaN(scale))
                scale = 1f;

            topLeft = new Vector2(addon->X, addon->Y);
            size = new Vector2(addon->RootNode->Width * scale, addon->RootNode->Height * scale);
            return true;
        }

        private void OnRetainerSellListFinalize(AddonEvent type, AddonArgs args)
        {
            var addon = args.Addon;
            if (addon == AddonRetainerSellList)
                DebugMessage($"AddonRetainerSellList.OnFinalize (known: {addon:X})");
            else
                DebugMessage(
                    $"AddonRetainerSellList.OnFinalize (unk. have {AddonRetainerSellList:X} got {addon:X})");
            AddonRetainerSellList = IntPtr.Zero;
        }

        private void OnRetainerSellListSetup(AddonEvent type, AddonArgs args)
        {
            var addon = args.Addon;
            DebugMessage($"AddonRetainerSellList.OnSetup (got: {addon:X})");
            AddonRetainerSellList = addon;
        }

        private void OnItemSearchResultSetup(AddonEvent type, AddonArgs args)
        {
            DebugMessage("AddonItemSearchResult.OnSetup");
            IntPtr addon = args.Addon;
            AddonItemSearchResult = addon;

            if (!IPCManager.IsLocked)
            {
                bool shouldOpenHistory = conf.AutoOpenHistory && !conf.HoldAltHistoryHandling
                                     || conf.AutoOpenHistory && conf.HoldAltHistoryHandling && !Keys[VirtualKey.MENU]
                                     || !conf.AutoOpenHistory && conf.HoldAltHistoryHandling && Keys[VirtualKey.MENU];

                if (shouldOpenHistory)
                    try
                    {
                        //open history on opening the list
                        var history = ((AddonItemSearchResult*)addon)->History->AtkComponentBase.OwnerNode;
                        //Client::UI::AddonItemSearchResult.ReceiveEvent this=0x1CC2BF42BD0 evt=EventType.CHANGE               a3=23  a4=0x1CCD86C1460 a5=0x90EF96E598
                        // 切頁籤不會關掉視窗，所以是 Auxiliary（各自 key）；
                        // 但那扇窗若已經被「回答」過（例如已經送出關閉），守衛一樣會擋下。
                        if (AddonPressGuard.TryPress("ItemSearchResult", addon, PressKind.Auxiliary, 23))
                            Commons.SendClick(addon, EventType.CHANGE, 23, history);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Houston, we have a problem");
                    }
            }
        }

        private void OnRetainerSellSetup(AddonEvent type, AddonArgs args)
        {
            DebugMessage("AddonRetainerSell.OnSetup");
            IntPtr addon = args.Addon;

            // A pending quick-list opened this window: its own listener fills
            // the cap price and confirms; skip the normal accelerator flow
            // (auto compare / CTRL paste) entirely.
            if (QuickLister?.IsQuickListPending == true)
                return;

            if (!IPCManager.IsLocked)
            {
                if (conf.HoldCtrlToPaste && Keys[VirtualKey.CONTROL])
                {
                    var cbValue = ImGuiEx.GetClipboardText();
                    if (int.TryParse(cbValue, out var priceValue) && priceValue > 0)
                        SetPrice(priceValue);
                    else
                        ChatGui.PrintError("[Marketbuddy] Clipboard does not contain a valid price".Loc());
                }
                else if (conf.AutoOpenComparePrices && !conf.HoldShiftToStop ||
                         conf.AutoOpenComparePrices && conf.HoldShiftToStop && !Keys[VirtualKey.SHIFT] ||
                         !conf.AutoOpenComparePrices && conf.HoldShiftToStop && Keys[VirtualKey.SHIFT])
                {
                    try
                    {
                        //open compare prices list on opening sell price selection
                        var comparePrices = ((AddonRetainerSell*)addon)->ComparePrices->AtkComponentBase.OwnerNode;
                        // Client::UI::AddonRetainerSell.ReceiveEvent this=0x214C05CB480 evt=EventType.CHANGE               a3=4   a4=0x2146C18C210 (src=0x214C05CB480; tgt=0x214606863B0) a5=0xBB316FE6C8
                        // 開比價視窗不會關掉 RetainerSell，所以是 Auxiliary。
                        if (AddonPressGuard.TryPress("RetainerSell", addon, PressKind.Auxiliary, 4))
                            Commons.SendClick(addon, EventType.CHANGE, 4, comparePrices);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Houston, we have a problem");
                    }
                }
            }
        }

        private void OnItemSearchResultReceiveEvent(AddonEvent type, AddonArgs args)
        {
            var eventArgs = (AddonReceiveEventArgs)args;
            var eventType = eventArgs.AtkEventType;
            var nodeParam = eventArgs.Data;
            if (!IPCManager.IsLocked)
            {
                if (conf.AutoInputNewPrice || conf.SaveToClipboard)
                    if (eventType == 35 && nodeParam != IntPtr.Zero) // && (*eventInfoStruct) != null ) // click
                        try
                        {
                            //AtkUldManager uldManager = (*eventInfoStruct)->UldManager;
#pragma warning disable IDE0004
                            //casts are necessary
                            var price = conf.UndercutUsePercent ? (int)((float)getPricePerItem(nodeParam) * (1f - (float)conf.UndercutPercent / 100f)) : getPricePerItem(nodeParam) - conf.UndercutPrice;
#pragma warning restore IDE0004
                            price =
                                price < Configuration.MIN_PRICE ? Configuration.MIN_PRICE
                                : price > Configuration.MAX_PRICE ? Configuration.MAX_PRICE
                                : price;

                            SetPrice(price);
                        }
                        catch (Exception e)
                        {
                            ChatGui.PrintError(
                                "[Marketbuddy] Error getting price per item or setting the new price. Use /xllog to see the error and submit it in a github issue"
                                    .Loc());
                            Log.Error(e, "Error getting price per item or setting the new price");
                        }
            }
        }

        private void OnItemSearchResultFinalize(AddonEvent type, AddonArgs args)
        {
            AddonItemSearchResult = IntPtr.Zero;
        }

        /// <summary>
        /// 出售品視窗目前的螢幕矩形：左上角座標與**已套用縮放**的尺寸。
        /// 重掛面板靠它貼在原生視窗下方並跟著它跑。
        ///
        /// 🔴 與 <see cref="LiveSellList"/> 同一套作法，刻意只讀已建模的**純欄位**
        /// （X / Y / RootNode->Width / RootNode->Height / Scale），不呼叫
        /// GetScaledWidth() 之類**特徵碼解析**的原生函式——台服上解到錯的函式就是
        /// AccessViolation，而 AVE 在 .NET Core 是 corrupted-state exception，try/catch 攔不到。
        /// </summary>
        internal unsafe bool AddonRetainerSellList_Frame(out Vector2 topLeft, out Vector2 size)
        {
            topLeft = Vector2.Zero;
            size = Vector2.Zero;
            if (AddonRetainerSellList == IntPtr.Zero)
                return false;

            var addon = (AtkUnitBase*)AddonRetainerSellList;
            if (!addon->IsVisible || addon->RootNode == null ||
                addon->UldManager.LoadedState != AtkLoadState.Loaded)
                return false;

            var scale = addon->Scale;
            if (scale <= 0f || float.IsNaN(scale))
                scale = 1f;

            topLeft = new Vector2(addon->X, addon->Y);
            size = new Vector2(addon->RootNode->Width * scale, addon->RootNode->Height * scale);
            return true;
        }

        public void Dispose()
        {
            AddonLifecycle.UnregisterListener(AddonEvent.PostReceiveEvent, "ItemSearchResult", OnItemSearchResultReceiveEvent);

            AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "RetainerSell", OnRetainerSellSetup);
            AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "ItemSearchResult", OnItemSearchResultSetup);
            AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "ItemSearchResult", OnItemSearchResultFinalize);
            AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "RetainerSellList", OnRetainerSellListSetup);
            AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "RetainerSellList", OnRetainerSellListFinalize);

            AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "RetainerList", OnRetainerListSetup);
            AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "RetainerList", OnRetainerListFinalize);
        }

        private unsafe void SetPrice(int newPrice)
        {
            var retainerSell = Commons.GetUnitBase("RetainerSell");
            if (retainerSell == null) return;

            if (TryBlockListing(retainerSell, newPrice))
                return;

            if (retainerSell->UldManager.NodeListCount != 23)
                throw new MarketException("Unexpected fields in addon RetainerSell");

            var priceComponentNumericInput = GetNumericInput(retainerSell, 15);
            var quantityComponentNumericInput = GetNumericInput(retainerSell, 11);
            DebugMessage($"componentNumericInput: {new IntPtr(priceComponentNumericInput).ToString("X")}");
            DebugMessage($"componentNumericInput: {new IntPtr(quantityComponentNumericInput).ToString("X")}");

            if (conf.AutoInputNewPrice && priceComponentNumericInput != null)
            {
                priceComponentNumericInput->SetValue(newPrice);

                if (conf.UseMaxStackSize && quantityComponentNumericInput != null)
                {
                    var quantityTextNode =
                        ((AtkComponentNumericInputCustom*)quantityComponentNumericInput)->AtkTextNode;
                    if (quantityTextNode != null)
                    {
                        var quantityValueString = Commons.Utf8StringToString(quantityTextNode->NodeText);
                        DebugMessage($"qty: {quantityValueString}");
                        if (int.TryParse(quantityValueString, out var quantityValue))
                        {
                            if (quantityValue > conf.MaximumStackSize)
                                quantityComponentNumericInput->SetValue(conf.MaximumStackSize);
                        }
                    }
                }
            }

            if (conf.SaveToClipboard)
                ImGui.SetClipboardText(newPrice.ToString());

            DebugMessage($"Asking price of {newPrice} gil set and copied to clipboard.");

            if (!conf.AutoConfirmNewPrice) return;

            // close ItemSearchResult
            // Component::GUI::AtkComponentWindow.ReceiveEvent this=0x1AC801863B0 evt=EventType.CHANGE               a3=2   a4=0x1AC66640090 (src=0x1AC801863B0; tgt=0x1AC98B47EA0) a5=0x4AAAEFE388
            CloseItemSearchResultWindow();

            // click confirm on RetainerSell
            // Client::UI::AddonRetainerSell.ReceiveEvent this=0x214B4D360E0 evt=EventType.CHANGE               a3=21  a4=0x214B920D2E0 (src=0x214B4D360E0; tgt=0x21460686550) a5=0xBB316FE6C8
            // 🔴 Confirm 送出之後視窗會留到伺服器回應才關 —— 那幾幀裡 GetUnitBase 仍然
            //    回得到它。SetPrice 可以被第二次列項事件重入，重送 Confirm 就是原生 AVE。
            var addonRetainerSell = (AddonRetainerSell*)retainerSell;
            if (AddonPressGuard.TryPress("RetainerSell", (nint)retainerSell, PressKind.Terminal))
                Commons.SendClick(new IntPtr(addonRetainerSell), EventType.CHANGE, 21, addonRetainerSell->Confirm);
        }

        /// <summary>
        /// 取出 RetainerSell 底下第 nodeIndex 個節點所掛的數值輸入元件,取不到就回 null。
        ///
        /// 呼叫端已經用 NodeListCount == 23 擋掉版面不符的情況,但那只保證索引在範圍內 ——
        /// NodeList[n] 本身仍可能是 null,而 AtkResNode.GetComponent() 是 [MemberFunction]
        /// 原生呼叫,對 null 節點呼叫就是攔不到的 AVE。這裡把「節點存在」與「索引在範圍內」
        /// 兩件事都驗過再呼叫。
        /// </summary>
        private static unsafe AtkComponentNumericInput* GetNumericInput(AtkUnitBase* addon, int nodeIndex)
        {
            if (addon == null)
                return null;

            ref var uld = ref addon->UldManager;
            if (uld.NodeList == null || nodeIndex < 0 || nodeIndex >= uld.NodeListCount)
                return null;

            var node = uld.NodeList[nodeIndex];
            if (node == null || (int)node->Type < 1000)
                return null;

            return (AtkComponentNumericInput*)((AtkComponentNode*)node)->Component;
        }

        /// <summary>
        /// Closes the "compare prices" window by replaying the window-close event.
        ///
        /// 原本這裡是一條零檢查的六層裸鏈:
        ///   addon->WindowNode->Component->UldManager.NodeList[7]->GetComponent()->OwnerNode
        /// WindowNode / Component / NodeList[7] / GetComponent() 任何一層是 null 都會解參考,
        /// 而 GetComponent() 本身是 [MemberFunction] 原生呼叫、對 null 節點呼叫即 AVE;
        /// AVE 在 .NET Core 屬於 corrupted-state exception,try/catch 完全攔不到。
        /// NodeList 又是原生指標陣列,沒有 Length 可以靠 —— 索引 7 一定要先比對 NodeListCount,
        /// 只驗 != null 是半套(越界讀到的是垃圾不是 null)。
        /// 失敗時的行為是「不送這個關閉事件」,視窗留著,不會崩。
        /// </summary>
        private unsafe void CloseItemSearchResultWindow()
        {
            var addon = Commons.GetUnitBase("ItemSearchResult");
            if (addon == null)
                return;

            var windowNode = addon->WindowNode;
            if (windowNode == null)
                return;

            var windowComponent = windowNode->Component;
            if (windowComponent == null)
                return;

            ref var uld = ref windowComponent->UldManager;
            if (uld.LoadedState != AtkLoadState.Loaded || uld.NodeList == null || uld.NodeListCount <= 7)
            {
                DebugMessage("CloseItemSearchResultWindow: window component uld not usable");
                return;
            }

            var closeButtonNode = uld.NodeList[7];
            // AtkResNode 的大小是 0xB0,而 AtkComponentNode.Component 位在 0xB0 ——
            // 對非 component 節點取 Component 就是讀出界。CS 對 component 節點的 Type 一律 >= 1000。
            if (closeButtonNode == null || (int)closeButtonNode->Type < 1000)
            {
                DebugMessage("CloseItemSearchResultWindow: node 7 is not a component node");
                return;
            }

            var closeButtonComponent = ((AtkComponentNode*)closeButtonNode)->Component;
            if (closeButtonComponent == null)
                return;

            // 守衛記的是 ItemSearchResult 這扇**視窗**（不是那個 window component）：
            // 關閉事件送出後的幾幀它還在，重送就是對關閉中的視窗再按一次。
            if (!AddonPressGuard.TryPress("ItemSearchResult", (nint)addon, PressKind.Terminal))
                return;

            Commons.SendClick(new IntPtr(windowComponent), EventType.CHANGE, 2, closeButtonComponent->OwnerNode);
        }

        /// <summary>
        /// Interactive-listing guard (covers the manual flow and listings
        /// initiated by AutoRetainer's quick "put up for sale" key, which only
        /// auto-selects the context menu entry and then hands the RetainerSell
        /// window to us). Purely synchronous - nothing here waits, so nothing
        /// can hang; worst case the price is simply not filled in.
        /// Gated by the same opt-in thresholds as the batch delist feature.
        /// </summary>
        private unsafe bool TryBlockListing(AtkUnitBase* retainerSell, int newPrice)
        {
            var engine = BatchEngine;
            if (engine == null || newPrice <= 0)
                return false;

            // Item identity: while the compare-prices list is open,
            // InfoProxyItemSearch.SearchItemId is authoritative for the item
            // being priced. Without it only the item-independent minimum-price
            // check applies.
            uint itemId = 0;
            if (Commons.GetUnitBase("ItemSearchResult") != null)
            {
                var infoModule = FFXIVClientStructs.FFXIV.Client.UI.Info.InfoModule.Instance();
                var proxy = infoModule == null
                    ? null
                    : (FFXIVClientStructs.FFXIV.Client.UI.Info.InfoProxyItemSearch*)infoModule->GetInfoProxyById(
                        FFXIVClientStructs.FFXIV.Client.UI.Info.InfoProxyId.ItemSearch);
                if (proxy != null)
                    itemId = proxy->SearchItemId;
            }

            // HQ-ness from the sell window's displayed item name (HQ icon char).
            var isHq = false;
            var itemNameNode = ((AddonRetainerSell*)retainerSell)->ItemName;
            if (itemNameNode != null)
                isHq = Commons.Utf8StringToString(itemNameNode->NodeText)
                    .Contains((char)SeIconChar.HighQuality);

            if (!engine.ShouldBlockListing((uint)newPrice, itemId, isHq, out var reason))
                return false;

            ChatGui.PrintError("[Marketbuddy] Price not set: ??".Loc(reason));

            if (conf.AutoConfirmNewPrice)
            {
                // The auto flow would have confirmed immediately; cancel the
                // whole listing instead so the item stays where it was. Both
                // closes are one-shot native calls, nothing waits on them.
                // 這兩個 Close 也是「對可能正在關閉中的視窗做原生呼叫」，
                // 而 SetPrice 可以被第二次列項事件重入 —— 一樣過守衛。
                var addonItemSearchResult = Commons.GetUnitBase("ItemSearchResult");
                if (addonItemSearchResult != null &&
                    AddonPressGuard.TryPress("ItemSearchResult", (nint)addonItemSearchResult, PressKind.Terminal))
                    addonItemSearchResult->Close(true);
                if (AddonPressGuard.TryPress("RetainerSell", (nint)retainerSell, PressKind.Terminal))
                    retainerSell->Close(true);
                ChatGui.Print("[Marketbuddy] Listing cancelled, the item stays where it was".Loc());
            }

            return true;
        }

        /// <summary>
        /// Quick-list path: unconditionally fills the asking price and clicks
        /// confirm on RetainerSell (independent of the accelerator settings -
        /// this flow's contract is fully automatic listing). Respects the
        /// stack-size limit setting. Returns false when the addon does not
        /// look like the expected RetainerSell layout.
        /// </summary>
        internal unsafe bool QuickListFillAndConfirm(IntPtr addonPtr, int price)
        {
            var retainerSell = (AtkUnitBase*)addonPtr;
            if (retainerSell == null)
                return false;

            if (retainerSell->UldManager.NodeListCount != 23)
            {
                Log.Warning("QuickList: unexpected fields in addon RetainerSell");
                return false;
            }

            var priceComponentNumericInput = GetNumericInput(retainerSell, 15);
            var quantityComponentNumericInput = GetNumericInput(retainerSell, 11);
            if (priceComponentNumericInput == null)
                return false;

            // 守衛擋下就整個不做（不要填了價格才發現不能按，留下一個半完成的視窗）。
            if (!AddonPressGuard.TryPress("RetainerSell", addonPtr, PressKind.Terminal))
                return false;

            priceComponentNumericInput->SetValue(price);

            if (conf.UseMaxStackSize && quantityComponentNumericInput != null)
            {
                var quantityTextNode = ((AtkComponentNumericInputCustom*)quantityComponentNumericInput)->AtkTextNode;
                if (quantityTextNode != null)
                {
                    var quantityValueString = Commons.Utf8StringToString(quantityTextNode->NodeText);
                    if (int.TryParse(quantityValueString, out var quantityValue) && quantityValue > conf.MaximumStackSize)
                        quantityComponentNumericInput->SetValue(conf.MaximumStackSize);
                }
            }

            var addonRetainerSell = (AddonRetainerSell*)retainerSell;
            Commons.SendClick(addonPtr, EventType.CHANGE, 21, addonRetainerSell->Confirm);
            return true;
        }

        private unsafe int getPricePerItem(IntPtr /* AtkResNode* */ nodeParam)
        {
            var listAtkResNode = (AtkResNode*)nodeParam;

            // list item renderer component
            var listAtkComponentBase = *(AtkComponentBase**)nodeParam;

            DebugMessage(
                $"component={(ulong)listAtkResNode->GetComponent():X}, childCount={listAtkResNode->ChildCount}, target={*(ulong*)nodeParam:X}, gotit={(ulong)listAtkComponentBase:X}");

            if (listAtkComponentBase == null) return 0;
            var uldManager = listAtkComponentBase->UldManager;

            var isMarketOpen = Commons.GetUnitBase("ItemSearch") != null;
            DebugMessage("1");

            if (isMarketOpen) return 0;
            DebugMessage("2");

            // NodeListCount 是上界的真值來源,但 NodeList 本身也可能還沒配置 ——
            // 兩個都要驗,只驗其中一個是半套。
            if (uldManager.NodeListCount < 14 || uldManager.NodeList == null) return 0;
            DebugMessage("3");

            var singlePriceNode = (AtkTextNode*)uldManager.NodeList[10];

            // AtkTextNode.NodeText 位在 AtkResNode(0xB0)之後,對非文字節點讀它就是讀出界。
            if (singlePriceNode == null || singlePriceNode->AtkResNode.Type != NodeType.Text)
            {
                DebugMessage($"singlePriceNode == null {singlePriceNode == null}");
                return 0;
            }

            var priceString = Commons.Utf8StringToString(singlePriceNode->NodeText)
                .Replace($"{(char)SeIconChar.Gil}", "")
                .Replace(",", "")
                .Replace(" ", "")
                .Replace(".", "");

            DebugMessage(
                $"priceString: '{priceString}', original: '{Commons.Utf8StringToString(singlePriceNode->NodeText)}'");

            if (!int.TryParse(priceString, out var priceValue)) return 0;
            return priceValue;
        }

        private void DebugMessage(string msg)
        {
#if DEBUG
            Log.Debug(msg);
#endif
        }
    }
}
