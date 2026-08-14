---
status: living-runbook
owner: data
last_verified: 2026-08-14
last_verified_commit: 00531b19
sources:
  - FSTService/Persistence/MaxScoreMaintenanceCommand.cs
  - FSTService/Persistence/MaxScoreMaintenanceService.cs
  - FSTService/Persistence/MaxScoreMaintenanceNotificationService.cs
  - FSTService/Persistence/RegistrationMutationGuard.cs
  - FSTService/Scraping/RegistrationBackfillWorker.cs
  - FSTService/Scraping/GlobalLeaderboardScraper.cs
  - FSTService/Scraping/MaxScoreMaintenanceDerivedStateService.cs
  - FSTService/Scraping/BandRankingRepairService.cs
  - FSTService/Persistence/BandCurrentProjectionBuilder.cs
  - FSTService/Scraping/PathGenerationCoordinator.cs
  - FSTService/Api/PublicReadGateService.cs
update_triggers:
  - Max-score stage, plan, apply, resume, freeze, notification, cache, validation, or rollback-evidence behavior changes.
---

# Max-score correction maintenance

Use this workflow only for a reviewed CHOpt/provider maximum correction whose
recurring path-generation rule is already fixed. It is CLI-only and bounded to
the songs in one canonical manifest. The generic
`POST /api/admin/regenerate-paths` endpoint is not completion because it does
not rebuild every maximum-dependent projection or protect notification
semantics.

The retired exact-four path-repair and notification-maintenance options remain
rejected at process startup. Do not use an old image to recover them.

## Safety contract

All files must resolve below `Scraper:DataDirectory`, which is on the 4 TB FST
drive in production.

Before plan or apply:

1. deploy the recurring inference/path fix and run `--initialize-schema-only`;
2. stop `fstworker` before another scrape starts;
3. require healthy PostgreSQL, API, and web roles, adequate disk/CPU/memory,
   no locks or long queries, no running scrape/phase, no working publication,
   unfrozen reads, and no failed-candidate isolation;
4. require the exact current publication/catalog, completed notification
   marker, and completed visible routine player and band detection lanes;
5. keep `Scraper__EnableAutomaticPathGeneration=false`.

Stage acquires the distributed path-generation lease, creates complete
immutable generations serially, and never changes a `songs` pointer. Plan
briefly acquires the path-generation and publication locks, validates the
manifest and immutable artifacts, fingerprints score sources, notification
state, and rank history, and requires zero unexplained routine candidates.

Apply acquires locks in this order:

1. path-generation advisory lock;
2. global publication advisory lock;
3. `leaderboard_entries_overlay`, `leaderboard_entries`, then
   `band_member_stats` share locks in that fixed order;
4. publication and song row locks inside bounded transactions.

Band maintenance is the intentional writer for target-song `band_entries`
threshold flags and affected projection scopes, so the source lock protects
`band_member_stats` while the global publication lock, source fingerprint, and
repeated worker-offline checks protect the remaining band boundary.

It creates a `max-score-maintenance:v1:<manifest-sha256>` freeze. During that
freeze publication-bound song, path, ranking, player, and band reads use an
already-built published cache or return `503`; the candidate state is never
served live. Exact solo leaderboard requests, including every leeway/max-score
query, follow the same cache-or-`503` rule. A warm `SongsCacheService` response
may be served, but path artifacts without a separately safe published response
remain blocked.

The same freeze rejects `POST /api/player/{accountId}/track` and the
registration-changing band `sync-status` request. Selected-profile activity
tracking also suppresses player touches and band/member registration writes,
including on outer public-cache hits. PostgreSQL triggers independently reject
registered-player, registered-band, and backfill status/progress mutations
under the digest-owned freeze. Registration-only backfill/history workers
check the durable state before work and hold a shared publication-row lease
across each mutation batch, so freeze establishment waits for an admitted batch
and new/resumed batches revalidate before their first write. A failed or resumed
maintenance run remains blocked until its exact freeze is released. Normal
scrape/publication freezes do not activate this registration guard.

## Stage request

The recommended request binds the expected publication and approved CHOpt
version. Expected old/new maxima may also be supplied as complete eight-field
objects; when omitted, verify the generated manifest before approving its
digest.

```json
{"requestVersion":1,"expectedPublishedScrapeId":1296,"songs":[{"songId":"3d7901c9-7ae2-4adb-9393-4ec4c54c2e3b"},{"songId":"ddd5447c-b5d7-4fe4-8f22-c9854168d11b"}],"expectedChoptVersion":"1.16.3","expectedChoptBinarySha256":null,"expectedGenerationProfile":null}
```

