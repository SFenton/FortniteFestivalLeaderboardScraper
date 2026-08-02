# Improvement Notification Recovery Runbook

Use this runbook when player or band improvement notifications stop advancing
even though a newer scrape is published.

## Safety gates

1. Confirm `scrape_publication_state.published_scrape_id`, `public_reads_frozen=false`,
   Docker health, PostgreSQL readiness, locks/long queries, disk, CPU, and memory.
2. Confirm the latest player and band rows in `improvement_detection_runs`.
3. Do not start a full scrape solely to recover notifications. Detection reads
   the already-published projections and rankings.
4. Keep the expected published scrape ID in the command so a concurrent
   publication fails closed.

## Recovery command

```bash
docker compose run --rm --no-deps --entrypoint dotnet fstservice \
  FSTService.dll --recover-improvement-notifications \
  --published-scrape-id <id>
```

The default replays the exact projection scope plan persisted with the
published scrape before player/band detection. Recovery never expands a
missing plan to every current scope. Use
`--notification-skip-projection-refresh` only when evidence proves the
persisted plan is unnecessary.

New players or bands registered after the prior completed detection run are
selectively baselined once. Their existing back catalog is not emitted as
first-play/first-score notifications; later improvements are emitted normally.
The run audit records the exact baseline-row counts.

`--notification-baseline-only` never satisfies publication completion. Only a
non-baseline `mode='execute'` run for every configured player/band and
song/ranking lane can mark the published scrape complete.

## Pro Lead max-score repair notification gate

This is separate from routine recovery. It is only for purpose
`maintenance_pro_lead_max_score_repair_v1`; there is no configurable purpose
or delivery cap. The code and database both fix visible delivery at exactly
zero.

Before any controlled repair, keep
`Scraper__EnableAutomaticPathGeneration=false`. The one-shot stage command
uses the four IDs compiled into
`ImprovementNotificationMaintenanceManifest.RequiredSongIds`; there is no
operator-provided song list:

```bash
docker compose run --rm --no-deps --entrypoint dotnet fstservice \
  FSTService.dll \
  --path-repair-stage-exact-four \
  --path-repair-manifest-output maintenance/path-repair/manifest.json
```

The output is resolved below configured `DataDirectory`; a relative path is
relative to that directory. Parent directories are created only below that
same 4 TB FST drive. The final `.json` file must not exist, and no output path
component may be a symbolic link. Path escape is rejected. The command also
requires explicit path generation enabled and automatic generation disabled.

Stage acquires the purpose-specific path-repair advisory lease and processes
the exact IDs serially. It reuses `PathGenerationCoordinator` decrypt, CHOpt,
runtime identity, and artifact validation. Each row must still have all six
maxima null and a charted Pro Lead part. Stage overrides the generation scope
to Pro Lead only, and easy, medium, hard, and expert PNG/JSON output must all
validate before its immutable generation is moved from
`DataDirectory/.path-work` to
`DataDirectory/paths/.../generations/...`. After promotion, non-Pro-Lead
requests continue to use legacy artifacts. Stage does not call the database
CAS and cannot change song maxima, DAT hash, generation timestamp, pointer,
revision, or pending flag. A failed song appends normal
`path_generation_errors`, stops later songs, and leaves no maintenance
manifest. Successful earlier immutable directories remain unreachable staged
evidence.

On success, the command writes a strict JSON manifest with
`manifestVersion: 1` and exactly four unique song rows in ordinal `songId`
order. Every row binds:

- the current `path_generation_revision`, catalog `last_modified`, and current
  `max_pro_lead_score`;
- a positive proposed Pro Lead maximum;
- a unique staged artifact generation ID and 64-character DAT SHA-256; and
- mandatory CHOpt version, binary SHA-256, and generation profile.

Each of the four `songs` elements uses these exact camel-case properties:

```json
{
  "songId": "example-song-id",
  "expectedCurrentPathRevision": 123,
  "expectedCatalogLastModified": "2026-08-01T12:34:56.0000000Z",
  "currentOldProLeadMaxScore": 100000,
  "proposedProLeadMaxScore": 101000,
  "stagedArtifactGenerationId": "immutable-generation-id",
  "stagedDatFileHash": "64-lowercase-hex-characters",
  "stagedChoptVersion": "required-version",
  "stagedChoptBinarySha256": "required-64-lowercase-hex-characters",
  "stagedGenerationProfile": "required-profile"
}
```

