namespace NtfyPushoverForwarder;

/// <summary>
/// Tracks per-topic SSE connection health for recovery notifications.
/// </summary>
public sealed class TopicConnectionTracker
{
    private readonly Dictionary<string, TopicState> _states = new();
    private readonly object _lock = new();

    public sealed class TopicState
    {
        public bool Connected { get; set; }
        public bool EverConnected { get; set; }
        public bool OutageAlerted { get; set; }
        public DateTimeOffset? DisconnectedAt { get; set; }
        public string? LastMessageId { get; set; }
        public long? LastMessageTime { get; set; }
    }

    public TopicState GetOrCreate(string topic)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(topic, out var state))
            {
                state = new TopicState();
                _states[topic] = state;
            }

            return state;
        }
    }

    public void MarkConnected(string topic)
    {
        lock (_lock)
        {
            var state = GetOrCreateUnsafe(topic);
            state.Connected = true;
            state.EverConnected = true;
            state.DisconnectedAt = null;
        }
    }

    public void MarkDisconnected(string topic)
    {
        lock (_lock)
        {
            var state = GetOrCreateUnsafe(topic);
            state.Connected = false;
            state.DisconnectedAt ??= DateTimeOffset.UtcNow;
        }
    }

    public void NoteMessage(string topic, string? messageId, long messageTime)
    {
        lock (_lock)
        {
            var state = GetOrCreateUnsafe(topic);
            if (!string.IsNullOrWhiteSpace(messageId))
            {
                state.LastMessageId = messageId;
            }

            if (messageTime > 0)
            {
                state.LastMessageTime = messageTime;
            }
        }
    }

    public string? GetSinceCursor(string topic)
    {
        lock (_lock)
        {
            var state = GetOrCreateUnsafe(topic);
            return state.LastMessageId;
        }
    }

    public bool ShouldSendRecovery(string topic)
    {
        lock (_lock)
        {
            var state = GetOrCreateUnsafe(topic);
            return state.EverConnected && state.OutageAlerted;
        }
    }

    public void MarkRecoverySent(string topic)
    {
        lock (_lock)
        {
            var state = GetOrCreateUnsafe(topic);
            state.OutageAlerted = false;
        }
    }

    public void MarkOutageAlerted(string topic)
    {
        lock (_lock)
        {
            var state = GetOrCreateUnsafe(topic);
            state.OutageAlerted = true;
        }
    }

    private TopicState GetOrCreateUnsafe(string topic)
    {
        if (!_states.TryGetValue(topic, out var state))
        {
            state = new TopicState();
            _states[topic] = state;
        }

        return state;
    }
}
