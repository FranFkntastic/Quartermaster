using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using RQ.Courier;
using RQ.Domain;
using RQ.Runtime;

namespace RQ.UI;

/// <summary>
/// The Courier section of the stock options popup. The logged-in character either carries for a
/// paired owner, or owns runs: then it picks a carrier, the return legs and meeting bells.
/// </summary>
internal sealed class CourierSettingsSection
{
    private readonly CourierSession courier;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;

    public CourierSettingsSection(CourierSession courier, AgentBridgeUiReviewRegistry reviewRegistry)
    {
        this.courier = courier;
        this.reviewRegistry = reviewRegistry;
    }

    public void Draw(QuartermasterRuntimeSnapshot runtime)
    {
        var owner = runtime.Owner;
        if (!owner.HasStableIdentity)
            return;
        var settings = runtime.State.Courier;
        var carrying = settings.CarrierOptIn && settings.Carrier is { } self && self.Matches(owner);

        ImGui.Separator();
        ImGui.TextDisabled("Courier");

        var optIn = carrying;
        if (ImGui.Checkbox("Carry for another character", ref optIn))
            SetCarrying(owner, optIn);
        reviewRegistry.RegisterLastItem("quartermaster.courier.carrier-opt-in", "Carry for another character",
            AgentBridgeUiControlKind.Toggle, true, carrying, carrying ? "on" : "off", () => SetCarrying(owner, !carrying));

        if (carrying)
        {
            DrawCharacterCombo("Owners", "##RQCourierOwner", "quartermaster.courier.paired-owner", null,
                chosen => courier.UpdateSettings(current => current with { PairedOwner=null,PairedOwners=current.IsPaired(chosen)?
                    [..current.Owners.Where(o=>!CourierPlanner.Exact(o,chosen))]:[..current.Owners,chosen] }),settings);
            return;
        }

        DrawCharacterCombo("Carrier", "##RQCourierCarrier", "quartermaster.courier.carrier", settings.Carrier,
            chosen => courier.UpdateSettings(current => current with { Carrier = chosen with { }, CarrierOptIn = false }));
        if (settings.Carrier is not null)
        {
            var returnOwner = settings.ReturnOwner;
            if (ImGui.Checkbox("Return to the start character", ref returnOwner))
                courier.UpdateSettings(current => current with { ReturnOwner = returnOwner });
            reviewRegistry.RegisterLastItem("quartermaster.courier.return-owner", "Return to the start character",
                AgentBridgeUiControlKind.Toggle, true, settings.ReturnOwner, settings.ReturnOwner ? "on" : "off",
                () => courier.UpdateSettings(current => current with { ReturnOwner = !current.ReturnOwner }));
            var returnCarrier = settings.ReturnCarrier;
            if (ImGui.Checkbox("Carrier returns home", ref returnCarrier))
                courier.UpdateSettings(current => current with { ReturnCarrier = returnCarrier });
            reviewRegistry.RegisterLastItem("quartermaster.courier.return-carrier", "Carrier returns home",
                AgentBridgeUiControlKind.Toggle, true, settings.ReturnCarrier, settings.ReturnCarrier ? "on" : "off",
                () => courier.UpdateSettings(current => current with { ReturnCarrier = !current.ReturnCarrier }));
            var autoResume = settings.AutoResume;
            if (ImGui.Checkbox("Retry halted courier runs automatically", ref autoResume))
                courier.UpdateSettings(current => current with { AutoResume = autoResume });
            reviewRegistry.RegisterLastItem("quartermaster.courier.auto-resume", "Retry halted courier runs automatically",
                AgentBridgeUiControlKind.Toggle, true, settings.AutoResume, settings.AutoResume ? "on" : "off",
                () => courier.UpdateSettings(current => current with { AutoResume = !current.AutoResume }));
        }
        DrawBells(runtime, settings);
    }

    private void SetCarrying(OwnerScope owner, bool on) =>
        courier.UpdateSettings(current => on
            ? current with { Carrier = owner with { }, CarrierOptIn = true }
            : current with { Carrier = null, CarrierOptIn = false, PairedOwner = null,PairedOwners=[] });

