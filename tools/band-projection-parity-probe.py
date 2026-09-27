#!/usr/bin/env python3
"""Read-only same-input parity probe for band current-projection query shapes.

Samples scopes refreshed since --since and recomputes each with the legacy
seven-subquery member-stats SQL (extracted verbatim from
BandCurrentProjectionBuilder.cs), comparing an ordered row hash with the rows
stored in current_band_leaderboard_entries. Band inputs are not written between
BandMaintenance and the next scrape, so a correct batched projection must match.
"""
import argparse, json, pathlib, re, subprocess, sys

SOURCE = pathlib.Path(__file__).resolve().parents[1] / "FSTService/Persistence/BandCurrentProjectionBuilder.cs"
MEMBERS = {"Band_Duets": 2, "Band_Trios": 3, "Band_Quad": 4}
COLUMNS = ("team_key, entry_combo_id, entry_instrument_combo, team_members, member_account_ids, "
           "member_instrument_ids, member_scores, member_accuracies, member_full_combos, member_stars, "
           "member_difficulties, score, accuracy, is_full_combo, stars, difficulty, season, rank, "
           "total_entries, end_time, first_seen_at, last_updated_at")


def extract(text: str, name: str, opener: str, closer: str) -> str:
    start = text.index(name)
    begin = text.index(opener, start) + len(opener)
    return text[begin:text.index(closer, begin)]


def build_select(source: str) -> str:
    combo = extract(source, "BandSongComboIdExpression =", '@"', '";')
    template = extract(source, "RebuildScopeSqlTemplate =", '$"""', '"""')
    legacy = extract(source, "LegacyMemberStatsProjectionSql =", '"""', '"""')
    sql = template.replace("{BandSongComboIdExpression}", combo)
    sql = sql.replace("__MEMBER_STATS_PROJECTION__", legacy).replace("__MEMBER_STATS_JOIN__", "")
    head = sql[:sql.index("), Inserted AS (")]
    return head + ")\nSELECT " + COLUMNS + " FROM RankedRows WHERE (SELECT exists FROM SourceScope)"


def literal(value) -> str:
    if isinstance(value, int):
        return str(value)
    return "'" + str(value).replace("'", "''") + "'"


def bind(sql: str, params: dict) -> str:
    for key in sorted(params, key=len, reverse=True):
        sql = re.sub(rf"@{key}\b", literal(params[key]), sql)
    if re.search(r"@[a-zA-Z]", sql):
        raise SystemExit("unbound parameter left in SQL")
    return sql


def psql(sql: str) -> str:
    result = subprocess.run(
        ["docker", "exec", "-i", "fst-postgres", "psql", "-X", "-q", "-At", "-v", "ON_ERROR_STOP=1",
         "-U", "fst", "-d", "fstservice"], input=sql, capture_output=True, text=True, timeout=600)
    if result.returncode != 0:
        raise SystemExit(result.stderr.strip()[:500])
    return result.stdout.strip()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--since", required=True, help="UTC timestamp; scopes rebuilt at or after it")
    parser.add_argument("--sample", type=int, default=200)
    args = parser.parse_args()
    select = build_select(SOURCE.read_text())
    scopes = json.loads(psql(f"""
        BEGIN READ ONLY;
        SELECT coalesce(json_agg(s), '[]') FROM (
          SELECT song_id, band_type, ranking_scope, scope_combo_id, projection_generation
          FROM band_current_projection_scope
          WHERE status = 'ready' AND last_rebuilt_at >= {literal(args.since)}::timestamptz
          ORDER BY md5(song_id || band_type || ranking_scope || scope_combo_id)
          LIMIT {int(args.sample)}) s;
        COMMIT;"""))
    mismatches = []
    for scope in scopes:
        params = {"songId": scope["song_id"], "bandType": scope["band_type"],
                  "rankingScope": scope["ranking_scope"], "scopeComboId": scope["scope_combo_id"],
                  "expectedMembers": MEMBERS[scope["band_type"]]}
        recomputed = bind(select, params)
        stored = (f"SELECT {COLUMNS} FROM current_band_leaderboard_entries "
                  f"WHERE song_id = {literal(scope['song_id'])} AND band_type = {literal(scope['band_type'])} "
                  f"AND ranking_scope = {literal(scope['ranking_scope'])} "
                  f"AND scope_combo_id = {literal(scope['scope_combo_id'])} "
                  f"AND projection_generation = {int(scope['projection_generation'])}")
        out = psql(f"""
            BEGIN READ ONLY; SET LOCAL statement_timeout = '120s';
            WITH a AS ({recomputed}), b AS ({stored})
            SELECT (SELECT count(*) FROM a) || '|' || (SELECT count(*) FROM b) || '|' ||
                   coalesce((SELECT md5(string_agg(a::text, ',' ORDER BY rank, team_key)) FROM a), '') || '|' ||
                   coalesce((SELECT md5(string_agg(b::text, ',' ORDER BY rank, team_key)) FROM b), '');
            COMMIT;""")
        rows_a, rows_b, hash_a, hash_b = out.split("|")
        if rows_a != rows_b or hash_a != hash_b:
            mismatches.append({**params, "legacy_rows": rows_a, "stored_rows": rows_b})
    print(json.dumps({"sampled": len(scopes), "mismatches": len(mismatches), "details": mismatches[:20]}, indent=1))
    return 1 if mismatches else 0


if __name__ == "__main__":
    sys.exit(main())
