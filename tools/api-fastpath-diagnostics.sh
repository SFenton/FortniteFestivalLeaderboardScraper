#!/usr/bin/env bash
set -euo pipefail

API_BASE="${API_BASE:-http://localhost:8081}"
PG_CONTAINER="${PG_CONTAINER:-fst-postgres}"
PG_USER="${PG_USER:-fst}"
PG_DB="${PG_DB:-fstservice}"
OUT_DIR="${OUT_DIR:-harness-output/api-fastpath-diagnostics-$(date -u +%Y%m%dT%H%M%SZ)}"
MAX_TIME="${MAX_TIME:-5}"
STATEMENT_TIMEOUT_MS="${STATEMENT_TIMEOUT_MS:-15000}"

mkdir -p "$OUT_DIR"

psql_cmd() {
  docker exec "$PG_CONTAINER" psql -U "$PG_USER" -d "$PG_DB" -v ON_ERROR_STOP=1 -P pager=off "$@"
}

json_escape() {
  python3 -c 'import json,sys; print(json.dumps(sys.stdin.read()))'
}

write_heading() {
  printf '\n## %s\n\n' "$1" >> "$OUT_DIR/report.md"
}

cat > "$OUT_DIR/report.md" <<EOF
# API Fast-Path Diagnostics

Generated: $(date -u +%Y-%m-%dT%H:%M:%SZ)
API base: \`$API_BASE\`
Postgres container: \`$PG_CONTAINER\`

This report is designed for before/after comparisons while keeping production deployment untouched. It captures live endpoint latency, database pressure, projection readiness, and direct SQL plan comparisons between the old dynamic current-state reconstruction and the projection-backed read model.
EOF

write_heading "Endpoint Timing"
{
  printf '['
  first=1
  for path in \
    "/healthz" \
    "/api/service-info" \
    "/api/account/search?q=SFenton&limit=10" \
    "/api/bands/search?q=SFentonX&pageSize=10" \
    "/api/player/195e93ef108143b2975ee46662d4d0e1" \
    "/api/leaderboard/song-a/Solo_Guitar?top=10" \
    "/api/rankings/Solo_Guitar?page=1&pageSize=50"
  do
    tmp="$OUT_DIR/curl-body.tmp"
    status_time=$(curl --max-time "$MAX_TIME" -fsS -o "$tmp" -w '%{http_code} %{time_total}' "$API_BASE$path" 2>/dev/null || true)
    status=$(printf '%s' "$status_time" | awk '{print $1}')
    time_total=$(printf '%s' "$status_time" | awk '{print $2}')
    [[ -n "$status" ]] || status="000"
    [[ -n "$time_total" ]] || time_total="$MAX_TIME"
    body_bytes=$(wc -c < "$tmp" 2>/dev/null || printf '0')
    if [[ $first -eq 0 ]]; then printf ','; fi
    first=0
    printf '\n  {"path":%s,"status":%s,"timeSeconds":%s,"bodyBytes":%s}' \
      "$(printf '%s' "$path" | json_escape)" \
      "$(printf '%s' "$status" | tr -cd '0-9')" \
      "$time_total" \
      "$(printf '%s' "$body_bytes" | tr -cd '0-9')"
  done
  printf '\n]\n'
} > "$OUT_DIR/endpoint-timings.json"
rm -f "$OUT_DIR/curl-body.tmp"

printf 'Raw: [%s](%s)\n\n' "endpoint-timings.json" "endpoint-timings.json" >> "$OUT_DIR/report.md"
if command -v jq >/dev/null 2>&1; then
  jq -r '.[] | "- \(.path): status=\(.status), time=\(.timeSeconds)s, bytes=\(.bodyBytes)"' "$OUT_DIR/endpoint-timings.json" >> "$OUT_DIR/report.md"
else
  sed 's/^/- /' "$OUT_DIR/endpoint-timings.json" >> "$OUT_DIR/report.md"
fi

write_heading "Database Pressure"
psql_cmd -c "SELECT now() AS sampled_at; SELECT state, COALESCE(wait_event_type,'none') AS wait_type, COALESCE(wait_event,'none') AS wait_event, COUNT(*) FROM pg_stat_activity WHERE datname = current_database() GROUP BY 1,2,3 ORDER BY COUNT(*) DESC, state; SELECT COUNT(*) FILTER (WHERE query LIKE 'WITH active_snapshots AS (%') AS active_dynamic_current_state_queries, MIN(now() - query_start) FILTER (WHERE query LIKE 'WITH active_snapshots AS (%') AS youngest_dynamic, MAX(now() - query_start) FILTER (WHERE query LIKE 'WITH active_snapshots AS (%') AS oldest_dynamic FROM pg_stat_activity WHERE datname = current_database() AND state='active';" > "$OUT_DIR/db-pressure.txt"
printf 'Raw: [%s](%s)\n\n```text\n' "db-pressure.txt" "db-pressure.txt" >> "$OUT_DIR/report.md"
sed -n '1,120p' "$OUT_DIR/db-pressure.txt" >> "$OUT_DIR/report.md"
printf '\n```\n' >> "$OUT_DIR/report.md"

