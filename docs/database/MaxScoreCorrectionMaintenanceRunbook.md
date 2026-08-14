---
status: living-runbook
owner: data
last_verified: 2026-08-14
last_verified_commit: f8cf6f02
sources:
  - FSTService/Api/AdminEndpoints.cs
  - FSTService/Persistence/MaxScoreMaintenanceCommand.cs
  - FSTService/Persistence/MaxScoreMaintenanceService.cs
  - FSTService/Persistence/MaxScoreMaintenanceArtifactValidator.cs
  - FSTService/Persistence/MaxScoreMaintenanceNotificationService.cs
  - FSTService/Persistence/RegistrationMutationGuard.cs
  - FSTService/Persistence/MetaDatabase.cs
  - FSTService/Persistence/DatabaseInitializer.cs
  - FSTService/Scraping/RegistrationBackfillWorker.cs
  - FSTService/Scraping/RegistrationMutationCoordinator.cs
  - FSTService/Scraping/BackfillOrchestrator.cs
  - FSTService/Scraping/GlobalLeaderboardScraper.cs
  - FSTService/Scraping/MaxScoreMaintenanceDerivedStateService.cs
  - FSTService/Scraping/RankingsCalculator.cs
  - FSTService/Scraping/ScrapeTimePrecomputer.cs
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
5. require each target's current v2 immutable generation directory, manifest,
   complete expected artifact tree, and hashes to be present and coherent;
6. keep `Scraper__EnableAutomaticPathGeneration=false`.

Stage acquires the distributed path-generation lease, creates complete
immutable generations serially, and never changes a `songs` pointer. Discovery
manifests are machine-rejected by plan/apply; promotion manifests require
complete old/new maxima and exact scope. Plan
briefly acquires the exclusive mutation gate, path-generation lock, publication
lock, and fixed-order source locks on one isolated unpooled session. It records
a durable random gate-owner token/backend identity while admitted, validates
the manifest and immutable artifacts, fingerprints score sources, notification
state, and rank history, and requires zero unexplained routine candidates
without creating a freeze.

Apply acquires locks in this order:

1. exclusive registration/backfill/history mutation advisory gate, waiting for
   every active shared lifecycle to drain and blocking later admissions, then
   persist its random owner token, publication, backend PID/start, and
   acquisition time in `scrape_publication_state`;
2. path-generation advisory lock;
3. global publication advisory lock;
4. establish a new digest-owned freeze or revalidate the exact resume freeze;
5. for each mutation/checkpoint transaction,
   `leaderboard_entries_overlay`, `leaderboard_entries`, then
   `band_member_stats` and `leaderboard_population` share locks in that fixed
   order;
6. publication and song row locks inside that same bounded transaction.

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

The same freeze rejects `POST /api/player/{accountId}/track`,
`POST /api/backfill/{accountId}`, and the registration-changing band
`sync-status` request. Selected-profile activity tracking also suppresses
player touches and band/member registration writes, including on outer
public-cache hits. PostgreSQL triggers independently reject registered-player,
registered-band, registered-user-refresh, band processing/discovery,
backfill, and history-reconstruction status/progress mutations under the
digest-owned freeze. Registration-only workers, normal registered-user/band
phases, HTTP tracking/activity, stale-registration pruning, and the manual
backfill endpoint hold the shared form of the session advisory gate across the
complete mutation lifecycle. The gate owns no transaction or publication-row
lock, so the production `idle_in_transaction_session_timeout=60000` cannot
release it during long Epic/history work. Exclusive acquisition drains active
score/history writers before freeze or source locks and prevents later writers
until final release.

All advisory holders and waiters use unpooled, non-multiplexed sessions, not
the 15-slot normal service pool. Background/manual workers may wait with
cancellation. HTTP player tracking, manual backfill, and band sync use a
pool-capacity-bounded `pg_try_advisory_lock_shared`; while plan/apply owns the
exclusive gate they return `503` with `Retry-After: 30` immediately, including
before the freeze exists. Cancellation and failures physically close the
isolated session. Immediately after acquiring the shared gate,
lookup-bearing backfill/history/band work invalidates path-maxima state and
synchronously refreshes scraper song/instrument support before any account or
seasonal lookup; metadata-only tracking/activity/pruning does not incur that
cache churn.