The maintenance command rejects missing, extra, duplicated, unsorted, stale, or
already-active identities. The manifest file must be a nonempty regular `.json`
file no larger than 256 KiB; symbolic links and unknown JSON properties are
rejected. Do not put URLs, credentials, tokens, or other secrets in it.

Run the read-only surface twice against the same published scrape and exact
manifest. The contract-bearing image must already have completed its normal
startup schema ensure; this command does not install or repair schema:

```bash
docker compose run --rm --no-deps --entrypoint dotnet fstservice \
  FSTService.dll \
  --notification-maintenance-pro-lead-max-score-repair \
  --published-scrape-id <id> \
  --notification-maintenance-manifest <absolute-manifestPath-from-stage-report>
```

Save both JSON results. The SHA-256 `dryRunDigest` values and canonical sorted
candidate arrays must match. The canonical digest binds the published scrape
ID, exact normalized manifest, catalog/ranking total-charted count, and
projected candidates. It excludes timestamps, detection/repair run IDs, UUIDs,
and generated notification GUIDs. The report includes total, allowed, rejected,
and per-classification counts; per-subject maximum numeric and rank movement;
and the zero-cap decision.

Dry run opens a repeatable-read, read-only transaction. It does not refresh
solo or band projections and does not write detection runs, events,
maintenance audit rows, or improvement state. It fails closed unless:

- the expected scrape is still published, completed, and unfrozen;
- its notification marker is completed and matches the published scrape;
- completed visible routine player and band runs cover both song and ranking
  lanes for that scrape; and
- the four current song rows, published exact catalog timestamps, and
  `song_stats` maxima exactly match the manifest, and current Pro Lead
  `total_charted_songs` agrees with that published charted-song catalog; and
- every maintenance-attributed candidate is only a Pro Lead
  (`Solo_PeripheralGuitar`) `max_score_percent_rank` movement.

The projection does not read live `account_rankings` for proposed ranks. It
recomputes the complete Pro Lead population read-only from
`current_leaderboard_entries`, `song_stats`, and `score_history`, substituting
the four proposed maxima. Current scores remain valid through the same
`max_score * 1.05` cutoff as normal rankings. An over-cutoff current score uses
that account/song's best historical score at or below the cutoff, or is omitted
when no fallback exists. Max-score percent uses the normal Bayesian adjustment
`(songs_played * raw + 50 * 0.5) / (songs_played + 50)` and the normal ranking
tie breakers.

Direct player/band score observations may coexist only when the gate can
independently classify them as routine work outside maintenance. They are not
quarantined or baselined and remain owned by the normal post-scrape workflow.
Missing rank state, another-instrument movement without ordinary-score
evidence, ambiguous Pro Lead attribution, and other unclassified
aggregate/rank changes block execute.

After two dry runs produce the same `dryRunDigest`, wait for an idle
publication boundary. Promotion and ranking rebuild both fail closed unless
the supplied scrape is still current, `working_publication_id` is null, public
reads are initially unfrozen or already carry this repair's owned freeze,
automatic path generation remains disabled, and the exact publication catalog
still matches the manifest.

Promote the staged generations with a new rollback output path:

```bash
docker compose run --rm --no-deps --entrypoint dotnet fstservice \
  FSTService.dll \
  --path-repair-promote-exact-four \
  --published-scrape-id <id> \
  --path-repair-manifest maintenance/path-repair/manifest.json \
  --path-repair-rollback-output maintenance/path-repair/rollback-before-promotion.json
```

The promotion command holds both the path-repair and publication advisory
locks. Before the first mutation it preflights all four database identities,
the exact published catalog timestamps, every immutable generation manifest
and file, runtime identity, expected instruments/difficulties, and reconstructed
expert maxima. It then writes the rollback snapshot, including all six old
maxima, revision, pointer, DAT/catalog identities, timestamp/runtime/profile,
expected instruments, and pending state. Only then does it establish the
public-read freeze with reason `path-repair-ranking-rebuild`. It verifies
ownership of that freeze before calling the existing CAS exactly once per song
in ordinal order. A successful promotion report must show
`publicReadsFrozen: true`.

This is not an all-four database transaction. Preserve the JSON result. If it
reports `partialPromotion: true`, stop: the result names promoted, failed, and
not-attempted songs, the rollback snapshot is authoritative, and public reads
remain frozen. Do not run ranking rebuild or notification execute from a
partial state.