write_heading "Projection Readiness"
psql_cmd -c "SELECT COUNT(*) AS ready_scopes, SUM(row_count) AS projected_rows, MIN(last_rebuilt_at) AS oldest_rebuild, MAX(last_rebuilt_at) AS newest_rebuild FROM solo_current_projection_scope WHERE status='ready'; SELECT instrument, COUNT(*) AS ready_scopes, SUM(row_count) AS projected_rows FROM solo_current_projection_scope WHERE status='ready' GROUP BY instrument ORDER BY instrument; SELECT * FROM solo_current_projection_state; SELECT * FROM band_search_projection_state;" > "$OUT_DIR/projection-readiness.txt"
printf 'Raw: [%s](%s)\n\n```text\n' "projection-readiness.txt" "projection-readiness.txt" >> "$OUT_DIR/report.md"
sed -n '1,160p' "$OUT_DIR/projection-readiness.txt" >> "$OUT_DIR/report.md"
printf '\n```\n' >> "$OUT_DIR/report.md"

sample=$(psql_cmd -At -c "SELECT song_id || '|' || instrument FROM solo_current_projection_scope WHERE status='ready' AND row_count > 0 ORDER BY row_count DESC LIMIT 1")
SAMPLE_SONG="${SAMPLE_SONG:-${sample%%|*}}"
SAMPLE_INSTRUMENT="${SAMPLE_INSTRUMENT:-${sample##*|}}"

write_heading "Dynamic vs Projection SQL Proof"
printf 'Sample scope: song \`%s\`, instrument \`%s\`\n\n' "$SAMPLE_SONG" "$SAMPLE_INSTRUMENT" >> "$OUT_DIR/report.md"

