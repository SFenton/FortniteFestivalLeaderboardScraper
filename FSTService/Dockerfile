# ── Stage 1: Build React web app ──────────────────────────────
FROM node:20-slim AS webapp
WORKDIR /src
COPY packages/core/ packages/core/
COPY FortniteFestivalWeb/package.json FortniteFestivalWeb/yarn.lock FortniteFestivalWeb/
WORKDIR /src/FortniteFestivalWeb
# Use node-modules linker in Docker (local dev uses PnP with global cache)
RUN corepack enable && echo 'nodeLinker: node-modules' > .yarnrc.yml && yarn install
COPY FortniteFestivalWeb/ .
# Overwrite .yarnrc.yml again after COPY brings in project files
RUN echo 'nodeLinker: node-modules' > .yarnrc.yml && yarn vite build --outDir /webapp-dist

# ── Stage 2: Build .NET service ──────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy project files first for layer caching on restore
COPY FortniteFestival.Core/FortniteFestival.Core.csproj FortniteFestival.Core/
COPY FSTService/FSTService.csproj FSTService/
RUN dotnet restore FSTService/FSTService.csproj

# Copy source
COPY FortniteFestival.Core/ FortniteFestival.Core/
COPY FSTService/ FSTService/

# Copy built React app into wwwroot/app BEFORE publish so the static assets manifest includes them
COPY --from=webapp /webapp-dist/ FSTService/wwwroot/

RUN dotnet publish FSTService/FSTService.csproj -c Release --no-restore -o /app

# ── Stage 3: Runtime ─────────────────────────────────────────
# Noble (Ubuntu 24.04) provides glibc 2.39+ and libstdc++ 3.4.32 required by CHOpt.
FROM mcr.microsoft.com/dotnet/aspnet:9.0-noble AS runtime
WORKDIR /app

# Install runtime dependencies for CHOpt (Qt6 GUI app that generates path images)
RUN apt-get update && apt-get install -y --no-install-recommends \
    libpng16-16t64 libglx0 libopengl0 libegl1 libfontconfig1 \
    libglib2.0-0t64 libxkbcommon0 libgl1 libfreetype6 libdbus-1-3 \
    && rm -rf /var/lib/apt/lists/*

# Create non-root user for container hardening (UID 1000 matches the previous
# Debian-based image so existing volume files remain writable).
RUN groupadd --gid 1000 appuser && \
    useradd --uid 1000 --gid 1000 --no-create-home --home-dir /app --shell /usr/sbin/nologin appuser

# Download CHOpt CLI for path generation (Linux x64) — pinned in tools/chopt-cli-linux/
COPY tools/chopt-cli-linux/ /app/tools/
RUN chmod +x /app/tools/CHOpt

# Copy published output
COPY --from=build /app .

# Data directory (DB files + device-auth.json) is a volume mount.
# Create it and set ownership before switching to non-root user.
RUN mkdir -p /app/data && chown -R appuser:appuser /app/data

USER appuser

VOLUME /app/data
EXPOSE 8080

ENTRYPOINT ["dotnet", "FSTService.dll"]
