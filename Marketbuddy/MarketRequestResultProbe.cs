using System;
using System.Reflection;
using System.Threading;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    /// <summary>
    /// 純診斷探針：在 <c>InfoProxyItemSearch::ProcessRequestResult</c> 上掛一個
    /// **只記錄、不改任何行為** 的 hook，用來回答一個問題 ——
    /// **台服的市場查價被拒絕時，伺服器到底有沒有回一個帶錯誤碼的封包？**
    ///
    /// ── 為什麼需要這一版 ──────────────────────────────────────────────
    /// DailyRoutines 的 <c>AutoRefreshMarketSearchResult</c> 的前提是：伺服器拒絕時會呼叫
    /// 這個函式並帶著 <c>errorCode != 0</c>，模組就在那個瞬間立刻重送。但我們自己的
    /// MBDIAG 實測顯示，台服的拒絕形態是 **完全靜默**（<see cref="BatchReprice"/> 只能靠
    /// 「連 history 封包都沒有 + 逾時」來判定被吞掉），而且使用者明確表示
    /// **從來沒在聊天窗看過「無法在市場中進行搜尋。」**（＝ <c>LogMessage[385]</c>，
    /// 也就是 <c>errorCode = 0x181</c> 時遊戲一定會印的那行）。
    ///
    /// 若拒絕時根本沒有封包回來，那個 hook 永遠不會觸發，整個移植是零效果的空轉。
    /// 所以先量一輪：**<c>errorCode</c> 恆為 0 就不做真功能。**
    ///
    /// ── 這一版做什麼、不做什麼 ────────────────────────────────────────
    /// 做：把每一次 <c>(listingCount, errorCode, SearchItemId)</c> 寫進 log，然後
    ///     **原封不動呼叫 Original 並回傳它的結果**。
    /// 不做：不重送請求、不改記憶體、不吞掉 Original。這三件事各有明確理由：
    ///   • 在 detour 裡直接呼叫 <c>RequestData()</c> 會 **繞過
    ///     <see cref="MarketRequestGate.NoteRequestSent"/>**，讓閘門對「上次送出時間」的
    ///     模型立刻失真，<see cref="BatchReprice"/> 下一次就算錯間隔 —— 那正是
    ///     2026-08-02 那輪診斷剛修掉的 bug 形狀。
    ///   • 吞掉 Original 的代價是 <c>ListingCount</c> 沒被寫成 0、<c>EntryCount</c> 沒被清，
    ///     於是卡住期間 <c>SearchItemId</c> 已經是新道具，清單與計數卻還是上一件的
    ///     —— 不會崩，會 **給錯價**。
    ///
    /// ── 位址從哪來 ────────────────────────────────────────────────────
    /// 用 bundled ClientStructs 自己解出來的
    /// <c>InfoProxyItemSearch.Addresses.ProcessRequestResult</c>，**不寫死特徵碼**。
    /// 已在台服 7.20 客戶端離線驗證：CS 的那條特徵碼在 <c>.text</c> 唯一命中
    /// （<c>0x1418C523A</c>，E8 跟隨後目標 <c>0x1409274A0</c>），且該函式不是死碼
    /// （call xref 1、jmp 0、lea 0、絕對指標 0；呼叫鏈往上接到 <c>.rdata</c> 兩張
    /// 函式指標表引用的封包分派函式）。解不出來時 <c>Value</c> 是 0，不會丟例外，
    /// 我們就 **不掛 hook、記一行 Warning、外掛照常運作**。
    ///
    /// ── 簽名 ──────────────────────────────────────────────────────────
    /// 呼叫端反編譯是決定性證據：
    /// <code>
    ///   mov   r8d, dword ptr [rbx]      ; arg3 = int32 errorCode
    ///   mov   rcx, rax                  ; arg1 = InfoProxyItemSearch*
    ///   movzx edx, byte ptr [rbx + 4]   ; arg2 = byte  listingCount
    ///   call  0x1409274A0               ; ← r9 從未被設定
    /// </code>
    /// 函式序言 <c>movzx ebp, dl</c>（只讀 DL）、<c>mov edi, r8d</c>（只讀 32 位），
    /// 而函式體內每一處 r9 都是 **寫入**（當成別的呼叫的出參），從未被當成入參讀取。
    /// ⚠️ 所以 DR 宣告的第四個參數 <c>a4</c> **根本不存在**，<c>entryCount</c> 也是
    /// <c>byte</c> 不是 <c>int</c> —— 不要照抄 DR 的 delegate。
    /// </summary>
    internal sealed unsafe class MarketRequestResultProbe : IDisposable
    {
        // 與 BatchReprice / MarketRequestGate 一致的 grep 標籤。
        // 這些 log 刻意用 Information 等級：使用者的記錄等級會濾掉 DBG/VRB。
        // Grep tag: MBDIAG / MKTRESULT
        private const string Diag = "[MBDIAG]";

        /// <summary>
        /// <c>nint (InfoProxyItemSearch* self, byte listingCount, int errorCode)</c>。
        /// 回傳型別取 <c>nint</c> 而不是 <c>void</c>，是為了把 Original 的 rax 原封不動傳回去
        /// （呼叫端實際上不讀 rax，兩種宣告都不會炸，但傳回去是嚴格較安全的那個）。
        /// </summary>
        private delegate nint ProcessRequestResultDelegate(InfoProxyItemSearch* self, byte listingCount, int errorCode);

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
                // 不掛 hook、不往無效位址寫，外掛其餘功能完全不受影響。
                Log.Warning(
                    $"{Diag} MKTRESULT probe disabled: ClientStructs could not resolve " +
                    "InfoProxyItemSearch.ProcessRequestResult (address 0). " +
                    "This is diagnostics only - nothing else is affected.");
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
                Log.Warning(e, $"{Diag} MKTRESULT probe could not be installed - diagnostics only, nothing else is affected.");
                return;
            }

            // ⚠️ Dalamud 的 "Loading plugin" 那行不帶版本號，所以在這裡自己印一次：
            // 之後看 log 時可以先確認「有載入到這一版」再往下判讀。
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";
            Log.Information(
                $"{Diag} MKTRESULT probe armed (Marketbuddy v{version}) at 0x{address:X} - " +
                "passive logging only, no behaviour change.");
        }

        public void Dispose()
        {
            hook?.Dispose();

            var total = Volatile.Read(ref totalSeen);
            if (total == 0)
                return;

            var errors = Volatile.Read(ref errorSeen);
            Log.Information(
                $"{Diag} MKTRESULT probe summary: {total} result(s) seen, {errors} with a non-zero errorCode.");
        }

        /// <summary>
        /// 純被動 detour。第一件事是記錄，然後 **一定** 呼叫 Original 並回傳它的結果。
        /// ⚠️ <paramref name="self"/> 是原生指標，只在這一格內讀，**絕不跨幀保存**。
        /// </summary>
        private nint Detour(InfoProxyItemSearch* self, byte listingCount, int errorCode)
        {
            try
            {
                var seq = Interlocked.Increment(ref totalSeen);
                // self 已由呼叫端驗過非 null（test rax,rax; je），這裡再驗一次純屬保險。
                var itemId = self != null ? self->SearchItemId : 0u;

                if (errorCode != 0)
                {
                    Interlocked.Increment(ref errorSeen);
                    // 這一行的存在與否就是「值不值得做真功能」的答案。
                    // errorCode 就是 LogMessage 的 row id（台服 385 = 「無法在市場中進行搜尋。」）。
                    Log.Information(
                        $"{Diag} MKTRESULT-ERR #{seq} listingCount={listingCount} " +
                        $"errorCode={errorCode} (0x{errorCode:X}) itemId={itemId}");
                }
                else
                {
                    Log.Information(
                        $"{Diag} MKTRESULT #{seq} listingCount={listingCount} " +
                        $"errorCode=0 itemId={itemId}");
                }
            }
            catch (Exception e)
            {
                // 記錄失敗絕對不能影響遊戲的處理流程。
                Log.Warning(e, $"{Diag} MKTRESULT probe logging failed");
            }

            return hook!.Original(self, listingCount, errorCode);
        }
    }
}
