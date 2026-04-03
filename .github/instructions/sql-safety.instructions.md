---
description: "Use when writing or modifying database queries in FSTService persistence layer. Covers parameterized queries, transaction patterns, bulk writes."
applyTo: "FSTService/Persistence/**/*.cs"
---

# SQL Safety — FSTService Persistence

- ALWAYS use parameterized queries: `cmd.Parameters.AddWithValue("name", value)`.
- NEVER interpolate values into SQL strings (even for safe types like int).
- Transactions: `using var tx = conn.BeginTransaction()` → `tx.Commit()`.
- Bulk writes: ≤50 → prepared statements with `cmd.Prepare()`, >50 → COPY binary import.
- Connection: `using var conn = _ds.OpenConnection()` — NpgsqlDataSource handles pooling.
- Null params: `(object?)nullable ?? DBNull.Value`.
- Schema DDL: `IF NOT EXISTS` for idempotency.
- Index naming: `ix_{alias}_{columns}`.
