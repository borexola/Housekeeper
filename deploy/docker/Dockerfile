# syntax=docker/dockerfile:1

########## Build ##########
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore on its own layer so source edits do not re-download packages.
COPY Directory.Build.props HearthSense.slnx ./
COPY src/HearthSense.Core/HearthSense.Core.csproj src/HearthSense.Core/
COPY src/HearthSense.Api/HearthSense.Api.csproj src/HearthSense.Api/
RUN dotnet restore src/HearthSense.Api/HearthSense.Api.csproj

COPY .editorconfig ./
COPY src/ src/
RUN dotnet publish src/HearthSense.Api/HearthSense.Api.csproj \
        -c Release -o /app/publish --no-restore /p:UseAppHost=false

########## Runtime ##########
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Inside the container the API binds 0.0.0.0, so HEARTHSENSE_API_TOKEN becomes mandatory —
# see the port mapping in compose.yaml, which only publishes to host loopback.
# /data holds the database and anything saved from the settings page, so it must be a persistent volume.
ENV DOTNET_EnableDiagnostics=0 \
    ASPNETCORE_ENVIRONMENT=Production \
    HEARTHSENSE__Api__BindAddress=0.0.0.0 \
    HEARTHSENSE__Storage__Path=/data/hearthsense.db

RUN useradd --uid 10001 --create-home --shell /usr/sbin/nologin hearthsense \
 && mkdir -p /data \
 && chown -R hearthsense:hearthsense /app /data

COPY --from=build --chown=hearthsense:hearthsense /app/publish ./

USER 10001
EXPOSE 5080
VOLUME ["/data"]

# Uses the runtime image's own dotnet rather than adding curl just for a health check.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD ["dotnet", "HearthSense.Api.dll", "--healthcheck"]

ENTRYPOINT ["dotnet", "HearthSense.Api.dll"]