The random session token, backend PID, three advisory locks, durable owner, and
four source relation locks are revalidated inside every dependent transaction
and again immediately before ordinary commits. Final completion validates the
exact owner/freeze, swaps caches, marks the workflow complete, and unfreezes
inside one source-locked transaction while retaining the durable owner token.
All max-score writes and checkpoints use the unpooled lock-owning session;
pooled connections are not mutation authority and `AsyncLocal` is not used
for fencing. Registration, leaderboard entry/population, score-history, and
all band entry/member/membership triggers lock the publication row and reject
the durable owner/freeze. Band persistence also performs this gate validation
unconditionally at transaction start, even when `MemberStats` is empty.
Disposal releases the publication, path-generation, and exclusive mutation
advisory locks before conditionally clearing the durable token. A stale writer
therefore cannot commit after cache cutover but before release, and backend
loss during that handoff leaves mutations fail-closed until a new validated
lease finishes release. Normal scrape/publication freezes do not activate this
registration guard.

## Stage request

Request version 2 is canonical strict JSON. Unknown properties, noncanonical
bytes, unsupported/duplicate/out-of-order instruments, and more than 32 songs
are rejected.

- `purpose=discovery` permits explicit partial old/new constraints. Its
  manifest is always non-promotable.
- `purpose=promotion` forbids partial constraints and requires complete
  eight-field old/new maxima for every song.
- Both purposes bind the exact generated and changed instrument arrays plus
  CHOpt version, binary SHA-256, and profile.
- Any plastic-drums change requires the approved CHOpt `1.16.4` binary SHA
  `4c3f9d55c50e8406080191a138580e377413ecc9b2edb60a877281f97018205f`
  and profile `chopt-fnf-ew0-s20-json-png-prodrums-v4`.

## Command sequence

Run from the production-owned Compose directory. Host-side request/report
files belong below the mounted FST data directory; command paths below are
relative to `Scraper:DataDirectory`. Replace `1296` only with the exact current
published scrape confirmed during preflight.

```bash
FST_DATA=/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data
PUBLISHED_SCRAPE_ID=1296
EVIDENCE_REL="maintenance/max-score-${PUBLISHED_SCRAPE_ID}-v4-canary"
EVIDENCE="$FST_DATA/$EVIDENCE_REL"
install -d "$EVIDENCE"

jq -cn --argjson scrape "$PUBLISHED_SCRAPE_ID" '
  {
    requestVersion: 2,
    purpose: "discovery",
    expectedPublishedScrapeId: $scrape,
    expectedPathInstruments: [
      "Solo_Guitar",
      "Solo_Bass",
      "Solo_Drums",
      "Solo_Vocals",
      "Solo_PeripheralGuitar",
      "Solo_PeripheralBass",
      "Solo_PeripheralCymbals",
      "Solo_PeripheralDrums"
    ],
    expectedChangedInstruments: [
      "Solo_Guitar",
      "Solo_PeripheralGuitar",
      "Solo_PeripheralCymbals",
      "Solo_PeripheralDrums"
    ],
    songs: [
      {
        songId: "3d7901c9-7ae2-4adb-9393-4ec4c54c2e3b",
        expectedOldMaxima: null,
        expectedNewMaxima: null,
        expectedOldConstraints: [
          {instrument:"Solo_Guitar",expectedValue:null},
          {instrument:"Solo_PeripheralGuitar",expectedValue:null},
          {instrument:"Solo_PeripheralCymbals",expectedValue:null},
          {instrument:"Solo_PeripheralDrums",expectedValue:null}
        ],
        expectedNewConstraints: [
          {instrument:"Solo_Guitar",expectedValue:63750},
          {instrument:"Solo_PeripheralGuitar",expectedValue:65367}
        ]
      },
      {
        songId: "ddd5447c-b5d7-4fe4-8f22-c9854168d11b",
        expectedOldMaxima: null,
        expectedNewMaxima: null,
        expectedOldConstraints: [
          {instrument:"Solo_Guitar",expectedValue:null},
          {instrument:"Solo_PeripheralGuitar",expectedValue:null},
          {instrument:"Solo_PeripheralCymbals",expectedValue:null},
          {instrument:"Solo_PeripheralDrums",expectedValue:null}
        ],
        expectedNewConstraints: [
          {instrument:"Solo_Guitar",expectedValue:51573},
          {instrument:"Solo_PeripheralGuitar",expectedValue:51573}
        ]
      }
    ],
    expectedChoptVersion: "1.16.4",
    expectedChoptBinarySha256: "4c3f9d55c50e8406080191a138580e377413ecc9b2edb60a877281f97018205f",
    expectedGenerationProfile: "chopt-fnf-ew0-s20-json-png-prodrums-v4"
  }
' | tr -d '\n' >"$EVIDENCE/discovery-request.json"

cd /home/sfenton/Docker/FestivalServiceTracker

docker compose run --rm --no-deps --entrypoint dotnet fstservice \
  FSTService.dll --initialize-schema-only

docker compose run --rm --no-deps \
  -e Scraper__EnablePathGeneration=true \
  -e Scraper__EnableAutomaticPathGeneration=false \
  --entrypoint dotnet fstservice \
  FSTService.dll \
  --max-score-maintenance-stage \
  --published-scrape-id "$PUBLISHED_SCRAPE_ID" \
  --max-score-maintenance-stage-request "$EVIDENCE_REL/discovery-request.json" \
  --max-score-maintenance-manifest-output "$EVIDENCE_REL/discovery-manifest.json" \
  --max-score-maintenance-report-output "$EVIDENCE_REL/discovery-stage-report.json"
```

