using Microsoft.AspNetCore.Mvc.Testing;
using SnpsGroup.SseGateway.Options;
using Testcontainers.Redis;

namespace SnpsGroup.SseGateway.Tests.Integration;

public class SseGatewayIntegrationTests : IAsyncLifetime
{
    private readonly RedisContainer _redisContainer = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .Build();

    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;
    private StackExchange.Redis.ConnectionMultiplexer? _redis;

    public async Task InitializeAsync()
    {
        await _redisContainer.StartAsync();

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting($"{SseGatewayOptions.SectionName}:RedisConnectionString",
                    _redisContainer.GetConnectionString());
                builder.UseSetting($"{SseGatewayOptions.SectionName}:Keycloak:Url", "");
            });

        _client = _factory.CreateClient();

        // Shared multiplexer: a synchronous Connect() per test added ~30s of setup latency
        // (slow handshake in the test host), which raced the publish against the 30s heartbeat.
        // Reusing one connection keeps publishes instant.
        _redis = await StackExchange.Redis.ConnectionMultiplexer.ConnectAsync(_redisContainer.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        _redis?.Dispose();
        _factory?.Dispose();
        await _redisContainer.DisposeAsync();
    }

    /// <summary>
    /// Reads SSE lines until one matches <paramref name="contains"/>, skipping heartbeat
    /// frames. Heartbeats (sent every 30s) can interleave with published events, so a single
    /// ReadLine would assert against a heartbeat and miss the payload.
    /// </summary>
    private static async Task<string?> ReadLineContainingAsync(StreamReader reader, string contains, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.Token.IsCancellationRequested)
        {
            var readTask = reader.ReadLineAsync(cts.Token);
            var line = await readTask.ConfigureAwait(false);
            if (line is not null && line.Contains(contains, StringComparison.Ordinal))
            {
                return line;
            }
        }
        return null;
    }

    /// <summary>
    /// Drains SSE lines for the timeout window and returns true if any line matches
    /// <paramref name="contains"/>. Heartbeats are tolerated (they broadcast to all sessions);
    /// only a match on the payload indicates a channel-isolation leak.
    /// </summary>
    private static async Task<bool> LineContainingSeenAsync(StreamReader reader, string contains, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                var line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
                if (line is not null && line.Contains(contains, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
        return false;
    }

    [Fact]
    public async Task HealthEndpoint_Returns200()
    {
        var response = await _client!.GetAsync("/health");
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task StatusEndpoint_Returns200()
    {
        var response = await _client!.GetAsync("/status");
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("SSE Gateway");
    }

    [Fact]
    public async Task SseEndpoint_WithInvalidChannel_Returns400()
    {
        var response = await _client!.GetAsync("/sse/invalid%20channel");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SseEndpoint_WithEmptyChannel_Returns404()
    {
        // No channel parameter at all — route doesn't match
        var response = await _client!.GetAsync("/sse/");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task EndToEnd_PublishAndReceive()
    {
        // Connect SSE client
        using var sseResponse = await _client!.GetAsync("/sse/test:e2e", HttpCompletionOption.ResponseHeadersRead);
        sseResponse.EnsureSuccessStatusCode();

        var stream = await sseResponse.Content.ReadAsStreamAsync();
        var reader = new StreamReader(stream);

        // Publish event to Redis Stream
        var db = _redis!.GetDatabase();
        await db.StreamAddAsync("sse:events",
        [
            new StackExchange.Redis.NameValueEntry("id", "test-1"),
            new StackExchange.Redis.NameValueEntry("channel", "test:e2e"),
            new StackExchange.Redis.NameValueEntry("eventType", "ping"),
            new StackExchange.Redis.NameValueEntry("data", """{"msg":"hello"}"""),
            new StackExchange.Redis.NameValueEntry("timestamp", DateTime.UtcNow.ToString("O"))
        ]);

        // Read SSE output, skipping heartbeats until the published payload arrives
        var line = await ReadLineContainingAsync(reader, "hello", TimeSpan.FromSeconds(15));
        line.Should().NotBeNull("SSE client should receive the published event within timeout");
    }

    [Fact]
    public async Task EndToEnd_MultipleChannels_Isolated()
    {
        // Connect two SSE clients on different channels
        using var responseA = await _client!.GetAsync("/sse/test:ch-a", HttpCompletionOption.ResponseHeadersRead);
        using var responseB = await _client!.GetAsync("/sse/test:ch-b", HttpCompletionOption.ResponseHeadersRead);

        var readerA = new StreamReader(await responseA.Content.ReadAsStreamAsync());
        var readerB = new StreamReader(await responseB.Content.ReadAsStreamAsync());

        // Publish event only to channel A
        var db = _redis!.GetDatabase();
        await db.StreamAddAsync("sse:events",
        [
            new StackExchange.Redis.NameValueEntry("id", "iso-1"),
            new StackExchange.Redis.NameValueEntry("channel", "test:ch-a"),
            new StackExchange.Redis.NameValueEntry("eventType", "isolated"),
            new StackExchange.Redis.NameValueEntry("data", """{"target":"A"}"""),
            new StackExchange.Redis.NameValueEntry("timestamp", DateTime.UtcNow.ToString("O"))
        ]);

        // Reader A should get the event (skip heartbeats)
        var readA = await ReadLineContainingAsync(readerA, "target", TimeSpan.FromSeconds(15));
        readA.Should().Contain("target");

        // Reader B may receive heartbeats (broadcast to all sessions), but must NOT receive
        // channel A's event payload. Drain lines for a short window and assert isolation.
        var leakedToB = await LineContainingSeenAsync(readerB, "target", TimeSpan.FromSeconds(2));
        leakedToB.Should().BeFalse("channel B must not receive channel A's event");
    }
}
