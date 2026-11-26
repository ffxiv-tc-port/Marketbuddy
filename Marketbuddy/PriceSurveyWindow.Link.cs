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
                if (ImGui.Button("Allow".Loc() + "##mblinkallow", new Vector2(Px(120), 0)))
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
                if (s.RemoteControlled && s.TourMax > 0)
                    ImGui.TextUnformatted("Round: world ?? of ??".Loc(Math.Max(1, s.TourStep), s.TourMax));
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

        private static string LocalTime(DateTime utc)
            => utc == DateTime.MinValue
                ? "?"
                : utc.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }
}
