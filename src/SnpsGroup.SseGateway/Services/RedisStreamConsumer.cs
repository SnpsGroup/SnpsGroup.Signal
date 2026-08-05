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
        // Stable per replica: the deploy sets --hostname per host/environment, so the
        // cursor survives restarts and each svcfabric replica keeps its own cursor key.
        _instanceId = Environment.MachineName;
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

        // Start from the beginning of the stream ("0-0"). A fan-out consumer must not skip
        // entries: an event published before (or while) the consumer was starting up must
        // still be delivered. Starting at the live tail ("$") is wrong because "$" is the
        // last ID at call time — entries published between two XREAD calls fall at or behind
        // it and are never returned. The cursor advances forward in memory (readFrom) after
        // each batch and is discarded on exit; DispatchToChannel only hands an entry to
        // currently-subscribed sessions, so replaying the backlog on startup delivers only
        // to clients that are actually connected and have not seen it yet.
        var readFrom = "0-0";

        _logger.LogInformation("Starting consumption from beginning: {Cursor}", readFrom);

        while (!cancellationToken.IsCancellationRequested)
        {
            // Skip reading if no sessions are active
            if (_sessionManager.ActiveConnectionCount == 0)
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // XREAD BLOCK <timeoutMs> COUNT <batch> STREAMS <key> <id>
            // Blocking server-side read: the call parks on Redis until an entry arrives or
            // the timeout elapses, instead of polling and racing the publisher. This is what
            // makes fan-out reliable — the consumer is already waiting when the event lands.
            var result = await db.ExecuteAsync(
                "XREAD",
                new object[]
                {
                    "BLOCK", _options.StreamReadTimeoutMs,
                    "COUNT", _options.StreamReadBatchSize,
                    "STREAMS", _options.StreamKey, readFrom!
                }).ConfigureAwait(false);

            var entries = ParseXReadResult(result);
            if (entries is null || entries.Length == 0)
            {
                // Timed out with no new entries — loop and block again.
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

            // Cursor is kept in memory (readFrom) for the lifetime of this process and is
            // NOT persisted to Redis. A persisted cursor is what caused events to be skipped
            // after a restart: the retained cursor could sit ahead of freshly-published
            // entries. Each run starts at the live tail ("$") and advances only forward.
        }
    }

    /// <summary>
    /// Parses the raw XREAD reply into stream entries. The reply is a nested array:
    /// [[streamKey, [[entryId, [field, value, ...]], ...]]]. Null/empty means no entries.
    /// </summary>
    private static StreamEntry[] ParseXReadResult(RedisResult result)
    {
        if (result.IsNull)
        {
            return Array.Empty<StreamEntry>();
        }

        var outer = (RedisResult[])result!;
        if (outer.Length == 0)
        {
            return Array.Empty<StreamEntry>();
        }

        // outer[0] = [streamKey, entriesArray]
        var streamPair = (RedisResult[])outer[0]!;
        if (streamPair.Length < 2)
        {
            return Array.Empty<StreamEntry>();
        }

        var entriesArray = (RedisResult[])streamPair[1]!;
        var entries = new StreamEntry[entriesArray.Length];
        for (var i = 0; i < entriesArray.Length; i++)
        {
            entries[i] = ParseStreamEntry((RedisResult[])entriesArray[i]!);
        }
        return entries;
    }

    private static StreamEntry ParseStreamEntry(RedisResult[] entryPair)
    {
        // entryPair = [entryId, [field, value, field, value, ...]]
        var id = (RedisValue)entryPair[0];
        var fields = (RedisResult[])entryPair[1]!;
        var pairs = new NameValueEntry[fields.Length / 2];
        for (var i = 0; i < fields.Length; i += 2)
        {
            pairs[i / 2] = new NameValueEntry((RedisValue)fields[i], (RedisValue)fields[i + 1]);
        }
        return new StreamEntry(id, pairs);
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
