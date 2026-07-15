public record DeploymentConfig(
    string Name,
    string DisplayName,
    int ApiExternalPort,
    int HealthCheckRetries,
    int HealthCheckDelaySeconds,
    string LogLevel
)
{
    public static DeploymentConfig Development => new(
        Name: "development",
        DisplayName: "Development",
        ApiExternalPort: 7100,
        HealthCheckRetries: 10,
        HealthCheckDelaySeconds: 5,
        LogLevel: "Debug"
    );

    public static DeploymentConfig Staging => new(
        Name: "staging",
        DisplayName: "Staging",
        ApiExternalPort: 7101,
        HealthCheckRetries: 30,
        HealthCheckDelaySeconds: 10,
        LogLevel: "Information"
    );

    public static DeploymentConfig Production => new(
        Name: "production",
        DisplayName: "Production",
        ApiExternalPort: 7102,
        HealthCheckRetries: 30,
        HealthCheckDelaySeconds: 10,
        LogLevel: "Information"
    );

    public static DeploymentConfig FromName(string name) => name.ToLower() switch
    {
        "development" => Development,
        "staging" => Staging,
        "production" => Production,
        _ => throw new ArgumentException($"Unknown environment: {name}")
    };
}
