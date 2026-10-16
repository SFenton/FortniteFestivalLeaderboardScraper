---
status: decision
owner: data
last_verified: 2026-10-16
last_verified_commit: c56460fa
sources:
  - FSTService/BandRankHistoryOptions.cs
  - FSTService/Persistence/MetaDatabase.BandRankHistoryCompactDaily.cs
  - FSTService/Scraping/PostScrapeOrchestrator.cs
  - docs/architecture/data-storage.md
update_triggers:
  - The band rank-history mode, compact table layout, daily partitioning, or retention changes.
---

# ADR 0015: Record band rank history as one compact partition per day

## Context

Band rank history powers the band page's 30-day rank chart. It was turned
off (`BandRankHistory:Mode=Disabled`) around 2026-07-06, during the
disk-full incident. In its first 71 days the v2 history had grown to about
800 GB: 918 million rows, about 1.1 KB per row, mostly indexes. Its
background jobs took 1.5 to 2.5 hours per band type per day on average, and
up to a day. In July the frozen rows were compacted into the `points_v3`
tables (221 GB, integer team and combo ids, monthly partitions), which the
API reads. Since then the chart has shown nothing. The operator wants band
history back with 30 days kept, like solo history.

Re-enabling the v2 writer would cost about 400 GB for 30 days and hours of
daily work that does not fit the current 15-minute gaps between scrapes. Its
new rows would also be invisible, because reads come only from the compact
tables.

## Decision

`BandRankHistory:Mode=CompactDaily` (the default) writes history straight
into the compact tables:

- Once per UTC day per band type, in the first post-publication cleanup after
  midnight, every published ranking row becomes one compact point in a new
  daily partition. It is built unattached and attached with a
  bound `CHECK`, so readers never wait.
- Retention drops whole partitions whose dates are all older than
  `BandRankHistory:RetentionDays` (default 30). No row is deleted, and the
  cleanup is not pressure-gated.
- Reads and status use the compact tables, with no background jobs.

## Consequences

- About 21.7 million ranking rows a day at about 240 bytes each (index
  included) is about 5.2 GB a day, or about 160 GB for 31 days. A production
  build of Duets during a fetch window (4.1 million rows, rolled back) took 34
  seconds to insert and 1 second to index, about 1 GB. All three band types
  should add about 3 to 4 minutes to the first scrape's cleanup after UTC
  midnight.
- Every team gets a point every day, so charts have no gaps, and a team that
  has not changed still shows a flat line.
- The frozen April–July compact partitions are older than 30 days, so the
  first retention run with a 30-day window drops them (221 GB).
- The v2 writer, its job tables, and the legacy row-delete retention remain
  for the `Inline` and `Background` modes only.
