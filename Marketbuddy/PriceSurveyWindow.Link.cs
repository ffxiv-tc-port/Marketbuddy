using System;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Marketbuddy.Common;
using static Marketbuddy.Common.ImGuiEx;

namespace Marketbuddy
{
    internal sealed partial class PriceSurveyWindow
    {
        // =====================================================================
        //  兩台電腦（區網連線）
        // =====================================================================

        private string? linkAddressEdit;

        private void DrawLinkTab()
        {
            var link = survey.Link;
            ImGui.Spacing();
            GreyWrapped("Two computers on the same home network can share survey prices: whatever either one looks up is merged into the other's own records (each side works out \"is that my listing\" for itself). The host can also hand its list to the other computer and start, pause, continue or stop a full round there - the other computer then runs every world on its own, even if this window is closed or the host's game goes away."
                .Loc());
            ImGui.Spacing();

            if (ImGui.CollapsingHeader(DrawText.Concat("This computer is the host".Loc(), "###mblinkhost"),
                    ImGuiTreeNodeFlags.DefaultOpen))
                DrawLinkHost(link);

            ImGui.Spacing();
            if (ImGui.CollapsingHeader(DrawText.Concat("This computer surveys for the host".Loc(), "###mblinkclient"),
                    ImGuiTreeNodeFlags.DefaultOpen))
                DrawLinkClient(link);

            ImGui.Spacing();
            if (ImGui.CollapsingHeader(DrawText.Concat("Full market scan on this computer".Loc(), "###mbfullmarket")))
                DrawFullMarketLocal();

            ImGui.Spacing();
            ImGui.Separator();
            var outbox = link.OutboxCount;
            if (outbox > 0)
                Grey(DrawText.Loc("?? price(s) waiting to be sent to the other computer.", outbox));
            if (link.MergeStatus.Length > 0)
                GreyWrapped(link.MergeStatus);
        }

        private void DrawLinkHost(SurveyLink link)
        {
            using (Disabled(conf.SurveyLinkClientEnabled))
            {
                if (ImGui.Checkbox("Open the link on this computer".Loc(), ref conf.SurveyLinkHostEnabled))
                {
                    if (conf.SurveyLinkHostEnabled)
                        conf.SurveyLinkClientEnabled = false;
                    conf.Save();
                }
            }

            TooltipInDisabled(
                "Off by default. While it is on, this computer listens on the port below for one other computer on your home network (private addresses only) - and only while the game is running. Nothing starts on its own: you press every button here yourself. The setting is remembered, so the link opens again next time the game starts, but a survey never starts by itself."
                    .Loc());

            using (Disabled(conf.SurveyLinkHostEnabled))
            {
                ImGui.SetNextItemWidth(Px(120));
                var port = conf.SurveyLinkPort;
                if (ImGui.InputInt("Port".Loc() + "##mblinkport", ref port, 0) && port is >= 1024 and <= 65535)
                {
                    conf.SurveyLinkPort = port;
                    conf.Save();
                }
            }

            if (!conf.SurveyLinkHostEnabled)
                return;

            var host = link.Host;
            if (host == null)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudRed);
                ImGui.TextWrapped(DrawText.Loc("Could not open the link: ??",
                    link.HostStartError.Length > 0 ? link.HostStartError : "?"));
                ImGui.PopStyleColor();
                return;
            }

            var addresses = link.LocalAddresses;
            ImGui.TextWrapped(addresses.Count > 0
                ? DrawText.Loc("Enter this on the other computer: ??", string.Join("  /  ", addresses) + ":" + host.Port)
                : DrawText.Loc("Listening on port ??. (This computer's network address could not be read - look it up in Windows.)", host.Port));
            GreyWrapped("The first time, Windows asks whether to allow the game through the firewall. That prompt can be hidden behind the game window - press Alt+Tab to find it and allow private networks."
                .Loc());

