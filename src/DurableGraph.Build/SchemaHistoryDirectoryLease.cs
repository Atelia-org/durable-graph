using System.Security.Cryptography;
using System.Text;

namespace Atelia.DurableGraph.Build;

internal sealed class SchemaHistoryDirectoryLease : IDisposable {
    private Mutex? _mutex;

    private SchemaHistoryDirectoryLease(Mutex mutex) {
        _mutex = mutex;
    }

    internal static SchemaHistoryDirectoryLease Acquire(string historyDirectory, TimeSpan? timeout = null) {
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(historyDirectory));
        string identity = OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
        string name = "Atelia.DurableGraph.SchemaHistory." + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        Mutex mutex = new(false, name, new NamedWaitHandleOptions {
            CurrentUserOnly = true,
            CurrentSessionOnly = false,
        });
        TimeSpan wait = timeout ?? TimeSpan.FromSeconds(30);
        bool acquired = false;
        try {
            try {
                acquired = mutex.WaitOne(wait);
            } catch (AbandonedMutexException) {
                // Ownership is granted; the caller must still validate all history.
                acquired = true;
            }

            if (!acquired) {
                throw new SchemaHistoryException(
                    $"Timed out after {wait.TotalSeconds:g} seconds waiting for Schema-history directory '{fullPath}'. " +
                    "Run builds serially and retry after the other publication finishes.");
            }

            return new SchemaHistoryDirectoryLease(mutex);
        } catch {
            if (acquired) {
                mutex.ReleaseMutex();
            }
            mutex.Dispose();
            throw;
        }
    }

    public void Dispose() {
        Mutex? mutex = _mutex;
        if (mutex is null) {
            return;
        }

        // The synchronous tool operation acquires and releases on the same thread.
        mutex.ReleaseMutex();
        _mutex = null;
        mutex.Dispose();
    }
}
