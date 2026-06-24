namespace SnpsGroup.SseGateway.Services;

/// <summary>
/// Consumes events from a Redis Stream and dispatches to SSE sessions.
/// Runs as a BackgroundService.
/// </summary>
public interface IRedisStreamConsumer
{
    /// <summary>
    /// Starts consuming from the stream. Blocks until cancelled.
    /// </summary>
    Task ConsumeAsync(CancellationToken cancellationToken);
}
