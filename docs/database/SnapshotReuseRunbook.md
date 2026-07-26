# Published Physical Snapshot Reuse Runbook

## Current decision

**Tier:** code/readiness accepted, live A/B blocked before deployment.

`Features:SkipUnchangedPhysicalLeaderboardSnapshots` remains default-off. The
2026-07-26 SNAPSHOT-REUSE preflight found that the stored Epic refresh token is
invalid, and the existing worker authentication path correctly requires device
login. No candidate image/config was deployed, no worker was started, no scrape
ID was allocated, and published scrape `1236` remains authoritative and
unfrozen.

Evidence:
`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/snapshot-reuse-20260726T010701Z`.

## Candidate contract

The flag becomes effective only when all existing correctness controls are
also enabled:

- `Features:WritePublishedScopeSources=true`;
- `Features:EnforceScopeCompletenessManifests=true`;
- `Features:UseLeaderboardScopeFingerprints=true`.

For each non-empty solo scope, the writer:

1. receives the completed manifest before the bounded online write is queued;
2. computes the current deduplicated physical content fingerprint;
3. requires a complete current manifest and exact current/published content
   and row-count parity;
4. requires exact coverage-fingerprint parity, except for the one-way upgrade
   from the legacy 32-character coverage fingerprint on published `1236` to a
   complete 64-character manifest fingerprint;
5. verifies the selected published physical source still exists with the exact
   mapped row count;
6. skips current-scrape physical rows only after all checks pass;
7. pins `leaderboard_snapshot_state` to the validated published source, never
   to a newer failed or merely active source.

Changed, new, incomplete, coverage-changed, missing-source, or ambiguous scopes
write a new physical snapshot. Empty scopes retain explicit-empty mapping
semantics. Publication still validates every expected mapping and promotes the
scope map, fingerprints, band/cache state, and global pointer atomically.

## 2026-07-26 preflight

| Gate | Result |
|---|---|
| Runtime | `gpt-5.6-sol`, reasoning `max`, context `long_context` |
| Production/public health | Postgres, service, web, `/readyz`, shell, service-info, and mapped leaderboard healthy |
| Publication | `1236`, unfrozen; latest `1263` remains failed and isolated |
| DB activity | No active scrape, ungranted lock, long query, vacuum, index build, or rewrite |
| Same-drive capacity | `48,960,053,248` free bytes |
| Measured baseline requirement | `45,148,225,536`; margin `3,811,827,712` |
| Candidate estimate | `44,394,828,933`; margin about `4.565 GB` |
| Estimated reuse | `1,203` scopes / `3,371,702` rows / `753,396,603` physical bytes |
| Published physical sources | `6,096/6,096` exact counts; `39,588,650` rows; `42` explicit empty scopes |
| Proxy guard | `25/25` healthy unique PIA exits, 30 canonical services, 400 aggregate RPS, 2 RPS and one in-flight per effective exit |
| Worker auth | **Blocked:** Epic returned `invalid_refresh_token`; interactive device login is required |
| Low-rate provider probe | Client-token control reached all 26 direct/PIA paths, but all returned JSON auth/entitlement responses; this is not a valid worker-user canary |

The estimate uses exact published-`1236` versus complete-`1263`
content/row parity and measured `1236 -> 1262` per-instrument snapshot relation
growth. It is a capacity estimate, not promotion evidence.

## Validation completed

- `186/186` focused writer/orchestrator tests passed, including bounded-online
  reuse and legacy-coverage upgrade.
- `317/317` PostgreSQL/API/projection/export tests passed.
- The full service run passed `2,068/2,072`; all four failures are documented
  pre-existing baseline fixtures outside SNAPSHOT-REUSE.
- Release build passed with zero errors.
- The evidence collector now retains expected fail-closed HTTP `503` bodies
  instead of aborting on them.

## Resume procedure

1. Complete operator-owned Epic device authentication without placing URLs,
   codes, tokens, or credentials in logs or reports.
2. Rerun the auth-only refresh canary. Stop unless it succeeds and persists the
   rotated refresh token on `/mnt/docker-storage`.
3. Rerun the low-rate authenticated direct/PIA JSON parity canary and the
   `25/25` compose guard. Do not add AirVPN/direct fallback or raise rates.
4. Rerun both the measured baseline and candidate capacity guards.
5. Build/deploy exactly the current accepted worker code with only
   `Features__SkipUnchangedPhysicalLeaderboardSnapshots=true`.
6. Verify the full public path, then use
   `tools/fst-worker-compose-guard.sh --recreate-runonce`.
7. Monitor every 60 seconds through one complete scrape, post-process,
   publication, unfreeze, and parity window. Hold the worker before another
   scrape.
8. Accept only with complete manifests, zero writer/critical failures, exact
   source/count/content/coverage/public API/workbook parity, meaningful
   physical/WAL growth reduction, and no sustained regression above 10%.

## Rollback

- Set `Features__SkipUnchangedPhysicalLeaderboardSnapshots=false`.
- Restore/recreate the prior accepted worker image/config.
- Retain the additive source map and manifests for diagnosis.
- A failed candidate must own zero published-source rows and leave published
  `1236` or its later accepted successor authoritative.

## Logical-shadow prerequisite

This blocked preflight does **not** clear the logical-shadow live-publication
prerequisite. No disabled-writer candidate globally published, so
`leaderboard_current_entries*` and `leaderboard_entry_versions*` must not be
truncated.
