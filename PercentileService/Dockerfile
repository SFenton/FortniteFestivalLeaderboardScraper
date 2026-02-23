# ── Stage 1: Build ────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy project files first for layer caching on restore
COPY FortniteFestival.Core/FortniteFestival.Core.csproj FortniteFestival.Core/
COPY PercentileService/PercentileService.csproj PercentileService/
RUN dotnet restore PercentileService/PercentileService.csproj

# Copy source and publish
COPY FortniteFestival.Core/ FortniteFestival.Core/
COPY PercentileService/ PercentileService/
RUN dotnet publish PercentileService/PercentileService.csproj -c Release --no-restore -o /app

# ── Stage 2: Runtime ─────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Create non-root user for container hardening
RUN adduser --disabled-password --gecos "" --home /app appuser

# Copy published output
COPY --from=build /app .

# Data directory (token credentials) is a volume mount
RUN mkdir -p /app/data && chown -R appuser:appuser /app/data

USER appuser

VOLUME /app/data

ENTRYPOINT ["dotnet", "PercentileService.dll"]
