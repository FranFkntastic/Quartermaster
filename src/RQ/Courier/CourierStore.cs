using RQ.Persistence;

namespace RQ.Courier;

// Stop and snapshot readers can run concurrently with the state machine. A Windows reader
// denying delete-sharing (another process, an antivirus scan, a backup agent) would otherwise
// sporadically fail the writer's atomic replace with a sharing violation or access denied.
// Such transient failures retry briefly; each retry and the final failure name the file.
internal sealed class CourierStore<T>(string path, Action<string>? warning = null) where T : new()
{
    internal static readonly int[] RetryDelaysMs = [50, 100, 200, 400, 800];
    private readonly AtomicDocumentStore<T> document = new(path);
    private readonly object gate = new();
    internal T Load() { lock (gate) return Retry("load", document.Load); }
    internal void Save(T value) { lock (gate) Retry("save", () => { document.Save(value); return 0; }); }

    private TResult Retry<TResult>(string verb, Func<TResult> io)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return io(); }
            catch (Exception e) when (Transient(e))
            {
                if (attempt >= RetryDelaysMs.Length)
                {
                    warning?.Invoke($"CourierStoreLocked: {verb} {Path.GetFileName(path)} failed after {attempt + 1} attempts: {e.GetType().Name}: {e.Message}");
                    throw;
                }
                warning?.Invoke($"CourierStoreRetry: {verb} {Path.GetFileName(path)} attempt {attempt + 1} hit {e.GetType().Name}: {e.Message}; retrying in {RetryDelaysMs[attempt]}ms.");
                Thread.Sleep(RetryDelaysMs[attempt]);
            }
        }
    }

    // 0x20 ERROR_SHARING_VIOLATION, 0x21 ERROR_LOCK_VIOLATION. File.Move over a target held open
    // without delete-sharing surfaces as UnauthorizedAccessException.
    internal static bool Transient(Exception e) => e is UnauthorizedAccessException ||
        e is IOException io && (io.HResult & 0xFFFF) is 0x20 or 0x21;
}
