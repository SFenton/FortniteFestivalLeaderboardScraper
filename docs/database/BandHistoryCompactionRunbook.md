# BAND-HISTORY-COMPACT Runbook

## Current decision

**Tier:** compact v3 accepted and promoted for Band Duets on 2026-07-28.
The incomplete, unpromoted Trios v3 build was reclaimed independently on the
same date. Trios and Quad remain on v2 and require separate future candidates
and capacity guards.

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
| Quad | 359,383,226 | 149,079,629,824 | 239,654,428,672 | 388,775,297,024 |

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

`BandRankHistory:CompactV3DuetsReadEnabled` is default-off. Production enables
it only for Duets; Trios and Quad continue through `V2NarrowOnly`. A readiness
row prevents candidate reads before validation, and successful readiness is
cached for the service lifetime.

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

## Final state

- Duets API/export reads use compact v3.
- The v2 Duets leaf no longer exists.
- No Trios v3 candidate object or readiness row remains.
- Trios and Quad v2 remain authoritative and unchanged.
- Published scrape `1267` remains unfrozen.
- Postgres, `fstservice`, and `festivalweb` are healthy.
- `fstworker` remains held/offline with restart `no`.
- No scrape ran.

## Next storage phase

Any future Trios v3 attempt starts from a clean schema and must be a new,
independently switchable data candidate after the active production scrape
window. Re-run the measured guard, copy all 51 dates, build the deferred local
unique indexes, prove full parity, and promote a Trios-specific read switch
before retaining the candidate or releasing its 305,843,961,856-byte v2
source. Do not start Quad until Trios has completed, released its source, and
the guard has been recalculated.
