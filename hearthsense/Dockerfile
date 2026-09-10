# syntax=docker/dockerfile:1
#
# The Home Assistant add-on image. Built from the repository root, not from this folder:
#
#   docker build -f hearthsense/Dockerfile -t hearthsense-addon .
#
# The release workflow builds one image per architecture and pushes them where config.yaml points.

########## Build ##########
# Always compile on the runner's own architecture. The published output is portable IL and carries the
# native SQLite binaries for every runtime, so there is nothing to cross-compile and nothing to emulate.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

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

ENV DOTNET_EnableDiagnostics=0 \
    ASPNETCORE_ENVIRONMENT=Production

# jq reads the add-on options the Supervisor writes to /data/options.json.
RUN apt-get update \
 && apt-get install --no-install-recommends -y jq \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish ./
COPY hearthsense/run.sh /run.sh
RUN chmod +x /run.sh

# The add-on runs as root because the Supervisor owns /data and manages the container's isolation.
# Outside Home Assistant, use deploy/docker/Dockerfile, which runs unprivileged.
EXPOSE 5080

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD ["dotnet", "/app/HearthSense.Api.dll", "--healthcheck"]

ENTRYPOINT ["/run.sh"]
