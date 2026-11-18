using System.Diagnostics;
using Dalamud.Plugin.Services;

namespace RQ.Runtime;

// One instance per callback. All marks are on the framework thread; names are literals.
internal sealed class FrameTimings(IPluginLog log, string callback, int thresholdMs)
{
    private readonly (string Name, long Ticks)[] steps = new (string, long)[32];
    private string section = "";
    private int count, gen0, gen1, gen2, suppressed;
    private long started, previous, nextWarning;

    internal Scope Start(string first)
    {
        gen0 = GC.CollectionCount(0);
        gen1 = GC.CollectionCount(1);
        gen2 = GC.CollectionCount(2);
        count = 0;
        section = first;
        started = previous = Stopwatch.GetTimestamp();
        return new Scope(this);
    }

    internal void Section(string next)
    {
        var now = Stopwatch.GetTimestamp();
        // Bound the diagnostics even if another window is added to this callback.
        if (count < steps.Length)
            steps[count++] = (section, now - previous);
        previous = now;
        section = next;
    }

    private void Finish()
    {
        Section("");
        var elapsed = Stopwatch.GetElapsedTime(started, previous).TotalMilliseconds;
        if (elapsed <= thresholdMs) return;
        if (previous < nextWarning)
        {
            suppressed++;
            return;
        }

        var delta0 = GC.CollectionCount(0) - gen0;
        var delta1 = GC.CollectionCount(1) - gen1;
        var delta2 = GC.CollectionCount(2) - gen2;
        nextWarning = previous + 5 * Stopwatch.Frequency;
        var detail = string.Join(", ", steps.Take(count)
            .Where(step => step.Ticks >= (Stopwatch.Frequency + 999) / 1000)
            .OrderByDescending(step => step.Ticks)
            .Select(step => $"{step.Name} {step.Ticks * 1000d / Stopwatch.Frequency:F1}ms"));
        log.Warning($"Slow RQ {callback} {elapsed:F1}ms: {detail}; GC(0/1/2)={delta0}/{delta1}/{delta2}; suppressed={suppressed}");
        suppressed = 0;
    }

    internal readonly struct Scope(FrameTimings timings) : IDisposable
    {
        public void Dispose() => timings.Finish();
    }
}