Discovery does not promote and its manifest cannot enter plan/apply. The
service already enforces exact scope/runtime, four changed maxima, positive
plastic values, cymbal mode greater than or equal to no-cymbal mode, non-empty
authored activation windows, and plastic note inventories distinct from
`Solo_Drums`. Keep the following operator check as readable evidence:

| Song | Lead | Pro Lead | Pro Cymbals | Pro Drums |
|---|---:|---:|---:|---:|
| Run It (`ddd5447c-...`) | `null → 51573` | `null → 51573` | `null → discovered > 0` | `null → discovered > 0` |
| Show Them Who We Are (`3d7901c9-...`) | `null → 63750` | `null → 65367` | `null → discovered > 0` | `null → discovered > 0` |

```bash
jq -e '
  .succeeded == true and
  .purpose == "discovery" and
  .promotable == false
' "$EVIDENCE/discovery-stage-report.json"

jq -e '
  .scope.purpose == "discovery" and
  .runtime.version == "1.16.4" and
  .runtime.binarySha256 == "4c3f9d55c50e8406080191a138580e377413ecc9b2edb60a877281f97018205f" and
  .runtime.profile == "chopt-fnf-ew0-s20-json-png-prodrums-v4" and
  (.songs | length) == 2 and
  all(.songs[];
    .changedInstruments == ["Solo_Guitar","Solo_PeripheralGuitar","Solo_PeripheralCymbals","Solo_PeripheralDrums"] and
    .stagedPath.expectedInstruments == ["Solo_Guitar","Solo_Bass","Solo_Drums","Solo_Vocals","Solo_PeripheralGuitar","Solo_PeripheralBass","Solo_PeripheralCymbals","Solo_PeripheralDrums"] and
    .currentPath.maxima.lead == null and
    .currentPath.maxima.proLead == null and
    .currentPath.maxima.proCymbals == null and
    .currentPath.maxima.proDrums == null and
    .stagedPath.maxima.proCymbals > 0 and
    .stagedPath.maxima.proDrums > 0 and
    .stagedPath.maxima.proCymbals >= .stagedPath.maxima.proDrums and
    .plasticDrumsEvidence.proCymbalsAuthoredActivationWindowCount > 0 and
    .plasticDrumsEvidence.proDrumsAuthoredActivationWindowCount > 0 and
    .plasticDrumsEvidence.soloDrumsNoteInventorySha256 != .plasticDrumsEvidence.proCymbalsNoteInventorySha256 and
    .plasticDrumsEvidence.soloDrumsNoteInventorySha256 != .plasticDrumsEvidence.proDrumsNoteInventorySha256) and
  ((.songs[] | select(.songId=="ddd5447c-b5d7-4fe4-8f22-c9854168d11b")) |
    .stagedPath.maxima.lead == 51573 and .stagedPath.maxima.proLead == 51573) and
  ((.songs[] | select(.songId=="3d7901c9-7ae2-4adb-9393-4ec4c54c2e3b")) |
    .stagedPath.maxima.lead == 63750 and .stagedPath.maxima.proLead == 65367)
' "$EVIDENCE/discovery-manifest.json"

jq -c '
  def maxima($m): {
    lead:$m.lead,
    bass:$m.bass,
    drums:$m.drums,
    vocals:$m.vocals,
    proLead:$m.proLead,
    proBass:$m.proBass,
    proCymbals:$m.proCymbals,
    proDrums:$m.proDrums
  };
  {
    requestVersion: 2,
    purpose: "promotion",
    expectedPublishedScrapeId: .expectedPublishedScrapeId,
    expectedPathInstruments: .scope.expectedPathInstruments,
    expectedChangedInstruments: .scope.expectedChangedInstruments,
    songs: [.songs[] | {
      songId:.songId,
      expectedOldMaxima:maxima(.currentPath.maxima),
      expectedNewMaxima:maxima(.stagedPath.maxima),
      expectedOldConstraints:[],
      expectedNewConstraints:[]
    }],
    expectedChoptVersion: .runtime.version,
    expectedChoptBinarySha256: .runtime.binarySha256,
    expectedGenerationProfile: .runtime.profile
  }
' "$EVIDENCE/discovery-manifest.json" |
  tr -d '\n' >"$EVIDENCE/promotion-request.json"

docker compose run --rm --no-deps \
  -e Scraper__EnablePathGeneration=true \
  -e Scraper__EnableAutomaticPathGeneration=false \
  --entrypoint dotnet fstservice \
  FSTService.dll \
  --max-score-maintenance-stage \
  --published-scrape-id "$PUBLISHED_SCRAPE_ID" \
  --max-score-maintenance-stage-request "$EVIDENCE_REL/promotion-request.json" \
  --max-score-maintenance-manifest-output "$EVIDENCE_REL/promotion-manifest.json" \
  --max-score-maintenance-report-output "$EVIDENCE_REL/promotion-stage-report.json"

jq -e '
  .succeeded == true and
  .purpose == "promotion" and
  .promotable == true
' "$EVIDENCE/promotion-stage-report.json"

MANIFEST_SHA=$(
  jq -er '.manifestSha256'
    "$EVIDENCE/promotion-stage-report.json"
)
```

