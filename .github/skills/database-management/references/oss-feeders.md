# OSS Feeder Sources

The database-management skill stack intentionally follows the same pattern as the ML umbrella skill: use permissively licensed OSS skills/agents as feeders, then adapt them to FortniteFestivalLeaderboardScraper safety, storage, and historical correctness rules.

## Feeder map

| Feeder | License observed | Local advisor/use |
|---|---|---|
| `wshobson/agents` database-architect, database-admin, database-optimizer, database-migration, PostgreSQL table-design sources | MIT | Generic DB architecture, DBA operations, optimization, migration safety, Postgres schema/index concepts |
| `TerminalSkills/skills` PostgreSQL, SQL optimizer, database-schema-designer, DuckDB, ClickHouse sources | Apache-2.0 | PostgreSQL operations/query design, SQL optimization workflow, schema design, DuckDB/Parquet analytics, ClickHouse platform-candidate framing |
| `duckdb/duckdb-skills` attach-db, query, read-file, install/docs skills | MIT | DuckDB direct-file querying, session-state conventions, schema/sample profiling, Friendly SQL, and safe result-size checks |
| PostgreSQL documentation | PostgreSQL license / project documentation | Lock/statistics/EXPLAIN/VACUUM/concurrent-index/backup concepts |
| DuckDB documentation and skills ecosystem | MIT/project docs where applicable | Parquet/direct-file analytics and embedded OLAP fit |

## Local adaptation rules

- Do not vendor upstream files blindly. Summarize concepts, preserve attribution, and keep the local skill repo-specific.
- Repository rules override feeder advice. In particular, live FST reliability, historical correctness data timing, Epic/API constraints, and README/docs maintenance are mandatory.
- Outside defaults are not imported automatically. For example, UUID-vs-identity guidance must follow existing schema conventions and measured requirements, not a generic skill preference.
- DuckDB/ClickHouse/TimescaleDB-style candidates start as artifact-only or bounded A/B work. Runtime adoption needs promotion-readiness evidence and rollback.
- Keep this reference and `attribution.md` current when adding new OSS database feeders.

## Candidate routing

| Question | Primary local advisor | Feeder emphasis |
|---|---|---|
| "Should this schema/platform change exist?" | `database-architecture-expert` | wshobson database-architect; TerminalSkills schema designer |
| "How should we tune Postgres?" | `postgres-database-expert` | wshobson PostgreSQL/admin/optimizer; TerminalSkills PostgreSQL/SQL optimizer |
| "Can DuckDB/Parquet speed evaluation artifacts?" | `duckdb-analytics-expert` | duckdb/duckdb-skills; TerminalSkills DuckDB |
| "Would ClickHouse/Timescale/other OLAP help?" | `database-platform-research` | wshobson platform architecture; TerminalSkills ClickHouse plus current vendor docs |
| "How do we prove it?" | `database-evaluation` | SQL optimizer and DuckDB query benchmark patterns |
