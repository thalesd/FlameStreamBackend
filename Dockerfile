# Sága backend container (Linux/amd64 — built to run under Docker in WSL2).
#
# Two things the stock ASP.NET image doesn't give us:
#   1. ffmpeg/ffprobe. Every transcode, scene thumbnail and embedded-subtitle extraction shells
#      out to them by bare name (HlsService, ThumbnailService, SubtitleService, FFprobeService),
#      so they must be on PATH or the app builds fine and then fails on first play.
#   2. A config that isn't Windows-specific. appsettings.json points LibraryRoot/CacheRoot at
#      D:\ and Kestrel's HTTPS cert at a path under the Windows user profile; the final stage
#      replaces it with docker/appsettings.container.json.
#
# HARDWARE ENCODING IS OFF IN HERE, deliberately. appsettings.json runs "Amd", i.e.
# "-hwaccel dxva2 -c:v h264_amf" — DXVA2 is a Windows API and AMF needs the Windows/AMDGPU-PRO
# driver stack. WSL2 exposes only /dev/dxg (no /dev/dri render node), so no VAAPI/AMF encoder is
# reachable from a container here either. The container encodes with libx264 on the CPU.

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Restore as its own layer so ordinary code edits don't re-download the package graph.
COPY SagaBackend.csproj .
RUN dotnet restore SagaBackend.csproj

COPY . .
RUN dotnet publish SagaBackend.csproj -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false


FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final

# tzdata so the /api/castlog timestamps (DateTime.Now) follow $TZ instead of sitting in UTC;
# curl only for the healthcheck below.
RUN apt-get update \
 && apt-get install -y --no-install-recommends ffmpeg tzdata curl \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .
COPY docker/appsettings.container.json ./appsettings.json

# Mount points, created up front so they exist (and are writable) even with nothing mounted over
# them. /media and /app-releases are read-only in normal use; /cache is the only thing the app
# writes — segments, thumbs, extracted .vtt, mediainfo.json and the two SQLite DBs.
RUN mkdir -p /media /cache /app-releases && chown -R $APP_UID /cache /app-releases

USER $APP_UID
EXPOSE 5000

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD curl -fsS http://localhost:5000/api/jobs || exit 1

ENTRYPOINT ["dotnet", "SagaBackend.dll"]
