using System;
using Dalamud.Game.Gui.ContextMenu;
using Lumina.Excel.Sheets;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    /// <summary>
    /// 背包道具右鍵選單的「查行情」：只打開行情查詢視窗並選好那件道具，不做別的。
    /// 設定 <see cref="Configuration.MarketLookupContextMenu"/> 關著（預設）時不加任何選項。
    /// </summary>
    internal sealed class MarketLookupContextMenu : IDisposable
    {
        private readonly Action<uint> open;
        private bool disposed;

        public MarketLookupContextMenu(Action<uint> open)
        {
            this.open = open;
            ContextMenu.OnMenuOpened += OnMenuOpened;
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            ContextMenu.OnMenuOpened -= OnMenuOpened;
        }

        private void OnMenuOpened(IMenuOpenedArgs args)
        {
            try
            {
                if (disposed || !Configuration.GetOrLoad().MarketLookupContextMenu)
                    return;
                if (args.MenuType != ContextMenuType.Inventory || args.Target is not MenuTargetInventory { TargetItem: { } item })
                    return;

                var itemId = item.BaseItemId;
                var sheet = DataManager.GetExcelSheet<Item>();
                if (itemId == 0 || sheet == null || !sheet.TryGetRow(itemId, out var row)
                    || row.ItemSearchCategory.RowId == 0 || row.IsUntradable)
                    return;

                args.AddMenuItem(new MenuItem
                {
                    Name = "Market lookup".Loc(),
                    PrefixChar = 'M',
                    OnClicked = _ =>
                    {
                        if (!disposed)
                            open(itemId);
                    },
                });
            }
            catch (Exception e)
            {
                Log.Information(e, "[行情查詢] 右鍵選單加「查行情」失敗，這次不加。");
            }
        }
    }
}