The request is strict JSON. Unknown properties are rejected. Song count is
bounded to `1..32`.

## Command sequence

Run from the production-owned Compose directory. Host-side request/report
files belong below the mounted FST data directory; command paths below are
relative to `Scraper:DataDirectory`.

```bash
FST_DATA=/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data
install -d "$FST_DATA/maintenance/max-score-1296"
cat >"$FST_DATA/maintenance/max-score-1296/request.json" <<'JSON'
{"requestVersion":1,"expectedPublishedScrapeId":1296,"songs":[{"songId":"3d7901c9-7ae2-4adb-9393-4ec4c54c2e3b"},{"songId":"ddd5447c-b5d7-4fe4-8f22-c9854168d11b"}],"expectedChoptVersion":"1.16.3","expectedChoptBinarySha256":null,"expectedGenerationProfile":null}
JSON

cd /home/sfenton/Docker/FestivalServiceTracker

docker compose run --rm --no-deps --entrypoint dotnet fstservice \
  FSTService.dll --initialize-schema-only

docker compose run --rm --no-deps \
  -e Scraper__EnablePathGeneration=true \
  -e Scraper__EnableAutomaticPathGeneration=false \
  --entrypoint dotnet fstservice \
  FSTService.dll \
  --max-score-maintenance-stage \
  --published-scrape-id 1296 \
  --max-score-maintenance-stage-request maintenance/max-score-1296/request.json \
  --max-score-maintenance-manifest-output maintenance/max-score-1296/manifest.json \
  --max-score-maintenance-report-output maintenance/max-score-1296/stage-report.json
```

Read `manifestSha256` from the stage report. Verify the manifest is canonical,
contains exactly the reviewed songs, uses CHOpt `1.16.3`, changes only the
approved instruments, and retains every other maximum exactly.

For the publication-1296 two-song repair, require:

| Song | Old Lead | New Lead | Old Pro Lead | New Pro Lead |
|---|---:|---:|---:|---:|
| `ddd5447c-b5d7-4fe4-8f22-c9854168d11b` | `null` | `51573` | `null` | `51573` |
| `3d7901c9-7ae2-4adb-9393-4ec4c54c2e3b` | `null` | `63750` | `null` | `65367` |

```bash
jq -e '
  .expectedPublishedScrapeId == 1296 and
  .runtime.version == "1.16.3" and
  (.songs | length) == 2 and
  all(.songs[];
    .changedInstruments == ["Solo_Guitar","Solo_PeripheralGuitar"] and
    ([.currentPath.maxima.bass,.currentPath.maxima.drums,.currentPath.maxima.vocals,.currentPath.maxima.proBass,.currentPath.maxima.proCymbals,.currentPath.maxima.proDrums] ==
     [.stagedPath.maxima.bass,.stagedPath.maxima.drums,.stagedPath.maxima.vocals,.stagedPath.maxima.proBass,.stagedPath.maxima.proCymbals,.stagedPath.maxima.proDrums])) and
  ((.songs[] | select(.songId=="ddd5447c-b5d7-4fe4-8f22-c9854168d11b")) |
    .currentPath.maxima.lead == null and .currentPath.maxima.proLead == null and
    .stagedPath.maxima.lead == 51573 and .stagedPath.maxima.proLead == 51573) and
  ((.songs[] | select(.songId=="3d7901c9-7ae2-4adb-9393-4ec4c54c2e3b")) |
    .currentPath.maxima.lead == null and .currentPath.maxima.proLead == null and
    .stagedPath.maxima.lead == 63750 and .stagedPath.maxima.proLead == 65367)
' "$FST_DATA/maintenance/max-score-1296/manifest.json"

MANIFEST_SHA=$(
  jq -er '.manifestSha256'
    "$FST_DATA/maintenance/max-score-1296/stage-report.json"
)
```

Then plan:

```bash
docker compose run --rm --no-deps --entrypoint dotnet fstservice \
  FSTService.dll \
  --max-score-maintenance-plan \
  --published-scrape-id 1296 \
  --max-score-maintenance-manifest maintenance/max-score-1296/manifest.json \
  --expected-max-score-manifest-digest "$MANIFEST_SHA" \
  --max-score-maintenance-report-output maintenance/max-score-1296/plan-report.json

PLAN_DIGEST=$(
  jq -er 'select(.canApply == true) | .planDigest'
    "$FST_DATA/maintenance/max-score-1296/plan-report.json"
)
```

Require `canApply=true`, every check passed, `routineCandidateCount=0`, and
retain `planDigest`. Apply uses both approved digests:

