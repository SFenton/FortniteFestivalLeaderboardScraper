---
status: living-runbook
owner: data
last_verified: 2026-08-11
last_verified_commit: 453fd9b6
sources:
  - FSTService/Scraping/SoloFamilyRankingBackfillCommand.cs
  - FSTService/Scraping/SoloFamilyRankingBackfillService.cs
update_triggers:
  - Backfill scope, maintenance gates, locks, timeouts, replacement, validation, or rollback changes.
---

# Solo-Family Ranking Denominator Backfill Runbook

## Status and incident evidence

This is a generic rebuild of all fixed solo-family scopes from canonical
`account_rankings`. It is not an exact-song or exact-account repair.

Scrape `1277` completed post-processing and passed `6,273/6,273` scope-source
validation, but publication failed closed with PostgreSQL `P0001` because
`solo_family_rankings` contained an impossible PAD denominator row. Account
`195e93ef108143b2975ee46662d4d0e1` had `2,788` songs played/full combos against
a `2,786` family denominator. The raw published catalog counts were:

| Instrument | Catalog charts |
|---|---:|
| Guitar | 695 |
| Bass | 697 |
| Drums | 697 |
| Vocals | 697 |

Two retained canonical Guitar account rows reported valid denominators of
`696` and `697`. The former family builder summed only raw catalog counts,
while the old generic backfill inferred only canonical maxima. Runtime and
backfill now share one rule:

```text
effective instrument denominator =
  max(supplied catalog count,
      max canonical account_rankings.total_charted_songs)

family denominator = sum(effective instrument denominators)
```

For the incident PAD data, Guitar is overridden from `695` to `697`, so every
PAD family row uses `2,788`. Scores, songs played, full combos, and total score
are not capped or dropped.

The command did not republish failed scrape `1277`; published scrape `1276`
remained authoritative during maintenance. The subsequent full scrape `1278`
independently completed every publication gate and supplied the required live
proof.

Two live dry runs on 2026-08-04 matched exactly. The first execute attempt then
proved that the former shared 30-second statement timeout was too small for the
replacement: binary `COPY` timed out at row `1,959,000` of `5,245,924`. The
same-transaction design rolled back completely. The original `5,245,924` rows
and one invalid row remained, no locks remained, and four public fingerprints
matched byte-for-byte after the API restart. Evidence is retained at:

```text
/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/solo-family-backfill-20260804T075218Z/execute.log
```

The bounded retry then completed successfully: all `5,245,924` rows were
rebuilt, PAD used denominator `2,788`, invalid rows were zero, and exact public
HTTP parity passed. Runtime and standalone replacement now also reject an
empty candidate or omission of any previously populated family scope before
`TRUNCATE`.

## Fail-closed contract

Both runtime ranking computation and this backfill refuse replacement when any
produced family row has:

- `songs_played > total_charted_songs`;
- `full_combo_count > total_charted_songs`;
- non-finite coverage/FC rate; or
- coverage/FC rate greater than `1 + 1e-9`.

The command does not alter or drop
`fst_account_rankings_denominator_guard_1100` and does not weaken the
publication guard.

The JSON report is deterministic for unchanged inputs and includes:

- published scrape and current publication IDs;
- source row counts by instrument;
- catalog, canonical, and effective denominators by instrument;
- family denominators and row counts by scope;
- total and invalid row counts; and
- `executed`.

Runtime logs each catalog-to-canonical denominator override in fixed instrument
order so scrape/publication evidence records the exact difference.

## Commands

Dry run is the default:

```bash
docker compose run --rm --no-deps --entrypoint dotnet fstservice \
  FSTService.dll --solo-family-ranking-backfill
```

Execute is explicit:

```bash
docker compose run --rm --no-deps --entrypoint dotnet fstservice \
  FSTService.dll \
  --solo-family-ranking-backfill \
  --solo-family-ranking-backfill-execute
```

The command is mutually exclusive with schema initialization, score-history
maintenance, and notification recovery. It never runs schema initialization
and registers no hosted services.

## Safety gate

Use production compose ownership at
`/home/sfenton/Docker/FestivalServiceTracker`. Keep reports, snapshots, and
rollback evidence on the 4 TB FST filesystem.

Before execute:

