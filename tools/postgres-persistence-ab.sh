#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd -P)"

BASELINE_REF="${BASELINE_REF:-origin/master}"
CANDIDATE_REF="${CANDIDATE_REF:-working-tree}"
OUT_DIR="${OUT_DIR:-$REPO_ROOT/harness-output/postgres-persistence-ab-$(date -u +%Y%m%dT%H%M%SZ)}"
POSTGRES_IMAGE="${POSTGRES_IMAGE:-postgres:17-alpine}"
SOLO_ROWS="${SOLO_ROWS:-5000}"
COMBO_ROWS="${COMBO_ROWS:-5000}"
BAND_ROWS="${BAND_ROWS:-500}"
KEEP_WORK="${KEEP_WORK:-0}"

usage() {
    cat <<'EOF'
Usage: tools/postgres-persistence-ab.sh [options]

Runs a local-only A/B persistence comparison against disposable PostgreSQL
containers. It does not connect to or modify production services.

Options:
  --baseline-ref REF   Git ref for baseline (default: origin/master)
  --candidate-ref REF  Git ref for candidate, or working-tree (default: working-tree)
  --out-dir DIR        Output directory (default: harness-output/postgres-persistence-ab-<timestamp>)
  --postgres-image IMG PostgreSQL image (default: postgres:17-alpine)
  --solo-rows N        Solo leaderboard rows for unchanged bulk-upsert scenario
  --combo-rows N       Combo leaderboard rows for replacement scenario
  --band-rows N        Band rows for member-stat no-op scenario
  --keep-work          Keep temp work directory and containers for inspection
  -h, --help           Show this help

Environment variables with matching names are also honored:
BASELINE_REF, CANDIDATE_REF, OUT_DIR, POSTGRES_IMAGE, SOLO_ROWS, COMBO_ROWS,
BAND_ROWS, KEEP_WORK.
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --baseline-ref) BASELINE_REF="$2"; shift 2 ;;
        --candidate-ref) CANDIDATE_REF="$2"; shift 2 ;;
        --out-dir) OUT_DIR="$2"; shift 2 ;;
        --postgres-image) POSTGRES_IMAGE="$2"; shift 2 ;;
        --solo-rows) SOLO_ROWS="$2"; shift 2 ;;
        --combo-rows) COMBO_ROWS="$2"; shift 2 ;;
        --band-rows) BAND_ROWS="$2"; shift 2 ;;
        --keep-work) KEEP_WORK=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) printf 'Unknown option: %s\n\n' "$1" >&2; usage >&2; exit 2 ;;
    esac
done

require_cmd() {
    if ! command -v "$1" >/dev/null 2>&1; then
        printf 'ERROR: required command not found: %s\n' "$1" >&2
        exit 1
    fi
}

require_cmd docker
require_cmd dotnet
require_cmd git
require_cmd python3

mkdir -p "$OUT_DIR"
WORK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/fst-pg-ab.XXXXXX")"
CONTAINERS=()
WORKTREES=()

cleanup() {
    if [[ "$KEEP_WORK" == "1" ]]; then
        printf 'Keeping work directory: %s\n' "$WORK_DIR" >&2
        printf 'Keeping containers: %s\n' "${CONTAINERS[*]:-none}" >&2
        return
    fi

    for container in "${CONTAINERS[@]:-}"; do
        docker rm -f "$container" >/dev/null 2>&1 || true
    done

    for worktree in "${WORKTREES[@]:-}"; do
        git -C "$REPO_ROOT" worktree remove --force "$worktree" >/dev/null 2>&1 || true
    done

    rm -rf "$WORK_DIR"
}
trap cleanup EXIT

copy_working_tree() {
    local target="$1"
    mkdir -p "$target"
    tar \
        --exclude='.git' \
        --exclude='bin' \
        --exclude='obj' \
        --exclude='harness-output' \
        --exclude='POSTGRES_IMPROVEMENTS_RESEARCH.md' \
        -C "$REPO_ROOT" -cf - . | tar -C "$target" -xf -
}

