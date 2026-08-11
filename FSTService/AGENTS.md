# FSTService Development Guidelines

## Stack

.NET 9 / ASP.NET Core with BackgroundService and PostgreSQL through Npgsql.
Use parameterized SQL; there is no ORM.

## Conventions

- Nullable reference types and implicit usings
- `async`/`await` with `CancellationToken` propagation
- `ILogger<T>`
- `System.Text.Json`
- `Path.Combine`/`Path.GetFullPath`
- No interpolated SQL

## Architecture

The same binary supports API/frontend, full-worker, registration-sync,
read-only rollout, setup, and one-shot modes. See:

- `docs/components/service-api.md`
- `docs/components/worker.md`
- `docs/architecture/data-publication-flow.md`
- `docs/reference/cli.md`

PostgreSQL is the service source of truth. `InstrumentDatabase` is a logical
per-instrument wrapper over shared PostgreSQL relations, not a set of SQLite
shards.

## API

`FSTService/Api/ApiEndpoints.cs` is the endpoint-group aggregator. Actual routes
live in domain `*Endpoints.cs` files.

Protected endpoints use `X-API-Key`. Public/auth/protected/global limiters
currently share a 100-request-per-second fixed window outside tests.
Publication-bound routes must retain their classification and required surface
contract.

See `docs/reference/api-contract.md`.

## Worker and publication

Candidate scrape state is not public state. Preserve exact catalog selection,
read freeze, writer/phase gates, durable failure isolation, atomic publication,
and post-commit client notification.

Proxy/VPN behavior is worker-only and documented in
`docs/operations/vpn-proxy-pool.md`.

## Testing

```bash
dotnet test FSTService.Tests/FSTService.Tests.csproj
dotnet build FSTService/FSTService.csproj -c Release
```

Use xUnit and existing helpers/fixtures. Keep the CI coverage gate passing.

## Documentation

Follow `.github/instructions/documentation.instructions.md`. Changes to
hosting, routes, middleware, phases, persistence, configuration, flags, or
deployment must update the matching canonical docs in the same change.
