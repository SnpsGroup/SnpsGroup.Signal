namespace SnpsGroup.SseGateway.Client.Options;

/// <summary>
/// Configuration for the SSE event publisher (producer side).
/// </summary>
public class SsePublisherOptions
{
    public const string SectionName = "SsePublisher";

    /// <summary>
    /// Redis connection string.
    /// </summary>
    public string RedisConnectionString { get; set; } = "localhost:6379";

    /// <summary>
    /// Redis stream key to publish events to.
    /// </summary>
    public string StreamKey { get; set; } = "sse:events";

    /// <summary>
    /// Approximate maximum stream length (uses MAXLEN ~ for efficient trimming).
    /// </summary>
    public long MaxStreamLength { get; set; } = 10000;
}
