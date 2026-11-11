using Dalamud.Bindings.ImGui;
using RQ.Domain;
using RQ.Runtime;

namespace RQ.UI;

internal static class RoutingPolicyEditor
{
    internal static void DrawDestination(StowageRoutingPolicy routing, QuartermasterRuntimeSnapshot runtime, Action? drawExtra = null)
    {
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo(
                "##draftdestination",
                TransferPresentation.RouteSummary(routing, runtime.Retainers, runtime.Owner)))
            return;

        ImGui.TextDisabled("Placement");
        foreach (var mode in Enum.GetValues<StowageRoutingMode>())
            if (ImGui.Selectable(TransferPresentation.RoutingModeLabel(mode), routing.Mode == mode))
                routing.Mode = mode;

        ImGui.Separator();
        ImGui.TextDisabled("Preferred retainers - in order");
        var ownerRetainers = runtime.Retainers.Values
            .Where(retainer => retainer.Owner.Matches(runtime.Owner))
            .OrderBy(retainer => retainer.RetainerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(retainer => retainer.RetainerId)
            .ToArray();
        for (var index = 0; index < routing.PreferredRetainerIds.Count; index++)
        {
            var retainerId = routing.PreferredRetainerIds[index];
            var retainer = ownerRetainers.FirstOrDefault(candidate => candidate.RetainerId == retainerId);
            ImGui.PushID($"inline-route:{retainerId}:{index}");
            ImGui.TextUnformatted(retainer?.RetainerName ?? $"Retainer {retainerId}");
            ImGui.SameLine();
            if (index > 0 && ImGui.SmallButton("Up"))
                (routing.PreferredRetainerIds[index - 1], routing.PreferredRetainerIds[index]) =
                    (routing.PreferredRetainerIds[index], routing.PreferredRetainerIds[index - 1]);
            ImGui.SameLine();
            if (index + 1 < routing.PreferredRetainerIds.Count && ImGui.SmallButton("Down"))
                (routing.PreferredRetainerIds[index], routing.PreferredRetainerIds[index + 1]) =
                    (routing.PreferredRetainerIds[index + 1], routing.PreferredRetainerIds[index]);
            ImGui.SameLine();
            if (ImGui.SmallButton("Remove"))
            {
                routing.PreferredRetainerIds.RemoveAt(index);
                ImGui.PopID();
                break;
            }
            ImGui.PopID();
        }
        var available = ownerRetainers
            .Where(retainer => !routing.PreferredRetainerIds.Contains(retainer.RetainerId))
            .ToArray();
        ImGui.SetNextItemWidth(240);
        if (ImGui.BeginCombo("##addinlinepreferred", "Add preferred retainer"))
        {
            foreach (var retainer in available)
                if (ImGui.Selectable($"{retainer.RetainerName}##addinline{retainer.RetainerId}"))
                    routing.PreferredRetainerIds.Add(retainer.RetainerId);
            ImGui.EndCombo();
        }

        drawExtra?.Invoke();
        ImGui.EndCombo();
    }
    internal static void DrawOverflow(StowageRoutingPolicy routing)
    {
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo("##draftoverflow", TransferPresentation.OverflowLabel(routing.Overflow)))
            return;
        foreach (var overflow in Enum.GetValues<StowageOverflowPolicy>())
            if (ImGui.Selectable(TransferPresentation.OverflowLabel(overflow), routing.Overflow == overflow))
                routing.Overflow = overflow;
        ImGui.EndCombo();
    }

}