    /// <summary>
    /// A picker over the characters on the other linked clients; stored ones stay listed while offline.
    /// With <paramref name="pairing"/> it is a multi-select of the paired owners and stays open while toggling.
    /// </summary>
    private void DrawCharacterCombo(string label, string id, string controlId, OwnerScope? current, Action<OwnerScope> choose,CourierSettings? pairing=null)
    {
        var candidates = (pairing==null?courier.Link.Peers.Select(peer=>peer.Character):
            courier.Link.Peers.SelectMany(peer=>peer.Roster??[peer.Character]).Concat(pairing.Owners)).DistinctBy(o=>(o.LocalContentId,o.HomeWorldId))
            .Where(character => character.HasStableIdentity)
            .OrderBy(character => character.CharacterName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (current is { HasStableIdentity: true } stored && !candidates.Any(candidate => CourierPlanner.Exact(candidate, stored)))
            candidates.Insert(0, stored);
        if (candidates.Count == 0)
            return;
        var preview = pairing is null
            ? current is null ? "—" : CourierSession.CharacterLabel(current)
            : pairing.Owners.Any() ? string.Join(", ", pairing.Owners.Select(CourierSession.CharacterLabel)) : "—";
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(pairing is null ? 200 : 260);
        var open = ImGui.BeginCombo(id, preview);
        reviewRegistry.RegisterLastItem(controlId, label, AgentBridgeUiControlKind.Select, true, false,
            preview == "—" ? null : preview, () => { });
        if (!open)
        {
            // A closed picker still offers its candidates to the agent bridge, on the picker's own rect.
            foreach (var closed in candidates)
            {
                var pick = closed;
                var picked = pairing?.IsPaired(closed) ?? CourierPlanner.Exact(closed, current);
                reviewRegistry.RegisterLastSelectable($"{controlId}.{closed.LocalContentId}", $"{label}: {closed.CharacterName}", true, picked,
                    () => { if (!picked || pairing != null) choose(pick); },
                    courier.Link.Peers.Any(peer => CourierPlanner.Exact(peer.Character, closed)) ? "online" : "offline");
            }
            return;
        }
        foreach (var candidate in candidates)
        {
            var online = courier.Link.Peers.Any(peer => CourierPlanner.Exact(peer.Character, candidate));
            var selected = pairing?.IsPaired(candidate) ?? CourierPlanner.Exact(candidate,current);
            var text = $"{CourierSession.CharacterLabel(candidate)} · {candidate.HomeWorldName}##{candidate.LocalContentId}";
            if (!online)
                ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled));
            if (ImGui.Selectable(text, selected, pairing is null ? ImGuiSelectableFlags.None : ImGuiSelectableFlags.DontClosePopups) &&
                (!selected || pairing!=null))
                choose(candidate);
            if (!online)
                ImGui.PopStyleColor();
            var chosen = candidate;
            reviewRegistry.RegisterLastSelectable($"{controlId}.{candidate.LocalContentId}",
                $"{label}: {candidate.CharacterName}", true, selected, () => choose(chosen), online ? "online" : "offline");
        }
        ImGui.EndCombo();
    }

    /// <summary>One meeting bell per character, captured where that character stands.</summary>
    private void DrawBells(QuartermasterRuntimeSnapshot runtime, CourierSettings settings)
    {
        var characters = courier.Characters(runtime);
        if (characters.Count == 0)
            return;
        var capturable = courier.CapturableBell(runtime);
        if (!ImGui.BeginTable("##RQCourierBells", 3, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoHostExtendX | ImGuiTableFlags.BordersInnerH))
            return;
        ImGui.TableSetupColumn("Character");
        ImGui.TableSetupColumn("Meeting bell");
        ImGui.TableSetupColumn("##capture", ImGuiTableColumnFlags.WidthFixed, 64);
        ImGui.TableHeadersRow();
        foreach (var character in characters)
        {
            var current = character.Matches(runtime.Owner);
            // The carrier's own bell is where it returns after a run; it is set from the carrier's client.
            if (settings.Carrier is { } carrier && CourierPlanner.Exact(carrier, character) && !current)
                continue;
            var id = character.LocalContentId!.Value;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(CourierSession.CharacterLabel(character));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(character.HomeWorldName);
            ImGui.TableNextColumn();
            if (settings.Bells.GetValueOrDefault(id) is { } bell)
                ImGui.TextUnformatted(bell.HasRoute?$"{bell.Label} · {bell.Trail!.Length} points":bell.Label);
            else
                ImGui.TextDisabled(current && courier.Link.BellBlock is { } block ? block : "—");
            ImGui.TableNextColumn();
            if (current && capturable is { } here)
            {
                if (ImGui.SmallButton("Use here##RQCourierCapture"))
                    courier.CaptureBell(runtime);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(here.Label);
                reviewRegistry.RegisterLastButton("quartermaster.courier.capture-bell", $"Use {here.Label} as the meeting bell", true,
                    () => courier.CaptureBell(runtime), here.Label);
            }
        }
        ImGui.EndTable();
        var link=courier.Link;
        if(link.RoutePoints>0)
        {
            ImGui.TextDisabled($"Recording · {link.RoutePoints} points");ImGui.SameLine();
            if(ImGui.SmallButton("Cancel route"))link.CancelRoute();
            reviewRegistry.RegisterLastButton("quartermaster.courier.cancel-route","Cancel route",true,link.CancelRoute);
        }
        else if(link.InWard)
        {
            if(ImGui.SmallButton("Record route"))link.StartRoute();
            reviewRegistry.RegisterLastButton("quartermaster.courier.record-route","Record route",true,link.StartRoute);
        }
        if(link.RouteError is {} reason){ImGui.SameLine();ImGui.TextDisabled(reason);}
    }
}
