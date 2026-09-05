FROM ghcr.io/denoland/deno AS deno
FROM ghcr.io/ggml-org/whisper.cpp:main AS whisper

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
COPY --from=whisper /app/build/ /app/build/
RUN apt-get update \
    && apt-get install --yes --no-install-recommends \
        ca-certificates \
        bzip2 \
        curl \
        ffmpeg \
        libgomp1 \
        python3 \
        python3-venv \
    && python3 -m venv /opt/yt-dlp \
    && /opt/yt-dlp/bin/pip install --no-cache-dir "yt-dlp[default,curl-cffi]" \
    && test -x /app/build/bin/whisper-cli \
    && /app/build/bin/whisper-cli --help >/dev/null \
    && ln -s /app/build/bin/whisper-cli /usr/local/bin/whisper-cli \
    && mkdir -p /opt/whisper/models \
    && curl --fail --location --silent --show-error \
        --output /opt/whisper/models/ggml-small.bin \
        https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin \
    && curl --fail --location --silent --show-error \
        --output /opt/whisper/models/ggml-silero-v6.2.0.bin \
        https://huggingface.co/ggml-org/whisper-vad/resolve/main/ggml-silero-v6.2.0.bin \
    && echo "2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987  /opt/whisper/models/ggml-silero-v6.2.0.bin" | sha256sum --check \
    && mkdir -p /opt/sherpa-onnx \
    && curl --fail --location --silent --show-error \
        --output /tmp/sherpa-segmentation.tar.bz2 \
        https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-segmentation-models/sherpa-onnx-pyannote-segmentation-3-0.tar.bz2 \
    && echo "24615ee884c897d9d2ba09bb4d30da6bb1b15e685065962db5b02e76e4996488  /tmp/sherpa-segmentation.tar.bz2" | sha256sum --check \
    && tar --extract --bzip2 --file /tmp/sherpa-segmentation.tar.bz2 --directory /opt/sherpa-onnx \
    && mv /opt/sherpa-onnx/sherpa-onnx-pyannote-segmentation-3-0 /opt/sherpa-onnx/speaker-segmentation \
    && curl --fail --location --silent --show-error \
        --output /opt/sherpa-onnx/nemo_en_titanet_large.onnx \
        https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/nemo_en_titanet_large.onnx \
    && echo "d51abcf31717ef28162f26acb9d44dd4127c3d44c9b8624f699f3425daca8e77  /opt/sherpa-onnx/nemo_en_titanet_large.onnx" | sha256sum --check \
    && mkdir -p /data/media \
    && chown -R app:app /data \
    && rm -rf /tmp/sherpa-segmentation.tar.bz2 /var/lib/apt/lists/*

ENV PATH="/opt/yt-dlp/bin:${PATH}" \
    ASPNETCORE_URLS="http://+:8080"

COPY --from=build --chown=app:app /app/publish ./
COPY docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
RUN chmod 0755 /usr/local/bin/docker-entrypoint.sh

EXPOSE 8080

ENTRYPOINT ["docker-entrypoint.sh"]
CMD ["dotnet", "HoloScoop.dll"]