psql_cmd -c "SET statement_timeout = '${STATEMENT_TIMEOUT_MS}ms'; EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) WITH active_snapshot AS ( SELECT active_snapshot_id FROM leaderboard_snapshot_state WHERE song_id = '$SAMPLE_SONG' AND instrument = '$SAMPLE_INSTRUMENT' AND is_finalized = TRUE AND active_snapshot_id IS NOT NULL ), base_rows AS ( SELECT account_id, score, accuracy, is_full_combo, stars, season, difficulty, percentile, end_time, api_rank, source, first_seen_at FROM leaderboard_entries WHERE song_id = '$SAMPLE_SONG' AND instrument = '$SAMPLE_INSTRUMENT' AND NOT EXISTS (SELECT 1 FROM active_snapshot) UNION ALL SELECT account_id, score, accuracy, is_full_combo, stars, season, difficulty, percentile, end_time, api_rank, source, first_seen_at FROM leaderboard_entries_snapshot WHERE song_id = '$SAMPLE_SONG' AND instrument = '$SAMPLE_INSTRUMENT' AND snapshot_id = (SELECT active_snapshot_id FROM active_snapshot) ), candidate_rows AS ( SELECT account_id, score, accuracy, is_full_combo, stars, season, difficulty, percentile, end_time, api_rank, source, first_seen_at, 1 AS origin_precedence, 0 AS source_priority FROM base_rows UNION ALL SELECT account_id, score, accuracy, is_full_combo, stars, season, difficulty, percentile, end_time, api_rank, source, first_seen_at, 0 AS origin_precedence, source_priority FROM leaderboard_entries_overlay WHERE song_id = '$SAMPLE_SONG' AND instrument = '$SAMPLE_INSTRUMENT' ), resolved_rows AS ( SELECT DISTINCT ON (account_id) account_id, score, accuracy, is_full_combo, stars, season, difficulty, percentile, end_time, api_rank, source, first_seen_at FROM candidate_rows ORDER BY account_id, origin_precedence ASC, source_priority DESC ), ranked_rows AS ( SELECT account_id, score, ROW_NUMBER() OVER (ORDER BY score DESC, COALESCE(end_time, first_seen_at::TEXT) ASC) AS rank, COUNT(*) OVER ()::INT AS total_count FROM resolved_rows ) SELECT account_id, score, rank, total_count FROM ranked_rows ORDER BY rank LIMIT 10;" > "$OUT_DIR/dynamic-current-state-plan.json" 2> "$OUT_DIR/dynamic-current-state-plan.err" || true

psql_cmd -c "SET statement_timeout = '${STATEMENT_TIMEOUT_MS}ms'; EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) SELECT account_id, score, rank, (SELECT row_count::INT FROM solo_current_projection_scope WHERE song_id = '$SAMPLE_SONG' AND instrument = '$SAMPLE_INSTRUMENT' AND status = 'ready') AS total_count FROM current_leaderboard_entries WHERE song_id = '$SAMPLE_SONG' AND instrument = '$SAMPLE_INSTRUMENT' ORDER BY rank LIMIT 10;" > "$OUT_DIR/projection-current-state-plan.json" 2> "$OUT_DIR/projection-current-state-plan.err" || true

printf 'Raw dynamic plan: [%s](%s)\n\n' "dynamic-current-state-plan.json" "dynamic-current-state-plan.json" >> "$OUT_DIR/report.md"
printf 'Raw projection plan: [%s](%s)\n\n' "projection-current-state-plan.json" "projection-current-state-plan.json" >> "$OUT_DIR/report.md"

if [[ -s "$OUT_DIR/dynamic-current-state-plan.err" ]]; then
  printf 'Dynamic plan stderr:\n\n```text\n' >> "$OUT_DIR/report.md"
  cat "$OUT_DIR/dynamic-current-state-plan.err" >> "$OUT_DIR/report.md"
  printf '\n```\n\n' >> "$OUT_DIR/report.md"
fi
if [[ -s "$OUT_DIR/projection-current-state-plan.err" ]]; then
  printf 'Projection plan stderr:\n\n```text\n' >> "$OUT_DIR/report.md"
  cat "$OUT_DIR/projection-current-state-plan.err" >> "$OUT_DIR/report.md"
  printf '\n```\n\n' >> "$OUT_DIR/report.md"
fi

write_heading "Code-Change Proof Points"
cat >> "$OUT_DIR/report.md" <<'EOF'
- Current-state reads now attempt `current_leaderboard_entries` / `solo_current_projection_scope` first, with dynamic snapshot/overlay reconstruction only as a fallback for missing projection scopes.
- `PrecomputeAll` now runs independent phases sequentially by default. Parallel phase execution is opt-in via `Scraper__RunPrecomputePhasesInParallel=true`.
- `PrecomputeLeaderboardAll` song and instrument fan-out are configurable and default to 1 × 1, preventing the previous dozens-of-current-state-queries burst.
- `/api/service-info` avoids DB access while an update is active and serves from the in-memory progress tracker.
- Account-name search has a bounded command timeout and returns a fast empty result if the database is too pressured.
EOF

printf '%s\n' "$OUT_DIR/report.md"
