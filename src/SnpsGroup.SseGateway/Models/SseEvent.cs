namespace SnpsGroup.SseGateway.Models;

/// <summary>
/// Represents an event read from the Redis Stream.
/// The gateway does not interpret <see cref="Data"/> — it is an opaque JSON payload.
/// </summary>
public class SseEvent
{
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Routing key that determines which SSE clients receive this event.
    /// Example: "dfe:sender:dashboard"
    /// </summary>
    public string Channel { get; set; } = string.Empty;

    /// <summary>
    /// Application-level event type (e.g., "tenant_update", "full_update").
    /// Forwarded to the SSE client as the event type field.
    /// </summary>
    public string EventType { get; set; } = "message";

    /// <summary>
    /// Opaque JSON payload. The gateway never inspects or modifies this.
    /// </summary>
    public string Data { get; set; } = "{}";

    /// <summary>
    /// When the event was created (ISO 8601).
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