After all four promotions succeed, rebuild rankings from the same current
published scrape/catalog:

```bash
docker compose run --rm --no-deps --entrypoint dotnet fstservice \
  FSTService.dll \
  --path-repair-rebuild-rankings \
  --published-scrape-id <id> \
  --path-repair-manifest maintenance/path-repair/manifest.json
```

The command revalidates all four post-promotion identities before work, holds
the same maintenance/publication locks, and requires the
`path-repair-ranking-rebuild` public-read freeze left by promotion. It uses the
immutable catalog bound to the supplied publication to recompute Pro Lead plus
dependent composite, solo-family, and combo rankings. It does not rebuild
unrelated solo instruments or bands, allocate a scrape, advance publication
pointers, run improvement notification detection, record scrape phase timings,
or append rank-history snapshots.

Failure or cancellation deliberately keeps public reads frozen because ranking
tables may be partially updated. Only post-rebuild manifest/catalog validation
allows the command to unfreeze. The API invalidates pre-freeze process caches
at that safety revision and broadcasts a same-publication refresh so connected
web clients clear their query and songs caches. A successful report must show
both `publicReadsFrozenDuringRebuild: true` and
`publicReadsRestored: true`; any failure requires investigation or rollback
before manual unfreeze.

Execute is deliberately a separate, fully bound command:

```bash
docker compose run --rm --no-deps --entrypoint dotnet fstservice \
  FSTService.dll \
  --notification-maintenance-pro-lead-max-score-repair \
  --notification-maintenance-execute \
  --published-scrape-id <id> \
  --notification-maintenance-manifest <absolute-manifestPath-from-stage-report> \
  --expected-notification-dry-run-digest <sha256>
```

Execute is run only after a separately controlled promotion of those exact four
staged generations and a separately completed ranking rebuild. It requires each
song to have advanced exactly one revision and to expose the manifest's
proposed maximum, generation ID, DAT hash, catalog identity, and supplied
runtime identity in both path state and `song_stats`. It locks the published
scrape and Pro Lead ranking/stat surfaces, rebuilds the same projection, and
requires the actual `account_rankings` notification candidate set to equal the
projection byte-for-byte before opening the audit/baseline write path.

Any scrape, manifest, digest, identity, projected-versus-actual candidate, stale
input, or rejected-classification mismatch fails closed. A passing execute
persists non-expiring audit/quarantine rows and selectively updates only the
allowed Pro Lead
`player_rank_improvement_state.max_score_percent_rank` rows. It creates zero
visible events, does not expire or supersede existing visible events, does not
touch player-song/band state, and does not broadcast
`notification_feed_changed`.

The required sequence is:

1. stage immutable path artifacts and the exact manifest with automatic
   generation disabled;
2. obtain two identical projected dry-run digests before any live mutation;
3. promote the exact staged generations serially and retain the rollback
   snapshot/result;
4. run the manifest-bound selective ranking rebuild and verify freeze
   restoration plus cache/client refresh;
5. run notification execute with the same manifest/digest so actual
   `account_rankings` must equal the projection; and
6. only then allow ordinary notification detection and the normal lane to
   resume.

The command above is an evidence gate, not authorization to regenerate paths,
recompute rankings, deploy, notify users, or run the four-song repair. The
maintenance audit stores the published scrape ID as a non-null immutable
integer without a retention-coupled `scrape_log` foreign key, so scrape-log
retention cannot erase its provenance.

### Rollback

Before promotion, omit later commands; staged immutable generations are
unreachable and can be retained for audit. After any promotion, use the
command-emitted rollback snapshot, not memory or the maintenance manifest, as
the source for a separately reviewed transaction restoring the exact affected
`songs` columns. Restore all six maxima, revision, pointer, DAT/catalog
identities, generated timestamp/runtime/profile, expected instruments, and
pending state for every row listed in the snapshot. Do not silently decrement
only the revision or restore only Pro Lead.

After restoring a partial or complete promotion, rebuild rankings again while
public reads are maintenance-frozen and verify the four restored
`song_stats`/`account_rankings` identities before resuming detection. The
forward `--path-repair-rebuild-rankings` command intentionally rejects this
pre-repair state; rollback therefore remains a separately reviewed maintenance
transaction plus full ranking recompute. The rollback snapshot is evidence;
there is intentionally no automatic rollback CLI that could overwrite a
concurrently changed song.

