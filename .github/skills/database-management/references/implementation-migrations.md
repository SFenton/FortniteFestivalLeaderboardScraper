# Implementation and Migration Reference

Use this reference for schema changes, migrations, repository persistence changes, indexes, import/export tooling, and runtime database configuration.

## Implementation rules

- Read the relevant `.github/instructions/*` file and database/design docs before editing DB-adjacent files.
- Prefer existing helpers and repository patterns over ad hoc SQL.
- Write or update the smallest test/invariant that captures the expected behavior when feasible.
- Keep migrations idempotent and safe to rerun.
- Use short lock and statement timeouts for startup or migration-time DDL.
- Avoid table rewrites and long exclusive locks in normal startup paths.
- Use concurrent index creation for large live tables when supported, and keep optional heavy indexes out of default `db:init` unless they are required.
- Preserve `computed_at`, Epic/API timestamps, scrape IDs, publication state, and historical correctness gates when moving or compacting data.
- Document rollback and cleanup before applying runtime defaults.

## Schema-change checklist

| Step | Question |
|---|---|
| Scope | Which tables, indexes, repository methods, scripts, and docs change? |
| Compatibility | Can old code read new rows and new code tolerate old rows during deploy? |
| Lock risk | What locks are taken, for how long, and during which service state? |
| Backfill | Is historical data needed, and can it be bounded/resumable? |
| Validation | What row counts, ranges, checksums, tests, or scrape/replay parity prove correctness? |
| Rollback | How do we revert schema/config and restore data if the change fails? |
| Docs | Which README, database, design, or runbook docs must be updated? |

## Import/export checklist

- Capture provider/source, scrape range, song/instrument/account scope, row count, byte count, format, compression, checksum, and storage path.
- Reject partial or failed manifests unless an explicit `--allow-incomplete` style flag is approved for a diagnostic.
- Import through existing typed repository helpers when possible.
- Store provenance in rows or manifests so future audits can distinguish sources.
- Validate count/range/checksum parity before enabling readers or pruning originals.

## Rollback patterns

- Keep experimental tables/indexes named and documented so they can be dropped safely.
- Put destructive steps behind `--apply` or equivalent operator flags.
- Keep old read paths available until candidate paths pass parity.
- For config defaults, document the old value and exact revert command.
- For archives/prunes, document how to rehydrate or regenerate before deletion.

## Implementation report

| Change | Files | Safety gate | Validation | Rollback | Docs |
|---|---|---|---|---|---|
| `<change>` | `<paths>` | `<timeouts/locks/approval>` | `<tests/parity>` | `<path>` | `<updated>` |
