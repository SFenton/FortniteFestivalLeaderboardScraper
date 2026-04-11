# Phase-Selective Scraping

Control which phases of the scrape pipeline execute per pass using CLI flags. When no phase flags are specified, the full pipeline runs (current default behavior).

## CLI Flags

### Group Flags

These enable a logical group of phases with a single flag:

| Flag | Description |
|------|-------------|
| `--solo-scrape` | Run solo instrument V1 scrape + enrichment + registered user refresh (positions 1–3) |
| `--band-scrape` | Run band V1 scrape + post-scrape band scrape + band extraction (positions 1–3) |
| `--solo-leaderboards` | Compute rankings + rivals + player stats + precompute + finalize (positions 4–8) |

### Micro-Phase Flags

Target individual phases for fine-grained control:

| Flag | Enum Member | Chain | Pos |
|------|-------------|-------|-----|
| `--solo-scrape` | `SoloScrape` | Solo | 1 |
| `--solo-enrichment` | `SoloEnrichment` | Solo | 2 |
| `--solo-refresh-users` | `SoloRefreshUsers` | Solo | 3 |
| `--solo-leaderboards` | `SoloRankings` | Solo | 4 |
| `--solo-rivals` | `SoloRivals` | Solo | 5 |
| `--solo-player-stats` | `SoloPlayerStats` | Solo | 6 |
| `--solo-precompute` | `SoloPrecompute` | Solo | 7 |
| `--solo-finalize` | `SoloFinalize` | Solo | 8 |
| `--band-scrape` | `BandScrape` | Band | 1 |
| `--band-post-scrape` | `BandScrapePhase` | Band | 2 |
| `--band-extraction` | `BandExtraction` | Band | 3 |

> **Note:** `--solo-scrape`, `--solo-leaderboards`, and `--band-scrape` act as both group flags (expanding to their downstream phases) and micro-phase flags (setting their individual bit). All other flags set only their individual phase.

Flags are **additive** — combine them freely. They modify each scrape pass, not the loop. Combine with `--once` for one-shot execution.

## Phase Inventory

### Solo Chain (positions 1–8)

| Pos | Enum Member | Group Flag | Micro Flag | What It Does |
|-----|-------------|------------|------------|-------------|
| 1 | `SoloScrape` | `--solo-scrape` | `--solo-scrape` | V1 alltime scrape for 6 solo instruments, spool flush, index rebuild, score change detection |
| 2 | `SoloEnrichment` | `--solo-scrape` | `--solo-enrichment` | Rank recomputation, FirstSeenSeason binary search, account name resolution, pruning |
| 3 | `SoloRefreshUsers` | `--solo-scrape` | `--solo-refresh-users` | V2 batch lookups for registered users, backfill + history recon integration |
| 4 | `SoloRankings` | `--solo-leaderboards` | `--solo-leaderboards` | Per-instrument stats, composite rankings, combo rankings, daily history snapshots |
| 5 | `SoloRivals` | `--solo-leaderboards` | `--solo-rivals` | Per-song ±50 rank rivals + leaderboard-wide rivals |
| 6 | `SoloPlayerStats` | `--solo-leaderboards` | `--solo-player-stats` | Leeway-tiered stats per instrument + overall |
| 7 | `SoloPrecompute` | `--solo-leaderboards` | `--solo-precompute` | Cache player + leaderboard API responses to PostgreSQL |
| 8 | `SoloFinalize` | `--solo-leaderboards` | `--solo-finalize` | WAL checkpoint + pre-warm rankings cache |

### Band Chain (positions 1–3)

| Pos | Enum Member | Group Flag | Micro Flag | What It Does |
|-----|-------------|------------|------------|-------------|
| 1 | `BandScrape` | `--band-scrape` | `--band-scrape` | V1 alltime scrape for Band_Duets/Trios/Quad via BandPageFetcher |
| 2 | `BandScrapePhase` | `--band-scrape` | `--band-post-scrape` | Bespoke post-scrape V1 band scrape using SharedDopPool at low priority |
| 3 | `BandExtraction` | `--band-scrape` | `--band-extraction` | SQL extraction from band_members_json → band tables |

## Intermediary Filling

When multiple flags enable phases on the solo chain, all phases between the lowest and highest enabled position are automatically filled in. This prevents gaps in the dependency chain.

**Example:** `--solo-scrape --solo-leaderboards` enables positions 1 and 4–8. The resolver fills positions 2–3, resulting in all 8 solo phases running.

## Flag Expansion Examples

### Group flags

| CLI Args | Solo Phases | Band Phases |
|----------|-------------|-------------|
| *(none)* | All (1–8) | All (1–3) |
| `--solo-scrape` | 1–3 | — |
| `--solo-leaderboards` | 4–8 | — |
| `--solo-scrape --solo-leaderboards` | 1–8 | — |
| `--band-scrape` | — | 1–3 |
| `--band-scrape --solo-leaderboards` | 4–8 | 1–3 |
| `--solo-scrape --band-scrape` | 1–3 | 1–3 |
| `--solo-scrape --band-scrape --solo-leaderboards` | 1–8 | 1–3 |

### Micro-phase flags

| CLI Args | Solo Phases | Band Phases |
|----------|-------------|-------------|
| `--solo-enrichment` | 2 only | — |
| `--solo-enrichment --solo-leaderboards` | 2–8 (gap filled) | — |
| `--solo-refresh-users --solo-rivals` | 3–5 (4 filled) | — |
| `--solo-precompute` | 7 only | — |
| `--band-extraction` | — | 3 only |
| `--band-post-scrape` | — | 2 only |
| `--solo-enrichment --band-scrape` | 2 only | 1–3 |

## Interaction with Existing Flags

Phase flags modify the scrape pass contents. They are combinable with:

- `--once` — Run the selected phases once, then exit.
- Instrument toggles (`QueryLead`, `QueryDrums`, etc.) — Further filter which instruments are scraped within the solo scrape phase.

Phase flags are **not compatible** with one-shot mode flags that bypass the scrape loop entirely:

- `--api-only`, `--setup`, `--resolve-only`, `--backfill-only`, `--test`, `--precompute`

### `--band-scrape` vs `EnableBandScraping`

The `--band-scrape` CLI flag overrides `EnableBandScraping=false` in appsettings, since the CLI intent is explicit. When no phase flags are specified, the `EnableBandScraping` config setting controls whether band phases run in the default full pipeline.

## Implementation

- **Enum:** `ScrapePhase` (`[Flags]`) in `FSTService/ScrapePhase.cs`
- **Resolver:** `ScrapePhaseResolver.Resolve()` handles group expansion + intermediary filling
- **Options:** `ScraperOptions.EnabledPhases` / `ScraperOptions.ResolvedPhases`
- **Gating:** `ScraperWorker.RunScrapePassAsync`, `ScrapeOrchestrator.RunAsync`, `PostScrapeOrchestrator.RunAsync`

## Startup Logging

When phase flags are active, the service logs the resolved phase set at pass start:

```
Phase-selective mode: SoloRankings | SoloRivals | SoloPlayerStats | SoloPrecompute | SoloFinalize
```

When no flags are specified: no additional log line (full pipeline is implied).
