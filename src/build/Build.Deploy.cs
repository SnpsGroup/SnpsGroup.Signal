using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.Docker;
using Serilog;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

// ReSharper disable AllUnderscoreLocalParameterName

internal partial class Build
{
    /// <summary>
    /// Resolves the OpenBao base URL. Priority:
    /// 1. OPENBAO_ADDR env var (explicit override)
    /// 2. Self-hosted svcfabric agent → direct internal URL (port 8200)
    /// 3. Microsoft-hosted agent → reverse-proxied public URL (port 443)
    /// </summary>
    private string OpenBaoBaseUrl =>
        EnvironmentInfo.GetVariable("OPENBAO_ADDR")
        ?? (EnvironmentInfo.GetVariable("AGENT_MACHINENAME")?.Contains("svcfabric", StringComparison.OrdinalIgnoreCase) == true
            ? "https://keyvault.snpsgroup.com:8200"
            : "https://keyvault.snpsgroup.com");

    private string? _openBaoToken;

    private async Task<string> GetOpenBaoTokenAsync()
    {
        if (_openBaoToken != null)
        {
            return _openBaoToken;
        }

        Log.Information("Authenticating with OpenBao AppRole...");
        using var http = new HttpClient { BaseAddress = new Uri(OpenBaoBaseUrl) };
        var vaultNamespace = EnvironmentInfo.GetVariable("VAULT_NAMESPACE");
        if (!string.IsNullOrEmpty(vaultNamespace))
            http.DefaultRequestHeaders.Add("X-Vault-Namespace", vaultNamespace);
        var payload = JsonSerializer.Serialize(new { role_id = OpenBaoRoleId, secret_id = OpenBaoSecretId });
        var response = await http.PostAsync("v1/auth/approle/login",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            Log.Error("OpenBao AppRole login failed: {Status}\n{Body}", response.StatusCode, errorBody);
        }
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        _openBaoToken = body.GetProperty("auth").GetProperty("client_token").GetString()!;
        Log.Information("OpenBao authentication successful");
        return _openBaoToken;
    }

    private async Task<Dictionary<string, string>> GetSecretsFromOpenBaoAsync(string path)
    {
        var token = await GetOpenBaoTokenAsync();
        using var http = new HttpClient { BaseAddress = new Uri(OpenBaoBaseUrl) };
        http.DefaultRequestHeaders.Add("X-Vault-Token", token);
        var vaultNamespace = EnvironmentInfo.GetVariable("VAULT_NAMESPACE");
        if (!string.IsNullOrEmpty(vaultNamespace))
            http.DefaultRequestHeaders.Add("X-Vault-Namespace", vaultNamespace);

        var response = await http.GetAsync($"v1/secret/data/{path}");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = body.GetProperty("data").GetProperty("data");

        return data.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
    }

    private Target ValidateSecrets => _ => _
        .Description("Validate required OpenBao credentials are present")
        .Executes(() =>
        {
            if (string.IsNullOrEmpty(OpenBaoRoleId))
            {
                throw new Exception("OPENBAO_ROLE_ID is required");
            }

            if (string.IsNullOrEmpty(OpenBaoSecretId))
            {
                throw new Exception("OPENBAO_SECRET_ID is required");
            }
        });

    private Target PrepareEnvironment => _ => _
        .Description("Fetch secrets from OpenBao and write .env files")
        .DependsOn(ValidateSecrets)
        .Executes(async () =>
        {
            var deployDir = RootDirectory / "deploy" / DeployEnvironment;
            deployDir.CreateOrCleanDirectory();

            var envName = DeployEnvironment;
            var sharedSecrets = await GetSecretsFromOpenBaoAsync("signal/shared");
            var envSecrets = await GetSecretsFromOpenBaoAsync($"signal/{envName}");

            var allSecrets = sharedSecrets.Concat(envSecrets).ToDictionary(k => k.Key, v => v.Value);
            var config = DeploymentConfig.FromName(DeployEnvironment);
            var envCaps = char.ToUpper(envName[0]) + envName.Substring(1);

            foreach (var svc in ServiceDefinitions.All)
            {
                var templatePath = SourceDirectory / "build" / "Templates" / $"{svc.Name}.env.template";
                var template = File.ReadAllText(templatePath);

                var rendered = template
                    .Replace("{{ENVIRONMENT_CAPITALIZED}}", envCaps)
                    .Replace("{{API_PORT}}", config.ApiExternalPort.ToString())
                    .Replace("{{REDIS_CONNECTION_STRING}}", SanitizeSecretValue(allSecrets.GetValueOrDefault("redis-connection-string", "")))
                    .Replace("{{API_KEY}}", allSecrets.GetValueOrDefault("api-key", ""))
                    .Replace("{{KEYCLOAK_URL}}", allSecrets.GetValueOrDefault("keycloak-url", ""))
                    .Replace("{{KEYCLOAK_REALM}}", allSecrets.GetValueOrDefault("keycloak-realm", "platform"))
                    .Replace("{{KEYCLOAK_CLIENT_ID}}", allSecrets.GetValueOrDefault("keycloak-client-id", "signal"))
                    .Replace("{{KEYCLOAK_VALID_ISSUER}}", ResolveKeycloakValidIssuer(allSecrets))
                    .Replace("{{LOG_LEVEL}}", config.LogLevel)
                    .Replace("{{IMAGE_VERSION}}", _imageVersion ?? ImageVersion);

                File.WriteAllText(deployDir / $"{svc.Name}.env", rendered);
                Log.Information("Written {Env}", svc.Name + ".env");
            }
        });

