FROM ghcr.io/denoland/deno:bin-2.9.5 AS deno

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

COPY HoloScoop.csproj ./
RUN dotnet restore HoloScoop.csproj

COPY . ./
RUN dotnet publish HoloScoop.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
WORKDIR /app

USER root
COPY --from=deno /deno /usr/local/bin/deno
RUN apt-get update \
    && apt-get install --yes --no-install-recommends \
        ca-certificates \
        ffmpeg \
        python3 \
        python3-venv \
    && python3 -m venv /opt/yt-dlp \
    && /opt/yt-dlp/bin/pip install --no-cache-dir "yt-dlp[default]" \
    && mkdir -p /data/media \
    && chown -R app:app /data \
    && rm -rf /var/lib/apt/lists/*

ENV PATH="/opt/yt-dlp/bin:${PATH}" \
    ASPNETCORE_URLS="http://+:8080"

COPY --from=build --chown=app:app /app/publish ./

USER app
EXPOSE 8080

ENTRYPOINT ["dotnet", "HoloScoop.dll"]
