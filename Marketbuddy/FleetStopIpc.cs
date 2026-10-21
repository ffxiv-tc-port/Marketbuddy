using System;
using System.Collections.Generic;
using System.Threading;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    /// <summary>
    /// 全艦隊急停端點 <c>Marketbuddy.StopAll</c>：<c>Action&lt;string&gt;</c>，參數是呼叫端署名；
    /// 與 BOCCHI.StopAll／HoardFarm.StopAll 同形。名字與型別是對外契約。
    /// 任何執行緒都可呼叫：框架執行緒上當場停，其他執行緒只記下來、下一幀停。不擲例外給呼叫端。
    /// 🔴 只做各功能「取消」按鈕做的事：不關使用者的視窗、不改設定；開著的上架視窗交給使用者。
    /// </summary>
    internal sealed class FleetStopIpc : IDisposable
    {
        internal const string IpcName = "Marketbuddy.StopAll";

        private readonly Marketbuddy plugin;
        private readonly ICallGateProvider<string, object> provider;
        private string? pendingSource;

        internal FleetStopIpc(Marketbuddy plugin)
        {
            this.plugin = plugin;
            provider = PluginInterface.GetIpcProvider<string, object>(IpcName);
            provider.RegisterAction(Request);
            Framework.Update += OnFrameworkUpdate;
        }

        public void Dispose()
        {
            Framework.Update -= OnFrameworkUpdate;
            provider.UnregisterAction();
        }

        private void Request(string? source)
        {
            var label = string.IsNullOrWhiteSpace(source) ? "?" : source.Trim();
            // 署名會填進聊天訊息的「??」佔位；署名裡若也有「??」，後面的參數會填錯位置。
            while (label.Contains("??", StringComparison.Ordinal))
                label = label.Replace("??", "?", StringComparison.Ordinal);
            try
            {
                if (Framework.IsInFrameworkUpdateThread)
                {
                    StopAll(label);
                    return;
                }
            }
            catch (Exception e)
            {
                Log.Error(e, $"[Marketbuddy] 急停（{label}）執行失敗");
                return;
            }

            Interlocked.Exchange(ref pendingSource, label);
        }

        private void OnFrameworkUpdate(IFramework framework)
        {
            if (Volatile.Read(ref pendingSource) == null)
                return;
            var label = Interlocked.Exchange(ref pendingSource, null);
            if (label == null)
                return;
            try
            {
                StopAll(label);
            }
            catch (Exception e)
            {
                Log.Error(e, $"[Marketbuddy] 急停（{label}）執行失敗");
            }
        }

        /// <summary>只在框架執行緒呼叫。每一項各自防護，一項失敗不擋後面的。</summary>
        private void StopAll(string source)
        {
            var reason = "stopped by ??".Loc(source);
            var stopped = new List<string>();

            // 多角色要先解除武裝：先停底下的巡迴的話，它會把這一角當成「跳過」接著跑下一角。
            Step("多角色重掛", () => plugin.MultiCharTour is { IsArmed: true } c && Run(() => c.Stop(reason)), stopped);
            Step("僱員巡迴", () => plugin.MultiTour is { IsRunning: true } t && Run(() => t.EmergencyStop(reason)), stopped);
            Step("價格巡檢", () =>
            {
                var survey = plugin.PriceSurvey;
                if (survey == null || (!survey.IsRunning && !survey.IsTourArmed))
                    return false;
                survey.RequestPause(reason);
                survey.RequestDisarmTour(reason);
                return true;
            }, stopped);
            Step("批次上架", () => plugin.BatchLister is { IsRunning: true } l && Run(() => l.EmergencyStop(reason)), stopped);
            Step("賣商店", () => plugin.VendorSeller is { IsRunning: true } v && Run(() => v.EmergencyStop(reason)), stopped);
            Step("快速上架", () => plugin.QuickLister?.EmergencyStop(reason) > 0, stopped);
            Step("批次下架", () => plugin.BatchDelist is { IsRunning: true } d && Run(() => d.EmergencyStop(reason)), stopped);
            Step("重掛", () => plugin.BatchReprice is { IsRunning: true } r && Run(() => r.EmergencyStop(reason)), stopped);

            Log.Information(stopped.Count == 0
                ? $"[Marketbuddy] 收到急停（{source}）：當下沒有在跑的自動化"
                : $"[Marketbuddy] 收到急停（{source}）：已停 {string.Join("、", stopped)}");
        }

        private static bool Run(Action action)
        {
            action();
            return true;
        }

        private static void Step(string name, Func<bool> step, List<string> stopped)
        {
            try
            {
                if (step())
                    stopped.Add(name);
            }
            catch (Exception e)
            {
                Log.Error(e, $"[Marketbuddy] 急停：停「{name}」時發生例外，繼續停其他項目");
            }
        }
    }
}
