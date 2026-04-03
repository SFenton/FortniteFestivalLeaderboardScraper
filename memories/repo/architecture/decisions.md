# Architecture Decisions

> Last updated: 2026-04-03
> Source: Full-org research deployment (all 37 agents)

## Key Decisions Discovered

### D1: No ORM — Raw Npgsql Everywhere
- All SQL is hand-written, parameterized Npgsql
- Enables COPY binary bulk writes, partition-aware queries, fine-grained timeout control
- Trade-off: more boilerplate, but full control over PostgreSQL features

### D2: Singleton-Heavy DI
- Almost all services registered as singletons (long-running service model)
- Only `SongProcessingMachine` is transient (disposable per-cycle)
- Keyed singletons for per-domain caches with distinct TTLs

### D3: AIMD Congestion Control for Scraping
- TCP-style adaptive concurrency (additive increase +16, multiplicative decrease ×0.75)
- 500-request evaluation windows, priority lanes (high/low at 80/20 split)
- Handles Epic CDN rate limiting gracefully

### D4: Three-Tier Caching Architecture
- Tier 1: Precomputed in-memory byte arrays (<1ms, rebuilt after each scrape)
- Tier 2: Keyed TTL caches with SHA-256 ETags + 304 support (2-5 min)
- Tier 3: On-demand DB queries (10-500ms fallback)

### D5: useStyles() Inline Pattern (Web)
- CSS Modules migration completed (Phase 16). Only 5 modules remain for CSS-only features
- Theme tokens are JS constants from @festival/theme, not CSS custom properties
- All style objects memoized via useMemo

### D6: HashRouter for SPA
- HashRouter avoids server-side routing configuration
- URL search params encoded per-page for bookmarkable state
- 14 lazy-loaded routes, only SongsPage eager

### D7: Instrument-Partitioned Tables
- PostgreSQL LIST partitioning on 5 tables by instrument (6 partitions each)
- Enables partition pruning since all queries filter by instrument
- 28 logical tables, ~58 physical tables

### D8: Pipelined Persistence
- Bounded channels per instrument for zero cross-instrument contention
- 10-item batched transactions with synchronous_commit=off
- COPY binary for batches >50, prepared statements for ≤50

### D9: Feature Flags
- 6 flags: shop, rivals, compete, leaderboards, firstRun, difficulty
- Server: FeatureOptions.cs → /api/features endpoint
- Client: FeatureFlagsContext.tsx → FeatureGate component

### D10: Dual Rival Systems
- Per-song rivals: neighborhood scan ±50 ranks, weighted scoring
- Leaderboard rivals: ±10 neighbors × 5 rank methods
- Both run in parallel post-rankings in scrape pipeline

## Cross-Cutting Concerns Discovered

### Security Issues (from security audit)
- HIGH: Dev credentials in appsettings.json (prod uses env vars)
- MEDIUM: 3 SQL string interpolations in InstrumentDatabase.cs (int types, but violates defense-in-depth)
- MEDIUM: No security headers in nginx.conf (CSP, HSTS, X-Frame-Options)
- MEDIUM: Rate limit policies all identical (100 req/s) — no tiered protection

### API Contract Misalignments (from api-contract audit)
- 5 misalignments found, all MEDIUM/LOW severity
- Missing fields in SyncStatusResponse and RivalDetailResponse
- Default parameter disagreements (rankBy, pageSize)

### Performance Opportunities (from performance audit)
- No Vite vendor chunk splitting
- Unvirtualized lists on several pages
- 300s ranking query needs EXPLAIN monitoring
- No CDN/edge layer

### Testing Gaps (from testing-vteam audit)
- Web CI coverage job is **disabled**
- No API contract testing across repos
- Compete page is **untested**
- E2E scope limited to first-run flows

### Accessibility Gaps (from web-principal-designer audit)
- No aria-live regions
- No focus trap in modals
- 12 role="button" divs should be <button> elements
- No skip navigation link
