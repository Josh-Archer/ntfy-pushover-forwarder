namespace NtfyPushoverForwarder;

public interface IDedupeStore
{
    /// <summary>
    /// Returns true if the fingerprint was already seen within the window (no write).
    /// </summary>
    bool Contains(string fingerprint, DateTimeOffset now, TimeSpan window);

    /// <summary>
    /// Returns true if the fingerprint is a duplicate within the window.
    /// Records the fingerprint when it is new.
    /// </summary>
    bool TryRecord(string fingerprint, DateTimeOffset now, TimeSpan window, int maxEntries);
}
