using SnpsGroup.SseGateway.Configuration;
using SnpsGroup.SseGateway.Options;
using SnpsGroup.SseGateway.Services;

namespace SnpsGroup.SseGateway.Infrastructure;

/// <summary>
/// Registers all SSE Gateway services with the DI container.
/// </summary>
public static class SseGatewayModule
{
    public static IServiceCollection AddSseGatewayServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<SseGatewayOptions>(
            configuration.GetSection(SseGatewayOptions.SectionName));

        services.AddSingleton<RedisConnectionFactory>();
        services.AddSingleton<ISseSessionManager, SseSessionManager>();
        services.AddSingleton<IRedisStreamConsumer, RedisStreamConsumer>();
        services.AddHostedService(sp => (RedisStreamConsumer)sp.GetRequiredService<IRedisStreamConsumer>());
        services.AddHostedService<HeartbeatService>();

        return services;
    }
}
