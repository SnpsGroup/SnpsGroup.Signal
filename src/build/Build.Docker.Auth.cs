using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tools.Docker;
using Serilog;
using System.Diagnostics;

partial class Build
{
    [Parameter("Docker registry username")]
    readonly string DockerRegistryUsername = EnvironmentInfo.GetVariable("DOCKER_REGISTRY_USERNAME");

    [Parameter("Docker registry password")]
    [Secret]
    readonly string DockerRegistryPassword = EnvironmentInfo.GetVariable("DOCKER_REGISTRY_PASSWORD");

    Target DockerLogin => _ => _
        .Description("Login to Docker registry (credentials from env vars or OpenBao)")
        .Executes(async () =>
        {
            var (username, password) = await ResolveDockerCredentials();
            Log.Information("Logging in to {Registry}", DockerRegistryServer);

            var psi = new ProcessStartInfo("docker",
                $"login --username {username} --password-stdin {DockerRegistryServer}")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var proc = Process.Start(psi)!;
            await proc.StandardInput.WriteAsync(password);
            proc.StandardInput.Close();
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (!string.IsNullOrWhiteSpace(stdout)) Log.Information("{Output}", stdout.Trim());
            if (!string.IsNullOrWhiteSpace(stderr) && proc.ExitCode != 0) Log.Error("{Error}", stderr.Trim());

            if (proc.ExitCode != 0)
                throw new Exception($"docker login failed (exit {proc.ExitCode}): {stderr.Trim()}");

            Log.Information("Docker login successful");
        });

    async Task<(string Username, string Password)> ResolveDockerCredentials()
    {
        if (!string.IsNullOrEmpty(DockerRegistryUsername) && !string.IsNullOrEmpty(DockerRegistryPassword))
            return (DockerRegistryUsername, DockerRegistryPassword);

        if (string.IsNullOrEmpty(OpenBaoRoleId) || string.IsNullOrEmpty(OpenBaoSecretId))
            throw new Exception(
                "Docker credentials not found. Set DOCKER_REGISTRY_USERNAME/DOCKER_REGISTRY_PASSWORD " +
                "or provide OPENBAO_ROLE_ID/OPENBAO_SECRET_ID to fetch them from OpenBao.");

        Log.Information("Fetching Docker credentials from OpenBao...");
        var secrets = await GetSecretsFromOpenBaoAsync("signal/shared");
        return (
            secrets["DockerRegistryUsername"],
            secrets["DockerRegistryPassword"]
        );
    }
}