Before any quarantine row exists, notification rollback is simply to omit
notification execute. After maintenance execute, do not roll back to an image
that lacks `delivery_state='visible'` public filters. Roll back only to a
contract-bearing image, leave quarantine evidence intact, and restore the
pre-repair Pro Lead ranking/state snapshot if the associated data repair is
reverted. Dropping columns/tables is neither required nor safe during normal
rollback.

## Durable completion

Publication atomically sets the improvement marker in
`scrape_publication_state` to `pending` and stores the exact bounded solo
projection scope plan in
`improvement_notifications_projection_scopes` with
`improvement_notifications_projection_ready=true`. Detection runs record
`published_scrape_id`. A shutdown leaves the marker and workset intact, and
`fstworker` retries the same published scrape before starting its next scrape.
A failed recovery holds the worker at the pre-scrape gate and retries once per
minute; it is not a best-effort warning that allows another scrape.

If publication cleanup did not make the projection current or later work
requires an unbounded refresh, publication fails closed while
`RefreshAllSoloScopesWhenNoImpactedScopes=false`. The
`notification-db-only` scrape profile explicitly requires that value.

`MetaDatabase.PublishScrapeRun` also locks the publication row and refuses to
publish a newer scrape while the current published scrape has a pending,
running, failed, mismatched, or otherwise incomplete notification marker.
This database invariant prevents a later publication from overwriting the
single durable marker even if a worker orchestration regression bypasses the
pre-scrape gate.

The database stores the scope plan's owning scrape ID and enforces a
`NOT VALID` compatibility constraint on all new/updated rows. Do not roll back
to a worker image that predates this contract: it cannot publish safely after
the constraint is installed. Build rollback images from the contract-bearing
commit and revert only candidate flags/configuration.

Legacy pending markers intentionally remain unadopted. After proving the
published projection is already current, the explicit
`--notification-skip-projection-refresh` operator path atomically adopts an
empty plan for that same published marker; startup recovery never does this
implicitly.

The protected status endpoint is:

```text
GET /api/diag/improvement-notifications
```

The API service also logs an error every
`ImprovementNotifications__StalenessCheckInterval` while a required lane is
behind `ImprovementNotifications__StaleAfterPublishedScrapes` or
`ImprovementNotifications__StaleAfterHours`.

## Registered phase budgets

The accepted proxy baseline completed the 1267 solo refresh cycle in about
`00:06:27`; its dedicated timeout is `00:10:00`.

During scrape 1267, discovery persisted `106` checks in `258 s` and targeted
band processing persisted `110` checks in `296 s`. Both now use a default
total budget of `80` successful checks per pass, predicting about `195 s` and
`215 s` respectively under the same measured throughput. Each successful
lookup is checkpointed, and least-recently processed accounts/bands are chosen
first on the next pass.

| Setting | Default |
|---|---:|
| `Scraper__RegisteredUserRefreshTimeout` | `00:00:00` (progress watchdog owns hangs) |
| `Scraper__RegisteredPlayerBandDiscoveryTimeout` | `00:05:00` |
| `Scraper__RegisteredBandTargetedProcessingTimeout` | `00:05:00` |
| `Scraper__RegisteredPlayerBandDiscoveryMaxLookupsPerPass` | `80` |
| `Scraper__RegisteredBandProcessingMaxLookupsPerPass` | `80` |

`Scraper__PostScrapeRefreshTimeout` remains the backward-compatible fallback
when a dedicated timeout is not configured.

Recurring solo refresh coverage is durable in
`registered_user_refresh_scope_progress`. Each pass still includes every
charted song, but missing scopes run first and complete scopes follow by their
oldest `checked_at`. A scope advances only after all required registered-user
all-time/current-season batches succeed; successful empty/known missing
leaderboards count, while transport/API failure does not. Checkpoints are
written from the live attachment callback, so a timeout or cancellation keeps
all scopes that finished before the boundary.

