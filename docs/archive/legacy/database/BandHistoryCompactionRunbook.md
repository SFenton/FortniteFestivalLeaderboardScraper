> [!CAUTION]
> **COMPLETED - DO NOT RE-EXECUTE.** Historical validation and rollback
> evidence only.

# BAND-HISTORY-COMPACT Runbook

## Current decision

**Tier:** compact v3 accepted and promoted for Band Duets on 2026-07-28,
Band Trios on 2026-07-29, and Band Quad on 2026-07-30. The incomplete first
Trios build was reclaimed before the clean rebuild. All three public/API export
read paths now use independently reversible compact-v3 switches.

**2026-07-29 clean Trios execution:** the compact relation contains all
`343,275,419` rows / 51 dates with exact monthly full-row multiset hashes and
valid local/parent unique indexes. Production enables the independently
reversible `CompactV3TriosReadEnabled` switch. The live service A/B, source
detach/reattach rollback, and v2 source drop all passed.

BAND-HISTORY-COMPACT ran with runtime `gpt-5.6-sol`, reasoning `max`, and
context `long_context`. Published scrape `1267` remained authoritative and
unfrozen. `fstworker` remained exited/offline with restart `no`; no worker or
scrape started.

Evidence:

- initial design/pilot:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/band-history-compact-20260728T100500Z`;
- lower-scratch build/cutover:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/band-history-compact-lowscratch-20260728T113000Z`;
- incomplete Trios candidate evaluation/reclaim:
  `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/trios-incomplete-candidate-reclaim-20260728T1914Z`.

## Source inventory

| Partition | Exact rows | Heap bytes | Index bytes | Total bytes |
|---|---:|---:|---:|---:|
| Duets | 215,134,574 | 72,368,676,864 | 81,847,418,880 | 154,235,944,960 |
| Trios | 343,275,419 | 126,774,484,992 | 179,034,488,832 | 305,843,961,856 |
| Quad | 359,383,226 | 149,079,629,824 | 239,699,402,752 | 388,779,032,576 |

Production history writes remain disabled. The v2 points recorded zero
inserts, updates, or deletes in the observed PostgreSQL statistics window.
The public API and player export remain readers.

## Why the original 902.78 GB guard blocked

The original `902,775,955,523`-byte result was a policy guard, not a measured
physical rebuild peak:

| Component | Bytes | Share |
|---|---:|---:|
| Seven-day reserve: 14 full-run windows | 845,501,997,242 | 93.66% |
| Conservative compact candidate | 57,273,958,281 | 6.34% |
| Source read allocation | 0 | Existing source was already allocated |
| Build WAL/temp/index spill | 0 | Not separately modeled |

The retained 154,235,944,960-byte source was not added to required free space.
It remained allocated for rollback.

The capacity guard now permits an explicit maintenance/rewrite floor only when
it is at least the measured one-full-scrape emergency window. Default
seven-day behavior is unchanged.

## Lower-scratch evaluation

| Variant | Evidence | Decision |
|---|---|---|
| Chunked copy, deferred indexes | Six committed date chunks; one checkpoint after each; local indexes built after heap load | Accepted and executed |
| Incremental source release | v2 Duets was one physical band-type leaf with no date children | Rejected: date `DELETE` cannot return filesystem space; only whole-leaf detach/drop can |
| Memory/parallel tuning | Copy used `work_mem=768MB`, two read workers, `wal_compression=on`; index builds used `maintenance_work_mem=1GB`, one maintenance worker | Accepted |
| `synchronous_commit=off` | Reduced commit wait only; it did not reduce WAL. Every chunk completed a checkpoint before validation | Accepted for rebuildable offline candidate |
| Reduced indexes | v3 has one unique partitioned index family ordered by team/scope/combo/date; no duplicate API secondary family | Accepted |
| Parquet first | 100,000-row oldest-date sample compressed from 32,440,701 CSV bytes to 8,074,450 Parquet bytes and rehydrated with exact count/range/checksum | Rejected as live source: no cold date is outside the 3,650-day API/export contract |

The existing v2 leaf cannot release dates incrementally. The complete source
must stay until compact reads are promoted and the whole leaf can be detached.

## Calibrated guard and actual build cost

A logged April canary measured:

| Component | Measured result |
|---|---:|
| Heap bytes/row | 199.805 |
| Unique index bytes/row | 40.597 |
| Full Duets dictionary | 401,833,984 bytes |
| April copy WAL | 4,567,004,168 bytes |
| April index WAL | 279,649,392 bytes |
| Build temp observed | 0 bytes at monitor ticks |

The calibrated start guard reserved:

- `60,392,999,803` bytes for one complete scrape;
- `46,831,753,267` remaining candidate bytes;
- `15,753,805,824` possible WAL-directory growth to `max_wal_size`;
- `7,349,632,273` conservative largest-leaf index sort spill;
- `8,000,000,000` additional variance.

Required free space was `138,328,191,167` bytes. Measured free space was
`164,826,165,248`, leaving `26,497,974,081` bytes of guarded margin.

The final compact objects are:

| Object | Heap bytes | Index bytes | Total bytes |
|---|---:|---:|---:|
| Four monthly point leaves | 42,984,955,904 | 8,735,670,272 | 51,732,602,880 |
| Team/combo dictionaries | 193,060,864 | 208,683,008 | 401,833,984 |
| **Total** | **43,178,016,768** | **8,944,353,280** | **52,134,436,864** |

Copy chunks stayed about 26.5 GB above the calibrated requirement. The build
generated about 51.8 GB of cumulative WAL, but checkpoints and the absence of
archive/replication slots kept the retained WAL directory below its existing
23.9 GB baseline. Index builds created no observed PostgreSQL temp files.

## Correctness proof

- Exact candidate count: `215,134,574`.
- Exact range: 2026-04-26 through 2026-07-05; 56 dates.
- Exact date/scope/combo groups: `103/103`, zero count mismatches.
- April: zero bidirectional full-row differences for `20,323,483` rows.
- May, June, and July: exact counts plus four independent 64-bit multiset
  hashes and a fifth numeric hash sum matched.
- A deterministic 1,003-team sample compared `131,444` reconstructed full
  rows with zero differences.
- Overall/combo, 30-day/full-range HTTP responses for all three band types
  remained `9/9` byte-identical before cutover, after cutover, after detach,
  and in five post-drop captures.

One unbounded May `EXCEPT ALL` validation was rejected after the operating
system killed its PostgreSQL backend for memory pressure. PostgreSQL completed
crash recovery in seconds and the full public path was healthy. A later exact
team sample also spilled about 58 GB and was not repeated. Final monthly
validation used constant-memory aggregates, under 1 GB container memory, and
zero temp files.

## Read cutover and performance

`BandRankHistory:CompactV3DuetsReadEnabled`,
`BandRankHistory:CompactV3TriosReadEnabled`, and
`BandRankHistory:CompactV3QuadReadEnabled` are independently default-off.
Production enables each only after its readiness row passes. A readiness row
prevents candidate reads before validation, and successful readiness is cached
for the service lifetime.

Matched 40-call HTTP A/B:

| Case | v2 p50/p95 | v3 p50/p95 | Decision |
|---|---:|---:|---|
| Duets overall, 30 days | 2.898 / 7.952 ms | 3.023 / 7.196 ms | Accepted |
| Duets overall, full | 2.640 / 3.971 ms | 2.668 / 3.634 ms | Accepted |
| Duets combo, full | 2.599 / 3.562 ms | 2.832 / 4.450 ms | Accepted with explicit tradeoff |

Combo p95 increased 24.93%, but the absolute increase was 0.888 ms and stayed
below 5 ms. It is accepted for the exact 102.10 GB net database reduction.
The direct full-range v3 plans returned 50 rows in 0.332 ms overall and
0.180 ms combo, with no temp I/O.

Matched Trios 20-call warm A/B after removing an incremental-sort query shape:

| Case | v2 p50/p95 | v3 p50/p95 | Decision |
|---|---:|---:|---|
| Trios overall, 30 days | 2.996 / 3.372 ms | 3.010 / 4.227 ms | Accepted with explicit 0.855 ms tradeoff |
| Trios overall, full | 2.848 / 4.136 ms | 2.790 / 3.315 ms | Accepted |
| Trios combo, full | 2.692 / 4.465 ms | 2.723 / 3.780 ms | Accepted |

All v3 p95 values remained below 5 ms, two slices improved, and all three
payloads were exact. The small 30-day regression is accepted for the
222.18 GB net database reduction.

## Detach, rollback, and drop

Before detach, the source received a validated
`CHECK (band_type='Band_Duets')` constraint. This makes reattachment
metadata-only.

- Transactional detach/rollback rehearsal passed.
- After committed detach, all nine API cases remained byte-identical.
- A committed-state reattach rehearsal completed in 11.827 ms, automatically
  reattaching both the primary and team/date child indexes, then rolled back.
- The retired source was dropped without `CASCADE` only after those checks.
- Post-commit rebuild SQL is
  `tools/sql/postgres-band-history-compact-v3/duets-rebuild-v2.sql`.
