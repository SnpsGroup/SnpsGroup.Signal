using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using SnpsGroup.SseGateway.Endpoints;
using SnpsGroup.SseGateway.Infrastructure;
using SnpsGroup.SseGateway.Options;

var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .CreateLogger();
builder.Host.UseSerilog();

builder.Configuration.AddEnvironmentVariables("SSE_GW_");

// Gateway services
builder.Services.AddSseGatewayServices(builder.Configuration);

// JWT Authentication (Keycloak)
var gwOptions = new SseGatewayOptions();
builder.Configuration.GetSection(SseGatewayOptions.SectionName).Bind(gwOptions);

if (!string.IsNullOrEmpty(gwOptions.Keycloak.Url))
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = $"{gwOptions.Keycloak.Url.TrimEnd('/')}/realms/{gwOptions.Keycloak.Realm}";
            // Configurable: defaults to true; set to false in QA to skip HTTPS cert validation
            // (e.g. when using self-signed certs or internal Docker URLs)
            options.RequireHttpsMetadata = gwOptions.Keycloak.RequireHttpsMetadata;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = false,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true
            };
            // When a public hostname is configured (e.g. https://auth.local) that
            // differs from the OIDC discovery hostname (e.g. http://shared-keycloak),
            // set ValidIssuer to accept tokens issued for the public hostname.
            if (!string.IsNullOrEmpty(gwOptions.Keycloak.ValidIssuer))
            {
                options.TokenValidationParameters.ValidIssuer = gwOptions.Keycloak.ValidIssuer;
            }
            // EventSource cannot send Authorization header — use query parameter
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    var token = context.Request.Query["token"].FirstOrDefault();
                    if (!string.IsNullOrEmpty(token))
                        context.Token = token;
                    return Task.CompletedTask;
                }
            };
        });
    builder.Services.AddAuthorization();
}

// CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("SseGateway", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .WithHeaders("Content-Type", "Authorization");
    });
});

var app = builder.Build();

app.UseCors("SseGateway");

if (!string.IsNullOrEmpty(gwOptions.Keycloak.Url))
{
    app.UseAuthentication();
    app.UseAuthorization();
}

// SSE endpoint
var sseEndpoint = app.MapGet("/sse/{channel}", SseEndpoint.Handle)
    .WithTags("SSE");

if (!string.IsNullOrEmpty(gwOptions.Keycloak.Url))
    sseEndpoint.RequireAuthorization();

// Health and status endpoints
HealthCheckEndpoints.Map(app);

await app.RunAsync();
