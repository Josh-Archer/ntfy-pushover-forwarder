namespace NtfyPushoverForwarder;

public sealed class InMemoryDedupeStore : IDedupeStore
{
    private readonly Dictionary<string, DateTimeOffset> _entries = new();
    private readonly object _lock = new();

    public int Count
    {
        get { lock (_lock) return _entries.Count; }
    }

    public bool Contains(string fingerprint, DateTimeOffset now, TimeSpan window)
    {
        if (window <= TimeSpan.Zero)
        {
            return false;
        }

        var cutoff = now - window;
        lock (_lock)
        {
            return _entries.TryGetValue(fingerprint, out var seen) && seen >= cutoff;
        }
    }

    public bool TryRecord(string fingerprint, DateTimeOffset now, TimeSpan window, int maxEntries)
    {
        if (window <= TimeSpan.Zero)
        {
            return false;
        }

        var cutoff = now - window;
        lock (_lock)
        {
            foreach (var stale in _entries.Where(e => e.Value < cutoff).Select(e => e.Key).ToArray())
            {
                _entries.Remove(stale);
            }

            if (_entries.ContainsKey(fingerprint))
            {
                return true;
            }

            _entries[fingerprint] = now;
            PruneToMax(maxEntries);
            return false;
        }
    }

    /// <summary>Seed entries (e.g. from durable store) without treating as duplicates.</summary>
    public void Seed(IEnumerable<KeyValuePair<string, DateTimeOffset>> entries, DateTimeOffset now, TimeSpan window, int maxEntries)
    {
        var cutoff = now - window;
        lock (_lock)
        {
            foreach (var (key, value) in entries)
            {
                if (value >= cutoff)
                {
                    _entries[key] = value;
                }
            }

            PruneToMax(maxEntries);
        }
    }

    public IReadOnlyDictionary<string, DateTimeOffset> Snapshot()
    {
        lock (_lock)
        {
            return new Dictionary<string, DateTimeOffset>(_entries);
        }
    }

    private void PruneToMax(int maxEntries)
    {
        var max = Math.Max(1, maxEntries);
        if (_entries.Count <= max)
        {
            return;
        }

        foreach (var oldest in _entries.OrderBy(e => e.Value).Take(_entries.Count - max).Select(e => e.Key).ToArray())
        {
            _entries.Remove(oldest);
        }
    }
}