prepare_source() {
    local out_var="$1"
    local label="$2"
    local ref="$3"
    local target="$WORK_DIR/src-$label"

    if [[ "$ref" == "working-tree" ]]; then
        copy_working_tree "$target"
    else
        git -C "$REPO_ROOT" worktree add --detach "$target" "$ref" >/dev/null
        WORKTREES+=("$target")
    fi

    printf -v "$out_var" '%s' "$target"
}

start_postgres() {
    local out_var="$1"
    local label="$2"
    local container="fst-pg-ab-${label}-$$"
    local password="fst_ab"

    docker run -d --rm \
        --name "$container" \
        -e POSTGRES_USER=fst \
        -e POSTGRES_PASSWORD="$password" \
        -e POSTGRES_DB=fstservice \
        -p 127.0.0.1::5432 \
        "$POSTGRES_IMAGE" \
        -c max_connections=200 \
        -c track_io_timing=on \
        -c shared_buffers=256MB \
        >/dev/null
    CONTAINERS+=("$container")

    for _ in {1..120}; do
        if docker exec "$container" pg_isready -U fst -d fstservice >/dev/null 2>&1; then
            local port
            port="$(docker port "$container" 5432/tcp | sed -E 's/.*:([0-9]+)$/\1/')"
            printf -v "$out_var" 'Host=127.0.0.1;Port=%s;Database=fstservice;Username=fst;Password=%s;Command Timeout=0;Maximum Pool Size=20' "$port" "$password"
            return
        fi
        sleep 1
    done

    printf 'ERROR: PostgreSQL container did not become ready: %s\n' "$container" >&2
    exit 1
}

write_harness_project() {
    local source_dir="$1"
    local harness_dir="$2"
    mkdir -p "$harness_dir"

    cat > "$harness_dir/PostgresPersistenceBench.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <ProjectReference Include="$source_dir/FSTService/FSTService.csproj" />
  </ItemGroup>
</Project>
EOF

    cat > "$harness_dir/Program.cs" <<'EOF'
using System.Diagnostics;
using System.Text.Json;
using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

var argsMap = ParseArgs(args);
var connectionString = Require(argsMap, "connection-string");
var variant = Require(argsMap, "variant");
var soloRows = GetInt(argsMap, "solo-rows", 5000);
var comboRows = GetInt(argsMap, "combo-rows", 5000);
var bandRows = GetInt(argsMap, "band-rows", 500);

await using var dataSource = NpgsqlDataSource.Create(connectionString);
await DatabaseInitializer.EnsureSchemaAsync(dataSource);

var metrics = new List<ScenarioMetric>
{
    await RunSoloUnchangedBulkAsync(dataSource, variant, soloRows),
    await RunBandMemberNoopAsync(dataSource, variant, bandRows),
    await RunComboReplaceAsync(dataSource, variant, comboRows),
};

Console.WriteLine(JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true }));

static async Task<ScenarioMetric> RunSoloUnchangedBulkAsync(NpgsqlDataSource ds, string variant, int rowCount)
{
    var db = new InstrumentDatabase("Solo_Guitar", ds, NullLogger<InstrumentDatabase>.Instance);
    var entries = Enumerable.Range(1, rowCount)
        .Select(index => new LeaderboardEntry
        {
            AccountId = $"solo-{index:D8}",
            Score = 1_000_000 - index,
            Accuracy = 950_000 + (index % 100),
            IsFullCombo = index % 3 == 0,
            Stars = 5,
            Season = 34,
            Difficulty = 5,
            Percentile = 0.001 * index,
            Rank = index,
            ApiRank = index,
            Source = "scrape",
            EndTime = $"2026-01-01T00:{index % 60:D2}:00Z",
        })
        .ToList();

    db.UpsertEntries("ab_solo_song", entries);
    await ResetStatsAsync(ds);

    var sw = Stopwatch.StartNew();
    var affected = db.UpsertEntries("ab_solo_song", entries);
    sw.Stop();

    return await BuildMetricAsync(ds, variant, "solo_bulk_unchanged", rowCount, affected, sw.Elapsed, ["leaderboard_entries"]);
}

