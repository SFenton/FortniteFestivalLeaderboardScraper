# Published Physical Snapshot Reuse Runbook

## Current decision

**Tier:** code/readiness accepted; prior live A/B reverted; resume capacity is
now sufficient but no new candidate has started.

`Features:SkipUnchangedPhysicalLeaderboardSnapshots` remains default-off. The
refreshed Epic device credential passed the auth-only persistence canary, all
`25/25` PIA exits returned valid authenticated Epic JSON with exact direct/proxy
entry parity, and corrected current-source image
`fstservice:snapshot-reuse-919daa32` ran candidate scrape `1264`.

The candidate completed all `8,232/8,232` manifests with zero incomplete
scopes, writer failures, parse failures, retry exhaustion, or observed
publication-critical failures. The post-writer capacity guard then failed at
`32,390,148,096` free bytes versus the `45,148,225,536` measured baseline
requirement and `44,394,828,933` candidate estimate. The worker was stopped
before rankings or global publication, scrape `1264` was reconciled failed at
`capacity_postwriter_guard`, and production was reverted to the prior worker
image/config with scheduling held. Published scrape `1236` remains
authoritative and unfrozen; `1264` owns zero published-source rows.

Evidence:
`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/snapshot-reuse-live-ab-20260726T032124Z`.

## 2026-07-26 same-drive capacity recovery

The separate BAND-SONG-PROJECTION phase retired only stale optional derived
data. It truncated four `band_song_team_rankings*` tables without `CASCADE`
after successful-scrape writer-disable evidence, live published-read parity,
two rolled-back production proofs, a deterministic rebuild proof, and an exact
same-drive archive.

| Gate | Result |
|---|---|
| Database reclaim | `28,315,533,312` bytes |
| Exact archive retained | `2,184,507,134` bytes, PostgreSQL custom/zstd, full read validation passed |
| Final free space | about `58.97 GB` |
| Baseline scrape margin | `13,822,787,584` bytes |
| SNAPSHOT-REUSE estimated margin | `14,576,143,227` bytes |
| Public/service health | Postgres, service, web, `/readyz`, shell, and `/api/service-info` healthy; `24/24` band route fingerprints exact |
| Worker | Held; not started |

Capacity now permits another guarded candidate start, but this does not change
the prior rejection, enable the default-off flag, or prove publication parity.
Evidence:
`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/band-song-projection-retirement-20260726T103231Z`.

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

## 2026-07-26 live A/B

| Gate | Result |
|---|---|
| Runtime | `gpt-5.6-sol`, reasoning `max`, context `long_context` |
| Auth persistence | Passed twice on `/mnt/docker-storage`; rotated credential remained mode `0600` |
| Authenticated provider canary | `25/25` direct and `25/25` PIA responses were valid Epic JSON; all 25 entry arrays and structures matched exactly |
| Proxy/compose guard | 30 canonical services, 25 healthy unique PIA exits, 400 aggregate RPS, 2 RPS and one in-flight per effective exit; no AirVPN/direct fallback |
| Image ancestry | Initial production-wrapper build was rejected before scrape allocation when it inherited stale `ghcr.io` code and attempted retired `ix_rh_latest`; the exact backend was cancelled, free space and DB size recovered, and the image was rebuilt with `FSTService/Dockerfile` from `919daa32` |
| Corrected candidate | `fstservice:snapshot-reuse-919daa32`, image `sha256:470d5a5d2bf7...`, binary contained snapshot-reuse/current ownership markers and no `ix_rh_latest` string |
| Current validation | `300/300` targeted snapshot/published-source/export/config tests passed; Release and corrected source-image builds passed |
| Start capacity | `52,199,215,104` free bytes; both initial scrape guards passed with severe alert |
| Scrape | `1264`; `8,232/8,232` complete manifests, `59,077,331` reported entries, `592,460` pages, zero writer failures |
| Scope behavior | `5,815` changed, `36` new, `281` unchanged reusable, and `42` explicit-empty solo scopes |
| Physical skip proof | `219,427` exact rows avoided; zero unchanged scope had a scrape-`1264` physical row |
| Storage benefit | `15,552,274,432` snapshot-relation bytes still grew; actual-run calibration estimates only `78,765,704` physical bytes avoided |
| WAL/temp | `97,876,358,577` WAL bytes and zero temp-byte growth through the stop; approximate avoided-WAL attribution is `166,448,926` bytes |
| Network/writer performance | About `23,247 s`, versus `22,326.583 s` for candidate `1262` (`+4.1%`); 30.7 RPS, 205 retries, 90.4 GB received |
| Resources/public health | 379 one-minute samples, zero health failures/locks/long queries; peak worker/Postgres RSS about `2.76/8.63 GiB`; minimum free space `23,176,040,448` during band flush |
| Post-writer capacity | **Blocked:** `32,390,148,096` free, below both measured requirements |
| Publication | None; `1264` owns zero published-source rows, `1236` remains unfrozen |
| Rollback parity | All 13 route/export/history/ranking fingerprints matched baseline exactly; final stable leaderboard p95 changed `6.482 -> 7.031 ms` (`+8.46%`) |
| Final capacity | `32,725,393,408` free; both baseline and candidate scrape guards block; worker remains held |

The default-off code remains a valid correctness candidate, but this observation
did not produce enough unchanged data to make the storage benefit meaningful
against current full-scrape growth. Do not promote or rerun it until the
post-writer capacity gate can pass on the FST drive.

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

1. Keep the worker held until the parent-owned retry. Both measured guards now
   pass, but rerun them immediately before start because evidence/archive and
   normal filesystem activity can change the margin.
2. Preserve all DB/storage work on `/mnt/docker-storage`; do not use alternate
   drive scratch or delete data to force a retry.
3. Build current source with `docker build -f FSTService/Dockerfile ...`.
   `/home/sfenton/Docker/FestivalServiceTracker/Dockerfile.fstservice` is a
   registry-wrapper Dockerfile and must not be labeled as a current-source
   candidate.
4. Before any future retry, revalidate auth persistence, authenticated
   direct/PIA JSON parity, the `25/25` compose guard, public health, locks, and
   both start and post-writer capacity gates.
5. Deploy only the snapshot-reuse flag while preserving all other writer
   behavior, then use `tools/fst-worker-compose-guard.sh --recreate-runonce`.
6. Monitor every 60 seconds through one complete scrape, post-process,
   publication, unfreeze, and parity window. Hold the worker before another
   scrape.
7. Accept only with complete manifests, zero writer/critical failures, exact
   source/count/content/coverage/public API/workbook parity, meaningful
   physical/WAL growth reduction, and no sustained regression above 10%.

## Rollback

- Set `Features__SkipUnchangedPhysicalLeaderboardSnapshots=false`.
- Restore/recreate the prior accepted worker image/config.
- Retain the additive source map and manifests for diagnosis.
- A failed candidate must own zero published-source rows and leave published
  `1236` or its later accepted successor authoritative.

## Logical-shadow prerequisite

Scrape `1264` does **not** clear the logical-shadow live-publication
prerequisite. The disabled-writer candidate did not globally publish, so
`leaderboard_current_entries*` and `leaderboard_entry_versions*` must not be
truncated. The hashed decision is
`parity/logical-shadow-retirement-live-gate.json` in the live A/B evidence
root, SHA-256
`75dc6c9ad8348199f447f9f4e549bb2b633c7e43f68338ea218fed3127e568b9`.
