---
status: living-runbook
owner: operations
last_verified: 2026-10-02
last_verified_commit: 598965ef
sources:
  - tools/postgres-retire-redundant-band-projection-indexes.py
  - tools/postgres-retire-redundant-band-projection-indexes.test.py
  - FSTService/Persistence/DatabaseInitializer.cs
  - FSTService/Persistence/ImprovementNotificationService.cs
  - FSTService/Persistence/BandCurrentProjectionBuilder.cs
  - https://www.postgresql.org/docs/17/sql-dropindex.html
update_triggers:
  - The current band projection indexes, their readers, or the retirement/rollback commands change.
---

# Redundant band projection index retirement

`ix_cble_duets_team_scope_generation`, `ix_cble_trios_team_scope_generation`,
and `ix_cble_quad_team_scope_generation` were per-partition indexes on
`current_band_leaderboard_entries_{duets,trios,quad}` with the key
`(band_type, team_key, song_id, ranking_scope, scope_combo_id,
projection_generation)`. They repeat the partitioned `ix_cble_team_song` key
with `projection_generation` appended. Registered-band notification detection,
which they were added for, joins only on `(band_type, team_key)`, and the
projection's candidate cleanup uses the primary key. Over 14 days of
statistics (about 56 scrapes, to 2026-10-02) the trios and quad copies had zero
scans and the duets copy 5,798; the planner already uses `ix_cble_team_song`
for the trios and quad partitions. Together they held about 33 GB while every
scrape rewrites about 15.6 million projection rows, so each insert and delete
maintained a fifth index for no reader benefit.

`DatabaseInitializer` no longer creates them. A deployed image that still
contains the old `CREATE INDEX IF NOT EXISTS` statements would rebuild them
non-concurrently at startup, so run `apply` only after every running service
and worker image includes this change.

## Commands

```bash
# Read-only: definitions, sizes, scan counts, and the ix_cble_team_song fallback per partition
python3 tools/postgres-retire-redundant-band-projection-indexes.py plan
# Network-bound fetch window only; DROP INDEX CONCURRENTLY per index, lock_timeout 2s with retries
python3 tools/postgres-retire-redundant-band-projection-indexes.py apply
# Rollback: CREATE INDEX CONCURRENTLY from the recorded definitions
python3 tools/postgres-retire-redundant-band-projection-indexes.py rollback
```

`apply` refuses unless each partition has a valid, ready `ix_cble_team_song`
child with the expected key and each present target matches its retired
definition exactly and is not attached to a partitioned index. It re-checks the
window before each drop, records `dropping` and `dropped` entries with the
definition, size, and scan count in
`fst-data/evidence/band-projection-index-retirement/manifest.jsonl`, and
verifies each index is gone. Lock contention defers (exit code 3); other
failures refuse (exit code 2). `rollback` refuses an invalid leftover index
instead of silently replacing it.

## Production state

Applied on 2026-10-02 during scrape `1459`'s leaderboard fetch, after the API
and worker both ran an image without the old `CREATE INDEX` statements
(bundle revision `bf4c4765`). `plan` reported no blockers. `apply` dropped all
three indexes on the first attempt each, reclaiming about 9.4, 9.8, and
11.6 GiB (30.8 GiB in total; the drive went from 44% to 43% used). Afterwards
a duets team lookup used the `ix_cble_team_song` partition index (about 0.1
ms). The manifest under
`fst-data/evidence/band-projection-index-retirement/` records each drop and its
definition for `rollback`. Running `apply` again finds nothing to drop.