    /// <summary>
    /// Returns the host port the service should listen on with --network host.
    /// </summary>
    private static int GetServiceHostPort(ServiceDefinition svc, DeploymentConfig config) => svc.Name switch
    {
        "sse-gateway" => config.ApiExternalPort,
        _ => throw new ArgumentOutOfRangeException(nameof(svc))
    };

    /// <summary>
    /// Resolves the explicit JWT issuer (<c>iss</c>) for the SSE Gateway.
    /// Tokens are minted for the public Keycloak hostname (e.g. https://auth.snpsgroup.com),
    /// but <c>keycloak-url</c> may be an internal Docker hostname (e.g. http://shared-keycloak)
    /// whose OIDC discovery advertises a different issuer. Without an explicit
    /// <c>ValidIssuer</c>, the gateway rejects valid tokens with 401 invalid_token.
    /// Priority: explicit <c>keycloak-valid-issuer</c> secret, else derive from keycloak-url + realm.
    /// </summary>
    private static string ResolveKeycloakValidIssuer(IReadOnlyDictionary<string, string> secrets)
    {
        var explicitIssuer = secrets.GetValueOrDefault("keycloak-valid-issuer", "");
        if (!string.IsNullOrWhiteSpace(explicitIssuer))
        {
            return explicitIssuer;
        }

        var url = (secrets.GetValueOrDefault("keycloak-url", "") ?? "").TrimEnd('/');
        var realm = secrets.GetValueOrDefault("keycloak-realm", "platform");
        return string.IsNullOrEmpty(url) ? string.Empty : $"{url}/realms/{realm}";
    }

    /// <summary>
    /// Sanitize fallback connection strings from OpenBao so they use short hostnames
    /// mapped via --add-host inside the container, avoiding FQDN resolution issues.
    /// </summary>
    private static string SanitizeSecretValue(string value) =>
        string.IsNullOrEmpty(value)
            ? value
            : value
                .Replace("sql01.internal.snpsgroup.com", "adptv-sql-001", StringComparison.OrdinalIgnoreCase)
                .Replace("ADPTV-SQL-001", "adptv-sql-001", StringComparison.OrdinalIgnoreCase)
                .Replace("cache01.internal.snpsgroup.com", "cache01", StringComparison.OrdinalIgnoreCase);

    private Target Deploy => _ => _
        .Description("Full deploy: PrepareEnvironment → DockerLogin → BlueGreenSwapAll → Cleanup")
        .DependsOn(PrepareEnvironment, DockerLogin, DockerBlueGreenSwapAll, DockerCleanupAll);

    private Target Rollback => _ => _
        .Description("Rollback all services to latest-stable image tag")
        .DependsOn(DockerLogin)
        .Executes(async () =>
        {
            var env = DeploymentConfig.FromName(DeployEnvironment);
            var deployDir = RootDirectory / "deploy" / DeployEnvironment;
            Log.Warning("Rollback requested for environment {Env} — pulling latest-stable tags", DeployEnvironment);

            foreach (var svc in ServiceDefinitions.All)
            {
                var stableTag = $"{DockerRegistry}/{svc.ImageName}:latest-stable";
                var blueName = $"{GetContainerNamePrefix(svc, env)}-blue";

                Log.Information("Rolling back {Service} to {Tag}", svc.DisplayName, stableTag);
                try
                {
                    DockerTasks.DockerPull(s => s.SetName(stableTag));
                    StopAndRemoveContainer(blueName);

                    var envFile = deployDir / $"{svc.Name}.env";
                    var runArgs = BuildDockerRunArgs(svc, stableTag, env, envFile, deployDir);
                    var cmd = "run -d --name " + blueName + " --restart unless-stopped " + runArgs;
                    ProcessTasks.StartProcess("docker", cmd).AssertZeroExitCode();

                    var healthy = await WaitForHealthy(svc, env, blueName, env.HealthCheckRetries, env.HealthCheckDelaySeconds);
                    Log.Information("Rollback of {Service} {Status}", svc.DisplayName, healthy ? "succeeded" : "failed");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Rollback failed for {Service} — manual intervention required", svc.DisplayName);
                }
            }
        });
}
