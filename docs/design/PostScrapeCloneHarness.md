# Post-Scrape Clone Harness

`FSTService.Harnesses/PostScrapeCloneHarness` is the benchmarking and validation harness for post-scrape PostgreSQL work. It exists to answer three questions safely:

1. Can we clone the exact live slice we care about into a disposable database?
2. Can we compare source and clone contents deterministically before and after changes?
3. Can we run individual downstream phases against that clone and measure them in isolation?

The harness never mutates the source database.

## Commands

| Command | Purpose |
|---|---|
| `inspect` | Count rows for a preset or filtered table set without copying anything. |
| `clone` | Copy a filtered preset from source Postgres into a disposable target database. |
| `compare` | Fingerprint source and target rows over the compatible shared columns. |
| `run-rankings` | Execute the rankings pipeline directly against the selected database. |
| `run-rivals` | Execute rivals generation for registered users or an explicit account slice. |
| `run-leaderboard-rivals` | Execute leaderboard rivals generation. |
| `run-player-stats` | Execute player stats tier generation. |
| `run-precompute` | Execute API response precompute for all registered users or an explicit account slice. |
| `run-band-rankings` | Rebuild derived band team rankings with a selectable write strategy. |
| `run-band-extraction` | Execute post-scrape band extraction. |

All commands can emit JSON artifacts with `--out`.

## Presets

The harness supports preset-based table selection so a clone or compare run can stay tightly scoped.

| Preset | Intended scope |
|---|---|
| `post-scrape` | Full downstream pipeline slice: core scrape inputs plus rankings, rivals, player stats, cache, band, and status tables. |
| `enrichment` | Song and leaderboard data needed for enrichment-only work. |
| `refresh` | Registered-user refresh and related progress/status tables. |
| `rankings` | Rankings inputs and outputs. |
| `rivals` | Rivals and leaderboard-rivals inputs and outputs. |
| `player-stats` | Player stats inputs and outputs. |
| `precompute` | Precompute inputs and cache tables. |
| `band` | Band extraction and derived band tables. |

`--tables` overrides the preset with an explicit table list.

## Typical Workflow

### 1. Inspect the source slice

Use `inspect` first when you want row counts for a filtered scope without paying clone cost.

```bash
dotnet run --project FSTService.Harnesses/PostScrapeCloneHarness/PostScrapeCloneHarness.csproj -- \
  inspect \
  --pg "<source-pg>" \
  --preset post-scrape \
  --account-ids "<csv>" \
  --out harness-output/source-inspect.json
```

### 2. Clone into a disposable target database

```bash
dotnet run --project FSTService.Harnesses/PostScrapeCloneHarness/PostScrapeCloneHarness.csproj -- \
  clone \
  --source-pg "<source-pg>" \
  --target-pg "<target-pg>" \
  --preset post-scrape \
  --account-ids "<csv>" \
  --out harness-output/clone.json
```

By default `clone`:

- ensures target schema with `DatabaseInitializer.EnsureSchemaAsync()`
- truncates the selected target tables with `RESTART IDENTITY CASCADE`
- streams rows via Npgsql binary COPY

### 3. Compare source and target

```bash
dotnet run --project FSTService.Harnesses/PostScrapeCloneHarness/PostScrapeCloneHarness.csproj -- \
  compare \
  --source-pg "<source-pg>" \
  --target-pg "<target-pg>" \
  --preset post-scrape \
  --account-ids "<csv>" \
  --out harness-output/compare.json
```

`compare` fingerprints sorted JSON payloads over the compatible shared columns between source and target. It is accurate but can be expensive on large slices.

Measured example on the filtered smoke clone:

- `leaderboard_entries` compare over 6,351,605 matched rows took about 96.7s

Use compare mode selectively on large tables.

### 4. Run one downstream phase at a time

Examples:

```bash
dotnet run --project FSTService.Harnesses/PostScrapeCloneHarness/PostScrapeCloneHarness.csproj -- \
  run-rankings --pg "<target-pg>" --out harness-output/run-rankings.json

dotnet run --project FSTService.Harnesses/PostScrapeCloneHarness/PostScrapeCloneHarness.csproj -- \
  run-band-rankings --pg "<target-pg>" --band-types "Band_Duets" --write-mode combo-batched --out harness-output/run-band-rankings.json

dotnet run --project FSTService.Harnesses/PostScrapeCloneHarness/PostScrapeCloneHarness.csproj -- \
  run-precompute --pg "<target-pg>" --account-ids "<csv>" --out harness-output/run-precompute.json
```

The downstream runners emit before/after row counts for their output tables. Rankings, band rankings, and precompute also emit timing breakdowns.

`run-band-rankings` supports these A/B switches:

- `--band-types` to target one or more band types instead of rebuilding all three
- `--write-mode combo-batched|monolithic` to compare the optimized batched writer against the legacy one-shot insert path
- `--command-timeout-seconds`, `--analyze-staging`, and `--disable-synchronous-commit` for controlled write-path experiments

## Benchmark Environment Used In This Workstream

Most post-scrape measurements in this optimization pass used:

- disposable database: `fst_clone_filtered_smoke`
- Docker network: `festivalservicetracker_default`
- containerized SDK execution via `mcr.microsoft.com/dotnet/sdk:9.0`
- either five explicit high-coverage accounts or a twenty-account explicit precompute slice

The harness works outside Docker too, but the containerized path keeps the toolchain and network identical to the running stack.

## Current Measured Reference Points

These are the kept wins from the 2026-04-19 optimization pass on `fst_clone_filtered_smoke`.

### Rivals

- steady-state explicit rerun: about `1.92s -> 662.8ms`

### Leaderboard rivals

- steady-state explicit rerun: about `1.15s -> 440.0ms`

### Band extraction

- steady-state rerun: about `7.53s -> 1.53s`
- cold run: about `7.67s -> 1.43s`

### Explicit precompute, top-20 rerun slice

- baseline: about `12.818s`
- after radius-0 leaderboard-neighborhood short-circuit: about `12.542s`
- after shared-input cache plus step timing: about `6.170s`
- after removing redundant stored-rank lookup from `PrecomputeSinglePlayer()`: about `2.536s`

The ranked-account-count cache experiment was not kept because it only moved the same rerun from about `2.536s` to `2.506s`, which was too small and noisy to justify extra shared state.

## Artifact Conventions

Artifacts are typically written under `harness-output/` and should be named for:

- command
- scope
- experiment name
- run type (`cold`, `rerun`, `fixed`, and so on)

Examples:

- `post-scrape-run-rivals-explicit-copy-rerun-fixed.json`
- `post-scrape-run-precompute-explicit-top20-no-stored-rank-rerun.json`
- `post-scrape-run-band-rankings-duets-combo-batched-rerun.json`
- `post-scrape-run-band-extraction-rerun-parallel.json`

This keeps A/B runs diffable without opening each file.

## Guardrails

- Never point `clone` target at the live source database.
- Prefer explicit account slices when iterating on downstream phases with large source tables.
- Keep source access read-only.
- Validate a clone with `compare` before trusting benchmark output.
- Keep only measured wins. Revert changes that add complexity without a clear timing improvement.