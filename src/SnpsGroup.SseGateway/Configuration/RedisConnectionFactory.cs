using Microsoft.Extensions.Options;
using StackExchange.Redis;
using SnpsGroup.SseGateway.Options;

namespace SnpsGroup.SseGateway.Configuration;

/// <summary>
/// Manages a shared Redis connection for the gateway.
/// Ensures a single ConnectionMultiplexer per gateway instance.
/// Connection is established lazily so the container can start and serve
/// liveness checks (e.g. /health) even when Redis is temporarily unreachable.
/// </summary>
public sealed class RedisConnectionFactory : IDisposable
{
    private readonly Lazy<ConnectionMultiplexer> _connection;
    private readonly Lazy<IDatabase> _database;

    public IDatabase Database => _database.Value;

    public RedisConnectionFactory(IOptions<SseGatewayOptions> options)
    {
        var connectionString = options.Value.RedisConnectionString;
        _connection = new Lazy<ConnectionMultiplexer>(() => ConnectionMultiplexer.Connect(connectionString));
        _database = new Lazy<IDatabase>(() => _connection.Value.GetDatabase());
    }

    public void Dispose()
    {
        if (_connection.IsValueCreated)
        {
            _connection.Value.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
