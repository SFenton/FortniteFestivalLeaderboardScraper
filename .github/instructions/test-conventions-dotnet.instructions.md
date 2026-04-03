---
description: "Use when writing xUnit tests in FSTService.Tests. Covers NSubstitute mocking, WebApplicationFactory, InMemoryMetaDatabase, coverage threshold."
applyTo: "FSTService.Tests/**/*.cs"
---

# Test Conventions — FSTService

- Framework: xUnit + NSubstitute (mocking) + FluentAssertions-style.
- Test file mirrors source: `MetaDatabaseTests.cs` tests `MetaDatabase.cs`.
- Mock interfaces with `Substitute.For<IInterface>()`.
- Persistence tests use `InMemoryMetaDatabase` or `TempInstrumentDatabase`.
- Integration tests use `WebApplicationFactory<Program>`.
- Coverage gate: 94% line coverage (CI enforced).
- Prefer testing public API surface; use internals only when needed.
- Always verify CancellationToken is propagated in async tests.
