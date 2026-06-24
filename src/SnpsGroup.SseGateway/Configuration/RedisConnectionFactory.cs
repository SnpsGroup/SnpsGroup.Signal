using Microsoft.Extensions.Options;
using StackExchange.Redis;
using SnpsGroup.SseGateway.Options;

namespace SnpsGroup.SseGateway.Configuration;

/// <summary>
/// Manages a shared Redis connection for the gateway.
/// Ensures a single ConnectionMultiplexer per gateway instance.
/// </summary>
public sealed class RedisConnectionFactory : IDisposable
{
    private readonly ConnectionMultiplexer _connection;

    public IDatabase Database { get; }

    public RedisConnectionFactory(IOptions<SseGatewayOptions> options)
    {
        var connectionString = options.Value.RedisConnectionString;
        _connection = ConnectionMultiplexer.Connect(connectionString);
        Database = _connection.GetDatabase();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