1. Stop the scrape worker and prevent another worker from starting.
2. Quiesce `fstservice` readers, or first prove a bounded atomic table-lock
   window against the deployed API. `solo_family_rankings` is unversioned and
   serves reads directly.
3. Check Docker/Postgres health, readiness, public-read freeze state, current
   and working publication pointers, active scrape/update state, locks/long
   queries, disk, CPU, and memory.
4. Run two dry runs against unchanged data and compare the complete JSON.
5. Require `invalidRowCount = 0`, the expected denominator overrides, and the
   expected scope/source row counts.
6. Capture rollback evidence for the current `solo_family_rankings` rows in a
   transaction-safe same-drive artifact before execute.

The process starts one PostgreSQL transaction, disables
`idle_in_transaction_session_timeout` locally for that bounded transaction,
retains a five-second lock timeout and 30-second maintenance statement timeout,
and takes the global publication advisory key with a non-blocking
transaction-scoped try lock. It then takes a five-second-bounded `SHARE` lock
on canonical `account_rankings`, holds a share lock on the publication-state
singleton, and fails closed unless:

- no `scrape_log` row is running;
- the worker ledger is absent, explicitly `offline`, or more than 90 seconds
  stale; every fresh worker heartbeat blocks maintenance even when its current
  operation is idle;
- `working_publication_id` is null;
- public reads are not frozen;
- current generation, published scrape, and publish timestamps agree;
- the published scrape is completed; and
- the current publication has an exact, ready provider catalog binding.

A stale or explicitly offline worker may retain a historical failed/running
operation record; liveness, rather than that historical operation payload,
controls this gate. Runtime ranking exceptions now close
`rankings.solo_family` as failed so new failures do not create another stuck
running record.

A busy global lock is an immediate failure, not a wait. Source loading occurs
while the publication, state, and canonical-ranking locks remain held. The
separate runtime-ledger read and every per-instrument canonical-ranking read
use explicit 30-second Npgsql command timeouts, matching the maintenance
transaction's statement budget. A blocked or stalled separate read therefore
throws, rolls back the maintenance transaction, and releases all advisory,
state, and table locks without replacing rows.

The final existing `ReplaceSoloFamilyRankings` path runs on that same
connection and transaction. Immediately before `TRUNCATE`/binary `COPY`, only
the statement timeout is raised to a bounded 180 seconds; after successful
replacement it is restored to 30 seconds before commit. Table-lock acquisition
remains bounded at five seconds, and source reads remain bounded at 30 seconds.
If replacement reaches 180 seconds, or the lock-holding connection or
transaction is lost, the whole transaction rolls back and replacement cannot
commit on a second connection. The transaction exposes neither a partially
copied table nor a successful `executed` result before commit, but concurrent
API reads can wait on the table lock; this is why live execute requires service
quiescence or separately proven bounded locking.

## Validation

After execute, retain the JSON and verify zero impossible rows:

```sql
SELECT COUNT(*) AS invalid_rows
FROM solo_family_rankings
WHERE songs_played > total_charted_songs
   OR full_combo_count > total_charted_songs
   OR coverage > 1.000000001
   OR fc_rate > 1.000000001;
```

For the `1277` incident shape, confirm every PAD row has denominator `2,788`
and the maximum coverage/FC rate is `1`:

```sql
SELECT
    MIN(total_charted_songs) AS min_denominator,
    MAX(total_charted_songs) AS max_denominator,
    MAX(coverage) AS max_coverage,
    MAX(fc_rate) AS max_fc_rate
FROM solo_family_rankings
WHERE scope_id = 'pad';
```

This repair alone is not publication proof. Run the next full scrape and
require normal post-processing, complete scope-source validation, ranking
guard success, atomic publication, and public-read unfreeze.

## Rollback

If execute output or post-checks are wrong:

1. Keep the worker and service quiesced.
2. Restore the pre-execute same-drive transaction/export evidence, or deploy
   the prior image and rerun its complete rankings path from the intended
   source state.
3. Recheck impossible-row counts, scope counts, and public-read state before
   restoring service.

If no pre-execute row evidence exists, do not improvise per-account edits.
Rebuild all rankings with the prior known-good image/source. The backfill
command has no schema rollback because it creates or alters no schema.
