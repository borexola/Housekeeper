# syntax=docker/dockerfile:1

########## Build ##########
# Always compile on the runner's own architecture: the published output is portable IL, so nothing has to be
# cross-compiled and an arm64 runner never emulates x64 (or vice versa) to build it.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore on its own layer so source edits do not re-download packages.
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

# Inside the container the API binds 0.0.0.0, so HOUSEKEEPER_API_TOKEN becomes mandatory —
# see the port mapping in compose.yaml, which only publishes to host loopback.
# /data holds the database and anything saved from the settings page, so it must be a persistent volume.
# The app stores and compares everything in UTC; TZ only sets the process's own local time (log stamps and
# any TimeZoneInfo.Local use). UTC is a sane default; override per host with -e TZ=Area/City or in compose.
ENV DOTNET_EnableDiagnostics=0 \
    ASPNETCORE_ENVIRONMENT=Production \
    TZ=UTC \
    HOUSEKEEPER__Api__BindAddress=0.0.0.0 \
    HOUSEKEEPER__Storage__Path=/data/housekeeper.db

RUN useradd --uid 10001 --create-home --shell /usr/sbin/nologin housekeeper \
 && mkdir -p /data \
 && chown -R housekeeper:housekeeper /app /data

COPY --from=build --chown=housekeeper:housekeeper /app/publish ./

USER 10001
EXPOSE 5080
VOLUME ["/data"]

# bash can open a TCP socket by itself, so the probe costs one shell rather than a second .NET runtime
# every interval — which mattered on the small boards Home Assistant tends to run on.
HEALTHCHECK --interval=60s --timeout=5s --start-period=20s --retries=3 \
    CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/${HOUSEKEEPER__Api__Port:-5080} && printf 'GET /health HTTP/1.0\\r\\nHost: localhost\\r\\n\\r\\n' >&3 && head -n 1 <&3 | grep -q ' 200 '"]

ENTRYPOINT ["dotnet", "Housekeeper.Api.dll"]
