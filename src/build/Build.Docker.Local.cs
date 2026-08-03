using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tools.Docker;
using Nuke.Common.Tooling;
using Polly;
using Serilog;
using System.Net.Http;

partial class Build
{
    Target DockerPullAll => _ => _
        .Description("Pull latest images for all services before blue-green swap")
        .DependsOn(DockerLogin)
        .Executes(async () =>
        {
            var version = await GetOrComputeImageVersion();
            foreach (var svc in ServiceDefinitions.All)
            {
                var tag = $"{DockerRegistry}/{svc.ImageName}:{version}";
                Log.Information("Pulling {Tag}", tag);
                DockerTasks.DockerPull(s => s.SetName(tag));
            }
        });

    Target DockerBlueGreenSwapAll => _ => _
        .Description("Blue-green container swap for all services")
        .DependsOn(DockerPullAll, PrepareEnvironment)
        .Executes(async () =>
        {
            var version = await GetOrComputeImageVersion();
            var env = DeploymentConfig.FromName(DeployEnvironment);
            var deployDir = RootDirectory / "deploy" / DeployEnvironment;

            foreach (var svc in ServiceDefinitions.All)
                await BlueGreenSwap(svc, version, env, deployDir);

            foreach (var svc in ServiceDefinitions.All)
            {
                var tag = $"{DockerRegistry}/{svc.ImageName}:{version}";
                var stableTag = $"{DockerRegistry}/{svc.ImageName}:latest-stable";
                Log.Information("Tagging {Tag} as latest-stable", tag);
                try
                {
                    ProcessTasks.StartProcess("docker", $"tag {tag} {stableTag}").AssertZeroExitCode();
                    DockerTasks.DockerPush(s => s.SetName(stableTag));
                }
                catch (Exception ex)
                {
                    Log.Warning("Failed to tag latest-stable: {Message}", ex.Message);
                }
            }
        });

    Target DockerCleanupAll => _ => _
        .Description("Remove dangling/old Docker images and stopped containers")
        .Executes(() =>
        {
            Log.Information("Pruning stopped containers...");
            DockerTasks.DockerContainerPrune(s => s.EnableForce());

            Log.Information("Pruning dangling images...");
            DockerTasks.DockerImagePrune(s => s.EnableForce());
        });

    async Task BlueGreenSwap(ServiceDefinition svc, string version, DeploymentConfig env, AbsolutePath deployDir)
    {
        var newTag = $"{DockerRegistry}/{svc.ImageName}:{version}";
        var containerPrefix = GetContainerNamePrefix(svc, env);
        var greenName = $"{containerPrefix}-green";
        var blueName = $"{containerPrefix}-blue";

        var envFile = deployDir / $"{svc.Name}.env";

        Log.Information("Blue-green swap for {Service} (image: {Tag})", svc.DisplayName, newTag);

        var tempPort = GetTemporaryPort(svc, env);
        Log.Information("Starting green container {Name} on temporary port {Port}", greenName, tempPort);

        var runArgs = BuildDockerRunArgs(svc, newTag, env, envFile, deployDir, tempPort);
        var dockerRunCommand = "run -d --name " + greenName + " --restart unless-stopped " + runArgs;
        ProcessTasks.StartProcess("docker", dockerRunCommand).AssertZeroExitCode();

        var healthy = await WaitForHealthyOnPort(svc, greenName, tempPort, env.HealthCheckRetries, env.HealthCheckDelaySeconds);

        if (!healthy)
        {
            Log.Error("Green container {Name} failed health check on temporary port", greenName);
            DumpContainerLogs(greenName);
            StopAndRemoveContainer(greenName);
            throw new Exception($"Health check failed for {svc.DisplayName}. Old container still serving. Deployment aborted.");
        }

        Log.Information("Green container {Name} passed health check — promoting to production port", greenName);

        StopAndRemoveContainer(blueName);
        StopAndRemoveContainer(greenName);

        var prodArgs = BuildDockerRunArgs(svc, newTag, env, envFile, deployDir);
        var prodCommand = "run -d --name " + blueName + " --restart unless-stopped " + prodArgs;
        ProcessTasks.StartProcess("docker", prodCommand).AssertZeroExitCode();

        var prodHealthy = await WaitForHealthy(svc, env, blueName, env.HealthCheckRetries, env.HealthCheckDelaySeconds);

        if (!prodHealthy && RollbackEnabled)
        {
            Log.Error("Container {Name} failed final health check — attempting rollback", blueName);
            DumpContainerLogs(blueName);
            await AttemptRollback(svc, env, envFile, deployDir);
            throw new Exception($"Health check failed for {svc.DisplayName} on production port. Rollback attempted.");
        }

        if (!prodHealthy)
        {
            Log.Error("Container {Name} failed final health check", blueName);
            DumpContainerLogs(blueName);
            throw new Exception($"Health check failed for {svc.DisplayName} on production port.");
        }

        Log.Information("Service {Service} deployed successfully as {Container}", svc.DisplayName, blueName);
    }

    int GetTemporaryPort(ServiceDefinition svc, DeploymentConfig env)
    {
        return 30000 + GetServiceHostPort(svc, env);
    }

