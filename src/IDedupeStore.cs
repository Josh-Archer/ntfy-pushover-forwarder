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

    /// <summary>
    /// Atomically checks whether a fingerprint was seen and reserves a new fingerprint
    /// while its delivery is in progress.
    /// </summary>
    DedupeAttemptResult TryBegin(string fingerprint, DateTimeOffset now, TimeSpan window);

    /// <summary>Completes a reservation, recording only successful deliveries.</summary>
    void Complete(string fingerprint, bool succeeded, DateTimeOffset now, TimeSpan window, int maxEntries);
}

public enum DedupeAttemptResult
{
    Acquired,
    Duplicate,
    InProgress
}
