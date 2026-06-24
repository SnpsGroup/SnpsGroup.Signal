namespace SnpsGroup.SseGateway.Services;

/// <summary>
/// Manages active SSE client sessions, organized by channel subscription.
/// Thread-safe singleton.
/// </summary>
public interface ISseSessionManager
{
    /// <summary>
    /// Number of currently active SSE connections.
    /// </summary>
    int ActiveConnectionCount { get; }

    /// <summary>
    /// Number of channels with at least one subscriber.
    /// </summary>
    int ActiveChannelCount { get; }

    /// <summary>
    /// Registers a new SSE session subscribed to the given channel.
    /// Returns an async enumerable of JSON payloads for use with TypedResults.ServerSentEvents.
    /// </summary>
    IAsyncEnumerable<string> AddSession(
        string channel,
        string connectionId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Sends a JSON payload to all sessions subscribed to the given channel.
    /// Returns the number of sessions that received the event.
    /// </summary>
    int DispatchToChannel(string channel, string jsonPayload);

    /// <summary>
    /// Broadcasts a JSON payload to all active sessions (e.g., heartbeats).
    /// </summary>
    int BroadcastToAll(string jsonPayload);

    /// <summary>
    /// Returns the set of channels that currently have subscribers.
    /// </summary>
    IReadOnlySet<string> GetSubscribedChannels();
}
