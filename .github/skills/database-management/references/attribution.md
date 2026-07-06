# Attribution and Source Notes

This database-management skill is a repository-local synthesis of open-source database skills/agents, database operations practice, and this repository's FST live-safety, Postgres, historical correctness, scrape evaluation, and documentation rules. It is not a blind vendored copy; repository-specific rules override upstream guidance.

## Source candidates reviewed

| Source | License/terms observed | How used |
|---|---|---|
| `wshobson/agents`, `plugins/database-design/agents/database-architect.md` | MIT in repository `LICENSE` | Database architecture, platform selection, schema modeling, migration planning, normalization/denormalization concepts |
| `wshobson/agents`, `plugins/database-migrations/agents/database-admin.md` | MIT in repository `LICENSE` | DBA operations, backup/restore, monitoring, high availability, reliability, and security concepts |
| `wshobson/agents`, `plugins/database-migrations/agents/database-optimizer.md` and `plugins/observability-monitoring/agents/database-optimizer.md` | MIT in repository `LICENSE` | Query optimization, index strategy, performance baselines, caching, partitioning, and capacity concepts |
| `wshobson/agents`, `plugins/database-design/skills/postgresql/SKILL.md` | MIT in repository `LICENSE` | PostgreSQL table design, data-type, index, JSONB, partitioning, and migration-safety concepts |
| `wshobson/agents`, `plugins/framework-migration/skills/database-migration/SKILL.md` | MIT in repository `LICENSE` | Migration phasing, rollback, zero-downtime, and data-transformation concepts |
| `TerminalSkills/skills`, `skills/postgresql/SKILL.md` | Apache-2.0 in skill frontmatter and repository metadata | PostgreSQL operations, JSONB, full-text, window/CTE, RLS, replication, and `pg_stat_statements` concepts |
| `TerminalSkills/skills`, `skills/sql-optimizer/SKILL.md` | Apache-2.0 in skill frontmatter and repository metadata | SQL optimization workflow, execution-plan red flags, index recommendations, and query rewrite concepts |
| `TerminalSkills/skills`, `skills/database-schema-designer/SKILL.md` | Apache-2.0 in skill frontmatter and repository metadata | Schema-design workflow, normalization, access-pattern-driven indexes, RLS, and migration output concepts |
| `TerminalSkills/skills`, `skills/duckdb/SKILL.md` | Apache-2.0 in skill frontmatter and repository metadata | DuckDB/Parquet direct-file querying, embedded OLAP, local analytics, and Node/Python usage concepts |
| `TerminalSkills/skills`, `skills/clickhouse/SKILL.md` | Apache-2.0 in skill frontmatter and repository metadata | Columnar OLAP and ClickHouse candidate framing for future platform research |
| `duckdb/duckdb-skills`, `skills/attach-db/SKILL.md`, `skills/query/SKILL.md`, `skills/read-file/SKILL.md` | MIT in repository `LICENSE` | DuckDB session-state, safe file profiling, result-size checks, Friendly SQL, and query troubleshooting concepts |
| PostgreSQL documentation | PostgreSQL license / project documentation | General concepts for locks, `pg_stat_activity`, `pg_locks`, `EXPLAIN`, vacuum/analyze, concurrent indexes, and backup/restore safety |
| Docker documentation | Docker documentation terms | General container health/resource-check concepts |
| Agent Skills specification, `agentskills.io/specification` | Public specification | `SKILL.md` structure, progressive disclosure, references/templates pattern |
| Microsoft Learn Agent Skills docs | Microsoft documentation | Confirmation that `.github/skills/<name>/SKILL.md` is an accepted project skill path for GitHub Copilot-compatible environments |
| Existing FortniteFestivalLeaderboardScraper skills and instructions | Repository-local | Plan->Confirm->Act workflow, advisor routing, report tables, and live-safety/historical correctness overrides |

## Maintenance rules

- Keep this file current when adding upstream-derived material.
- Preserve license notices when copying substantial upstream text or code. Prefer repo-specific summaries and links when exact copying is unnecessary.
- Do not import upstream scripts into this repo without reviewing dependencies, network behavior, data access, and licensing.
- If upstream guidance conflicts with repository instructions, keep the repository-specific rule and document the conflict.
- Update `references/oss-feeders.md` whenever adding or removing OSS database feeder sources.
