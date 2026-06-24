using StackExchange.Redis;
using Microsoft.Extensions.Options;
using SnpsGroup.SseGateway.Configuration;
using SnpsGroup.SseGateway.Models;
using SnpsGroup.SseGateway.Options;

namespace SnpsGroup.SseGateway.Services;

/// <summary>
/// Reads events from a Redis Stream using XREAD (fan-out pattern).
/// Each gateway instance maintains its own cursor, so every instance
/// sees every message and dispatches to its local SSE sessions only.
/// </summary>
public class RedisStreamConsumer : BackgroundService, IRedisStreamConsumer
{
    private readonly RedisConnectionFactory _redisFactory;
    private readonly ISseSessionManager _sessionManager;
    private readonly SseGatewayOptions _options;
    private readonly ILogger<RedisStreamConsumer> _logger;
    private readonly string _instanceId;

    public RedisStreamConsumer(
        RedisConnectionFactory redisFactory,
        ISseSessionManager sessionManager,
        IOptions<SseGatewayOptions> options,
        ILogger<RedisStreamConsumer> logger)
    {
        _redisFactory = redisFactory;
        _sessionManager = sessionManager;
        _options = options.Value;
        _logger = logger;
        _instanceId = $"{Environment.MachineName}-{Guid.NewGuid():N}";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Redis Stream Consumer started. Instance: {InstanceId}, Stream: {Stream}",
            _instanceId, _options.StreamKey);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Redis Stream Consumer error. Reconnecting in 5s...");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("Redis Stream Consumer stopped. Instance: {InstanceId}", _instanceId);
    }

    public async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        var db = _redisFactory.Database;
        var cursorKey = $"{_options.CursorKeyPrefix}:{_instanceId}";

        // Get last cursor or start from beginning (0-0 = earliest)
        var lastId = await db.StringGetAsync(cursorKey).ConfigureAwait(false);
        var readFrom = lastId.HasValue ? lastId.ToString() : "0-0";

        _logger.LogInformation("Starting consumption from cursor: {Cursor}", readFrom);

        while (!cancellationToken.IsCancellationRequested)
        {
            // Skip reading if no sessions are active
            if (_sessionManager.ActiveConnectionCount == 0)
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // XREAD STREAM key AFTER cursor COUNT batchSize BLOCK timeoutMs
            var entries = await db.StreamReadAsync(
                _options.StreamKey,
                readFrom,
                _options.StreamReadBatchSize).ConfigureAwait(false);

            if (entries is null || entries.Length == 0)
            {
                // No new messages — brief pause to avoid busy loop
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                continue;
            }

            foreach (var entry in entries)
            {
                try
                {
                    var sseEvent = ParseEntry(entry);
                    if (sseEvent is null)
                    {
                        _logger.LogWarning("Skipping malformed stream entry: {EntryId}", entry.Id);
                        readFrom = entry.Id;
                        continue;
                    }

                    var delivered = _sessionManager.DispatchToChannel(sseEvent.Channel, sseEvent.Data);

                    _logger.LogDebug(
                        "Dispatched event {EventId} on channel '{Channel}' to {Count} session(s)",
                        sseEvent.Id, sseEvent.Channel, delivered);

                    readFrom = entry.Id;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing stream entry {EntryId}", entry.Id);
                    readFrom = entry.Id; // Advance cursor even on error to avoid getting stuck
                }
            }

            // Persist cursor after batch
            await db.StringSetAsync(cursorKey, readFrom, TimeSpan.FromHours(24)).ConfigureAwait(false);
        }
    }

    private static SseEvent? ParseEntry(StreamEntry entry)
    {
        var values = entry.Values;

        string? GetValue(string key)
        {
            foreach (var pair in values)
            {
                if (string.Equals(pair.Name, key, StringComparison.OrdinalIgnoreCase))
                    return pair.Value;
            }
            return null;
        }

        var channel = GetValue("channel");
        if (string.IsNullOrEmpty(channel))
            return null;

        return new SseEvent
        {
            Id = GetValue("id") ?? entry.Id.ToString(),
            Channel = channel,
            EventType = GetValue("eventType") ?? "message",
            Data = GetValue("data") ?? "{}",
            Timestamp = DateTime.TryParse(GetValue("timestamp"), out var ts)
                ? ts
                : DateTime.UtcNow
        };
    }
}
