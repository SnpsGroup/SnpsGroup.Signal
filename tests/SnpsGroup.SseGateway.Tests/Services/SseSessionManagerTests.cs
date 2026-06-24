using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SnpsGroup.SseGateway.Services;
using SseGatewayOptions = SnpsGroup.SseGateway.Options.SseGatewayOptions;

namespace SnpsGroup.SseGateway.Tests.Services;

public class SseSessionManagerTests
{
    private static SseSessionManager CreateManager(int maxConnections = 100)
    {
        var options = new SseGatewayOptions { MaxConnections = maxConnections };
        var logger = new Mock<ILogger<SseSessionManager>>();
        return new SseSessionManager(Microsoft.Extensions.Options.Options.Create(options), logger.Object);
    }

    [Fact]
    public void ActiveConnectionCount_StartsAtZero()
    {
        var manager = CreateManager();
        manager.ActiveConnectionCount.Should().Be(0);
    }

    [Fact]
    public void ActiveChannelCount_StartsAtZero()
    {
        var manager = CreateManager();
        manager.ActiveChannelCount.Should().Be(0);
    }

    [Fact]
    public void AddSession_IncrementsConnectionCount()
    {
        var manager = CreateManager();
        using var cts = new CancellationTokenSource();

        manager.AddSession("test:channel", "conn-1", cts.Token);

        manager.ActiveConnectionCount.Should().Be(1);
        manager.ActiveChannelCount.Should().Be(1);
    }

    [Fact]
    public void AddSession_MultipleConnectionsOnSameChannel()
    {
        var manager = CreateManager();
        using var cts = new CancellationTokenSource();

        manager.AddSession("test:channel", "conn-1", cts.Token);
        manager.AddSession("test:channel", "conn-2", cts.Token);

        manager.ActiveConnectionCount.Should().Be(2);
        manager.ActiveChannelCount.Should().Be(1);
    }

    [Fact]
    public void AddSession_MultipleChannels()
    {
        var manager = CreateManager();
        using var cts = new CancellationTokenSource();

        manager.AddSession("test:channel-a", "conn-1", cts.Token);
        manager.AddSession("test:channel-b", "conn-2", cts.Token);

        manager.ActiveConnectionCount.Should().Be(2);
        manager.ActiveChannelCount.Should().Be(2);
        manager.GetSubscribedChannels().Should().BeEquivalentTo("test:channel-a", "test:channel-b");
    }

    [Fact]
    public void AddSession_RejectsAtMaxConnections()
    {
        var manager = CreateManager(maxConnections: 2);
        using var cts = new CancellationTokenSource();

        manager.AddSession("ch", "conn-1", cts.Token);
        manager.AddSession("ch", "conn-2", cts.Token);

        var act = () => manager.AddSession("ch", "conn-3", cts.Token);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Maximum*");
    }

    [Fact]
    public void AddSession_RejectsDuplicateConnectionId()
    {
        var manager = CreateManager();
        using var cts = new CancellationTokenSource();

        manager.AddSession("ch", "conn-1", cts.Token);

        var act = () => manager.AddSession("ch", "conn-1", cts.Token);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*already exists*");
    }

    [Fact]
    public void DispatchToChannel_DeliversToSubscribers()
    {
        var manager = CreateManager();
        using var cts = new CancellationTokenSource();

        manager.AddSession("test:ch", "conn-1", cts.Token);
        manager.AddSession("test:ch", "conn-2", cts.Token);

        var payload = """{"eventType":"test","data":"hello"}""";
        var delivered = manager.DispatchToChannel("test:ch", payload);

        delivered.Should().Be(2);
    }

    [Fact]
    public void DispatchToChannel_DoesNotReachOtherChannels()
    {
        var manager = CreateManager();
        using var cts = new CancellationTokenSource();

        manager.AddSession("test:ch-a", "conn-1", cts.Token);
        manager.AddSession("test:ch-b", "conn-2", cts.Token);

        var payload = """{"eventType":"test","data":"hello"}""";
        var delivered = manager.DispatchToChannel("test:ch-a", payload);

        delivered.Should().Be(1);
    }

    [Fact]
    public void DispatchToChannel_ReturnsZeroForUnsubscribedChannel()
    {
        var manager = CreateManager();
        using var cts = new CancellationTokenSource();

        manager.AddSession("test:ch-a", "conn-1", cts.Token);

        var payload = """{"eventType":"test","data":"hello"}""";
        var delivered = manager.DispatchToChannel("test:ch-b", payload);

        delivered.Should().Be(0);
    }

    [Fact]
    public void BroadcastToAll_ReachesAllSessions()
    {
        var manager = CreateManager();
        using var cts = new CancellationTokenSource();

        manager.AddSession("ch-a", "conn-1", cts.Token);
        manager.AddSession("ch-b", "conn-2", cts.Token);

        var payload = """{"eventType":"heartbeat"}""";
        var delivered = manager.BroadcastToAll(payload);

        delivered.Should().Be(2);
    }

    [Fact]
    public async Task Cancellation_CleansUpSession()
    {
        var manager = CreateManager();
        using var cts = new CancellationTokenSource();

        var stream = manager.AddSession("test:ch", "conn-1", cts.Token);
        manager.ActiveConnectionCount.Should().Be(1);

        // Start consuming the async enumerable (like ServerSentEvents would)
        var enumTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in stream.WithCancellation(cts.Token)) { }
            }
            catch (OperationCanceledException) { }
        });

        cts.Cancel();

        // Wait for the consumer to observe cancellation and run cleanup
        await enumTask;

        manager.ActiveConnectionCount.Should().Be(0);
        manager.ActiveChannelCount.Should().Be(0);
    }
}
