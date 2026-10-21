using System.Runtime.CompilerServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    /// <summary>
    /// 原生 Get／SetRetainerMarketPrice 的唯一入口。
    /// 🔴 原生端只做有號的上界比較、沒有下界：負格號會讀寫市場價陣列前面的記憶體，Set 還會送出一筆改價指令
    /// ⇒ 格號不在 0～19 一律不呼叫原生。🔴 只從 framework 執行緒呼叫。
    /// </summary>
    internal static unsafe class RetainerMarketPrice
    {
        /// <summary>原生改價函式接受的格數上限（市場容器的格數不會超過它）。</summary>
        public const int SlotCount = 20;

        public static bool IsValidSlot(int slot) => (uint)slot < SlotCount;

        /// <summary>回 false＝格號不合法或沒有 InventoryManager，<paramref name="price"/> 為 0。</summary>
        public static bool TryGet(InventoryManager* inventoryManager, int slot, out ulong price,
            [CallerMemberName] string caller = "")
        {
            price = 0;
            if (inventoryManager == null || !CheckSlot(slot, caller, "讀價"))
                return false;
            price = inventoryManager->GetRetainerMarketPrice((short)slot);
            return true;
        }

        /// <summary>回 false＝格號不合法或沒有 InventoryManager，什麼都沒送。</summary>
        public static bool TrySet(InventoryManager* inventoryManager, int slot, uint price,
            [CallerMemberName] string caller = "")
        {
            if (inventoryManager == null || !CheckSlot(slot, caller, "改價"))
                return false;
            inventoryManager->SetRetainerMarketPrice((short)slot, price);
            return true;
        }

        /// <summary>格號合法回 true；不合法寫一行 Information 並回 false。</summary>
        internal static bool CheckSlot(int slot, string caller, string what)
        {
            if (IsValidSlot(slot))
                return true;
            Log.Information($"[Marketbuddy] 市場格號 {slot} 不在 0～{SlotCount - 1}，{caller} 的{what}沒有呼叫原生函式");
            return false;
        }
    }
}
