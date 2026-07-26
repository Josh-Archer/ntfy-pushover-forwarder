using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NtfyPushoverForwarder;

/// <summary>
/// File-backed dedupe store. Loads on construction and persists after changes.
/// Safe for single-replica deployments; not multi-writer safe.
/// </summary>
public sealed class FileDedupeStore : IDedupeStore
{
    private readonly string _path;
    private readonly InMemoryDedupeStore _memory = new();
    private readonly ILogger? _logger;
    private readonly object _ioLock = new();

    public FileDedupeStore(string path, TimeSpan window, int maxEntries, ILogger? logger = null)
    {
        _path = path;
        _logger = logger;
        Load(window, maxEntries);
    }

    public bool Contains(string fingerprint, DateTimeOffset now, TimeSpan window)
        => _memory.Contains(fingerprint, now, window);

    public bool TryRecord(string fingerprint, DateTimeOffset now, TimeSpan window, int maxEntries)
    {
        var isDuplicate = _memory.TryRecord(fingerprint, now, window, maxEntries);
        if (!isDuplicate)
        {
            Persist();
        }

        return isDuplicate;
    }

    private void Load(TimeSpan window, int maxEntries)
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var json = File.ReadAllText(_path);
            var data = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(json);
            if (data == null || data.Count == 0)
            {
                return;
            }

            _memory.Seed(data, DateTimeOffset.UtcNow, window, maxEntries);
            _logger?.LogInformation("Loaded {Count} dedupe entries from {Path}", _memory.Count, _path);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load dedupe store from {Path}; starting empty", _path);
        }
    }

    private void Persist()
    {
        lock (_ioLock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var snapshot = _memory.Snapshot();
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot));
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to persist dedupe store to {Path}", _path);
            }
        }
    }
}
