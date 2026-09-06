namespace CantoTranscribe;

// A lease lasts until native destruction finishes, including asynchronous cancellation.
internal static class ModelUseGate
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, int> Users = new(StringComparer.OrdinalIgnoreCase);
    public static IDisposable Acquire(string revisionPath, bool mutation = false)
    {
        var key = Path.GetFullPath(Path.GetDirectoryName(revisionPath.TrimEnd(Path.DirectorySeparatorChar))!);
        lock (Sync)
        {
            Users.TryGetValue(key, out var count);
            if (count < 0 || (mutation && count != 0))
                throw new InvalidOperationException("模型正在使用或更新中，請等工作完全停止後再試。");
            Users[key] = mutation ? -1 : count + 1;
        }
        return new Lease(key);
    }
    private sealed class Lease(string key) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            lock (Sync)
            {
                if (_disposed) return;
                _disposed = true;
                if (Users[key] <= 1) Users.Remove(key); else Users[key]--;
            }
        }
    }
}
