# syntax=docker/dockerfile:1
#
# The Home Assistant add-on image. Built from the repository root, not from this folder:
#
#   docker build -f housekeeper/Dockerfile -t housekeeper-addon .
#
# The release workflow builds one image per architecture and pushes them where config.yaml points.

########## Build ##########
# Always compile on the runner's own architecture. The published output is portable IL and carries the
# native SQLite binaries for every runtime, so there is nothing to cross-compile and nothing to emulate.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Housekeeper.slnx ./
COPY src/Housekeeper.Core/Housekeeper.Core.csproj src/Housekeeper.Core/
COPY src/Housekeeper.Api/Housekeeper.Api.csproj src/Housekeeper.Api/
RUN dotnet restore src/Housekeeper.Api/Housekeeper.Api.csproj

COPY .editorconfig ./
COPY src/ src/
RUN dotnet publish src/Housekeeper.Api/Housekeeper.Api.csproj \
        -c Release -o /app/publish --no-restore /p:UseAppHost=false

########## Runtime ##########
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# The app stores and compares everything in UTC; TZ only sets the process's own local time, and the
# Supervisor does not set one for add-ons, so pin it rather than inheriting whatever the host carries.
ENV DOTNET_EnableDiagnostics=0 \
    ASPNETCORE_ENVIRONMENT=Production \
    TZ=UTC

# jq reads the add-on options the Supervisor writes to /data/options.json.
RUN apt-get update \
 && apt-get install --no-install-recommends -y jq \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish ./
COPY housekeeper/run.sh /run.sh
RUN chmod +x /run.sh

# The add-on runs as root because the Supervisor owns /data and manages the container's isolation.
# Outside Home Assistant, use deploy/docker/Dockerfile, which runs unprivileged.
EXPOSE 5080

# bash can open a TCP socket by itself, so the probe costs one shell rather than a second .NET runtime
# every interval — which mattered on the small boards Home Assistant tends to run on.
HEALTHCHECK --interval=60s --timeout=5s --start-period=20s --retries=3 \
    CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/${HOUSEKEEPER__Api__Port:-5080} && printf 'GET /health HTTP/1.0\\r\\nHost: localhost\\r\\n\\r\\n' >&3 && head -n 1 <&3 | grep -q ' 200 '"]

ENTRYPOINT ["/run.sh"]
