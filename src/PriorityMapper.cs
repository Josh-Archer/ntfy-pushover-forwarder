namespace NtfyPushoverForwarder;

/// <summary>
/// Maps ntfy priorities (1-5) to Pushover priorities (-2..2).
/// Null/missing ntfy priority is treated as the ntfy default (3 / "default").
/// </summary>
public static class PriorityMapper
{
    /// <summary>ntfy default when priority is omitted.</summary>
    public const int DefaultNtfyPriority = 3;

    public static int ResolveNtfyPriority(int? ntfyPriority)
        => ntfyPriority ?? DefaultNtfyPriority;

    public static bool MeetsMinimum(int? ntfyPriority, int minimumPriority)
        => ResolveNtfyPriority(ntfyPriority) >= minimumPriority;

    /// <summary>
    /// ntfy 1→-2, 2→-1, 3→0, 4→1, 5+→2.
    /// Values outside 1-5 clamp to the nearest edge after resolve.
    /// </summary>
    public static int ToPushover(int? ntfyPriority)
    {
        var p = ResolveNtfyPriority(ntfyPriority);
        return p switch
        {
            <= 1 => -2,
            2 => -1,
            3 => 0,
            4 => 1,
            _ => 2
        };
    }
}
