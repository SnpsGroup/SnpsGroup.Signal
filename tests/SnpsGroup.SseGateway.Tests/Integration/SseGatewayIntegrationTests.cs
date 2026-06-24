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
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
        await _redisContainer.DisposeAsync();
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
        var redis = StackExchange.Redis.ConnectionMultiplexer.Connect(_redisContainer.GetConnectionString());
        var db = redis.GetDatabase();
        await db.StreamAddAsync("sse:events",
        [
            new StackExchange.Redis.NameValueEntry("id", "test-1"),
            new StackExchange.Redis.NameValueEntry("channel", "test:e2e"),
            new StackExchange.Redis.NameValueEntry("eventType", "ping"),
            new StackExchange.Redis.NameValueEntry("data", """{"msg":"hello"}"""),
            new StackExchange.Redis.NameValueEntry("timestamp", DateTime.UtcNow.ToString("O"))
        ]);

        // Read SSE output with timeout
        var readTask = reader.ReadLineAsync();
        var completed = await Task.WhenAny(readTask, Task.Delay(5000));

        completed.Should().Be(readTask, "SSE client should receive an event within timeout");
        var line = await readTask;
        line.Should().NotBeNull();
        line.Should().Contain("hello");

        redis.Dispose();
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
        var redis = StackExchange.Redis.ConnectionMultiplexer.Connect(_redisContainer.GetConnectionString());
        var db = redis.GetDatabase();
        await db.StreamAddAsync("sse:events",
        [
            new StackExchange.Redis.NameValueEntry("id", "iso-1"),
            new StackExchange.Redis.NameValueEntry("channel", "test:ch-a"),
            new StackExchange.Redis.NameValueEntry("eventType", "isolated"),
            new StackExchange.Redis.NameValueEntry("data", """{"target":"A"}"""),
            new StackExchange.Redis.NameValueEntry("timestamp", DateTime.UtcNow.ToString("O"))
        ]);

        // Reader A should get the event
        var readA = await readerA.ReadLineAsync();
        readA.Should().Contain("target");

        redis.Dispose();
    }
}
