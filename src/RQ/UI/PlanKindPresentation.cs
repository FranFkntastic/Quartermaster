using System.Numerics;
using Dalamud.Bindings.ImGui;
using RQ.Domain;

namespace RQ.UI;

/// <summary>How the two plan kinds are named and marked wherever plans are listed.</summary>
internal static class PlanKindPresentation
{
    private static readonly Vector4 TransferColor = new(.56f, .70f, .85f, 1f);

    internal static string Label(StowagePlanKind kind) => kind == StowagePlanKind.Courier ? "Courier" : "Transfer";

    internal static string Plural(StowagePlanKind kind) => kind == StowagePlanKind.Courier ? "Courier plans" : "Transfer plans";

    internal static string Noun(StowagePlanKind kind) => kind == StowagePlanKind.Courier ? "courier" : "transfer";

    /// <summary>Stable agent-bridge id segment.</summary>
    internal static string Key(StowagePlanKind kind) => kind == StowagePlanKind.Courier ? "courier" : "transfer";

    internal static Vector4 Color(StowagePlanKind kind) => kind == StowagePlanKind.Courier ? TransferPresentation.SendColor : TransferColor;

    /// <summary>The kind's short tag in its color, drawn inline.</summary>
    internal static void Badge(StowagePlanKind kind)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Color(kind), Label(kind));
    }
}
