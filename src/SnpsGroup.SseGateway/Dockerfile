# ─── Stage 1: Runtime base ────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS base
WORKDIR /app
EXPOSE 7089
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*

# ─── Stage 2: SDK build ───────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG NUGET_TOKEN
WORKDIR /repo

COPY src/NuGet.config ./NuGet.config
RUN sed -i "s|</packageSources>|  <packageSourceCredentials>\n    <nuget.snpsgroup.com>\n      <add key=\"Username\" value=\"ci\" />\n      <add key=\"ClearTextPassword\" value=\"${NUGET_TOKEN}\" />\n    </nuget.snpsgroup.com>\n  </packageSourceCredentials>\n</packageSources>|" NuGet.config

COPY global.json ./
COPY Directory.Build.props ./

COPY src/SnpsGroup.SseGateway/SnpsGroup.SseGateway.csproj src/SnpsGroup.SseGateway/

RUN dotnet restore src/SnpsGroup.SseGateway/SnpsGroup.SseGateway.csproj --configfile NuGet.config

COPY src/ src/

RUN dotnet build src/SnpsGroup.SseGateway/SnpsGroup.SseGateway.csproj \
    -c Release --no-restore

# ─── Stage 3: Publish ─────────────────────────────────────────────────────────
FROM build AS publish
RUN dotnet publish src/SnpsGroup.SseGateway/SnpsGroup.SseGateway.csproj \
    -c Release --no-build -o /app/publish

# ─── Stage 4: Final ───────────────────────────────────────────────────────────
FROM base AS final
WORKDIR /app
COPY --from=publish --chown=app:app /app/publish .

HEALTHCHECK --interval=30s --timeout=10s --start-period=30s --retries=3 \
    CMD curl -f http://localhost:7089/health || exit 1

ENV ASPNETCORE_URLS=http://+:7089
USER app
ENTRYPOINT ["dotnet", "SnpsGroup.SseGateway.dll"]
