using RQ.Domain;
using RQ.Persistence;
using System.Text.Json;

namespace RQ.Operations;

/// <summary>Read-only summaries for the UI, never an execution or recovery authority.</summary>
public sealed class MarketRunHistoryStore(string directory)
{
    private readonly object gate = new();
    private readonly object writeGate = new();
    private readonly Dictionary<Guid, MarketRunRecord> records = [];
    private long revision;
    private int loading;
    private bool loadRequested;
    private string? warning;
    public long Revision => Interlocked.Read(ref revision);
    public bool IsLoading => Volatile.Read(ref loading) != 0;
    public string? Warning { get { lock (gate) return warning; } }

    public void Save(MarketRunRecord record)
    {
        lock (writeGate)
        {
            try
            {
                // Detached immutable-by-convention snapshots; the executor keeps mutating its own record.
                var snapshot = JsonSerializer.Deserialize<MarketRunRecord>(
                    JsonSerializer.Serialize(record, AtomicDocumentStore<MarketRunRecord>.JsonOptions),
                    AtomicDocumentStore<MarketRunRecord>.JsonOptions)!;
                lock (gate) records[record.Id] = snapshot;
                Directory.CreateDirectory(directory);
                new AtomicDocumentStore<MarketRunRecord>(Path.Combine(directory, $"{record.Id:N}.json")).Save(snapshot);
                lock (gate) warning = null;
            }
            catch (Exception exception) { ReportWarning($"Run history could not be saved: {exception.Message}"); }
            Interlocked.Increment(ref revision);
        }
    }

    public void ReportWarning(string message)
    {
        lock (gate) { warning = message; Interlocked.Increment(ref revision); }
    }

    public IReadOnlyList<MarketRunRecord> Read(OwnerScope owner)
    {
        lock (gate)
        {
            if (!owner.HasStableIdentity) return [];
            if (!loadRequested)
            {
                loadRequested = true;
                Volatile.Write(ref loading, 1);
                _ = Task.Run(LoadArchive);
            }
            // No filesystem work or deserialization on the framework thread.
            return records.Values.Where(r => r.Owner.Matches(owner))
                .OrderByDescending(r => r.StartedAtUtc).ThenBy(r => r.Id).ToArray();
        }
    }

    private void LoadArchive()
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                try
                {
                    var record = new AtomicDocumentStore<MarketRunRecord>(path).Load();
                    lock (gate)
                    {
                        // A concurrent save is newer than this archive read.
                        if (records.TryAdd(record.Id, record)) Interlocked.Increment(ref revision);
                    }
                }
                catch (Exception exception) { ReportWarning($"A run history file could not be read: {exception.Message}"); }
            }
        }
        catch (Exception exception) { ReportWarning($"Run history could not be read: {exception.Message}"); }
        finally { Volatile.Write(ref loading, 0); Interlocked.Increment(ref revision); }
    }
}
