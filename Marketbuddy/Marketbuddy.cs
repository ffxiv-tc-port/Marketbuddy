using System;
using System.Reflection;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Logging;
using Dalamud.Plugin;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;
using Dalamud.Bindings.ImGui;
using Marketbuddy.Common;
using Marketbuddy.Structs;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    public unsafe class Marketbuddy : IDalamudPlugin
    {
        private const string commandName = "/mbuddy";

        internal PluginUI PluginUi { get; private set; }

        internal MarketGuiEventHandler MarketGuiEventHandler { get; private set; }

        internal BatchReprice BatchReprice { get; private set; }

        internal MultiRetainerReprice MultiReprice { get; private set; }

        internal QuickLister? QuickLister { get; private set; }

        internal ManualRequery ManualRequery { get; private set; }

        internal LiveSellList LiveSellList { get; private set; }

        /// <summary>
        /// 純診斷探針（只記錄、零行為變更）。失敗時為 null，其餘功能不受影響。
        /// </summary>
        private MarketRequestResultProbe? requestResultProbe;

        // Assembly compatible with dev & published versions
        public string AssemblyLocation { get; set; } = Assembly.GetExecutingAssembly().Location;
        public string Name => "Marketbuddy";

        public Marketbuddy(IDalamudPluginInterface pluginInterface)
        {
            try
            {
                DalamudInitialize(pluginInterface);

                // Must run before the UI is constructed or the command registered, as those
                // resolve their .Loc() text once at construction time.
                Localization.Init(pluginInterface.AssemblyLocation.DirectoryName);

                AutoRetainerBridge.Init();

                // Passive global market data cache. Must come up before the
                // engines so nothing that arrives during startup is lost; it
                // only ever writes to a dictionary and never drives anything.
                MarketDataCache.Init();

                // 純被動診斷：記錄每一次市場查價結果的 (listingCount, errorCode, itemId)，
                // 然後原封不動呼叫 Original。用來判定「伺服器拒絕時到底有沒有回封包」。
                // 掛不上去只影響診斷，所以自己吞掉例外。
                try
                {
                    requestResultProbe = new MarketRequestResultProbe();
                }
                catch (Exception e)
                {
                    Log.Warning(e, "Market request result probe could not be initialized (diagnostics only)");
                }

                MarketGuiEventHandler = new MarketGuiEventHandler();

                BatchReprice = new BatchReprice(MarketGuiEventHandler);
                MarketGuiEventHandler.BatchEngine = BatchReprice;

                MultiReprice = new MultiRetainerReprice(MarketGuiEventHandler, BatchReprice);

                ManualRequery = new ManualRequery(MarketGuiEventHandler);

                // Display only: draws a live copy of the retainer's listings next to the
                // game's own (which never redraws itself after a headless reprice).
                LiveSellList = new LiveSellList(MarketGuiEventHandler, BatchReprice);

                try
                {
                    QuickLister = new QuickLister(MarketGuiEventHandler, BatchReprice, MultiReprice);
                    MarketGuiEventHandler.QuickLister = QuickLister;
                }
                catch (Exception e)
                {
                    // Address resolution failure only disables quick listing;
                    // the rest of the plugin keeps working.
                    Log.Error(e, "QuickLister could not be initialized, quick listing disabled");
                }

                PluginUi = new PluginUI(this);

                Common.Dalamud.CommandManager.AddHandler(commandName, new CommandInfo(OnCommand)
                {
                    HelpMessage = "Show plugin configuration window.".Loc()
                });

                PluginInterface.UiBuilder.Draw += DrawUi;
                PluginInterface.UiBuilder.OpenConfigUi += DrawConfigUi;
                IPCManager.Init();

            }
            catch (Exception e)
            {
                if (e is not OperationCanceledException)
                    Log.Error(e, "Error loading plugin");
            }
        }

        public void Dispose()
        {
            IPCManager.Shutdown();
            PluginInterface.UiBuilder.Draw -= DrawUi;
            PluginInterface.UiBuilder.OpenConfigUi -= DrawConfigUi;
            Common.Dalamud.CommandManager.RemoveHandler(commandName);
            PluginUi.Dispose();
            LiveSellList.Dispose();
            QuickLister?.Dispose();
            ManualRequery.Dispose();
            MultiReprice.Dispose();
            BatchReprice.Dispose();
            MarketGuiEventHandler.Dispose();
            requestResultProbe?.Dispose();
            MarketDataCache.Shutdown();
            // Last: must run after every engine released its reference so a
            // leftover suppression can never survive an unload.
            AutoRetainerBridge.Shutdown();
            Commons.Dispose();
        }

        private void OnCommand(string command, string args)
        {
            if (command == commandName)
                PluginUi.SettingsVisible = !PluginUi.SettingsVisible;
        }

        private void DrawUi()
        {
            PluginUi.Draw();
        }

        private void DrawConfigUi()
        {
            PluginUi.SettingsVisible = true;
        }
    }
}