    async Task AttemptRollback(ServiceDefinition svc, DeploymentConfig env,
        AbsolutePath envFile, AbsolutePath deployDir)
    {
        var stableTag = $"{DockerRegistry}/{svc.ImageName}:latest-stable";
        var blueName = $"{GetContainerNamePrefix(svc, env)}-blue";

        Log.Warning("Attempting rollback to {Tag}", stableTag);
        try
        {
            DockerTasks.DockerPull(s => s.SetName(stableTag));
            StopAndRemoveContainer(blueName);

            var rollbackArgs = BuildDockerRunArgs(svc, stableTag, env, envFile, deployDir);
            var cmd = "run -d --name " + blueName + " --restart unless-stopped " + rollbackArgs;
            ProcessTasks.StartProcess("docker", cmd).AssertZeroExitCode();

            var healthy = await WaitForHealthy(svc, env, blueName, env.HealthCheckRetries, env.HealthCheckDelaySeconds);
            Log.Information("Rollback to {Tag} {Status}", stableTag, healthy ? "succeeded" : "failed — manual intervention required");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Rollback failed — manual intervention required");
        }
    }

    string BuildDockerRunArgs(ServiceDefinition svc, string tag, DeploymentConfig env,
        AbsolutePath envFile, AbsolutePath deployDir, int? overridePort = null)
    {
        var args = new System.Text.StringBuilder();
        args.Append("--network host ");

        // Stable per-host hostname: the app's Redis cursor instanceId is Environment.MachineName,
        // so this keeps the cursor stable across container recreations and unique per svcfabric replica.
        var hostname = $"{GetContainerNamePrefix(svc, env)}-{Environment.MachineName}".ToLowerInvariant();
        args.Append($"--hostname {hostname} ");

        args.Append("--add-host adptv-sql-001:10.10.10.17 ");
        args.Append("--add-host sql01:10.10.10.17 ");
        args.Append("--add-host sql01.internal.snpsgroup.com:10.10.10.17 ");
        args.Append("--add-host cache01:10.10.30.30 ");
        args.Append("--add-host cache01.internal.snpsgroup.com:10.10.30.30 ");

        var port = overridePort ?? GetServiceHostPort(svc, env);
        args.Append($"-e ASPNETCORE_URLS=http://*:{port} ");
        args.Append("-e DOTNET_SYSTEM_NET_DISABLEIPV6=1 ");

        args.Append($"--env-file {envFile} ");
        args.Append(tag);
        return args.ToString();
    }

    async Task<bool> WaitForHealthyOnPort(ServiceDefinition svc, string containerName, int port, int retries, int delaySeconds)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var retryPolicy = Policy
            .Handle<Exception>()
            .WaitAndRetryAsync(retries, _ => TimeSpan.FromSeconds(delaySeconds));

        var result = await retryPolicy.ExecuteAndCaptureAsync(async () =>
        {
            var response = await client.GetAsync($"http://localhost:{port}/health");
            response.EnsureSuccessStatusCode();
        });

        return result.Outcome == OutcomeType.Successful;
    }

    async Task<bool> WaitForHealthy(ServiceDefinition svc, DeploymentConfig env, string containerName, int retries, int delaySeconds)
    {
        if (!svc.ExposesPort)
        {
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds));

            for (int i = 0; i < retries; i++)
            {
                try
                {
                    var inspect = ProcessTasks.StartProcess(
                        "docker",
                        $"inspect --format={{{{.State.Health.Status}}}} {containerName}");
                    inspect.AssertZeroExitCode();
                    var firstOutput = inspect.Output.FirstOrDefault();
                    var status = firstOutput.Text?.Trim();
                    if (status == "healthy") return true;
                    if (status == "unhealthy") return false;
                }
                catch { }
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
            }
            return false;
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var retryPolicy = Policy
            .Handle<Exception>()
            .WaitAndRetryAsync(retries, _ => TimeSpan.FromSeconds(delaySeconds));

        var result = await retryPolicy.ExecuteAndCaptureAsync(async () =>
        {
            var response = await client.GetAsync($"http://localhost:{GetServiceHostPort(svc, env)}/health");
            response.EnsureSuccessStatusCode();
        });

        return result.Outcome == OutcomeType.Successful;
    }

    string GetContainerNamePrefix(ServiceDefinition svc, DeploymentConfig env)
    {
        return env.Name == "production"
            ? svc.ContainerNamePrefix
            : $"{svc.ContainerNamePrefix}-{env.Name}";
    }

    void DumpContainerLogs(string name)
    {
        try
        {
            Log.Information("Dumping logs for container {Name}", name);
            var logs = ProcessTasks.StartProcess("docker", $"logs --tail 500 {name}");
            logs.WaitForExit();
            foreach (var line in logs.Output)
                Log.Information("[{Name}] {Log}", name, line.Text);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not dump logs for container {Name}", name);
        }
    }

    void StopAndRemoveContainer(string name)
    {
        try
        {
            Log.Information("Stopping container {Name}", name);
            ProcessTasks.StartProcess("docker", $"stop {name}").WaitForExit();
            ProcessTasks.StartProcess("docker", $"rm {name}").WaitForExit();
        }
        catch
        {
            Log.Debug("Container {Name} not running (skipping stop/remove)", name);
        }
    }
}
