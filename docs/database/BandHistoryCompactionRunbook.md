# BAND-HISTORY-COMPACT Runbook

## Current decision

**Tier:** compact v3 design and bounded pilot accepted; production rewrite
blocked by the same-drive capacity guard on 2026-07-28.

BAND-HISTORY-COMPACT ran with runtime `gpt-5.6-sol`, reasoning `max`, and
context `long_context`. Published scrape `1267` remained authoritative and
unfrozen. `fstworker` remained exited/offline with restart `no`; no worker,
scrape, service deploy, or production history mutation ran.

Evidence:

`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/band-history-compact-20260728T100500Z`

## Exact live inventory

| Partition | Exact rows | Heap bytes | Index bytes | TOAST bytes | Total bytes |
|---|---:|---:|---:|---:|---:|
| Duets | 215,134,574 | 72,368,676,864 | 81,847,418,880 | 8,192 | 154,235,944,960 |
| Trios | 343,275,419 | 126,774,484,992 | 179,034,488,832 | 8,192 | 305,843,961,856 |
| Quad | 359,383,226 | 149,079,629,824 | 239,654,428,672 | 8,192 | 388,775,297,024 |
| **Total** | **917,793,219** | **348,222,791,680** | **500,536,336,384** | **24,576** | **848,759,203,840** |

The tables are frozen. Production has `BandRankHistory__Mode=Disabled` and
`BandRankHistory__ApiReadSource=V2NarrowOnly`. PostgreSQL statistics since
the 2026-07-13 postmaster start show zero inserts, updates, or deletes.
Completed snapshot metadata spans 2026-04-26 through 2026-07-06, depending
on band type, and 57 distinct dates overall.

The live team/date secondary indexes are public-read owners. Since the same
statistics window they recorded 1,060 Duets, 1,082 Trios, and 1,369 Quad
scans. The equally wide primary keys enforce point identity but recorded no
read scans. Representative cold API SQL used the team/date index and returned
full-range history in 9.095-10.509 ms. The snapshot freshness plan used the
small unique metadata index in 0.582 ms.

The production route had 474 database calls in the captured statement window,
19,737 returned point rows, 7.398 ms mean execution, and no temp I/O. A
rate-safe HTTP baseline covered overall and combo scopes for all band types,
30-day and full-range windows. All nine cases returned HTTP 200; warm p50 was
2.085-2.759 ms and p95 was 2.257-3.234 ms.

Current readers are:

- `/api/rankings/bands/{bandType}/{teamKey}/history`;
- `PlayerDataExportService` band history export;
- `MetaDatabase` status, parity, backfill, and preview diagnostics.

## Chosen v3 design

Keep PostgreSQL as the public source of truth and preserve every v2 row:

- a per-band integer/bigint `team_id` dictionary retaining exact `team_key`;
- an integer combo dictionary retaining exact combo text;
- typed smallint band/scope IDs;
- `BYTEA(16)` decoded MD5 fingerprints;
- monthly `snapshot_date` subpartitions below each band type;
- `fillfactor=100` for this frozen history;
- one primary-key access path ordered as
  `(band_type_id, team_id, scope_id, combo_id_int, snapshot_date)`.

The compact primary key serves both uniqueness and the API lookup, eliminating
the current duplicate-width primary and secondary trees. Proposed DDL is in
`sql/compact-v3-proposed-ddl.sql` under the evidence root.

### Bounded pilot

A same-drive unlogged Duets pilot selected 100,000 team keys and copied
4,651,508 overall rows:

| Metric | Result |
|---|---:|
| Bidirectional row differences | 0 / 0 |
| Source/candidate date range | 2026-04-26 to 2026-07-05 |
| Source/candidate distinct dates | 51 / 51 |
| Compact heap + primary key | 1,172,094,976 bytes |
| Compact bytes per row | 251.98 |
| Current Duets bytes per row | 716.93 |
| Pilot team dictionary | 22,937,600 bytes |
| Matched compact plan | 51 rows in 0.396 ms |
| Matched cold v2 plan | 51 rows in 15.561 ms |
| Warm command p50/p95 | tied at 40/50 ms |

