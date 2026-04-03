# API Contract Registry — Server ↔ Client Alignment

> **Maintained by**: API Contract Agent
> **Last verified**: 2026-04-03
> **Server**: FSTService/Api/ (C# minimal API endpoints)
> **Client**: FortniteFestivalWeb/src/api/client.ts + packages/core/src/api/serverTypes.ts

---

## Route Alignment

### Public Endpoints

| Server Route | Method | Client Method | Query Keys | Status |
|---|---|---|---|---|
| `/api/songs` | GET | `api.getSongs()` | `queryKeys.songs()` | ✅ Aligned |
| `/api/shop` | GET | `api.getShop()` | inline `['shop']` | ✅ Aligned |
| `/api/features` | GET | direct `fetch('/api/features')` in FeatureFlagsContext | inline `['features']` | ✅ Aligned |
| `/api/version` | GET | `api.getVersion()` | `queryKeys.version()` | ✅ Aligned |
| `/api/account/search?q=&limit=` | GET | `api.searchAccounts()` | none (imperative) | ✅ Aligned |
| `/api/leaderboard/{songId}/{instrument}?top=&offset=&leeway=` | GET | `api.getLeaderboard()` | `queryKeys.leaderboard()` | ✅ Aligned |
| `/api/leaderboard/{songId}/all?top=&leeway=` | GET | `api.getAllLeaderboards()` | `queryKeys.allLeaderboards()` | ✅ Aligned |
| `/api/player/{accountId}?songId=&instruments=&leeway=` | GET | `api.getPlayer()` | `queryKeys.player()` | ✅ Aligned |
| `POST /api/player/{accountId}/track` | POST | `api.trackPlayer()` | none (mutation) | ✅ Aligned |
| `/api/player/{accountId}/sync-status` | GET | `api.getSyncStatus()` | `queryKeys.syncStatus()` | ⚠️ DTO Mismatch |
| `/api/player/{accountId}/stats` | GET | `api.getPlayerStats()` | `queryKeys.playerStats()` | ✅ Aligned |
| `/api/player/{accountId}/history?songId=&instrument=` | GET | `api.getPlayerHistory()` | `queryKeys.playerHistory()` | ✅ Aligned |
| `/api/player/{accountId}/rivals` | GET | `api.getRivalsOverview()` | `queryKeys.rivalsOverview()` | ✅ Aligned |
| `/api/player/{accountId}/rivals/suggestions?combo=&limit=` | GET | `api.getRivalSuggestions()` | none (imperative) | ✅ Aligned |
| `/api/player/{accountId}/rivals/all` | GET | `api.getRivalsAll()` | none (imperative) | ⚠️ Shape Mismatch |
| `/api/player/{accountId}/rivals/{combo}` | GET | `api.getRivalsList()` | `queryKeys.rivalsList()` | ✅ Aligned |
| `/api/player/{accountId}/rivals/{combo}/{rivalId}?limit=&offset=&sort=` | GET | `api.getRivalDetail()` | `queryKeys.rivalDetail()` | ⚠️ DTO Incomplete |
| `/api/player/{accountId}/leaderboard-rivals/{instrument}?rankBy=` | GET | `api.getLeaderboardRivals()` | none (useState) | ✅ Aligned |
| `/api/player/{accountId}/leaderboard-rivals/{instrument}/{rivalId}?rankBy=&sort=` | GET | `api.getLeaderboardRivalDetail()` | none (useState) | ⚠️ DTO Mismatch |
| `/api/rankings/{instrument}?rankBy=&page=&pageSize=` | GET | `api.getRankings()` | `queryKeys.rankings()` | ✅ Aligned |
| `/api/rankings/{instrument}/{accountId}` | GET | `api.getPlayerRanking()` | `queryKeys.playerRanking()` | ✅ Aligned |
| `/api/rankings/{instrument}/{accountId}/neighborhood?radius=` | GET | `api.getLeaderboardNeighborhood()` | `queryKeys.leaderboardNeighborhood()` | ✅ Aligned |
| `/api/rankings/composite?page=&pageSize=` | GET | `api.getCompositeRankings()` | `queryKeys.compositeRankings()` | ✅ Aligned |
| `/api/rankings/composite/{accountId}` | GET | `api.getPlayerCompositeRanking()` | `queryKeys.playerCompositeRanking()` | ✅ Aligned |
| `/api/rankings/composite/{accountId}/neighborhood?radius=` | GET | `api.getCompositeNeighborhood()` | `queryKeys.compositeNeighborhood()` | ✅ Aligned |
| `/api/rankings/combo?combo=&rankBy=&page=&pageSize=` | GET | `api.getComboRankings()` | `queryKeys.comboRankings()` | ✅ Aligned |
| `/api/rankings/combo/{accountId}?combo=&rankBy=` | GET | `api.getPlayerComboRanking()` | `queryKeys.playerComboRanking()` | ✅ Aligned |
| `WS /api/ws` | WS | `useShopWebSocket()` | — | ✅ Aligned |

### Server-Only Endpoints (no web client method)

| Server Route | Method | Reason |
|---|---|---|
| `/healthz` | GET | Infrastructure probe |
| `/readyz` | GET | Infrastructure probe |
| `/api/progress` | GET | Not consumed by web app |
| `/api/account/check?username=` | GET | Mobile-only |
| `/api/paths/{songId}/{instrument}/{difficulty}` | GET | Not yet used in web |
| `/api/player/{accountId}/rivals/diagnostics` | GET | Admin (auth required) |
| `/api/player/{accountId}/rivals/{rivalId}/songs/{instrument}` | GET | Not consumed by web (uses combo detail instead) |
| `POST /api/player/{accountId}/rivals/recompute` | POST | Admin (auth required) |
| `/api/rankings/{instrument}/{accountId}/history?days=` | GET | Not yet used in web |
| `/api/rankings/overview?rankBy=&pageSize=` | GET | Not consumed by web |
| `/api/status` | GET | Admin (auth required) |
| `/api/admin/epic-token` | GET | Admin (auth required) |
| `POST /api/admin/shop/refresh` | POST | Admin (auth required) |
| `POST /api/register` | POST | Mobile (auth required) |
| `DELETE /api/register` | DELETE | Mobile (auth required) |
| `/api/firstseen` | GET | Not consumed by web |
| `POST /api/firstseen/calculate` | POST | Admin (auth required) |
| `/api/diag/events` | GET | Diagnostic proxy |
| `/api/diag/leaderboard` | GET | Diagnostic proxy |

### Client methods with no React Query keys

| Client Method | Pattern Used | Notes |
|---|---|---|
| `api.getLeaderboardRivals()` | `useState` + `useEffect` in LeaderboardRivalsTab | Module-level cache instead of React Query |
| `api.getLeaderboardRivalDetail()` | `useState` + `useEffect` in RivalDetailPage | Module-level cache |
| `api.getRivalsAll()` | `useState` in useSuggestions | Called imperatively |
| `api.getRivalSuggestions()` | `useState` in useSuggestions | Called imperatively |

---

## DTO Alignment

### Compact Wire Format (Player)

Server sends compressed keys (`si`, `ins`, `sc`, `acc`, `fc`, etc.) via `PlayerEndpoints.cs`. Client expands via `expandWirePlayerResponse()` in `serverTypes.ts`. Accuracy ÷1000 on server, ×1000 on client.

| Wire Field | Expanded Field | Transform |
|---|---|---|
| `si` | `songId` | direct |
| `ins` | `instrument` | `instrumentFromHex()` — hex bitmask → instrument key |
| `sc` | `score` | direct |
| `acc` | `accuracy` | × 1000 |
| `fc` | `isFullCombo` | direct |
| `st` | `stars` | direct |
| `dif` | `difficulty` | direct |
| `sn` | `season` | direct |
| `pct` | `percentile` | direct |
| `rk` | `rank` | direct |
| `et` | `endTime` | direct |
| `te` | `totalEntries` | direct |
| `ml` | `minLeeway` | direct |
| `vs` | `validScores` | nested expansion |

### Compact Wire Format (Stats)

Server sends compressed stats via `PlayerEndpoints.cs`. Client expands via `expandWireStatsResponse()`. Same accuracy ÷1000 pattern. Percentile dist sent as int array + index instead of strings.

### Compact Wire Format (Songs — populationTiers)

Server sends `{ bc, t: [{ l, t }] }`. Client expands via `expandWireSongsResponse()` → `{ baseCount, tiers: [{ leeway, total }] }`.

### Full-form Response Types

| Server Endpoint | TS Response Type | Status |
|---|---|---|
| `/api/songs` | `SongsResponse` | ✅ Aligned (via wire expansion) |
| `/api/shop` | `ShopResponse` | ✅ Aligned |
| `/api/leaderboard/{songId}/{instrument}` | `LeaderboardResponse` | ✅ Aligned |
| `/api/leaderboard/{songId}/all` | `AllLeaderboardsResponse` | ✅ Aligned |
| `/api/player/{accountId}` | `PlayerResponse` (via wire) | ✅ Aligned |
| `/api/player/{accountId}/track` | `TrackPlayerResponse` | ✅ Aligned |
| `/api/player/{accountId}/sync-status` | `SyncStatusResponse` | ⚠️ Missing `rivals` field |
| `/api/player/{accountId}/stats` | `PlayerStatsResponse` (via wire) | ✅ Aligned |
| `/api/player/{accountId}/history` | `PlayerHistoryResponse` | ✅ Aligned |
| `/api/player/{accountId}/rivals` | `RivalsOverviewResponse` | ✅ Aligned |
| `/api/player/{accountId}/rivals/suggestions` | `RivalSuggestionsResponse` | ✅ Aligned |
| `/api/player/{accountId}/rivals/all` | `RivalsAllResponse` | ⚠️ Non-precomputed path differs |
| `/api/player/{accountId}/rivals/{combo}` | `RivalsListResponse` | ✅ Aligned |
| `/api/player/{accountId}/rivals/{combo}/{rivalId}` | `RivalDetailResponse` | ⚠️ Missing fields |
| `/api/player/{accountId}/leaderboard-rivals/{instrument}` | `LeaderboardRivalsListResponse` | ✅ Aligned |
| `/api/player/{accountId}/leaderboard-rivals/{instrument}/{rivalId}` | `RivalDetailResponse` (reused) | ⚠️ Shape differs |
| `/api/rankings/{instrument}` | `RankingsPageResponse` | ✅ Aligned |
| `/api/rankings/{instrument}/{accountId}` | `AccountRankingDto` | ✅ Aligned |
| `/api/rankings/{instrument}/{accountId}/neighborhood` | `LeaderboardNeighborhoodResponse` | ✅ Aligned |
| `/api/rankings/composite` | `CompositePageResponse` | ✅ Aligned |
| `/api/rankings/composite/{accountId}` | `CompositeRankingDto` | ✅ Aligned |
| `/api/rankings/composite/{accountId}/neighborhood` | `CompositeNeighborhoodResponse` | ✅ Aligned |
| `/api/rankings/combo` | `ComboPageResponse` | ✅ Aligned |
| `/api/rankings/combo/{accountId}` | `ComboRankingEntry & { comboId, rankBy, totalAccounts }` | ✅ Aligned |
| `/api/account/search` | `AccountSearchResponse` | ✅ Aligned |

---

## Feature Flags

| Server (`FeatureOptions.cs`) | API Response (`/api/features`) | Client (`FeatureFlagsContext.tsx` type) | Status |
|---|---|---|---|
| `Shop` | `shop` | `shop` | ✅ Aligned |
| `Rivals` | `rivals` | `rivals` | ✅ Aligned |
| `Compete` (derived) | `compete` | `compete` | ✅ Aligned |
| `Leaderboards` | `leaderboards` | `leaderboards` | ✅ Aligned |
| `FirstRun` | `firstRun` | `firstRun` | ✅ Aligned |
| `Difficulty` | `difficulty` | `difficulty` | ✅ Aligned |

All 6 flags are fully aligned across server config, API response, and client type.

---

## WebSocket Messages

| Message Type | Server Method | Client Handler | Scope | Status |
|---|---|---|---|---|
| `shop_snapshot` | `SendShopSnapshotAsync()` | `useShopWebSocket` → `case 'shop_snapshot'` | Per-connection (on connect) | ✅ Aligned |
| `shop_changed` | `NotifyShopChangedAsync()` | `useShopWebSocket` → `case 'shop_changed'` | Broadcast (all clients) | ✅ Aligned |
| `backfill_complete` | `NotifyBackfillCompleteAsync()` | `WsNotificationMessage` type defined, not handled | Per-account | ⚠️ Type-only |
| `history_recon_complete` | `NotifyHistoryReconCompleteAsync()` | `WsNotificationMessage` type defined, not handled | Per-account | ⚠️ Type-only |
| `rivals_complete` | `NotifyRivalsCompleteAsync()` | `WsNotificationMessage` type defined, not handled | Per-account | ⚠️ Type-only |

The `WsNotificationMessage` union type in `serverTypes.ts` includes all 5 message types. The `useShopWebSocket` hook only handles `shop_snapshot` and `shop_changed`; the other 3 fall through to the `default` case (silently ignored). This is acceptable — the types are future-proofed.

---

## Misalignments

### M1: SyncStatusResponse missing `rivals` field — MEDIUM

**Server** (`PlayerEndpoints.cs` line ~345): Returns `rivals` object with `{ status, combosComputed, totalCombosToCompute, rivalsFound, startedAt, completedAt }`.

**Client** (`serverTypes.ts` `SyncStatusResponse`): Only has `backfill` and `historyRecon` — no `rivals` field.

**Impact**: Client silently ignores rivals sync status data. Any UI showing sync progress won't show rivals computation status.

**Fix**: Add `rivals` field to `SyncStatusResponse` in `packages/core/src/api/serverTypes.ts`.

### M2: RivalDetailResponse missing `songsToCompete` and `yourExclusiveSongs` — MEDIUM

**Server** (`RivalsEndpoints.cs` combo rival detail): Returns `songsToCompete` and `yourExclusiveSongs` arrays alongside `songs`.

**Client** (`serverTypes.ts` `RivalDetailResponse`): Only has `songs` — missing both extra arrays.

**Impact**: Client can't display "songs to compete on" or "your exclusive songs" sections from the rival detail response. Data is fetched but not typed.

**Fix**: Add `songsToCompete` and `yourExclusiveSongs` to `RivalDetailResponse` (or create a new extended type).

### M3: LeaderboardRivalDetail response shape differs from `RivalDetailResponse` — MEDIUM

**Server** (`LeaderboardRivalsEndpoints.cs`): Returns `{ rival, instrument, rankBy, totalSongs, sort, songs, songsToCompete, yourExclusiveSongs }` — has `instrument` + `rankBy`, NO `combo`/`offset`/`limit`.

**Client** (`client.ts`): Uses `RivalDetailResponse` (which expects `combo`, `offset`, `limit`; lacks `instrument`, `rankBy`, `songsToCompete`, `yourExclusiveSongs`).

**Impact**: TypeScript thinks the response has `combo`/`offset`/`limit` (undefined at runtime) and misses `instrument`/`rankBy`. Currently works because the client only reads `rival.displayName` and `songs[]`.

**Fix**: Create a separate `LeaderboardRivalDetailResponse` type, or extend `RivalDetailResponse` to be a union.

### M4: Rivals-all non-precomputed path differs from `RivalsAllResponse` — LOW

**Server precomputed** (`ScrapeTimePrecomputer.cs`): Includes `songs: string[]` index and rivals with `{ direction, samples }`.

**Server non-precomputed** (`RivalsEndpoints.cs`): No `songs` field, rivals use `MapRivalSummary` (no `direction`, no `samples`, has `avgSignedDelta`).

**Client** (`serverTypes.ts` `RivalsAllResponse`): Expects `songs: string[]` and `RivalsAllEntry` with `samples`.

**Impact**: If precomputed cache misses, the fallback response doesn't match the TS type. `songs` would be undefined, `samples` would be undefined on each rival. Most registered users should hit the precomputed path.

**Fix**: Align the non-precomputed path to include `songs` and `samples`, or document that this endpoint requires precomputation.

### M5: Default `rankBy` disagreement on `getRankings` — LOW

**Server** (`RankingsEndpoints.cs`): `rankBy ?? "adjusted"` (defaults to `adjusted`).

**Client** (`client.ts`): `getRankings(..., rankBy = 'totalscore')` (defaults to `totalscore`).

**Impact**: None in practice — client always sends the `rankBy` parameter. But implicit contract disagrees.

**Fix**: Align defaults (either both `adjusted` or both `totalscore`), or document this is intentional.

### M6: Default `pageSize` disagreement — LOW

**Server** rankings endpoints default to `pageSize ?? 50`.

**Client** `getRankings()` defaults to `pageSize = 10`.

**Impact**: None in practice — client always sends `pageSize`. Server's precomputed cache keys use `50`, so pageSize=10 will miss precomputed cache.

**Fix**: Consider aligning to `50` on the client or documenting this as intentional.

---

## Recommendations

### Priority 1 — Fix DTO Mismatches

1. **Add `rivals` field to `SyncStatusResponse`** in `packages/core/src/api/serverTypes.ts`:
   ```typescript
   rivals: {
     status: string;
     combosComputed: number;
     totalCombosToCompute: number;
     rivalsFound: number;
     startedAt: string | null;
     completedAt: string | null;
   } | null;
   ```

2. **Extend `RivalDetailResponse`** with `songsToCompete` and `yourExclusiveSongs`:
   ```typescript
   songsToCompete?: { songId: string; title?: string; artist?: string; instrument: string; score: number; rank: number }[];
   yourExclusiveSongs?: { songId: string; title?: string; artist?: string; instrument: string; score: number; rank: number }[];
   ```

3. **Create `LeaderboardRivalDetailResponse`** type or make `RivalDetailResponse` a discriminated union.

### Priority 2 — Align Fallback Paths

4. **Align the rivals-all non-precomputed response** to include `songs` index and `samples` to match `RivalsAllResponse` type.

### Priority 3 — Housekeeping

5. **Add query keys** for leaderboard rivals and rivals-all to `queryKeys.ts` (enables targeted invalidation).
6. **Align default `rankBy`/`pageSize`** or add comments documenting intentional divergence.

---

## Hardcoded URLs Audit

All API calls in the web app go through `client.ts` except:
- `/api/features` — direct fetch in `FeatureFlagsContext.tsx` (acceptable — loaded before client is available)
- `/api/ws` — constructed in `useShopWebSocket.ts` using `location.host` (correct for WS upgrade)

No hardcoded external API URLs found in the web app source.
