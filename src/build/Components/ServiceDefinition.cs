public record ServiceDefinition(
    string Name,
    string DisplayName,
    string ProjectPath,
    string ImageName,
    string ContainerNamePrefix,
    bool ExposesPort,
    string? DockerfilePath
);

public static class ServiceDefinitions
{
    public static ServiceDefinition SseGateway => new(
        Name: "sse-gateway",
        DisplayName: "Signal SSE Gateway",
        ProjectPath: "src/SnpsGroup.SseGateway",
        ImageName: "snpsgroup/signal/gateway",
        ContainerNamePrefix: "signal-gateway",
        ExposesPort: true,
        DockerfilePath: "SnpsGroup.SseGateway/Dockerfile"
    );

    public static IReadOnlyList<ServiceDefinition> All =>
        new[] { SseGateway };
}