- Runtime rollback before a rebuild is to disable
  `BAND_RANK_HISTORY_COMPACT_V3_DUETS_READ_ENABLED` and deploy the prior
  service image. After source drop, v2 restoration requires the checked-in
  deterministic rebuild and sufficient same-drive headroom.

## Reclaim outcome

| Metric | Result |
|---|---:|
| Retired source drop | 154,235,944,960 database bytes |
| Compact replacement | 52,134,436,864 database bytes |
| Net database reduction from phase start | 102,101,475,328 bytes |
| Immediate filesystem gain from source drop | 154,236,563,456 bytes |
| Terminal sampled free space | 276,272,738,304 bytes |

The filesystem gain from the phase start is larger than the net relation
reduction because retained WAL also fell from 23,890,755,584 to
14,562,623,488 bytes.

## Incomplete Trios candidate reclaim

A follow-on offline build created a compact Trios candidate but stopped before
validation, index construction, or promotion:

| Candidate property | Exact evidence |
|---|---:|
| Point rows | 335,757,940 |
| Authoritative v2 rows | 343,275,419 |
| Missing rows | 7,517,479 |
| Candidate dates | 49 / 51 |
| Missing dates | 2026-07-01 and 2026-07-05 |
| Point-table indexes | 0 |
| Candidate bytes | 73,478,529,024 |

The candidate covered 2026-04-26 through 2026-06-30. Every included date had
the same row count as v2, but the July partition was empty. Its
`band_rank_history_compact_v3_state` row remained `building`, with
`row_count=0`, no validation timestamp, and no promotion timestamp.

Ownership proof found:

- no exact repository or deployed-binary reference to the Trios v3 objects;
- no view, materialized view, routine, trigger, policy, publication, prepared
  statement, API reader, or runtime writer;
- only manual build `INSERT` statements in `pg_stat_statements`;
- production `BandRankHistory__Mode=Disabled`,
  `WriteMode=V2Only`, and `ApiReadSource=V2NarrowOnly`;
- the only compact runtime switch and SQL implementation are Duets-specific.

The public Trios plan continued to use the v2 team/date index. Three
representative Trios history payloads were byte-identical to the prior v2
baseline, while Duets retained its `ready` compact state and v3 index plan.
Two pre-action public captures were `13/13` exact.

A 256 MB bounded full v2/v3 checksum probe was rejected when its parallel plan
hit `temp_file_limit`; no mutation or lock remained and public health stayed
HTTP 200. Exact candidate manifests, the existing exact v2 date manifest,
authoritative query plans, and live payload parity provided the replacement
proof.

The rollback-only drop rehearsal completed in 0.15 seconds and restored all
objects. The committed 0.70-second transaction then deleted only the
fail-closed Trios `building` state row and dropped the compact Trios parent,
its four monthly leaves, both dictionaries, and their owned sequences without
`CASCADE`.

| Reclaim metric | Result |
|---|---:|
| Database bytes reclaimed | 73,478,529,024 |
| Stable filesystem gain | 73,477,279,744 |
| Final filesystem free | about 285.49 GB |
| Final modeled full-scrape margin | 225,098,810,501 bytes |

Immediate and 60-second public captures were `13/13` exact; all three Trios
payloads were also exact. Published scrape `1267` remained unfrozen,
notifications remained complete, Duets v3 stayed `ready`, Trios v2 stayed
intact, and no lock, query, vacuum, index build, or maintenance operation
remained.

## Clean Trios promotion and reclaim

After scrape `1268` published/unfroze, the clean rebuild ran under a calibrated
rewrite guard:

- six committed date chunks copied all `343,275,419` rows;
- four monthly source/candidate comparisons each matched row count plus five
  full-row multiset hashes;
- four local unique indexes and the attached parent index were valid/ready;
- `118/118` focused tests and the Release build passed;
- the default-off Trios read switch was deployed to `fstservice` only;
- baseline, candidate, optimized, post-detach, and post-drop public captures
  remained `13/13` exact, while all nine Duets/Trios/Quad history payloads
  remained exact.

The v2 source received a validated band-type constraint. Detach rollback
completed in 5.132 ms. After committed detach and public parity, a metadata-only
reattach rehearsal completed in 3.306 ms and rolled back to the detached state.
The source was then dropped without `CASCADE` in 1.11 seconds.

| Trios promotion metric | Result |
|---|---:|
| Compact v3 bytes | 83,664,461,824 |
| Retired v2 source bytes | 305,843,961,856 |
| Net database reduction | 222,179,500,032 |
| Stable filesystem gain | 227,630,555,136 |
| Final database size | 3,389,362,312,883 |
| Final filesystem free | about 483.72 GB |

