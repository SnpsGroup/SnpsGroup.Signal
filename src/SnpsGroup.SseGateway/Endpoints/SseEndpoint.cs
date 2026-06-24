using SnpsGroup.SseGateway.Services;

namespace SnpsGroup.SseGateway.Endpoints;

/// <summary>
/// SSE endpoint that subscribes a client to a specific channel.
/// Route: GET /sse/{channel}
/// Returns raw JSON payloads for direct wire compatibility with existing consumers.
/// </summary>
public static class SseEndpoint
{
    public static IResult Handle(
        string channel,
        ISseSessionManager sessionManager,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("SseGateway.SseEndpoint");

        if (string.IsNullOrWhiteSpace(channel) || !IsValidChannelName(channel))
        {
            return Results.BadRequest(new { error = "Invalid channel name. Use only letters, digits, colons, dashes, and underscores." });
        }

        var connectionId = Guid.NewGuid().ToString("N");

        logger.LogInformation(
            "SSE connection request for channel '{Channel}'. Active sessions: {Count}",
            channel, sessionManager.ActiveConnectionCount);

        try
        {
            var stream = sessionManager.AddSession(channel, connectionId, cancellationToken);
            return TypedResults.ServerSentEvents(stream);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Maximum"))
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static bool IsValidChannelName(string channel)
    {
        foreach (var c in channel)
        {
            if (!char.IsLetterOrDigit(c) && c != ':' && c != '-' && c != '_')
                return false;
        }
        return true;
    }
}
