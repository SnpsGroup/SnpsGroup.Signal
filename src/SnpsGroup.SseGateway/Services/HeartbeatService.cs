using Microsoft.Extensions.Options;
using SnpsGroup.SseGateway.Options;

namespace SnpsGroup.SseGateway.Services;

/// <summary>
/// Sends periodic heartbeat events to all active SSE connections.
/// Prevents load balancers and reverse proxies from closing idle connections.
/// </summary>
public class HeartbeatService : BackgroundService
{
    private readonly ISseSessionManager _sessionManager;
    private readonly SseGatewayOptions _options;
    private readonly ILogger<HeartbeatService> _logger;

    public HeartbeatService(
        ISseSessionManager sessionManager,
        IOptions<SseGatewayOptions> options,
        ILogger<HeartbeatService> logger)
    {
        _sessionManager = sessionManager;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Heartbeat service started. Interval: {Interval}s",
            _options.HeartbeatInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_options.HeartbeatInterval, stoppingToken).ConfigureAwait(false);

            if (_sessionManager.ActiveConnectionCount == 0)
                continue;

            var heartbeatPayload = $"{{\"EventType\":\"heartbeat\",\"Timestamp\":\"{DateTime.UtcNow:O}\"}}";
            var delivered = _sessionManager.BroadcastToAll(heartbeatPayload);
            _logger.LogDebug("Heartbeat sent to {Count} sessions", delivered);
        }
    }
}