The first reclaim guard correctly blocked while three legitimate candidate
autovacuums ran. No backend was terminated; the workflow waited until all
vacuums completed, reran the guard, and then dropped the source. Post-drop
immediate and 60-second checks retained exact parity, zero locks/queries, and
healthy public paths.

Post-drop rebuild SQL is
`tools/sql/postgres-band-history-compact-v3/trios-rebuild-v2.sql`.

## Clean Quad promotion and reclaim

After scrape `1271` published/unfroze and completed notifications, the Quad
candidate resumed from its cadence-yield checkpoint:

- exact rows: `359,383,226`;
- date range: 2026-04-26 through 2026-07-05;
- four local unique indexes plus the attached parent index valid/ready;
- April, May, June, and July each matched v2 on row count plus five full-row
  multiset hashes;
- `119/119` focused band-history tests and the Release build passed;
- all three Quad history payloads and all `13/13` public fingerprints were
  byte-exact before cutover, after cutover, after detach, immediately after
  source drop, and 60 seconds after source drop.

The default-off `CompactV3QuadReadEnabled` path is in commit `791f7f74`; the
failure-isolated rebuild and guarded detach follow in `a05eb605` and
`1e257b48`. Production service image
`fstservice:band-history-quad-a05eb605` enables the Quad flag while the worker
remains held with restart `no`.

Matched same-image warm HTTP A/B:

| Case | v2 p50/p95 | v3 p50/p95 | Decision |
|---|---:|---:|---|
| Quad combo, full | 1.755 / 3.175 ms | 2.247 / 2.737 ms | Accepted |
| Quad overall, 30 days | 2.191 / 5.030 ms | 2.234 / 4.269 ms | Accepted |
| Quad overall, full | 2.131 / 3.554 ms | 2.676 / 4.203 ms | Accepted with explicit +0.649 ms tradeoff |

All v3 p95 values remained below 5 ms. The full-overall p95 regression is
accepted for the large storage reduction; the other two slices improved.

The source received a validated
`CHECK (band_type = 'Band_Quad')` constraint. Detach rollback completed in
13.786 ms. After committed detach, metadata-only reattach completed in
4.257 ms, automatically resolving both child index attachments, and rolled
back to the detached state.

| Quad promotion metric | Result |
|---|---:|
| Compact v3 points + dictionaries | 87,994,753,024 bytes |
| Retired v2 source | 388,779,032,576 bytes |
| Net database reduction | 300,784,279,552 bytes |
| Immediate filesystem gain | 388,780,703,744 bytes |
| Final database size | 3,132,719,068,851 bytes |
| Terminal filesystem free | 732,566,126,592 bytes |

Post-drop rebuild SQL is
`tools/sql/postgres-band-history-compact-v3/quad-rebuild-v2.sql`. Runtime
rollback before a rebuild is to disable
`BAND_RANK_HISTORY_COMPACT_V3_QUAD_READ_ENABLED` and deploy the prior service
image.

## Final state

- Duets API/export reads use compact v3.
- Trios API/export reads use compact v3.
- Quad API/export reads use compact v3.
- The v2 Duets leaf no longer exists.
- The v2 Trios leaf no longer exists.
- The v2 Quad leaf no longer exists.
- Published scrape `1271` remains unfrozen and notification-complete.
- Postgres, `fstservice`, and `festivalweb` are healthy.
- `fstworker` remains held/offline with restart `no`.
- No scrape ran during the Quad validation/cutover.

### API history-status interpretation

- Each enabled compact-v3 flag is effective only while its per-band readiness
  row is `ready`. The history endpoint and its status metadata use the same
  source decision.
- For a ready compact source, `historyComputedThrough` comes from
  `band_rank_history_compact_v3_state.max_snapshot_date`; the status probe
  does not depend on dropped v2 point leaves.
- `BandRankHistory:Mode=Disabled` means no continuous history writes are
  expected. The API therefore reports `historyStatus=disabled`, retains the
  current-ranking, history-through, and latest-job timestamps, and explains
  the readable historical cutoff.
- If writes are enabled, `current` requires the history date to equal the UTC
  calendar date of `currentRankingsComputedAt`. An older or otherwise
  mismatched date is `stale`; queued/running/paused background jobs remain
  `catching_up`, and failed jobs remain `failed`.

## Next phase

Begin the approved atomic-publication proof harness and gating probes. Do not
start the next scrape until its paired network/data card is ready.
> [!CAUTION]
> **COMPLETED - DO NOT RE-EXECUTE.** This runbook is retained as historical
> evidence and rollback context, not as an active procedure.
