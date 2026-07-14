using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.Docker;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Utilities.Collections;
using Serilog;
using SnpsGroup.Nuke.Target.Common;
using System.Threading.Tasks;

partial class Build : NukeBuild
{
    public static int Main() => Execute<Build>(x => x.Default);

    [Parameter("Build configuration")]
    readonly Configuration Configuration = Configuration.Release;

    [Parameter("Docker registry base URL")]
    readonly string DockerRegistry = "nuget.snpsgroup.com/app-images";

    [Parameter("Docker registry server (hostname only, for docker login)")]
    readonly string DockerRegistryServer = "nuget.snpsgroup.com";

    [Parameter("NuGet feed URL")]
    readonly string NuGetFeedUrl = "https://nuget.snpsgroup.com/nuget/SnpsGroupNugetFeed/v3/index.json";

    [Parameter("NuGet API Key", Name = "NUGET_API_KEY")]
    [Secret]
    readonly string NuGetApiKey = EnvironmentInfo.GetVariable("NUGET_API_KEY");

    [Parameter("Target deployment environment (development|staging|production)")]
    readonly string DeployEnvironment = "development";

    [Parameter("Image version / semver tag", Name = "SNPS_IMAGE_VERSION")]
    readonly string ImageVersion = "latest";

    [Parameter("Enable rollback on failed health check")]
    readonly bool RollbackEnabled = true;

    [Parameter("OpenBao Role ID", Name = "OPENBAO_ROLE_ID")]
    readonly string OpenBaoRoleId = EnvironmentInfo.GetVariable("OPENBAO_ROLE_ID");

    [Parameter("OpenBao Secret ID", Name = "OPENBAO_SECRET_ID")]
    [Secret]
    readonly string OpenBaoSecretId = EnvironmentInfo.GetVariable("OPENBAO_SECRET_ID");

    [Parameter("Cloud provider name for versioning API")]
    readonly string CloudProvider = "azure";

    AbsolutePath SourceDirectory => RootDirectory / "src";
    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";
    AbsolutePath SolutionFile => RootDirectory / "SnpsGroup.Signal.sln";

    bool IsLocalBuild => NukeBuild.IsLocalBuild;
    bool IsProduction => DeployEnvironment.ToLower() == "production";

    string? _imageVersion;

    async Task<string> GetOrComputeImageVersion()
    {
        if (_imageVersion != null) return _imageVersion;

        if (ImageVersion != "latest")
        {
            _imageVersion = ImageVersion;
            return _imageVersion;
        }

        await VersionControlController.GetVersionInfo(CloudProvider, "SnpsGroup.Signal", IsLocalBuild, IsProduction);
        _imageVersion = VersionControlController.VersionInfo.SemVersion;
        Log.Information("Computed image version: {Version}", _imageVersion);
        return _imageVersion!;
    }

    Target Clean => _ => _
        .Description("Clean build artifacts")
        .Executes(() =>
        {
            ArtifactsDirectory.CreateOrCleanDirectory();
        });

    Target Restore => _ => _
        .Description("Restore NuGet packages for entire solution")
        .DependsOn(Clean)
        .Executes(() =>
        {
            DotNetTasks.DotNetRestore(s => s
                .SetProjectFile(SolutionFile)
                .SetConfigFile(SourceDirectory / "NuGet.config"));
        });

    Target Compile => _ => _
        .Description("Build the entire solution")
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetTasks.DotNetBuild(s => s
                .SetProjectFile(SolutionFile)
                .SetConfiguration(Configuration.ToString())
                .EnableNoRestore());
        });

    Target Test => _ => _
        .Description("Run unit and integration tests")
        .DependsOn(Compile)
        .Executes(() =>
        {
            DotNetTasks.DotNetTest(s => s
                .SetProjectFile(SourceDirectory / "SnpsGroup.SseGateway.Tests" / "SnpsGroup.SseGateway.Tests.csproj")
                .SetConfiguration(Configuration.ToString())
                .EnableNoRestore()
                .EnableNoBuild());
        });

    Target DockerBuildAll => _ => _
        .Description("Build Docker images for all services")
        .DependsOn(Compile)
        .DependsOn(DockerLogin)
        .Executes(async () =>
        {
            DockerPruneBuildCache();
            var version = await GetOrComputeImageVersion();
            foreach (var svc in ServiceDefinitions.All)
            {
                await DockerBuildServiceAsync(svc, version);
                DockerPushService(svc, version);
                DockerRemoveLocalImage(svc, version);
                DockerPruneDangling();
            }
        });

    Target DockerPushAll => _ => _
        .Description("Push Docker images to registry (images already pushed by DockerBuildAll)")
        .DependsOn(DockerBuildAll)
        .Executes(() =>
        {
            Log.Information("All images already built and pushed during DockerBuildAll.");
        });

    Target Default => _ => _
        .DependsOn(DockerPushAll);

    async Task DockerBuildServiceAsync(ServiceDefinition svc, string version)
    {
        var dockerfile = svc.DockerfilePath != null
            ? SourceDirectory / svc.DockerfilePath
            : SourceDirectory / svc.ProjectPath.Split('/').Last() / "Dockerfile";

        var tag = $"{DockerRegistry}/{svc.ImageName}:{version}";

        var nugetToken = NuGetApiKey;
        if (string.IsNullOrEmpty(nugetToken))
        {
            try
            {
                var shared = await GetSecretsFromOpenBaoAsync("signal/shared");
                nugetToken = shared.GetValueOrDefault("NuGetApiKey")
                             ?? shared.GetValueOrDefault("NugetApiKey");
            }
            catch { /* OpenBao may not be available in all contexts */ }
        }
        nugetToken ??= SnpsGroup.Nuke.Target.Common.Constants.ProgetNugetApiKey;

        Log.Information("Building image {Tag} from {Dockerfile}", tag, dockerfile);

        var settings = new DockerBuildSettings()
            .SetPath(RootDirectory.ToString())
            .SetFile(dockerfile)
            .SetTag(tag)
            .SetBuildArg($"NUGET_TOKEN={nugetToken}");

        DockerTasks.DockerBuild(settings);
    }

    void DockerPushService(ServiceDefinition svc, string version)
    {
        var tag = $"{DockerRegistry}/{svc.ImageName}:{version}";
        Log.Information("Pushing image {Tag}", tag);
        DockerTasks.DockerPush(s => s.SetName(tag));
    }

    void DockerPruneBuildCache()
    {
        try
        {
            Log.Information("Pruning Docker build cache to free disk space...");
            ProcessTasks.StartProcess("docker", "buildx prune -f").WaitForExit();
        }
        catch (Exception ex)
        {
            Log.Warning("Failed to prune build cache: {Message}", ex.Message);
        }
    }

    void DockerPruneDangling()
    {
        try
        {
            Log.Information("Pruning dangling images...");
            DockerTasks.DockerImagePrune(s => s.EnableForce());
        }
        catch (Exception ex)
        {
            Log.Warning("Failed to prune dangling images: {Message}", ex.Message);
        }
    }

    void DockerRemoveLocalImage(ServiceDefinition svc, string version)
    {
        var tag = $"{DockerRegistry}/{svc.ImageName}:{version}";
        try
        {
            Log.Information("Removing local image {Tag} to free disk space...", tag);
            ProcessTasks.StartProcess("docker", $"rmi {tag}").WaitForExit();
        }
        catch (Exception ex)
        {
            Log.Warning("Failed to remove local image: {Message}", ex.Message);
        }
    }
}
