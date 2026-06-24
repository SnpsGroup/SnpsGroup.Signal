using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using SnpsGroup.SseGateway.Options;

namespace SnpsGroup.SseGateway.Services;

/// <summary>
/// Channel-aware SSE session manager. Organizes connections by subscription channel
/// and dispatches events only to subscribers of each channel.
/// Stores raw JSON payloads for direct SSE wire compatibility.
/// Thread-safe singleton.
/// </summary>
public class SseSessionManager : ISseSessionManager
{
    private readonly ConcurrentDictionary<string, Channel<string>> _sessions = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _channelSubscriptions = new();
    private readonly SseGatewayOptions _options;
    private readonly ILogger<SseSessionManager> _logger;

    public SseSessionManager(
        IOptions<SseGatewayOptions> options,
        ILogger<SseSessionManager> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public int ActiveConnectionCount => _sessions.Count;

    public int ActiveChannelCount => _channelSubscriptions.Count;

    public IAsyncEnumerable<string> AddSession(
        string channel,
        string connectionId,
        CancellationToken cancellationToken)
    {
        if (_sessions.Count >= _options.MaxConnections)
        {
            throw new InvalidOperationException(
                $"Maximum SSE connection limit reached ({_options.MaxConnections}).");
        }

        var channelWriter = Channel.CreateBounded<string>(
            new BoundedChannelOptions(_options.PerConnectionBufferCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });

        if (!_sessions.TryAdd(connectionId, channelWriter))
        {
            throw new InvalidOperationException(
                $"Connection {connectionId} already exists.");
        }

        _channelSubscriptions.AddOrUpdate(
            channel,
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { connectionId },
            (_, existing) => { lock (existing) { existing.Add(connectionId); } return existing; });

        _logger.LogInformation(
            "SSE session added: {ConnectionId} on channel '{Channel}'. " +
            "Total sessions: {Count}/{Max}, Active channels: {ChannelCount}",
            connectionId, channel, _sessions.Count, _options.MaxConnections,
            _channelSubscriptions.Count);

        return StreamWithCleanup(channel, connectionId, channelWriter.Reader, cancellationToken);
    }

    public int DispatchToChannel(string channel, string jsonPayload)
    {
        if (!_channelSubscriptions.TryGetValue(channel, out var subscribers))
            return 0;

        int delivered = 0;
        lock (subscribers)
        {
            foreach (var connectionId in subscribers)
            {
                if (_sessions.TryGetValue(connectionId, out var channelWriter))
                {
                    if (channelWriter.Writer.TryWrite(jsonPayload))
                    {
                        delivered++;
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Failed to write to SSE session {ConnectionId} on channel '{Channel}' " +
                            "(buffer full or closed)", connectionId, channel);
                    }
                }
            }
        }
        return delivered;
    }

    public int BroadcastToAll(string jsonPayload)
    {
        int delivered = 0;
        foreach (var (_, channelWriter) in _sessions)
        {
            if (channelWriter.Writer.TryWrite(jsonPayload))
            {
                delivered++;
            }
        }
        return delivered;
    }

    public IReadOnlySet<string> GetSubscribedChannels()
        => _channelSubscriptions.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private async IAsyncEnumerable<string> StreamWithCleanup(
        string channel,
        string connectionId,
        ChannelReader<string> reader,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in reader.ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            RemoveSessionCore(channel, connectionId);
        }
    }

    private void RemoveSessionCore(string channel, string connectionId)
    {
        _sessions.TryRemove(connectionId, out var ch);
        ch?.Writer.TryComplete();

        if (_channelSubscriptions.TryGetValue(channel, out var subscribers))
        {
            lock (subscribers)
            {
                subscribers.Remove(connectionId);
                if (subscribers.Count == 0)
                {
                    _channelSubscriptions.TryRemove(channel, out _);
                }
            }
        }

        _logger.LogInformation(
            "SSE session removed: {ConnectionId} from channel '{Channel}'. " +
            "Total sessions: {Count}/{Max}",
            connectionId, channel, _sessions.Count, _options.MaxConnections);
    }
}