The second stage reruns CHOpt and must match every complete old/new maximum
copied from discovery. Plan then validates observed scores and both artifact
trees before it can return `canApply=true`:

```bash
docker compose run --rm --no-deps --entrypoint dotnet fstservice \
  FSTService.dll \
  --max-score-maintenance-plan \
  --published-scrape-id "$PUBLISHED_SCRAPE_ID" \
  --max-score-maintenance-manifest "$EVIDENCE_REL/promotion-manifest.json" \
  --expected-max-score-manifest-digest "$MANIFEST_SHA" \
  --max-score-maintenance-report-output "$EVIDENCE_REL/plan-report.json"

PLAN_DIGEST=$(
  jq -er '
    select(
      .canApply == true and
      .affectedInstruments == ["Solo_Guitar","Solo_PeripheralGuitar","Solo_PeripheralCymbals","Solo_PeripheralDrums"] and
      .routineCandidateCount == 0 and
      all(.checks[]; .passed) and
      all(.observedScoreChecks[]; .passed) and
      (.artifactEvidence | length) == 2 and
      all(.artifactEvidence[];
        (.currentArtifactTreeSha256 | length) == 64 and
        .currentArtifactFileCount > 0 and
        (.stagedArtifactTreeSha256 | length) == 64 and
        .stagedArtifactFileCount > 0)
    ) |
    .planDigest
  ' "$EVIDENCE/plan-report.json"
)
```

