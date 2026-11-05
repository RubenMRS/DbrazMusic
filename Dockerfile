# ---- Build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restaurar primeiro (melhor cache de layers)
COPY MusicBot.csproj ./
COPY libs/ libs/
RUN dotnet restore MusicBot.csproj

COPY . .
RUN dotnet publish MusicBot.csproj -c Release -o /out /p:UseAppHost=false


# ---- Runtime ----
FROM mcr.microsoft.com/dotnet/runtime:10.0

# ffmpeg + bibliotecas nativas da voz (opus, sodium)
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      ffmpeg libopus0 libsodium23 ca-certificates curl unzip tzdata \
 && ln -sf /usr/lib/x86_64-linux-gnu/libopus.so.0 /usr/lib/x86_64-linux-gnu/libopus.so \
 && ln -sf /usr/lib/x86_64-linux-gnu/libsodium.so.23 /usr/lib/x86_64-linux-gnu/libsodium.so \
 && rm -rf /var/lib/apt/lists/*

# yt-dlp (binário oficial, não precisa de Python)
RUN curl -fsSL https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux \
      -o /usr/local/bin/yt-dlp \
 && chmod 0755 /usr/local/bin/yt-dlp

# libdave (encriptação E2EE obrigatória da voz) para Linux x64
RUN curl -fsSL https://github.com/discord/libdave/releases/download/v1.2.2/cpp/libdave-Linux-X64-boringssl.zip \
      -o /tmp/libdave.zip \
 && unzip -j /tmp/libdave.zip 'lib/libdave.so' -d /usr/local/lib \
 && ldconfig \
 && rm -f /tmp/libdave.zip

WORKDIR /app
COPY --from=build /out ./

ENV DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_NOLOGO=1 \
    TZ=Europe/Lisbon

EXPOSE 8080
CMD ["dotnet", "MusicBot.dll"]
