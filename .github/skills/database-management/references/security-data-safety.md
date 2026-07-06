# Security and Data Safety Reference

Use this reference for secrets handling, backups/restores, destructive maintenance, data retention, Epic/API constraints, and data-integrity gates.

## Secrets and access

- Never commit credentials, connection strings with passwords, API keys, tokens, or provider secrets.
- Redact secrets from command output before sharing.
- Prefer existing environment variables and `.env.example` documentation when adding configuration.
- Use least privilege for new users or service accounts.
- Do not send proprietary data, credentials, or nonpublic artifacts to third-party systems.

## Backup and restore readiness

Before destructive or irreversible work, identify:

- Source database, tables, schemas, and date ranges.
- Current row counts and min/max timestamps.
- Backup, archive, or regeneration path.
- Restore/rehydration command or documented manual path.
- Expected downtime, locks, and services that must pause.
- Post-restore validation: counts, ranges, checksums, application health, and representative queries.

## Destructive maintenance gates

Require explicit operator approval for:

- Data deletion or pruning.
- `VACUUM FULL`, table rewrites, column drops on large tables, and large non-concurrent index builds.
- Switching default read paths to compact/exported data.
- Retention policy changes.
- Platform cutovers or source-of-truth changes.
- Terminating database backends or restarting services during live-sensitive windows.

## Integrity gates

Use the strongest practical gate for the risk:

| Operation | Minimum gate |
|---|---|
| Read-only probe | Query text, bounded scope, result sample |
| Export | Count, min/max timestamp, byte count, checksum/fingerprint, manifest |
| Import | Source manifest validation, row count/range parity, duplicate/idempotency behavior |
| Compact projection | Count/range parity, typed-field spot checks, query parity, original timestamp gates |
| Runtime read-path switch | Correctness parity plus scrape/replay parity when behavior can change |
| Prune/delete | Complete manifest coverage, restore path, maintenance approval, post-prune validation |

## Provider and retention constraints

When data comes from external providers, document:

- Provider, feed, entitlement, and source label.
- Rate limits, quotas, pacing, and retry behavior.
- Retention/storage terms and cleanup expectations.
- Whether data may be archived, transformed, redistributed, or rehydrated.
- How provenance is stored for future audits.

## Data-safety report

| Operation | Data scope | Approval needed | Integrity gate | Restore path | Residual risk | Decision |
|---|---|---|---|---|---|---|
| `<operation>` | `<tables/range>` | `<yes/no>` | `<gate>` | `<path>` | `<risk>` | `<tier>` |
