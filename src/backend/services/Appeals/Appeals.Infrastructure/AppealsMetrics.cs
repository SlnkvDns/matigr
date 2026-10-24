using System.Diagnostics.Metrics;
namespace Matigr.Appeals.Infrastructure;
internal static class AppealsMetrics
{
    private static IReadOnlyDictionary<string, long> current = new Dictionary<string, long>();
    private static readonly Meter Meter = new("Matigr.Appeals", "1.0");
    internal static readonly Counter<long> Transitions = Meter.CreateCounter<long>("matigr.appeals.transitions");
    internal static readonly Counter<long> Conflicts = Meter.CreateCounter<long>("matigr.appeals.conflicts");
    static AppealsMetrics() => Meter.CreateObservableGauge("matigr.appeals.processes", () => Volatile.Read(ref current)
        .Select(x => new Measurement<long>(x.Value, new KeyValuePair<string, object?>("state", x.Key))));
    internal static void Counts(IReadOnlyDictionary<string, long> counts) => Volatile.Write(ref current, counts);
}
