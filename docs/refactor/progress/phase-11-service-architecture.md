# Phase 11: FSTService Architecture

**Status:** ⬜ Not Started
**Depends on:** Nothing
**Parallel with:** Anything

## Goal
Decompose ScraperWorker monolith, split ApiEndpoints, extract shared auth to Core, fix database issues.

## Steps

### 11.1 — Extract Auth to Core
- [ ] Create `FortniteFestival.Core/Auth/EpicDeviceAuth.cs`
- [ ] Create `FortniteFestival.Core/Auth/EpicTokenRefresh.cs`
- [ ] Create `FortniteFestival.Core/Auth/IDeviceAuthStore.cs` interface
- [ ] Move `FileDeviceAuthStore.cs` to Core
- [ ] Refactor FSTService EpicAuthService → consume Core auth
- [ ] Refactor PercentileService EpicTokenManager → consume Core auth
- [ ] Delete duplicated auth code from PercentileService (~300 lines)

### 11.2 — Decompose ScraperWorker
- [ ] Create `ScrapeOrchestrator.cs` (phases 1-4: auth, catalog, paths, global scrape)
- [ ] Create `EnrichmentOrchestrator.cs` (phases 5-8: first-seen, names, personal DB, refresh)
- [ ] Create `BackfillOrchestrator.cs` (phases 9-10: backfill, history recon)
- [ ] Create `ScraperCoordinator.cs` (~100 lines, sequences orchestrators)
- [ ] Delete original ScraperWorker.cs (1,020 lines)

### 11.3 — Split ApiEndpoints
- [ ] Create `SongEndpoints.cs` (/api/songs, /api/leaderboard/*)
- [ ] Create `PlayerEndpoints.cs` (/api/player/*)
- [ ] Create `RivalsEndpoints.cs` (/api/player/*/rivals/*)
- [ ] Create `AdminEndpoints.cs` (/api/register, /api/backfill/*, /api/progress)
- [ ] Refactor to use `app.MapGroup()` pattern
- [ ] Delete monolithic ApiEndpoints.cs (1,440 lines)

### 11.4 — Database Fixes
- [ ] Extract PRAGMA setup to `SqlitePragmaHelper.SetupOnce(conn)` (~80 lines consolidated)
- [ ] Add missing index: `ScoreHistory(ChangedAt)`
- [ ] Add missing index: `LeaderboardEntries(AccountId, Season, Score)`
- [ ] Add missing index: `UserRivals(RivalAccountId, UserId)` (reverse)
- [ ] Fix double materialization in MetaDatabase pagination
- [ ] Fix Rank=0 ambiguity → use NULL default
- [ ] Fix UPSERT overwriting EndTime with NULL → COALESCE
- [ ] Rename: `SeasonRank` → `SeasonRankFinal`, `AllTimeRank` → `AllTimeRankAtLookup`

### 11.5 — HTTP Client Consolidation
- [ ] Extend ResilientHttpExecutor or create shared EpicHttpClient in Core
- [ ] Refactor FSTService HTTP calls → use shared client
- [ ] Refactor PercentileService HTTP calls → use shared client

### 11.6 — Cleanup
- [ ] Add `Deprecation` header to `POST /api/register`
- [ ] Delete diagnostic hardcoded lookup in ScraperWorker (song "092c2537")
- [ ] Audit/delete vestigial migration methods in InstrumentDatabase
- [ ] Delete nested index drop+recreate in InstrumentDatabase

## Verification Checks

- [ ] `dotnet test FSTService.Tests` — all pass
- [ ] `dotnet test PercentileService.Tests` — all pass
- [ ] Coverage remains ≥ 94% (FSTService) / ≥ 95% (PercentileService)
- [ ] Docker build succeeds for both services
- [ ] Scrape loop completes successfully in --once mode
