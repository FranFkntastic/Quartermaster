using System.Text.Json;
using RQ.Domain;

namespace RQ.Persistence;

public enum StateChangeKind
{
    Plans,
    Listings,
    Operations,
}

public sealed class StateRepository
{
    private readonly object gate = new();
    private readonly QuartermasterStateStore store;
    private QuartermasterState state;

    public StateRepository(QuartermasterStateStore store)
    {
        this.store = store;
        state = store.Load();
    }

    public event Action<StateChangeKind>? Changed;

    public QuartermasterState Snapshot()
    {
        lock (gate)
            return Clone(state);
    }

    public T Read<T>(Func<QuartermasterState, T> read)
    {
        lock (gate)
            return read(state);
    }

    public T Mutate<T>(Func<QuartermasterState, T> mutate) => Mutate(StateChangeKind.Plans, mutate);

    public T Mutate<T>(StateChangeKind changeKind, Func<QuartermasterState, T> mutate)
    {
        T result;
        lock (gate)
        {
            var candidate = Clone(state);
            result = mutate(candidate);
            candidate.Revision = checked(state.Revision + 1);
            store.Save(candidate);
            state = candidate;
        }
        Changed?.Invoke(changeKind);
        return result;
    }

    public void Mutate(Action<QuartermasterState> mutate) => Mutate(state =>
    {
        mutate(state);
        return true;
    });

    public void Mutate(StateChangeKind changeKind, Action<QuartermasterState> mutate) => Mutate(changeKind, state =>
    {
        mutate(state);
        return true;
    });

    private static QuartermasterState Clone(QuartermasterState value) =>
        JsonSerializer.Deserialize<QuartermasterState>(
            JsonSerializer.Serialize(value, AtomicDocumentStore<QuartermasterState>.JsonOptions),
            AtomicDocumentStore<QuartermasterState>.JsonOptions) ?? new QuartermasterState();
}