static async Task<ScenarioMetric> RunBandMemberNoopAsync(NpgsqlDataSource ds, string variant, int rowCount)
{
    var persistence = new BandLeaderboardPersistence(ds, NullLogger<BandLeaderboardPersistence>.Instance);
    var entries = Enumerable.Range(1, rowCount)
        .Select(index =>
        {
            var left = $"band-{index:D8}-a";
            var right = $"band-{index:D8}-b";
            return new BandLeaderboardEntry
            {
                TeamKey = string.Join(':', new[] { left, right }.Order(StringComparer.OrdinalIgnoreCase)),
                TeamMembers = [left, right],
                InstrumentCombo = "0:1",
                Score = 2_000_000 - index,
                BaseScore = 1_800_000 - index,
                InstrumentBonus = 100_000,
                OverdriveBonus = 100_000,
                Accuracy = 950_000,
                IsFullCombo = index % 2 == 0,
                Stars = 5,
                Difficulty = 5,
                Season = 34,
                Rank = index,
                Percentile = 0.001 * index,
                EndTime = $"2026-01-01T01:{index % 60:D2}:00Z",
                Source = "scrape",
                MemberStats =
                [
                    new BandMemberStats { MemberIndex = 0, AccountId = left, InstrumentId = 0, Score = 900_000 - index, Accuracy = 950_000, IsFullCombo = true, Stars = 5, Difficulty = 5 },
                    new BandMemberStats { MemberIndex = 1, AccountId = right, InstrumentId = 1, Score = 900_000 - index, Accuracy = 950_000, IsFullCombo = true, Stars = 5, Difficulty = 5 },
                ],
            };
        })
        .ToList();

    persistence.UpsertBandEntries("ab_band_song", "Band_Duets", entries);
    await ResetStatsAsync(ds);

    var sw = Stopwatch.StartNew();
    var affected = persistence.UpsertBandEntries("ab_band_song", "Band_Duets", entries);
    sw.Stop();

    return await BuildMetricAsync(ds, variant, "band_member_stats_unchanged", rowCount * 2, affected, sw.Elapsed, ["band_member_stats"]);
}

static async Task<ScenarioMetric> RunComboReplaceAsync(NpgsqlDataSource ds, string variant, int rowCount)
{
    var meta = new MetaDatabase(ds, NullLogger<MetaDatabase>.Instance);
    var entries = Enumerable.Range(1, rowCount)
        .Select(index => (
            AccountId: $"combo-{index:D8}",
            AdjustedRating: index / (double)rowCount,
            WeightedRating: (rowCount - index) / (double)rowCount,
            FcRate: (index % 100) / 100d,
            TotalScore: 1_000_000L + index,
            MaxScorePercent: 0.9 + (index % 10) / 100d,
            SongsPlayed: 10 + (index % 5),
            FullComboCount: index % 4))
        .ToList();

    meta.ReplaceComboLeaderboard("ab_combo", entries, entries.Count);
    await ResetStatsAsync(ds);

    var sw = Stopwatch.StartNew();
    meta.ReplaceComboLeaderboard("ab_combo", entries, entries.Count);
    sw.Stop();

    return await BuildMetricAsync(ds, variant, "combo_replace", rowCount, rowCount, sw.Elapsed, ["combo_leaderboard"]);
}

static async Task<ScenarioMetric> BuildMetricAsync(
    NpgsqlDataSource ds,
    string variant,
    string scenario,
    int inputRows,
    long affectedRows,
    TimeSpan elapsed,
    string[] tableNames)
{
    await ForceStatsFlushAsync(ds);
    var walBytes = await TryReadWalBytesAsync(ds);
    var tableStats = await ReadTableStatsAsync(ds, tableNames);
    return new ScenarioMetric(
        variant,
        scenario,
        inputRows,
        affectedRows,
        Math.Round(elapsed.TotalMilliseconds, 3),
        walBytes,
        tableStats.Inserted,
        tableStats.Updated,
        tableStats.Deleted,
        tableStats.DeadTuples);
}

