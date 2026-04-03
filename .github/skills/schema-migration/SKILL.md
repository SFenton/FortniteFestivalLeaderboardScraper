---
name: schema-migration
description: "Add a database schema change to FSTService. Use when adding tables, columns, indexes, or modifying existing schema. Covers migration in DatabaseInitializer, query updates, DTO changes, and tests."
argument-hint: "Description of the schema change (e.g., 'Add difficulty column to LeaderboardEntries')"
---

# Schema Migration

## When to Use

- Adding a new table, column, or index to PostgreSQL
- Modifying existing schema (ALTER TABLE)
- Changing a DTO to match schema changes

## Prerequisites

Read the DB consistency registry: `/memories/repo/architecture/db-consistency-registry.md`

## Procedure

### 1. Plan the Schema Change

Determine:
- Which database? MetaDatabase (fst-meta) or InstrumentDatabase (fst-{instrument})
- New table or ALTER existing?
- Index requirements for query paths
- Impact on existing queries

### 2. Update DatabaseInitializer

In `FSTService/Persistence/DatabaseInitializer.cs`, add to the appropriate `EnsureSchemaAsync()`:

```csharp
// New table
await ExecuteAsync(conn, @"
    CREATE TABLE IF NOT EXISTS {table_name} (
        id SERIAL PRIMARY KEY,
        {columns}
    )");

// New column (idempotent)
await ExecuteAsync(conn, @"
    ALTER TABLE {table_name} ADD COLUMN IF NOT EXISTS {column} {type}");

// New index
await ExecuteAsync(conn, @"
    CREATE INDEX IF NOT EXISTS ix_{alias}_{columns}
    ON {table_name} ({columns})");
```

All DDL MUST use `IF NOT EXISTS` for idempotency.

### 3. Update DTOs

In `FSTService/Persistence/DataTransferObjects.cs`, add/modify the DTO:

```csharp
public record {DtoName}(
    string Field1,
    int Field2,
    // ... matching table columns
);
```

### 4. Update Database Class Queries

In `MetaDatabase.cs` or `InstrumentDatabase.cs`:
- Update SELECT queries to include new columns
- Update INSERT/UPSERT queries
- Update ON CONFLICT merge logic if applicable
- Use parameterized queries ALWAYS: `cmd.Parameters.AddWithValue("name", value)`
- Handle nulls: `(object?)nullable ?? DBNull.Value`

### 5. Write Tests

- Test schema creation (DatabaseInitializer idempotency)
- Test CRUD operations with new schema
- Test null handling for nullable columns
- Test migration from old schema (column added to existing data)

### 6. Verify

```bash
dotnet test FSTService.Tests\FSTService.Tests.csproj
```

## Checklist

- [ ] DDL uses `IF NOT EXISTS` for idempotency
- [ ] Index naming follows `ix_{alias}_{columns}` convention
- [ ] All queries parameterized (no string interpolation)
- [ ] DTOs updated in DataTransferObjects.cs
- [ ] Null handling: `(object?)nullable ?? DBNull.Value`
- [ ] ON CONFLICT logic follows domain trust model
- [ ] Tests cover schema creation + CRUD + nulls
- [ ] Coverage ≥ 94%
- [ ] Reviewed by fst-principal-db for consistency