            if (host.PendingPairing is { } pairing)
            {
                ImGui.Spacing();
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudYellow);
                ImGui.TextWrapped(DrawText.Loc("?? wants to pair. Code: ?? - check the other computer shows the same code.",
                    pairing.Address, pairing.Code));
                ImGui.PopStyleColor();
                if (ImGui.Button("Allow pairing".Loc() + "##mblinkallow", new Vector2(Px(120), 0)))
                    host.ResolvePairing(true);
                ImGui.SameLine();
                if (ImGui.Button("Deny".Loc() + "##mblinkdeny", new Vector2(Px(120), 0)))
                    host.ResolvePairing(false);
            }

            ImGui.Spacing();
            var paired = conf.SurveyLinkHostKeyId.Length > 0;
            if (!paired)
            {
                Grey("Not paired yet: on the other computer, enter the address above and press Pair.".Loc());
                return;
            }

            Grey("Paired.".Loc());
            SameLineIfFits(ButtonWidth("Revoke pairing".Loc()));
            if (ImGui.Button("Revoke pairing".Loc()))
                link.Revoke();
            Tooltip("Forgets the other computer's key and disconnects it. It has to pair again to reconnect.".Loc());

            var connected = host.IsConnected;
            var status = host.LatestStatus;
            if (!connected)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
                ImGui.TextWrapped(host.LastConnectedUtc == DateTime.MinValue
                    ? "The other computer is not connected.".Loc()
                    : DrawText.Loc("The other computer is not connected (last connected ??).", LocalTime(host.LastSeenUtc)));
                ImGui.PopStyleColor();
            }
            else
            {
                ImGui.TextUnformatted(DrawText.Loc("Connected: ??", host.ClientAddress));
                if (status != null)
                    DrawClientStatus(status);
            }

            ImGui.Spacing();
            var block = status?.StartBlock ?? string.Empty;
            var canStart = connected && status != null && block.Length == 0;
            using (Disabled(!canStart))
            {
                if (ImGui.Button("Start a round there".Loc(), new Vector2(Px(180), 0)))
                    link.PushList(true);
            }

            if (TooltipHovered())
                Tooltip(canStart
                    ? "Sends your current list and your excluded worlds, then the other computer surveys every world that is not excluded, one after another, and stops when it is done. Prices come back here as they are found; if this game closes, it carries on and sends them once you reconnect."
                        .Loc()
                    : !connected
                        ? "The other computer is not connected.".Loc()
                        : block.Length > 0 ? block : "Waiting for the other computer's status.".Loc());

            ImGui.SameLine();
            using (Disabled(!connected))
            {
                if (ImGui.Button("Send the list only".Loc()))
                    link.PushList(false);
            }

            var canFull = canStart && status!.SupportsFullMarket;
            using (Disabled(!canFull))
            {
                if (ImGui.Button("Full market scan there".Loc(), new Vector2(Px(180), 0)))
                    link.SendFullMarketStart();
            }

            if (TooltipHovered())
                Tooltip(!connected
                    ? "The other computer is not connected.".Loc()
                    : status == null
                        ? "Waiting for the other computer's status.".Loc()
                        : !status.SupportsFullMarket
                            ? "The other computer runs an older Marketbuddy that cannot do a full market scan.".Loc()
                            : block.Length > 0
                                ? block
                                : FullMarketExplain() + "\n\n" + FullMarketEstimate(status.FullPending, status.FullPlanned));

            if (connected && status is { Gil: >= 0 } && status.Gil < PriceSurvey.LowGilWarning)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
                ImGui.TextWrapped(DrawText.Loc(
                    "The other computer has only ?? gil: if Lifestream's departure city is not a free aetheryte, it may not be able to pay for the teleport when changing worlds.",
                    status.Gil.ToString("N0", CultureInfo.InvariantCulture)));
                ImGui.PopStyleColor();
            }

            using (Disabled(!connected || status is not { RemoteControlled: true }))
            {
                if (ImGui.Button("Pause".Loc() + "##mblinkpause", new Vector2(Px(120), 0)))
                    link.SendCommand(RemoteCommandKind.Pause);
                ImGui.SameLine();
                if (ImGui.Button("Stop".Loc() + "##mblinkstop", new Vector2(Px(120), 0)))
                    link.SendCommand(RemoteCommandKind.Stop);
            }

            ImGui.SameLine();
            using (Disabled(!connected || status is not { State: "paused", RemoteControlled: true }))
            {
                if (ImGui.Button("Continue".Loc() + "##mblinkresume", new Vector2(Px(120), 0)))
                    link.SendCommand(RemoteCommandKind.Resume);
            }

            if (link.LastPushStatus.Length > 0)
                GreyWrapped(link.LastPushStatus);
            if (host.LastCommandResult is { } result)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, result.Ok ? ImGuiColors.DalamudGrey : ImGuiColors.DalamudOrange);
                ImGui.TextWrapped(result.Ok
                    ? DrawText.Loc("The other computer did it (??).", LocalTime(result.AtUtc))
                    : DrawText.Loc("The other computer refused: ??", result.Why));
                ImGui.PopStyleColor();
            }
        }

        private void DrawClientStatus(ClientStatus s)
        {
            if (!s.LoggedIn)
            {
                Grey("The other computer is not logged in.".Loc());
            }
            else
            {
                var state = s.State switch
                {
                    "running" => "surveying ?? / ??".Loc(s.Processed, s.Total),
                    "preparing" => "preparing".Loc(),
                    "paused" => "paused".Loc(),
                    _ => "idle".Loc(),
                };
                ImGui.TextWrapped(DrawText.Loc("World ??, ??", s.WorldName.Length > 0 ? s.WorldName : "?", state));
                if (s.Mode == "full")
                    ImGui.TextUnformatted("Full market: world ?? / ??, ?? / ?? item(s)".Loc(Math.Max(1, s.FullWorld),
                        Math.Max(s.FullWorld, s.FullWorlds), s.Processed, s.Total));
                else if (s.RemoteControlled && s.TourMax > 0)
                    ImGui.TextUnformatted("Round: world ?? of ??".Loc(Math.Max(1, s.TourStep), s.TourMax));
            }

            if (s.Halt.Length > 0 && !s.RemoteControlled)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
                ImGui.TextWrapped(DrawText.Loc("The other computer stopped: ??", s.Halt));
                ImGui.PopStyleColor();
            }

            if (s.TourStatus.Length > 0)
                GreyWrapped(s.TourStatus);
            else if (s.StatusText.Length > 0)
                GreyWrapped(s.StatusText);
            if (!s.Consent)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
                ImGui.TextWrapped("The other computer has not allowed remote control yet.".Loc());
                ImGui.PopStyleColor();
            }
            else if (s.StartBlock.Length > 0 && !s.RemoteControlled)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
                ImGui.TextWrapped(DrawText.Loc("It cannot start right now: ??", s.StartBlock));
                ImGui.PopStyleColor();
            }

            if (s.Outbox > 0)
                Grey(DrawText.Loc("?? price(s) on the other computer still waiting to be sent here.", s.Outbox));
        }

        private void DrawLinkClient(SurveyLink link)
        {
            using (Disabled(conf.SurveyLinkHostEnabled))
            {
                if (ImGui.Checkbox("Connect to the host".Loc(), ref conf.SurveyLinkClientEnabled))
                {
                    if (conf.SurveyLinkClientEnabled)
                        conf.SurveyLinkHostEnabled = false;
                    conf.Save();
                }
            }

            TooltipInDisabled(
                "Off by default. This computer connects out to the host (nothing has to be opened in this computer's firewall). If the host does not answer - its game is closed, or its link is off - it simply tries again later."
                    .Loc());

            linkAddressEdit ??= conf.SurveyLinkHostAddress;
            ImGui.SetNextItemWidth(Px(220));
            ImGui.InputTextWithHint("##mblinkaddress", "192.168.1.10:47835", ref linkAddressEdit, 64);
            if (ImGui.IsItemDeactivatedAfterEdit() && linkAddressEdit != conf.SurveyLinkHostAddress)
            {
                conf.SurveyLinkHostAddress = linkAddressEdit.Trim();
                conf.Save();
            }

            ImGui.SameLine();
            Grey("Host address".Loc());

            if (!conf.SurveyLinkClientEnabled)
                return;

            var client = link.Client;
            if (client == null)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudRed);
                ImGui.TextWrapped("The host address must be a private IPv4 address, for example 192.168.1.10 (a port can follow after a colon)."
                    .Loc());
                ImGui.PopStyleColor();
                return;
            }

            switch (client.State)
            {
                case ClientLinkState.Connected:
                    ImGui.TextUnformatted("Connected to the host.".Loc());
                    break;
                case ClientLinkState.Pairing:
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudYellow);
                    ImGui.TextWrapped(DrawText.Loc("Pairing code ??: press Allow on the host if it shows the same code.", client.PairCode));
                    ImGui.PopStyleColor();
                    break;
                case ClientLinkState.NeedsPairing:
                    Grey("Not paired yet - press Pair, then allow it on the host.".Loc());
                    break;
                case ClientLinkState.Rejected:
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
                    ImGui.TextWrapped(client.Detail switch
                    {
                        "unknown-key" => "The host no longer knows this computer (pairing revoked) - pair again.".Loc(),
                        "pair-denied" => "The host denied the pairing (or nobody answered in time).".Loc(),
                        "busy" => "The host is already pairing with someone else - try again.".Loc(),
                        _ => DrawText.Loc("The host refused the connection (??).", client.Detail),
                    });
                    ImGui.PopStyleColor();
                    break;
                case ClientLinkState.WaitingRetry:
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
                    ImGui.TextWrapped(DrawText.Loc(
                        "The host is not answering (its game is closed, or its link is off). Trying again at ??.",
                        LocalTime(client.NextRetryUtc)));
                    ImGui.PopStyleColor();
                    break;
                default:
                    Grey("Connecting...".Loc());
                    break;
            }

            if (client.State is ClientLinkState.WaitingRetry or ClientLinkState.Rejected)
            {
                if (ImGui.Button("Try now".Loc()))
                    client.Wake();
                ImGui.SameLine();
            }

            using (Disabled(client.State is ClientLinkState.Pairing or ClientLinkState.Connecting))
            {
                if (ImGui.Button("Pair".Loc()))
                    client.RequestPairing();
            }

            if (conf.SurveyLinkClientKeyId.Length > 0)
            {
                ImGui.SameLine();
                if (ImGui.Button("Forget the pairing".Loc()))
                    link.ForgetPairing();
            }

            ImGui.Spacing();
            var consent = conf.SurveyLinkAllowRemoteControl;
            if (ImGui.Checkbox("Allow the host to start, pause, continue and stop the survey here".Loc(), ref consent))
            {
                conf.SurveyLinkAllowRemoteControl = consent;
                if (consent)
                {
                    conf.PriceSurveyEnabled = true;
                    conf.PriceSurveyAutoTour = true;
                }

                conf.Save();
            }

            Tooltip(
                "Off by default. Ticking it also turns on the price survey and automatic world hopping on this computer, because a round from the host needs both. The host can only ever start, pause, continue or stop a survey round here - never list, reprice, delist or sell anything. A round is refused while this character is in a duty, an event, combat, a mini game, or while another plugin is moving it, and the reason is shown on the host. This computer's own Stop button and Marketbuddy.StopAll always win."
                    .Loc());

            if (survey.RemoteListId.Length > 0)
                Grey(DrawText.Loc("List from the host: ?? item(s).", survey.RemoteListCount));
        }

        private static string FullMarketExplain()
            => "Every marketable item on every world of that computer's data centre, stalest first: the order follows when anyone last updated it on Universalis, and items someone updated recently are skipped. Items nobody sells or has ever bought on any world are checked less often. It runs one full round and then waits. Before starting, turn off AutoRetainer's multi-mode and Saucy on that computer - if AutoRetainer gets busy, the round stops there."
                .Loc();

        private static string FullMarketEstimate(int pending, bool planned)
        {
            if (pending < 0)
                return "Estimate not ready yet.".Loc();
            var seconds = FullMarketPlanner.EstimateSeconds(pending, MarketRequestGate.IntervalMs);
            var text = "Estimate: ?? item(s) x ?? s = about ?? hour(s).".Loc(pending.ToString("N0", CultureInfo.InvariantCulture),
                (MarketRequestGate.IntervalMs / 1000.0).ToString("0.#", CultureInfo.InvariantCulture),
                (seconds / 3600).ToString("0.0", CultureInfo.InvariantCulture));
            return planned
                ? text
                : text + " " + "Not counting items someone updated on Universalis recently, so the real run is shorter.".Loc();
        }

        /// <summary>這台自己按的全市場掃描：開始、停止、兩個門檻與 Gil 提醒。</summary>
        private void DrawFullMarketLocal()
        {
            survey.NoteFullPreviewWanted();
            var active = survey.IsFullMarketActive;
            var canArm = survey.CanArmTour(out var why) && !survey.IsRunning;
            using (Disabled(active || !canArm))
            {
                if (ImGui.Button("Start a full market round".Loc() + "##mbfullstart", new Vector2(Px(200), 0)))
                    survey.RequestArmFullMarket();
            }

            if (TooltipHovered())
                Tooltip(active
                    ? "A full market scan is already running.".Loc()
                    : !canArm
                        ? why.Length > 0 ? why : "A price survey is already running".Loc()
                        : FullMarketExplain() + "\n\n" + FullMarketEstimate(survey.FullPendingEstimate, survey.FullPendingPlanned));

            ImGui.SameLine();
            using (Disabled(!active))
            {
                if (ImGui.Button("Stop".Loc() + "##mbfullstop", new Vector2(Px(120), 0)))
                {
                    survey.RequestStop("Stopped by the user".Loc());
                    survey.RequestDisarmTour("Disarmed by the user".Loc());
                }
            }

            if (survey.LowGil)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
                ImGui.TextWrapped(DrawText.Loc(
                    "This character has only ?? gil: if Lifestream's departure city is not a free aetheryte, changing worlds may fail because the teleport cannot be paid for.",
                    survey.CurrentGil.ToString("N0", CultureInfo.InvariantCulture)));
                ImGui.PopStyleColor();
            }

            if (survey.LastHalt.Length > 0 && !active)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
                ImGui.TextWrapped(DrawText.Loc("The last round stopped: ??", survey.LastHalt));
                ImGui.PopStyleColor();
            }

            ImGui.SetNextItemWidth(Px(100));
            var fresh = conf.FullMarketFreshHours;
            if (ImGui.InputInt("Skip items updated within (hours)".Loc() + "##mbfullfresh", ref fresh, 1))
            {
                conf.FullMarketFreshHours = Math.Clamp(fresh, 0, 168);
                conf.Save();
            }

            Tooltip("Items that someone (on Universalis, or either of your computers) updated within this many hours are skipped this round. 0 = never skip."
                .Loc());

            ImGui.SetNextItemWidth(Px(100));
            var noData = conf.FullMarketNoDataDays;
            if (ImGui.InputInt("Recheck dead items after (days)".Loc() + "##mbfullnodata", ref noData, 1))
            {
                conf.FullMarketNoDataDays = Math.Clamp(noData, 0, 90);
                conf.Save();
            }

            Tooltip("Items that nobody sells and that have no sales at all on every world are checked again only after this many days. The first round always checks everything. 0 = check them every round."
                .Loc());
        }

        private static string LocalTime(DateTime utc)
            => utc == DateTime.MinValue
                ? "?"
                : utc.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }
}
