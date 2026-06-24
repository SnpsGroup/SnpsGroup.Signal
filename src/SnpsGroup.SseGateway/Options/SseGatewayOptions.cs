namespace SnpsGroup.SseGateway.Options;

/// <summary>
/// Configuration for the SSE Gateway service.
/// Bound from the "SseGateway" section in appsettings.json.
/// </summary>
public class SseGatewayOptions
{
    public const string SectionName = "SseGateway";

    /// <summary>
    /// Redis connection string.
    /// </summary>
    public string RedisConnectionString { get; set; } = "localhost:6379";

    /// <summary>
    /// Redis stream key to consume events from.
    /// </summary>
    public string StreamKey { get; set; } = "sse:events";

    /// <summary>
    /// Key prefix for storing per-instance cursors in Redis.
    /// Full key: {CursorKeyPrefix}:{instanceId}
    /// </summary>
    public string CursorKeyPrefix { get; set; } = "sse:cursor";

    /// <summary>
    /// Maximum concurrent SSE connections across all channels.
    /// </summary>
    public int MaxConnections { get; set; } = 500;

    /// <summary>
    /// Bounded channel capacity per SSE connection (backpressure).
    /// </summary>
    public int PerConnectionBufferCapacity { get; set; } = 64;

    /// <summary>
    /// Interval for sending heartbeat events to idle SSE connections.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long to block on XREAD per poll cycle (milliseconds).
    /// </summary>
    public int StreamReadTimeoutMs { get; set; } = 2000;

    /// <summary>
    /// Maximum number of messages to read per XREAD call.
    /// </summary>
    public int StreamReadBatchSize { get; set; } = 100;

    /// <summary>
    /// Keycloak settings for JWT token validation.
    /// </summary>
    public KeycloakOptions Keycloak { get; set; } = new();
}

/// <summary>
/// Keycloak OpenID Connect configuration for JWT validation.
/// </summary>
public class KeycloakOptions
{
    public string Url { get; set; } = string.Empty;
    public string Realm { get; set; } = "platform";
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Whether to require HTTPS when fetching the OIDC discovery document.
    /// Set to false in QA when the Keycloak Authority uses a self-signed certificate
    /// or an internal Docker network URL (e.g. http://shared-keycloak:8080).
    /// </summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// Expected token issuer (iss claim). Leave empty to use the issuer from the
    /// OIDC discovery document. Set explicitly when the internal Docker hostname
    /// (e.g. http://shared-keycloak/realms/platform) differs from the public
    /// hostname in the token (e.g. https://auth.local/realms/platform).
    /// </summary>
    public string ValidIssuer { get; set; } = string.Empty;
}
