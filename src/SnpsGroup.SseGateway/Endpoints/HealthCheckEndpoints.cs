namespace SnpsGroup.SseGateway.Endpoints;

/// <summary>
/// Health check endpoints for the SSE Gateway.
/// </summary>
public static class HealthCheckEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/health", (IServiceProvider sp) =>
        {
            // Basic liveness check — process is running
            return Results.Ok(new { status = "healthy", time = DateTimeOffset.UtcNow });
        }).AllowAnonymous();

        app.MapGet("/status", (IServiceProvider sp) =>
        {
            return Results.Ok(new
            {
                service = "SSE Gateway",
                status = "operational",
                time = DateTimeOffset.UtcNow
            });
        }).AllowAnonymous();
    }
}
