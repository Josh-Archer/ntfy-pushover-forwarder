namespace NtfyPushoverForwarder;

public interface IDedupeStore
{
    /// <summary>
    /// Returns true if the fingerprint is a duplicate within the window.
    /// Records the fingerprint when it is new.
    /// </summary>
    bool TryRecord(string fingerprint, DateTimeOffset now, TimeSpan window, int maxEntries);
}
