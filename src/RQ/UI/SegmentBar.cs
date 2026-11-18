using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RQ.UI;

/// <summary>
/// The Market tab's cell bar: 4 px cells with 1 px gaps, the same shape as the retainer slot bar.
/// Segments fill cells left to right; a non-empty segment always gets at least one cell.
/// </summary>
internal static class SegmentBar
{
    internal static readonly Vector4 Empty = new(.15f, .15f, .15f, 1);
    internal const float Cell = 4;
    internal const float Gap = 1;
    internal const float Height = 12;

    /// <summary>Draws the bar vertically centred on a frame-height line and advances the cursor.</summary>
    internal static void Draw(float width, long total, params (long Units, Vector4 Color)[] segments)
    {
        var frame = ImGui.GetFrameHeight();
        var pos = ImGui.GetCursorScreenPos();
        var y = pos.Y + MathF.Floor((frame - Height) / 2);
        var cells = Math.Max(1, (int)((width + Gap) / (Cell + Gap)));
        var colors = new uint[cells];
        Array.Fill(colors, ImGui.GetColorU32(Empty));
        if (total > 0)
        {
            var next = 0;
            foreach (var (units, color) in segments)
            {
                if (units <= 0 || next >= cells) continue;
                var count = Math.Clamp((int)Math.Round((double)units * cells / total), 1, cells - next);
                Array.Fill(colors, ImGui.GetColorU32(color), next, count);
                next += count;
            }
        }
        var draw = ImGui.GetWindowDrawList();
        for (var i = 0; i < cells; i++)
        {
            var x = pos.X + (i * (Cell + Gap));
            draw.AddRectFilled(new Vector2(x, y), new Vector2(x + Cell, y + Height), colors[i]);
        }
        ImGui.Dummy(new Vector2((cells * (Cell + Gap)) - Gap, frame));
    }

    internal static float Width(float width) => (Math.Max(1, (int)((width + Gap) / (Cell + Gap))) * (Cell + Gap)) - Gap;
}