Normal scrape passes store a positive `scrape_id` with `provenance='scrape'`.
Supported phase-only `SoloRefreshUsers` execution stores a null scrape ID with
`provenance='phase_only'`; it must not fail or synthesize a scrape ledger row.
At season rollover, the discovered highest window is authoritative over an
instrument maximum that is still one season behind, and that exact seasonal
lookup must finish successfully before the scope is marked complete. Nonblank
discovered window IDs are sent unchanged; conventional `seasonNNN` lookup IDs
are used only for synthetic rows whose persisted window ID is blank.
FirstSeenSeason discovery now precedes probing and calculation version `4`
retries questionable version `3` rollover rows. Only fresh event-API discovery
plus conclusive probes can advance the version; auth, transport, and 5xx
failures remain retryable. Registered-band discovery/targeted progress stores
the exact lookup ID so an ID change reopens that season. Legacy and batched
history reconstruction likewise remain pending when any required window is
missing or its lookup fails, and version/fingerprint changes invalidate prior
completion.

The cyclical machine snapshots the active season/window fingerprint. Late
attachments requesting a different fingerprint wait for a new cycle rather
than joining the active pass and receiving an all-time-only checkpoint.
Multi-season history runs all reconstruction seasons, including current, in
one coherent history pass. Backfill and history resume keys are separate, and
all versioned history writes are conditional on the active fingerprint so a
late prior run cannot overwrite newer progress or completion.

Each admitted history run also owns a monotonic revision. Staged score-history
rows and pair progress flush atomically only for that active token; cancellation
or stale-token rejection discards both. Backfill completion requires exact
all-time pair coverage independently of history completion. Legacy history
queries through the authoritative current season, and FirstSeen rows reopen
when the authoritative window fingerprint or maximum season advances.
History reconstruction version `2` invalidates version `1`, and current
catalog pair enumeration ignores obsolete removed-song progress without
allowing counts to hide a missing current pair. FirstSeen null/not-found rows
retry even when the window binding is unchanged.

The worker emits `Registered-user refresh coverage (before|after)` with
`expectedScopes`, `checkedScopes`, `missingScopes`, `oldestCheckedAtUtc`,
`oldestCheckedAge`, and `currentScrapeCompletions`. These reads are bounded to
the current charted-song/instrument cross product. A growing missing count or
oldest age indicates recurring backlog; registration backfill/history and
solo-projection dirty scopes are intentionally not represented by this table.

## 2026-07-29 normal-path qualification

Scrape `1268` installed and exercised the complete publication/recovery
contract:

- publication persisted `improvement_notifications_projection_scopes=[]`,
  marked the plan ready and owned by scrape `1268`, and never invoked the
  all-`6,174`-scope fallback;
- player run `166` completed in `13.53 s`; band run `167` completed in
  `68.33 s`; both required song and ranking lanes;
- the publication marker completed `82.15 s` after it started and `101.76 s`
  after `published_at`, well below the 10-minute target;
- the recovery advisory lock count never exceeded one, and the notification
  window emitted zero Epic requests;
- the bounded window added `266,652,828` WAL bytes and zero temp bytes or
  checkpoints. The prior standalone recovery evidence added about `52.51 GB`
  WAL across its unbounded recovery work.

The functional notification path passed, but the shared full-scrape promotion
gate remains **iterate**. During publication, `api_response_cache` was
truncated before long band ranking snapshot copies and index builds completed,
holding an `ACCESS EXCLUSIVE` lock for minutes. Festivalweb recorded `13`
HTTP `504` and `20` client-cancelled `499` responses.

The prepared repair keeps publication atomic but performs band snapshot work
and fingerprint validation before the cache truncate/insert. A concurrent
regression test locks a band ranking source table and proves the old public
cache remains readable while publication waits. The 60-second monitor now
selects and probes a real leaderboard route so this class of failure cannot be
hidden by a healthy `/api/service-info` fast path. The repair is commit
`44a1fe9a`, built as `fstservice:publication-lock-44a1fe9a`; production compose
selects it for the next explicitly armed card, but the exited worker was not
recreated.

Do not promote the notification lane or enable another scheduled scrape until
that contract-bearing repair passes a new dual-lane full-scrape window.

Evidence:

`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/scrape-1268-dual-lane-20260728T184812Z`

## 2026-07-28 recovery evidence

Published scrape `1267` remained authoritative and unfrozen. Runs `164`
(player) and `165` (band) completed for scrape `1267`, inserting `995` player
notification rows and `3,996` band notification rows. Selective baselining
suppressed `4,193` player-song, `15` player-rank, `12,112` band-song, and
`4,958` band-rank back-catalog rows.

Evidence:

`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/notification-recovery-20260728T1428Z`
