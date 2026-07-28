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
| `Scraper__RegisteredUserRefreshTimeout` | `00:10:00` |
| `Scraper__RegisteredPlayerBandDiscoveryTimeout` | `00:05:00` |
| `Scraper__RegisteredBandTargetedProcessingTimeout` | `00:05:00` |
| `Scraper__RegisteredPlayerBandDiscoveryMaxLookupsPerPass` | `80` |
| `Scraper__RegisteredBandProcessingMaxLookupsPerPass` | `80` |

`Scraper__PostScrapeRefreshTimeout` remains the backward-compatible fallback
when a dedicated timeout is not configured.

## 2026-07-28 recovery evidence

Published scrape `1267` remained authoritative and unfrozen. Runs `164`
(player) and `165` (band) completed for scrape `1267`, inserting `995` player
notification rows and `3,996` band notification rows. Selective baselining
suppressed `4,193` player-song, `15` player-rank, `12,112` band-song, and
`4,958` band-rank back-catalog rows.

Evidence:

`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/notification-recovery-20260728T1428Z`