Sampled tuple width fell 33.94% for Duets, 41.78% for Trios, and 48.10% for
Quad. Sampled key width fell from 128.71/166.00/207.73 bytes to about 42.4
bytes. The pilot tables were dropped after validation and free space returned
to baseline.

Conservative pilot-calibrated projections are:

| Partition | Candidate bytes | Projected reclaim | Reduction |
|---|---:|---:|---:|
| Duets | 57,273,958,281 | 96,961,986,679 | 62.87% |
| Trios | 91,698,045,811 | 214,145,916,045 | 70.02% |
| Quad | 96,310,682,272 | 292,464,614,752 | 75.23% |

These are planning estimates, not reclaimed bytes.

## Rejected alternatives

### Date retention

Rejected for this phase. The public API accepts up to 3,650 days and exports
consume band history. No approved product policy permits deleting any of the
retained dates. Row deletes would not return filesystem space because the
current partition key is band type; a date-retention win would still require
partition replacement.

### Same-drive Parquet archive as the public source

Rejected. The data is rarely read but still served. FST has no production
Parquet rehydration or direct-read tier that can preserve byte-identical API
responses. Creating a same-drive archive beside v2 also increases peak space.
Parquet remains suitable only as a future independently verified cold backup
after compact PostgreSQL history is authoritative.

## Capacity blocker

The final production guard sampled `164,830,613,504` free bytes, database
size `3,700,705,048,243` bytes, WAL directory `23,890,755,584` bytes,
published scrape `1267`, public reads unfrozen, and zero active scrapes,
vacuums, index builds, rewrites, or ungranted locks.

The repository rewrite guard rejected Duets:

- required seven-day headroom plus projected candidate:
  `902,775,955,523` bytes;
- available: `164,830,613,504` bytes;
- guard shortfall: `737,945,342,019` bytes.

Even using only the measured one-full-scrape emergency floor,
`60,392,999,803` bytes, a conservative Duets build may consume
`57,273,958,281` bytes and leave `107,556,655,223` bytes. That simplified
projection omits build WAL, temporary sort/hash files, filesystem variance,
and the explicit seven-day rewrite policy. The current capacity tool therefore
correctly applies the stricter rewrite gate. No production build was attempted,
so Trios and Quad were not eligible to start.

## Future execution sequence

Re-run only after the rewrite guard passes with current measured candidate
bytes:

1. Hold the worker and capture public/API/checksum/plan baselines.
2. Build Duets v3 and its dictionaries/month leaves.
3. Validate exact rows, dates, scopes, source-to-v3 bidirectional
   fingerprints, and all nine HTTP cases.
4. Rename/detach-swap while retaining the complete original Duets partition
   under a retired name.
5. Compare p50/p95/p99 and reject a sustained regression above 10%.
6. Drop the retired source only after post-swap validation, then remeasure.
7. Repeat the capacity guard and exact process for Trios, then Quad.

The original v2 partition is the rollback until each step passes. Before a
drop, retain exact recreate DDL, dictionary manifests, partition manifests,
and a source-to-v3 rebuild command. After a drop, the deterministic rebuild
source is the retained v3 dictionaries and points; do not claim Parquet or
date-pruned recovery unless independently drilled.

## Final state

- Production v2 remains authoritative and unchanged.
- Reclaimed production bytes: `0`.
- Final measured free space: `164,830,613,504` bytes at the production guard.
- Published scrape: `1267`, public reads unfrozen.
- Postgres, `fstservice`, and `festivalweb` remained healthy.
- Worker remained held/offline; no scrape started.

## Next storage phase

Evaluate `player_score_observations` as the next zero-scratch reclaim. It is
about 12.68 GB, has no production reader, and published scrape `1267` can now
be checked for both default-off writers. Keep BAND-HISTORY-COMPACT as the
largest pending rewrite and resume it only when the same-drive rewrite guard
passes.
