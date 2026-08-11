# Fortnite Festival Score Tracker

Fortnite Festival Score Tracker (FST) continuously collects Epic Festival
leaderboards and preserves scores across seasonal resets. The public web app
exposes songs, players, bands, histories, rivals, rankings, statistics, shop
data, and improvement notifications from a safely published PostgreSQL
generation.

## Architecture

```text
Epic APIs/CDN
    ^
    |  per-request HTTP proxy selection when configured
Gluetun VPN pool <--- fstworker ---> PostgreSQL <--- fstservice <--- festivalweb/browser
```

| Component | Path | Responsibility |
|---|---|---|
| API and worker host | `FSTService/` | ASP.NET Core API, hosted workers, publication and persistence |
| Public web app | `FortniteFestivalWeb/` | React/Vite browser application and Nginx image |
| Shared .NET code | `FortniteFestival.Core/` | Epic/domain/path logic and compatibility code |
| Shared TypeScript packages | `packages/` | Domain/API types, theme tokens, and UI utilities |

Production normally runs the same FSTService image as separate `fstservice`
and `fstworker` roles. PostgreSQL is the source of truth. Candidate scrape data
does not become public until publication validation and an atomic generation
commit succeed.

Start with [`docs/README.md`](docs/README.md) or the
[system overview](docs/architecture/system-overview.md).

## Local validation

Service:

```bash
dotnet test FSTService.Tests/FSTService.Tests.csproj
dotnet build FSTService/FSTService.csproj -c Release
```

Web:

```bash
cd FortniteFestivalWeb
corepack yarn install --immutable
corepack yarn test:unit
corepack yarn build
```

Documentation:

```bash
node tools/check-docs.mjs
```

More commands and targeted suites are listed in
[`docs/testing/README.md`](docs/testing/README.md).

## Development server

Run the Vite app against an existing API:

```bash
cd FortniteFestivalWeb
VITE_API_BASE=http://127.0.0.1:8080 corepack yarn dev --port 5173
```

Use `VITE_API_KEY` only in the shell or an ignored `.env.local` when the target
requires one.

## Deployment and credentials

Repository Compose files are templates. The production Compose project is
owned from:

```text
/home/sfenton/Docker/FestivalServiceTracker
```

Never commit PostgreSQL, API, Epic, MIDI/path, VPN, or reporting credentials.
See:

- [Deployment topology](docs/operations/deployment.md)
- [VPN proxy pool and rationale](docs/operations/vpn-proxy-pool.md)
- [Configuration](docs/reference/configuration.md)
- [Live-safety rules](docs/operations/live-safety.md)

## Contributing

Read [`CONTRIBUTING.md`](CONTRIBUTING.md) and the scoped `AGENTS.md` file for
the component you change.

Documentation is part of every change. Update the canonical page when
documented behavior changes, create and index a page for a new documentable
area, and report either the updated paths or a specific no-impact reason.
