using System;
using Dalamud.Game.Network.Structures;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Marketbuddy.Common;
using static Marketbuddy.Common.Dalamud;
using CSFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace Marketbuddy
{
    /// <summary>
    /// Auto-requery for the interactive market price list (the native
    /// "ItemSearchResult" addon - both the standalone market board search and
    /// the "compare prices" popup opened from a retainer's sell window).
    ///
    /// The server occasionally throttles a market query ("please wait before
    /// searching again") or the response otherwise never lands; previously the
    /// only recourse was for the player to close and reopen the window by
    /// hand. This engine polls the (read-only) InfoProxyItemSearch.SearchItemId
    /// from Framework.Update while the window is open, and if neither
    /// IMarketBoard.OfferingsReceived nor .HistoryReceived fires for that item
    /// within a short window, re-issues InfoProxyItemSearch.RequestData() for
    /// the same item - the same request the game's own UI would have sent.
    ///
    /// No hooks, no memory patches: this is exactly the same mechanism
    /// BatchReprice already uses for headless batch pricing, just watching the
    /// user's own interactive window instead of driving one of our own. It
    /// steps aside whenever BatchReprice/MultiRetainerTour own the shared
    /// InfoProxyItemSearch request slot, so the two never fight over it.
    /// Backoff is capped and the retry count bounded, so a genuinely rejected
    /// search can never turn into a request flood: if the cap is reached
    /// without a response, this simply gives up and the window is left
    /// exactly as it would have been without this feature.
    /// </summary>
    internal sealed unsafe class ManualRequery : IDisposable
    {
        // Tuned for the interactive case: the player is watching this window,
        // so retries need to beat "close it and reopen by hand" (roughly a
        // 1-2 second human reaction), not just eventually succeed. First retry
        // fires fast; the backoff is capped low (never escalates to multi-
        // second waits) in exchange for a generous attempt budget.
        private const int StallCheckMs = 800;   // wait this long for any response before treating the attempt as stalled
        private const int BackoffStepMs = 500;  // backoff grows by this much per retry...
        private const int BackoffCapMs = 2000;  // ...capped here
        private const int MaxAttempts = 10;     // give up quietly after this many retries

        private readonly MarketGuiEventHandler gui;

        private bool watching;
        private uint watchedItemId;
        private bool responseSeen;
        private int attempt;
        private DateTime nextActionAt;

        private Configuration conf => Configuration.GetOrLoad();

        public ManualRequery(MarketGuiEventHandler gui)
        {
            this.gui = gui;
            MarketBoard.OfferingsReceived += OnOfferingsReceived;
            MarketBoard.HistoryReceived += OnHistoryReceived;
            Framework.Update += OnFrameworkUpdate;
        }

        public void Dispose()
        {
            Framework.Update -= OnFrameworkUpdate;
            MarketBoard.HistoryReceived -= OnHistoryReceived;
            MarketBoard.OfferingsReceived -= OnOfferingsReceived;
        }

        private void OnFrameworkUpdate(IFramework framework)
        {
            if (!conf.AutoRequeryOnThrottle || !gui.IsItemSearchResultOpen || IPCManager.IsLocked)
            {
                watching = false;
                return;
            }

            // Never contend with our own headless engines for the shared
            // InfoProxyItemSearch request slot (BatchReprice drives both the
            // "relist all" batch and every quick-list single-slot reprice).
            if (gui.BatchEngine?.IsRunning == true)
            {
                watching = false;
                return;
            }

            // 🔴 CSFramework.Instance() 是 [StaticAddress(..., isPointer: true)]:產生器讀
            //    「指標的位址」再解參考一層,所以它會回 null(不帶 isPointer 的那種才保證
            //    非 null)。裸解參考 null 原生指標是 AccessViolationException,在 .NET Core
            //    屬 corrupted-state exception,try/catch 攔不到 ⇒ 只能事前判空。
            //    這裡每幀執行,取不到就當成「視窗未啟用」直接 return(fail-closed),不寫 log。
            var csFramework = CSFramework.Instance();
            if (csFramework == null || csFramework->WindowInactive)
                return;

            var proxy = GetItemSearchProxy();
            if (proxy == null)
            {
                watching = false;
                return;
            }

            var currentItemId = proxy->SearchItemId;
            if (!watching || currentItemId != watchedItemId)
            {
                // Fresh query: the window just opened, or the player searched
                // a different item without closing it. Either way, start a
                // new watch and give the game's own request a chance first.
                watching = true;
                watchedItemId = currentItemId;
                responseSeen = false;
                attempt = 0;
                nextActionAt = DateTime.UtcNow.AddMilliseconds(StallCheckMs);
                return;
            }

            if (responseSeen)
                return; // the server already answered for this item

            if (DateTime.UtcNow < nextActionAt)
                return;

            if (attempt >= MaxAttempts)
                return; // quietly give up - same as if this feature did not exist

            attempt++;
            Log.Information($"[Marketbuddy] ManualRequery: item {watchedItemId} search looked stalled, retrying ({attempt}/{MaxAttempts})");
            // Same shared server-side pacing applies to this request as to the batch
            // engine's, so it has to be recorded even though this path never waits on
            // the gate itself: the player is watching this window and a retry that is
            // 200 ms "too early" is far better than one that never fires. Recording it
            // keeps a batch started right afterwards from colliding with it.
            MarketRequestGate.NoteRequestSent(DateTime.UtcNow);
            proxy->RequestData();
            var backoff = Math.Min(BackoffStepMs * attempt, BackoffCapMs);
            nextActionAt = DateTime.UtcNow.AddMilliseconds(backoff);
        }

        private void OnOfferingsReceived(IMarketBoardCurrentOfferings offerings)
        {
            if (!watching)
                return;

            var listings = offerings.ItemListings;
            // A non-empty page can be attributed to an item; an empty one
            // cannot, but with only one interactive window ever watched here,
            // accepting it unconditionally is safe (worst case we stop
            // watching a stalled query one response early - a no-op).
            if (listings.Count > 0 && listings[0].ItemId != watchedItemId)
                return;

            responseSeen = true;
        }

        private void OnHistoryReceived(IMarketBoardHistory history)
        {
            if (!watching || history.ItemId != watchedItemId)
                return;

            responseSeen = true;
        }

        private static InfoProxyItemSearch* GetItemSearchProxy()
        {
            var infoModule = InfoModule.Instance();
            return infoModule == null
                ? null
                : (InfoProxyItemSearch*)infoModule->GetInfoProxyById(InfoProxyId.ItemSearch);
        }
    }
}
