# Operational Probing Reference

Use this reference for read-only database inventory, health checks, monitoring, incident triage, and current-system diagnosis.

## Safety rules

- Prefer read-only probes and bounded result sets.
- Disable pagers and redact secrets from output.
- Avoid broad table scans during live-sensitive scrape/publication windows unless the operator approves the load.
- Use `EXPLAIN` before `EXPLAIN ANALYZE`; only run `ANALYZE` variants when the query and live load are safe.
- Do not terminate database backends unless the operator explicitly approves a specific PID and the blocker is understood.

## Live-safety preflight

Run from the active compose project directory. Production is normally `/home/sfenton/Docker/FestivalServiceTracker`, not the repo `deploy/` template.

```bash
docker compose ps
docker stats --no-stream
docker compose exec -T fstservice curl -sf http://localhost:8080/readyz
docker compose exec -T postgres pg_isready -U fst -d fstservice
```

## Postgres health probes

```bash
docker compose exec -T postgres psql -U fst -d fstservice -c "
SELECT now() AS checked_at;

SELECT pid, backend_type, state, now() - query_start AS age, wait_event_type, wait_event, left(query, 180) AS query
FROM pg_stat_activity
WHERE datname = current_database()
ORDER BY age DESC NULLS LAST
LIMIT 20;

SELECT count(*) AS ungranted_locks
FROM pg_locks
WHERE NOT granted;"
```

## Table and index size probes

```bash
docker compose exec -T postgres psql -U fst -d fstservice -c "
SELECT relname AS table_name,
       pg_size_pretty(pg_total_relation_size(relid)) AS total_size,
       pg_size_pretty(pg_relation_size(relid)) AS heap_size,
       n_live_tup,
       n_dead_tup,
       last_vacuum,
       last_autovacuum,
       last_analyze,
       last_autoanalyze
FROM pg_stat_user_tables
ORDER BY pg_total_relation_size(relid) DESC
LIMIT 25;"
```

```bash
docker compose exec -T postgres psql -U fst -d fstservice -c "
SELECT schemaname, tablename, indexname,
       pg_size_pretty(pg_relation_size(indexrelid)) AS index_size,
       idx_scan
FROM pg_stat_user_indexes
ORDER BY pg_relation_size(indexrelid) DESC
LIMIT 25;"
```

## Scrape and publication probes

Use table-specific watermarks that match the task. Common FST examples:

```bash
docker compose exec -T postgres psql -U fst -d fstservice -c "
SELECT published_scrape_id, is_frozen, frozen_scrape_id, updated_at
FROM scrape_publication_state;

SELECT id, status, started_at, completed_at, error_message
FROM scrape_log
ORDER BY id DESC
LIMIT 10;

SELECT scrape_id, instrument, total_observed, new_rows, changed_rows, unchanged_rows
FROM leaderboard_logical_write_metrics
ORDER BY scrape_id DESC, instrument
LIMIT 30;"
```

For data movement, record counts and scope ranges before and after:

```bash
docker compose exec -T postgres psql -U fst -d fstservice -c "
SELECT count(*) AS rows,
       count(DISTINCT song_id) AS songs,
       count(DISTINCT account_id) AS accounts
FROM leaderboard_entries_snapshot_pro_lead
WHERE scrape_id = 1214;"
```

## Query-plan probes

Start with plain `EXPLAIN`:

```bash
docker compose exec -T postgres psql -U fst -d fstservice -c "
EXPLAIN
SELECT *
FROM leaderboard_entries_snapshot_pro_lead
WHERE scrape_id = 1214
  AND song_id = 'example-song-id'
ORDER BY rank
LIMIT 100;"
```

Only escalate to `EXPLAIN (ANALYZE, BUFFERS)` after confirming the query is bounded and safe.

## Probe report

| Surface | Probe | Result | Risk | Next action |
|---|---|---|---|---|
| `<db/table/query>` | `<command/metric>` | `<finding>` | `<low/medium/high>` | `<monitor/fix/defer>` |
