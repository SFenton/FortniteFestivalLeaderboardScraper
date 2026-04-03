---
description: "Use when writing or modifying C# code in FSTService. Covers nullable refs, async/await, ILogger, parameterized SQL, DI patterns."
applyTo: "FSTService/**/*.cs"
---

# C# Conventions — FSTService

- Nullable reference types enabled. Handle nullability explicitly.
- `async/await` throughout. Propagate `CancellationToken` in all background work.
- `ILogger<T>` for logging. INFO for milestones, WARN for recoverable errors, DEBUG for details.
- `System.Text.Json` for serialization. No Newtonsoft in FSTService.
- Parameterized SQL ALWAYS: `cmd.Parameters.AddWithValue()` or `cmd.Parameters.Add(name, NpgsqlDbType)`.
- NEVER string-interpolate values into SQL.
- Constructor DI with `private readonly` fields.
- Error handling: `catch (Exception ex) when (ex is not OperationCanceledException)`.