static async Task ResetStatsAsync(NpgsqlDataSource ds)
{
    await using var conn = await ds.OpenConnectionAsync();
    foreach (var sql in new[]
    {
        "SELECT pg_stat_reset()",
        "SELECT pg_stat_reset_shared('wal')",
    })
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }
        catch (PostgresException)
        {
        }
    }
}

static async Task ForceStatsFlushAsync(NpgsqlDataSource ds)
{
    try
    {
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT pg_stat_force_next_flush()";
        await cmd.ExecuteNonQueryAsync();
    }
    catch (PostgresException)
    {
    }
}

static async Task<long?> TryReadWalBytesAsync(NpgsqlDataSource ds)
{
    try
    {
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT wal_bytes::bigint FROM pg_stat_wal";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
    catch (PostgresException)
    {
        return null;
    }
}

static async Task<(long Inserted, long Updated, long Deleted, long DeadTuples)> ReadTableStatsAsync(
    NpgsqlDataSource ds,
    IReadOnlyCollection<string> tableNames)
{
    await using var conn = await ds.OpenConnectionAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT
            COALESCE(SUM(n_tup_ins), 0)::bigint,
            COALESCE(SUM(n_tup_upd), 0)::bigint,
            COALESCE(SUM(n_tup_del), 0)::bigint,
            COALESCE(SUM(n_dead_tup), 0)::bigint
        FROM pg_stat_user_tables
        WHERE relname = ANY(@tableNames)
        """;
    cmd.Parameters.AddWithValue("tableNames", tableNames.ToArray());
    await using var reader = await cmd.ExecuteReaderAsync();
    return await reader.ReadAsync()
        ? (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3))
        : (0, 0, 0, 0);
}

static Dictionary<string, string> ParseArgs(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < args.Length; index++)
    {
        var arg = args[index];
        if (!arg.StartsWith("--", StringComparison.Ordinal))
            continue;
        var key = arg[2..];
        if (index + 1 >= args.Length)
            throw new ArgumentException($"Missing value for {arg}");
        result[key] = args[++index];
    }

    return result;
}

static string Require(IReadOnlyDictionary<string, string> args, string key) =>
    args.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new ArgumentException($"Missing --{key}");

static int GetInt(IReadOnlyDictionary<string, string> args, string key, int fallback) =>
    args.TryGetValue(key, out var raw) && int.TryParse(raw, out var value) && value > 0
        ? value
        : fallback;

public sealed record ScenarioMetric(
    string Variant,
    string Scenario,
    int InputRows,
    long AffectedRows,
    double ElapsedMs,
    long? WalBytes,
    long InsertedTuples,
    long UpdatedTuples,
    long DeletedTuples,
    long DeadTuples);
EOF
}

run_variant() {
    local label="$1"
    local source_dir="$2"
    local connection_string="$3"
    local harness_dir="$WORK_DIR/harness-$label"
    local output_file="$OUT_DIR/$label.json"
    local raw_output_file="$OUT_DIR/$label.raw.log"

    write_harness_project "$source_dir" "$harness_dir"
    dotnet run --project "$harness_dir/PostgresPersistenceBench.csproj" -- \
        --connection-string "$connection_string" \
        --variant "$label" \
        --solo-rows "$SOLO_ROWS" \
        --combo-rows "$COMBO_ROWS" \
        --band-rows "$BAND_ROWS" \
        > "$raw_output_file"

    python3 - "$raw_output_file" "$output_file" <<'PY'
import json
import sys
from pathlib import Path

raw_path, output_path = sys.argv[1:]
text = Path(raw_path).read_text()
decoder = json.JSONDecoder()

for index, char in enumerate(text):
    if char != "[":
        continue
    try:
        payload, end = decoder.raw_decode(text[index:])
    except json.JSONDecodeError:
        continue
    if isinstance(payload, list):
        Path(output_path).write_text(json.dumps(payload, indent=2) + "\n")
        break
else:
    raise SystemExit(f"No JSON array found in {raw_path}")
PY

    printf '%s' "$output_file"
}

write_report() {
    local baseline_json="$1"
    local candidate_json="$2"
    local combined_json="$OUT_DIR/results.json"
    local report="$OUT_DIR/report.md"

    python3 - "$baseline_json" "$candidate_json" "$combined_json" "$report" "$BASELINE_REF" "$CANDIDATE_REF" <<'PY'
import json
import sys
from pathlib import Path

baseline_path, candidate_path, combined_path, report_path, baseline_ref, candidate_ref = sys.argv[1:]
baseline = json.loads(Path(baseline_path).read_text())
candidate = json.loads(Path(candidate_path).read_text())
combined = baseline + candidate
Path(combined_path).write_text(json.dumps(combined, indent=2) + "\n")

by_key = {}
for row in combined:
    by_key[(row["Scenario"], row["Variant"])] = row

metrics = [
    ("AffectedRows", "affected rows"),
    ("ElapsedMs", "elapsed ms"),
    ("WalBytes", "WAL bytes"),
    ("InsertedTuples", "inserted tuples"),
    ("UpdatedTuples", "updated tuples"),
    ("DeletedTuples", "deleted tuples"),
    ("DeadTuples", "dead tuples"),
]

scenarios = []
for row in combined:
    if row["Scenario"] not in scenarios:
        scenarios.append(row["Scenario"])

def fmt(value):
    if value is None:
        return "n/a"
    if isinstance(value, float):
        return f"{value:,.3f}"
    return f"{value:,}"

def delta(candidate_value, baseline_value):
    if candidate_value is None or baseline_value is None:
        return "n/a"
    diff = candidate_value - baseline_value
    if isinstance(diff, float):
        return f"{diff:+,.3f}"
    return f"{diff:+,}"

lines = [
    "# Postgres Persistence A/B Report",
    "",
    f"- Baseline: `{baseline_ref}`",
    f"- Candidate: `{candidate_ref}`",
    "",
    "| Scenario | Metric | Baseline | Candidate | Delta |",
    "|---|---:|---:|---:|---:|",
]

for scenario in scenarios:
    b = by_key[(scenario, "baseline")]
    c = by_key[(scenario, "candidate")]
    for key, label in metrics:
        lines.append(
            f"| {scenario} | {label} | {fmt(b.get(key))} | {fmt(c.get(key))} | {delta(c.get(key), b.get(key))} |"
        )

lines.extend([
    "",
    "## Notes",
    "",
    "- Lower affected rows, updated tuples, dead tuples, WAL bytes, and elapsed time are better.",
    "- `pg_stat_*` values are local-container measurements and should be treated as comparative, not absolute.",
    "- This harness never connects to production; it starts disposable local PostgreSQL containers.",
    "",
])

Path(report_path).write_text("\n".join(lines))
PY
}

printf 'Preparing baseline source: %s\n' "$BASELINE_REF" >&2
prepare_source BASELINE_SOURCE baseline "$BASELINE_REF"
printf 'Preparing candidate source: %s\n' "$CANDIDATE_REF" >&2
prepare_source CANDIDATE_SOURCE candidate "$CANDIDATE_REF"

printf 'Starting disposable baseline PostgreSQL container...\n' >&2
start_postgres BASELINE_CONN baseline
printf 'Starting disposable candidate PostgreSQL container...\n' >&2
start_postgres CANDIDATE_CONN candidate

printf 'Running baseline harness...\n' >&2
BASELINE_JSON="$(run_variant baseline "$BASELINE_SOURCE" "$BASELINE_CONN")"
printf 'Running candidate harness...\n' >&2
CANDIDATE_JSON="$(run_variant candidate "$CANDIDATE_SOURCE" "$CANDIDATE_CONN")"

write_report "$BASELINE_JSON" "$CANDIDATE_JSON"

printf 'A/B report: %s\n' "$OUT_DIR/report.md"
printf 'Raw results: %s\n' "$OUT_DIR/results.json"
