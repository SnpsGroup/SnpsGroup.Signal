using System.Text.Json;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using SnpsGroup.SseGateway.Client.Options;

namespace SnpsGroup.SseGateway.Client;

/// <summary>
/// Publishes events to the SSE Gateway Redis Stream.
/// Used by producer applications (e.g., DFE API) to push real-time events.
/// </summary>
public interface ISseEventPublisher
{
    /// <summary>
    /// Publishes an event to the SSE gateway stream.
    /// The payload is serialized to JSON — the gateway treats it as opaque data.
    /// </summary>
    Task PublishAsync(string channel, string eventType, object payload, CancellationToken ct = default);
}

public class SseEventPublisher : ISseEventPublisher, IDisposable
{
    private readonly ConnectionMultiplexer _redis;
    private readonly IDatabase _db;
    private readonly SsePublisherOptions _options;

    public SseEventPublisher(IOptions<SsePublisherOptions> options)
    {
        _options = options.Value;
        _redis = ConnectionMultiplexer.Connect(_options.RedisConnectionString);
        _db = _redis.GetDatabase();
    }

    public async Task PublishAsync(
        string channel,
        string eventType,
        object payload,
        CancellationToken ct = default)
    {
        var fields = new NameValueEntry[]
        {
            new("id", Guid.NewGuid().ToString("N")),
            new("channel", channel),
            new("eventType", eventType),
            new("data", JsonSerializer.Serialize(payload)),
            new("timestamp", DateTime.UtcNow.ToString("O"))
        };

        await _db.StreamAddAsync(
            _options.StreamKey,
            fields,
            maxLength: _options.MaxStreamLength,
            useApproximateMaxLength: true).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _redis.Dispose();
        GC.SuppressFinalize(this);
    }
}
