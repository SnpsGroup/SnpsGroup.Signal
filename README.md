# SnpsGroup.Signal

Multi-repository solution for the **SnpsGroup SSE Gateway** stack — extracted from
[SnpsGroup.Dfe](https://github.com/SnpsGroup/SnpsGroup.Dfe).

## Repositories

| Repository | Purpose |
|---|---|
| [SnpsGroup.Signal](https://github.com/SnpsGroup/SnpsGroup.Signal) | This repository — SSE Gateway runtime + Client library + tests |
| [SnpsGroup.Dfe](https://github.com/SnpsGroup/SnpsGroup.Dfe) | Brazilian fiscal document (NFCom) processing platform. Consumes `SnpsGroup.SseGateway.Client` as a NuGet package. |

## Solution Layout

```
SnpsGroup.Signal.sln
├── src/SnpsGroup.SseGateway/             # ASP.NET Core 10 Web API (SSE consumer)
│   ├── Configuration/RedisConnectionFactory.cs
│   ├── Endpoints/{SseEndpoint,HealthCheckEndpoints}.cs
│   ├── Infrastructure/SseGatewayModule.cs
│   ├── Models/{SseClientMessage,SseEvent}.cs
│   ├── Options/SseGatewayOptions.cs
│   ├── Services/{HeartbeatService,RedisStreamConsumer,SseSessionManager,...}.cs
│   ├── Properties/launchSettings.json
│   ├── appsettings.json
│   ├── appsettings.Development.json
│   ├── Dockerfile
│   ├── Program.cs
│   └── SnpsGroup.SseGateway.csproj
├── src/SnpsGroup.SseGateway.Client/      # Client library (NuGet-packable)
│   ├── Options/SsePublisherOptions.cs
│   ├── SseEventPublisher.cs
│   └── SnpsGroup.SseGateway.Client.csproj
└── tests/SnpsGroup.SseGateway.Tests/     # xUnit + Testcontainers.Redis
    ├── Integration/SseGatewayIntegrationTests.cs
    ├── Services/SseSessionManagerTests.cs
    └── SnpsGroup.SseGateway.Tests.csproj
```

## Quick Start

```bash
# Restore
dotnet restore SnpsGroup.Signal.sln

# Build
dotnet build SnpsGroup.Signal.sln

# Run the gateway
dotnet run --project src/SnpsGroup.SseGateway/SnpsGroup.SseGateway.csproj

# Run tests (integration tests use Testcontainers — Docker required)
dotnet test tests/SnpsGroup.SseGateway.Tests/SnpsGroup.SseGateway.Tests.csproj
```

## SnpsGroup.SseGateway

ASP.NET Core 10 service that consumes events from a **Redis Stream** and broadcasts
them to subscribed SSE (Server-Sent Events) clients. Supports JWT (Keycloak)
authentication.

Default port: **7089**

### Configuration

`SseGateway` section in `appsettings.json`:

| Key | Description | Default |
|---|---|---|
| `RedisConnectionString` | Redis connection string | `localhost:6379` |
| `StreamKey` | Redis stream key to consume | `sse:events` |
| `CursorKeyPrefix` | Per-instance cursor key prefix | `sse:cursor` |
| `MaxConnections` | Maximum concurrent SSE connections | `500` |
| `PerConnectionBufferCapacity` | Bounded channel capacity per connection (backpressure) | `64` |
| `HeartbeatInterval` | Heartbeat interval (TimeSpan) | `00:00:30` |
| `StreamReadTimeoutMs` | XREAD block timeout (ms) | `2000` |
| `StreamReadBatchSize` | Messages per XREAD call | `100` |
| `Keycloak:Url` | Keycloak base URL (leave empty to disable auth) | `""` |
| `Keycloak:Realm` | Keycloak realm | `platform` |
| `Keycloak:ClientId` | Keycloak client ID | `""` |
| `Keycloak:RequireHttpsMetadata` | Require HTTPS for OIDC discovery | `true` |
| `Keycloak:ValidIssuer` | Optional explicit issuer (overrides discovery) | `""` |

Environment variables use the prefix `SSE_GW_` (e.g. `SSE_GW_SseGateway__RedisConnectionString`).

### Endpoints

| Route | Description |
|---|---|
| `GET /sse/{channel}` | Subscribe to SSE events on a channel |
| `GET /health` | Health check |
| `GET /status` | Status info |

When `Keycloak:Url` is set, `/sse/{channel}` requires a valid JWT (Authorization
header or `?token=...` query parameter for EventSource compatibility).

## SnpsGroup.SseGateway.Client

Producer-side library used by applications to push events to the SSE Gateway's
Redis Stream. Published to the private feed
`https://nuget.snpsgroup.com/nuget/SnpsGroupNugetFeed/v3/index.json`.

### Usage in another project

1. Add the package reference:

   ```xml
   <PackageReference Include="SnpsGroup.SseGateway.Client" Version="1.0.0" />
   ```

2. Configure `SsePublisher` section:

   ```json
   "SsePublisher": {
     "RedisConnectionString": "localhost:6379",
     "StreamKey": "sse:events",
     "MaxStreamLength": 10000
   }
   ```

3. Inject and use:

   ```csharp
   public class MyService(ISseEventPublisher publisher)
   {
       public async Task NotifyAsync(string payload, CancellationToken ct)
       {
           await publisher.PublishAsync(
               channel: "my:channel",
               eventType: "update",
               payload: payload,
               ct: ct);
       }
   }
   ```

## Framework & Libraries

| Concern | Library |
|---|---|
| Target framework | .NET 10 |
| HTTP framework | ASP.NET Core 10 |
| Authentication | `Microsoft.AspNetCore.Authentication.JwtBearer` (Keycloak) |
| Redis client | `StackExchange.Redis` 2.11.8 |
| Logging | `Serilog.AspNetCore` |
| Testing | xUnit + FluentAssertions + Moq + Testcontainers.Redis |

## Build & Test

```bash
# Build entire solution (Release, x64)
dotnet build SnpsGroup.Signal.sln -c Release -p:Platform=x64

# Run unit + integration tests
dotnet test tests/SnpsGroup.SseGateway.Tests/SnpsGroup.SseGateway.Tests.csproj -c Release -p:Platform=x64

# Pack the Client NuGet package
dotnet pack src/SnpsGroup.SseGateway.Client/SnpsGroup.SseGateway.Client.csproj -c Release -o artifacts/

# Publish the Gateway (self-contained)
dotnet publish src/SnpsGroup.SseGateway/SnpsGroup.SseGateway.csproj -c Release -o publish/
```

## Deployment

This repository uses the same deployment patterns as SnpsGroup.Dfe:

| File | Purpose |
|---|---|
| `azure-pipelines-hybrid.yml` | Azure DevOps pipeline (multi-stage: Version → Build → Staging → Production) |
| `deploy.ps1` | Local packaging script (publish + zip) |
| `templates/*.yml` | Reusable pipeline steps |
| `scripts/*.ps1` | Versioning, IIS init, deployment helpers |
| `Dockerfile` | Linux container build for the SseGateway service |

### Docker

```bash
docker build -f src/SnpsGroup.SseGateway/Dockerfile -t snpsgroup/signal/sse-gateway:1.0.0 .
docker run --rm -p 7089:7089 \
  -e SSE_GW_SseGateway__RedisConnectionString=host.docker.internal:6379 \
  -e SSE_GW_SseGateway__Keycloak__Url=http://host.docker.internal:8080 \
  snpsgroup/signal/sse-gateway:1.0.0
```

## Custom NuGet Feed

Packages like `SnpsGroup.SseGateway.Client` are published to the private feed:

```
https://nuget.snpsgroup.com/nuget/SnpsGroupNugetFeed/v3/index.json
```

The `NuGet.config` at the root already references it.

## License

Proprietary — © SnpsGroup.
