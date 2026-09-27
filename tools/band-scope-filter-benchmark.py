#!/usr/bin/env python3
"""Read-only A/B of the band current-projection scope-selection SQL.

Runs the pre-change (per-scope join) and current (aggregate-once) queries,
extracted from BandCurrentProjectionBuilder.cs at the two given git revisions,
over the same sample of existing scopes supplied as a VALUES CTE, and compares
the selected scope sets and wall times.
"""
import argparse, json, re, subprocess, sys, time

FILE = "FSTService/Persistence/BandCurrentProjectionBuilder.cs"


def source_at(rev: str) -> str:
    return subprocess.run(["git", "show", f"{rev}:{FILE}"], capture_output=True, text=True, check=True).stdout


def filter_sql(source: str) -> str:
    combo_start = source.index("BandSongComboIdExpression =")
    combo = source[source.index('@"', combo_start) + 2:source.index('";', combo_start)]
    anchor = source.index("private async Task<BandCurrentProjectionScopeKey[]> FilterScopesNeedingRefreshAsync")
    body_start = source.index('cmd.CommandText = $"""', anchor) + len('cmd.CommandText = $"""')
    body = source[body_start:source.index('"""', body_start)]
    scope_table = re.search(r'ScopeTable\s*=\s*"([a-z_]+)"', source)
    body = body.replace("{BandSongComboIdExpression}", combo)
    body = body.replace("{ScopeTable}", scope_table.group(1) if scope_table else "band_current_projection_scope")
    return body


def psql(sql: str) -> str:
    r = subprocess.run(["docker", "exec", "-i", "fst-postgres", "psql", "-X", "-q", "-At", "-v", "ON_ERROR_STOP=1",
                        "-U", "fst", "-d", "fstservice"], input=sql, capture_output=True, text=True, timeout=7200)
    if r.returncode != 0:
        raise SystemExit(r.stderr.strip()[:600])
    return r.stdout.strip()


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--baseline-rev", required=True)
    ap.add_argument("--candidate-rev", default="HEAD")
    ap.add_argument("--songs", type=int, default=40)
    ap.add_argument("--aggregates", action="store_true",
                    help="compare every requested scope's projected_rows and max_source_updated_at "
                         "instead of only the refresh selection (meaningful when all scopes are fresh)")
    args = ap.parse_args()
    rows = json.loads(psql(f"""BEGIN READ ONLY;
        WITH songs AS (SELECT song_id FROM (SELECT DISTINCT song_id FROM band_current_projection_scope) d ORDER BY md5(song_id) LIMIT {int(args.songs)})
        SELECT coalesce(json_agg(json_build_array(s.song_id, s.band_type, s.ranking_scope, s.scope_combo_id)), '[]')
        FROM band_current_projection_scope s JOIN songs USING (song_id); COMMIT;"""))
    q = lambda v: "'" + v.replace("'", "''") + "'"
    values = ",".join(f"({q(a)},{q(b)},{q(c)},{q(d)})" for a, b, c, d in rows)
    results = {}
    for label, rev in (("baseline", args.baseline_rev), ("candidate", args.candidate_rev)):
        sql = filter_sql(source_at(rev)).replace(
            "_band_current_refresh_scopes",
            "requested_input")
        sql = re.sub(r"^\s*WITH\s+", "", sql, count=1)
        if args.aggregates:
            final = sql.rindex("SELECT source_scope.song_id")
            sql = (sql[:final] + "SELECT source_scope.song_id || '|' || source_scope.band_type || '|' || "
                   "source_scope.ranking_scope || '|' || source_scope.scope_combo_id || '|' || "
                   "source_scope.projected_rows || '|' || coalesce(source_scope.max_source_updated_at::text, '') "
                   "FROM source_scope")
        wrapped = (f"BEGIN READ ONLY; SET LOCAL statement_timeout = '60min';\n"
                   f"WITH requested_input(song_id, band_type, ranking_scope, scope_combo_id) AS (VALUES {values}),\n{sql};\nCOMMIT;")
        started = time.monotonic()
        out = psql(wrapped)
        results[label] = {"seconds": round(time.monotonic() - started, 1),
                          "selected": sorted(out.splitlines()) if out else []}
    same = results["baseline"]["selected"] == results["candidate"]["selected"]
    print(json.dumps({"scopes": len(rows),
                      "baseline_seconds": results["baseline"]["seconds"],
                      "candidate_seconds": results["candidate"]["seconds"],
                      "baseline_selected": len(results["baseline"]["selected"]),
                      "candidate_selected": len(results["candidate"]["selected"]),
                      "identical_selection": same}, indent=1))
    return 0 if same else 1


if __name__ == "__main__":
    sys.exit(main())
