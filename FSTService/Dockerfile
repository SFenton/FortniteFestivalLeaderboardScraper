# ── Stage 1: Build React web app ──────────────────────────────
FROM node:20-slim AS webapp
WORKDIR /src
COPY packages/core/ packages/core/
COPY FortniteFestivalWeb/package.json FortniteFestivalWeb/yarn.lock FortniteFestivalWeb/.yarnrc.yml FortniteFestivalWeb/
COPY FortniteFestivalWeb/.yarn/ FortniteFestivalWeb/.yarn/
COPY FortniteFestivalWeb/.pnp.cjs FortniteFestivalWeb/.pnp.cjs
WORKDIR /src/FortniteFestivalWeb
RUN corepack enable && yarn install --immutable
COPY FortniteFestivalWeb/ .
RUN yarn vite build

# ── Stage 2: Build .NET service ──────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy project files first for layer caching on restore
COPY FortniteFestival.Core/FortniteFestival.Core.csproj FortniteFestival.Core/
COPY FSTService/FSTService.csproj FSTService/
RUN dotnet restore FSTService/FSTService.csproj

# Copy source and publish
COPY FortniteFestival.Core/ FortniteFestival.Core/
COPY FSTService/ FSTService/

# Copy built React app into wwwroot/app before publish
COPY --from=webapp /src/FSTService/wwwroot/app/ FSTService/wwwroot/app/

RUN dotnet publish FSTService/FSTService.csproj -c Release --no-restore -o /app

# ── Stage 2: Runtime ─────────────────────────────────────────
# Using aspnet base image to support the future HTTP API layer.
# For a pure BackgroundService, dotnet/runtime:9.0 would suffice.
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Create non-root user for container hardening
RUN adduser --disabled-password --gecos "" --home /app appuser

# Copy published output
COPY --from=build /app .

# Data directory (DB files + device-auth.json) is a volume mount.
# Create it and set ownership before switching to non-root user.
RUN mkdir -p /app/data && chown -R appuser:appuser /app/data

USER appuser

VOLUME /app/data
EXPOSE 8080

ENTRYPOINT ["dotnet", "FSTService.dll"]
