using RQ.Persistence;

namespace RQ.Courier;

// Stop and snapshot readers can run concurrently with the state machine. A Windows reader
// denying delete-sharing would otherwise sporadically prevent the writer's atomic replace.
internal sealed class CourierStore<T>(string path) where T : new()
{
    private readonly AtomicDocumentStore<T> document = new(path);
    private readonly object gate = new();
    internal T Load() { lock (gate) return document.Load(); }
    internal void Save(T value) { lock (gate) document.Save(value); }
}
