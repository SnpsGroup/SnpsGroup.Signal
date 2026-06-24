namespace SnpsGroup.SseGateway.Models;

/// <summary>
/// The envelope sent to SSE clients over the wire.
/// Maps to the standard SSE format: event type + data payload.
/// </summary>
public class SseClientMessage
{
    /// <summary>
    /// SSE event type (maps to the 'event:' SSE field).
    /// </summary>
    public string EventType { get; set; } = "message";

    /// <summary>
    /// JSON payload (maps to the 'data:' SSE field).
    /// </summary>
    public string Data { get; set; } = "{}";

    /// <summary>
    /// Factory method to create from an <see cref="SseEvent"/>.
    /// </summary>
    public static SseClientMessage FromEvent(SseEvent sseEvent) => new()
    {
        EventType = sseEvent.EventType,
        Data = sseEvent.Data
    };
}