Each observed-score row requires `highestObservedScore` to be null or less
than/equal to `newMaximum`. Apply uses both approved digests:

```bash
docker compose run --rm --no-deps --entrypoint dotnet fstservice \
  FSTService.dll \
  --max-score-maintenance-apply \
  --published-scrape-id "$PUBLISHED_SCRAPE_ID" \
  --max-score-maintenance-manifest "$EVIDENCE_REL/promotion-manifest.json" \
  --expected-max-score-manifest-digest "$MANIFEST_SHA" \
  --expected-max-score-plan-digest "$PLAN_DIGEST" \
  --max-score-maintenance-rollback-output "$EVIDENCE_REL/rollback.json" \
  --max-score-maintenance-report-output "$EVIDENCE_REL/apply-report.json"
```

Apply:

- persists file and PostgreSQL rollback evidence before promotion; canonical
  rollback JSON uses the durable run creation timestamp so file-first,
  checkpoint-second retries reproduce identical bytes, and each song records
  the validated current-v2 generation file count and artifact-tree SHA-256;
- promotes every song in one transaction;
- refreshes in-process song/instrument admission immediately after promotion,
  removes only prior negative backfill checks for newly usable path-backed
  song/instrument pairs, removes matching successful history-reconstruction
  checkpoints, and requeues only affected all-time/history accounts. Affected
  history status is fenced to a new admission revision and returned to
  `pending`; positive backfill checks and unrelated history pairs remain
  intact;
- rebuilds all four affected `song_stats`/solo ranking instruments, then composite,
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
publication ID does not change. A registration worker that acquires its lease
before the one-second monitor pass still invalidates path state and refreshes
instrument support synchronously before its first lookup, so it cannot recreate
a stale negative checkpoint.

## Failure and resume

Any failure after freeze records the last durable phase and error while leaving
reads frozen. Do not manually clear the freeze. Correct only the reported
cause, then rerun with the same manifest, digests, and rollback path:

```bash
docker compose run --rm --no-deps --entrypoint dotnet fstservice \
  FSTService.dll \
  --max-score-maintenance-resume \
--published-scrape-id "$PUBLISHED_SCRAPE_ID" \
--max-score-maintenance-manifest "$EVIDENCE_REL/promotion-manifest.json" \
  --expected-max-score-manifest-digest "$MANIFEST_SHA" \
  --expected-max-score-plan-digest "$PLAN_DIGEST" \
--max-score-maintenance-rollback-output "$EVIDENCE_REL/rollback.json" \
--max-score-maintenance-report-output "$EVIDENCE_REL/resume-report-1.json"
```

Resume rejects a changed digest, publication, catalog, source fingerprint,
notification state at pre-quarantine phases, rank-history fingerprint, freeze
owner, rollback path, or phase identity. Completed phases are not deleted;
idempotent derived work may be rerun when a crash occurred before its durable
checkpoint. A rollback file written before its database phase checkpoint is
loaded through the same persisted run timestamp and must match byte-for-byte;
resume never invents a new evidence timestamp.

If `pg_terminate_backend`, network loss, or session failure removes the
advisory locks, the current transaction is aborted with its mutation and phase
checkpoint. Final cache publication and unfreeze are likewise refused. The
durable freeze and owner token remain fail-closed; the run may retain its last
status rather than recording a new failure on a dead backend. A new
plan/resume lease must reacquire the fixed lock order, replace the stale owner
token only after exclusive admission, revalidate the checkpoint/fingerprints,
and then continue. Do not clear either field manually.

## Validation and rollback

After success, verify:

- publication remains `$PUBLISHED_SCRAPE_ID`, reads are unfrozen, and no
  working publication exists;
- all four path routes (Lead, Pro Lead, Pro Cymbals, and Pro Drums) resolve each
  manifest generation;
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
