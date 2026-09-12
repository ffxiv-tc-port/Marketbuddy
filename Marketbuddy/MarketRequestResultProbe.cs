using System;
using System.Reflection;
using System.Threading;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    /// <summary>
    /// 伺服器對一次市場查價的**真實答覆**。由 <see cref="MarketRequestResultProbe"/> 的 detour
    /// 在封包處理堆疊上填好，再由 framework tick 取走。
    /// </summary>
    /// <param name="ItemId">這個答覆屬於哪一件道具（取自 <c>InfoProxyItemSearch.SearchItemId</c>）。</param>
    /// <param name="ListingCount">伺服器宣告的**總**掛售筆數（跨所有分頁，見 <see cref="MarketRequestResultProbe"/> 的說明）。</param>
    /// <param name="Refused">這次查詢被拒絕了（<c>errorCode != 0</c>）。</param>
    /// <param name="ErrorCode">診斷用的原始錯誤碼；<see cref="Refused"/> 為 false 時是 0。</param>
    internal readonly record struct MarketRequestResult(uint ItemId, byte ListingCount, bool Refused, int ErrorCode);

    /// <summary>
    /// 在 <c>InfoProxyItemSearch::ProcessRequestResult</c> 上掛一個 hook，把伺服器對每一次市場
    /// 查價的答覆轉交給 <see cref="BatchReprice"/> —— 讓它不必再用逾時去**猜**伺服器怎麼了。
    /// • **不呼叫 <c>RequestData()</c>。** 那會繞過 <see cref="MarketRequestGate.NoteRequestSent"/>，
    ///   重送一律由既有的 framework tick 路徑發起、走既有的閘門。
    /// • **不碰任何集合或外掛狀態。** 封包分派不保證在主執行緒，所以交接只用一個
    ///   <see cref="Interlocked"/> 單槽（<see cref="signal"/>），沒有鎖、沒有配置、沒有字典。
    /// • **不吞掉 Original。** 代價會是 <c>ListingCount</c>/<c>EntryCount</c> 留著上一件的值，
    ///   而那時 <c>SearchItemId</c> 已經是新道具 —— 不會崩，會**給錯價**。
    /// </summary>
    internal sealed unsafe class MarketRequestResultProbe : IDisposable
    {
        // 與 BatchReprice / MarketRequestGate 一致的 grep 標籤。
        // 這些 log 刻意用 Information 等級：使用者的記錄等級只會濾掉 VRB、DBG 收得到但單檔數十萬行會淹沒。
        // Grep tag: MBDIAG / MKTRESULT
        private const string Diag = "[MBDIAG]";

        /// <summary>
        /// <c>nint (InfoProxyItemSearch* self, byte listingCount, int errorCode)</c>。
        /// 回傳型別取 <c>nint</c> 而不是 <c>void</c>，是為了把 Original 的 rax 原封不動傳回去
        /// （呼叫端實際上不讀 rax，兩種宣告都不會炸，但傳回去是嚴格較安全的那個）。
        /// </summary>
        private delegate nint ProcessRequestResultDelegate(InfoProxyItemSearch* self, byte listingCount, int errorCode);

        // 跨執行緒交接：**一個 long 的單槽**，detour 寫、framework tick 取。
        // 佈局（0 ＝ 空槽）：
        //   bit 63     valid
        //   bit 40     refused（errorCode != 0）
        //   bits 39-32 listingCount
        //   bits 31-0  itemId
        // 寫入順序（先 errorCode 後 Interlocked 發佈）提供 release 語意。
        private const long ValidBit = unchecked((long)0x8000_0000_0000_0000UL);
        private const long RefusedBit = 1L << 40;

        private static long signal;
        private static int signalErrorCode;
        private static int installedFlag;

        /// <summary>
        /// hook 是否真的掛上了。false 時 <see cref="TryTakeResult"/> 永遠回 false，
        /// 消費端必須退回自己原本的逾時／寬限判定。
        /// </summary>
        public static bool IsInstalled => Volatile.Read(ref installedFlag) != 0;

        /// <summary>
        /// 送出一次 <c>RequestData()</c> **之前**呼叫：丟掉任何還留在槽裡的舊答覆
        /// （上一次嘗試的、或是玩家自己開市場板查別的東西留下的），
        /// 這樣取到的一定是這一次請求之後才到的結果。
        /// </summary>
        public static void ArmForRequest() => Interlocked.Exchange(ref signal, 0);

        /// <summary>
        /// 取走伺服器對 <paramref name="expectedItemId"/> 的答覆。取得後槽即清空（消費一次）。
        /// </summary>
        public static bool TryTakeResult(uint expectedItemId, out MarketRequestResult result)
        {
            result = default;
            var raw = Interlocked.Exchange(ref signal, 0);
            if (raw == 0)
                return false;

            var itemId = (uint)(raw & 0xFFFF_FFFFL);
            if (itemId != expectedItemId)
                return false;

            var refused = (raw & RefusedBit) != 0;
            result = new MarketRequestResult(
                itemId,
                (byte)((raw >> 32) & 0xFF),
                refused,
                refused ? Volatile.Read(ref signalErrorCode) : 0);
            return true;
        }

        private readonly Hook<ProcessRequestResultDelegate>? hook;

        // 統計只給 Dispose 時的一行總結用；detour 可能在非主執行緒的封包堆疊上跑，
        // 所以用 Interlocked，不要用 ++。
        private int totalSeen;
        private int errorSeen;

        public MarketRequestResultProbe()
        {
            var address = InfoProxyItemSearch.Addresses.ProcessRequestResult.Value;
            if (address == 0)
            {
                // 優雅降級：ClientStructs 解不出這個位址（改版後特徵碼失效）。
                // 不掛 hook、不往無效位址寫；BatchReprice 會看到 IsInstalled == false
                // 而退回原本的逾時／寬限判定，功能不受影響（只是慢回去）。
                Log.Warning(
                    $"{Diag} MKTRESULT probe disabled: ClientStructs could not resolve " +
                    "InfoProxyItemSearch.ProcessRequestResult (address 0). " +
                    "Batch repricing falls back to timeout-based detection.");
                return;
            }

            try
            {
                hook = Hook.HookFromAddress<ProcessRequestResultDelegate>(address, Detour);
                hook.Enable();
            }
            catch (Exception e)
            {
                hook = null;
                Log.Warning(e, $"{Diag} MKTRESULT probe could not be installed - batch repricing falls back to timeout-based detection.");
                return;
            }

            Interlocked.Exchange(ref signal, 0);
            Volatile.Write(ref installedFlag, 1);

            // ⚠️ Dalamud 的 "Loading plugin" 那行不帶版本號，所以在這裡自己印一次：
            // 之後看 log 時可以先確認「有載入到這一版」再往下判讀。
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";
            Log.Information(
                $"{Diag} MKTRESULT probe armed (Marketbuddy v{version}) at 0x{address:X} - " +
                "server verdicts now feed the batch engine (retries still go through MarketRequestGate).");
        }

        public void Dispose()
        {
            // 先讓消費端停止相信這個訊號，再拆 hook。
            Volatile.Write(ref installedFlag, 0);
            Interlocked.Exchange(ref signal, 0);
            hook?.Dispose();

            var total = Volatile.Read(ref totalSeen);
            if (total == 0)
                return;

            var errors = Volatile.Read(ref errorSeen);
            Log.Information(
                $"{Diag} MKTRESULT probe summary: {total} result(s) seen, {errors} with a non-zero errorCode.");
        }

        /// <summary>
        /// detour。**一定**呼叫 Original 並回傳它的結果 —— 遊戲自己的處理完全不變。
        /// 我們只是在旁邊記下伺服器說了什麼。
        /// ⚠️ <paramref name="self"/> 是原生指標，只在這一格內讀，**絕不跨幀保存**。
        /// </summary>
        private nint Detour(InfoProxyItemSearch* self, byte listingCount, int errorCode)
        {
            try
            {
                var seq = Interlocked.Increment(ref totalSeen);
                // self 已由呼叫端驗過非 null（test rax,rax; je），這裡再驗一次純屬保險。
                var itemId = self != null ? self->SearchItemId : 0u;
                var refused = errorCode != 0;

                // 先寫診斷欄位、再用 Interlocked 發佈整包：Interlocked 提供 release 語意，
                // 消費端讀到 valid 位元時必定看得到上面這一行的值。
                Volatile.Write(ref signalErrorCode, errorCode);
                Interlocked.Exchange(
                    ref signal,
                    ValidBit | (refused ? RefusedBit : 0L) | ((long)listingCount << 32) | itemId);

                if (refused)
                {
                    Interlocked.Increment(ref errorSeen);
                    // errorCode ≥ 0x70000000 時遊戲**不會**印聊天訊息（InfoModule 只對
                    // [1, 0x6FFFFFFF] 走 LogMessage），所以這一行是使用者唯一的線索。
                    Log.Information(
                        $"{Diag} MKTRESULT-ERR #{seq} listingCount={listingCount} " +
                        $"errorCode={errorCode} (0x{errorCode:X}) itemId={itemId}");
                }
                else
                {
                    // 正常答覆每一次查價都有一筆，是 log 的大宗 -> Debug。
                    // 上面的 MKTRESULT-ERR 維持 Information：errorCode >= 0x70000000 時
                    // 遊戲不會印任何聊天訊息，那一行是使用者唯一的線索。
                    Log.Debug(
                        $"{Diag} MKTRESULT #{seq} listingCount={listingCount} " +
                        $"errorCode=0 itemId={itemId}");
                }
            }
            catch (Exception e)
            {
                // 記錄失敗絕對不能影響遊戲的處理流程。
                Log.Warning(e, $"{Diag} MKTRESULT probe logging failed");
            }

            return hook!.OriginalDisposeSafe(self, listingCount, errorCode);
        }
    }
}
