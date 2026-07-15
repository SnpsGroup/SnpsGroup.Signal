using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tools.DotNet;
using Serilog;
using SnpsGroup.Nuke.Target.Common;

partial class Build
{
    static readonly string[] NuGetProjectPaths =
    [
        "src/SnpsGroup.SseGateway.Client/SnpsGroup.SseGateway.Client.csproj"
    ];

    Target PackLibraries => _ => _
        .Description("Pack SnpsGroup.SseGateway.Client as NuGet package")
        .DependsOn(Compile)
        .Executes(async () =>
        {
            var packDir = ArtifactsDirectory / "nuget";
            packDir.CreateOrCleanDirectory();

            await VersionControlController.GetVersionInfo(
                "azure",
                "SnpsGroup.Signal",
                IsLocalBuild,
                IsProduction);

            var version = VersionControlController.VersionInfo.SemVersion;
            Log.Information("NuGet package version: {Version}", version);

            foreach (var projectRelativePath in NuGetProjectPaths)
            {
                var csprojPath = RootDirectory / projectRelativePath;
                var projectName = System.IO.Path.GetFileNameWithoutExtension(csprojPath);

                Log.Information("Restoring {Project}", projectName);
                DotNetTasks.DotNetRestore(s => s
                    .SetProjectFile(csprojPath)
                    .SetConfigFile(SourceDirectory / "NuGet.config"));

                Log.Information("Packing {Project} at version {Version}", projectName, version);

                DotNetTasks.DotNetPack(s => s
                    .SetProject(csprojPath)
                    .SetConfiguration(Configuration.Release.ToString())
                    .SetOutputDirectory(packDir)
                    .SetProperty("Version", version)
                    .SetProperty("PackageVersion", version)
                    .SetProperty("TreatWarningsAsErrors", "false")
                    .EnableNoRestore());
            }
        });

    Target PushNuGet => _ => _
        .Description("Publish SnpsGroup.SseGateway.Client NuGet package to private feed")
        .DependsOn(PackLibraries)
        .Executes(async () =>
        {
            var apiKey = NuGetApiKey;
            if (string.IsNullOrEmpty(apiKey))
            {
                Log.Information("NuGetApiKey not in env — fetching from OpenBao signal/shared");
                try
                {
                    var sharedSecrets = await GetSecretsFromOpenBaoAsync("signal/shared");
                    apiKey = sharedSecrets.GetValueOrDefault("NuGetApiKey")
                             ?? sharedSecrets.GetValueOrDefault("NugetApiKey");
                }
                catch { /* OpenBao may not be available in all contexts */ }
            }

            apiKey ??= EnvironmentInfo.GetVariable("DOCKER_REGISTRY_PASSWORD");
            apiKey ??= SnpsGroup.Nuke.Target.Common.Constants.ProgetNugetApiKey;

            if (string.IsNullOrEmpty(apiKey))
                throw new Exception("NuGet API key not found. Set NUGET_API_KEY env var or configure OpenBao signal/shared.");

            var packages = (ArtifactsDirectory / "nuget").GlobFiles("*.nupkg");
            foreach (var package in packages)
            {
                Log.Information("Pushing {Package}", package.Name);
                DotNetTasks.DotNetNuGetPush(s => s
                    .SetTargetPath(package)
                    .SetSource(NuGetFeedUrl)
                    .SetApiKey(apiKey)
                    .EnableSkipDuplicate());
            }
        });
}
