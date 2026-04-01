# ── Stage 1: Build .NET service ──────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy project files first for layer caching on restore
COPY FortniteFestival.Core/FortniteFestival.Core.csproj FortniteFestival.Core/
COPY FSTService/FSTService.csproj FSTService/
RUN dotnet restore FSTService/FSTService.csproj

# Copy source
COPY FortniteFestival.Core/ FortniteFestival.Core/
COPY FSTService/ FSTService/

RUN dotnet publish FSTService/FSTService.csproj -c Release --no-restore -o /app

# ── Stage 2: Runtime ─────────────────────────────────────────
# Noble (Ubuntu 24.04) provides glibc 2.39+ and libstdc++ 3.4.32 required by CHOpt.
FROM mcr.microsoft.com/dotnet/aspnet:9.0-noble AS runtime
WORKDIR /app

# Install runtime dependencies for CHOpt (Qt6 GUI app that generates path images)
RUN apt-get update && apt-get install -y --no-install-recommends \
    libpng16-16t64 libglx0 libopengl0 libegl1 libfontconfig1 \
    libglib2.0-0t64 libxkbcommon0 libgl1 libfreetype6 libdbus-1-3 \
    && rm -rf /var/lib/apt/lists/*

# Reuse the existing 'ubuntu' user (UID 1000) for container hardening.
# Noble ships with this user pre-created; UID 1000 matches the previous
# Debian-based image so existing volume files remain writable.
RUN usermod --home /app --shell /usr/sbin/nologin ubuntu

# Download CHOpt CLI for path generation (Linux x64) — pinned in tools/chopt-cli-linux/
COPY tools/chopt-cli-linux/ /app/tools/
RUN chmod +x /app/tools/CHOpt

# Copy published output
COPY --from=build /app .

# Data directory (DB files + device-auth.json) is a volume mount.
# Create it and set ownership before switching to non-root user.
RUN mkdir -p /app/data && chown -R ubuntu:ubuntu /app/data

USER ubuntu

VOLUME /app/data
EXPOSE 8080

ENTRYPOINT ["dotnet", "FSTService.dll"]