```bash
docker compose run --rm --no-deps --entrypoint dotnet fstservice \
  FSTService.dll \
  --max-score-maintenance-apply \
  --published-scrape-id 1296 \
  --max-score-maintenance-manifest maintenance/max-score-1296/manifest.json \
  --expected-max-score-manifest-digest "$MANIFEST_SHA" \
  --expected-max-score-plan-digest "$PLAN_DIGEST" \
  --max-score-maintenance-rollback-output maintenance/max-score-1296/rollback.json \
  --max-score-maintenance-report-output maintenance/max-score-1296/apply-report.json
```

Apply:

- persists file and PostgreSQL rollback evidence before promotion; canonical
  rollback JSON uses the durable run creation timestamp so file-first,
  checkpoint-second retries reproduce identical bytes;
- promotes every song in one transaction;
- refreshes in-process song/instrument admission immediately after promotion,
  removes only prior negative backfill checks for newly usable path-backed
  song/instrument pairs, and requeues only affected accounts. Positive checks
  and unrelated completed pairs remain intact;
- rebuilds affected `song_stats` and solo rankings, then composite,
  solo-family, and combo rankings; recalculates target-song band
  over-threshold flags, refreshes affected band current-projection scopes, and
  rebuilds dependent band rankings without rank-history snapshots;
- rebuilds affected player-stat tiers and every registered player's
  leaderboard rivals;
- classifies affected-instrument player-rank and target-song/dependent-band
  candidates as maintenance, including max-score-percent rank changes that the
  routine visible lane does not emit. Routine parity uses the same visible
  event cardinality as normal delivery: player-rank metrics coalesce per
  player/instrument, band-song metrics coalesce per play across overall/combo
  rows, and band rank metrics group per subject/scope while progress metrics
  remain individual. Raw candidate rows remain in the audit, but
  `band_rank_state_missing` is excluded from visible parity because routine
  delivery never emits it. Missing band subjects are created and their
  song/rank state is baselined inside the quarantine transaction before
  candidate collection, preventing a later visible `band_first_score`. The
  audit advances matching state, emits no visible event, and leaves publication
  `1296`'s completed notification marker unchanged;
- stages a complete current-publication API cache; and
- validates paths, maxima, rankings, rollback coverage, rank-history
  fingerprint, notification audit, and staged cache before atomically swapping
  the cache and releasing the freeze.

Freeze release invalidates API/path/song and scraper admission caches in every
monitoring role and forces connected clients to refresh even though the
publication ID does not change.

## Failure and resume

Any failure after freeze records the last durable phase and error while leaving
reads frozen. Do not manually clear the freeze. Correct only the reported
cause, then rerun with the same manifest, digests, and rollback path:

```bash
docker compose run --rm --no-deps --entrypoint dotnet fstservice \
  FSTService.dll \
  --max-score-maintenance-resume \
  --published-scrape-id 1296 \
  --max-score-maintenance-manifest maintenance/max-score-1296/manifest.json \
  --expected-max-score-manifest-digest "$MANIFEST_SHA" \
  --expected-max-score-plan-digest "$PLAN_DIGEST" \
  --max-score-maintenance-rollback-output maintenance/max-score-1296/rollback.json \
  --max-score-maintenance-report-output maintenance/max-score-1296/resume-report-1.json
```

Resume rejects a changed digest, publication, catalog, source fingerprint,
notification state at pre-quarantine phases, rank-history fingerprint, freeze
owner, rollback path, or phase identity. Completed phases are not deleted;
idempotent derived work may be rerun when a crash occurred before its durable
checkpoint. A rollback file written before its database phase checkpoint is
loaded through the same persisted run timestamp and must match byte-for-byte;
resume never invents a new evidence timestamp.

## Validation and rollback

After success, verify:

- publication remains `1296`, reads are unfrozen, and no working publication
  exists;
- both path routes for Lead and Pro Lead resolve the manifest generation;
- `songs` and `song_stats` contain the approved maxima and every other maximum
  is unchanged;
- affected rankings, player stats, rivals, band rankings, and precomputed
  responses are current;
- the maintenance notification audit has `visible_delivery_count=0`;
- no player/band visible event or publication notification marker/cursor was
  rewritten; and
- API/web health and same-publication client refresh succeeded.

There is intentionally no automatic rollback command. The rollback JSON plus
`max_score_maintenance_rollback_songs` is authoritative. A reversal requires a
separately reviewed transaction while reads remain frozen, restoration of
every captured path field for every song, the same complete derived rebuild
and notification quarantine rules, cache restaging, and full validation before
unfreeze. Never delete the promoted immutable generations or audit rows as
rollback.
