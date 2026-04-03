---
name: add-scrape-phase
description: "Add a new scrape pipeline phase to FSTService. Use when creating a new phase class, registering it in an orchestrator, adding progress tracking, and writing tests. Covers the full lifecycle from class creation to test coverage."
argument-hint: "Name of the new scrape phase (e.g., 'DifficultyCalculator')"
---

# Add Scrape Phase

## When to Use

- Adding a new scrape pipeline phase to FSTService
- Creating a new background processing step that runs during the scrape pass

## Prerequisites

Read the consistency registry first: `/memories/repo/architecture/fst-consistency-registry.md`

## Procedure

### 1. Create the Phase Class

Create `FSTService/Scraping/{PhaseName}.cs` following the canonical contract:

```csharp
public class {PhaseName}
{
    private readonly ILogger<{PhaseName}> _log;
    private readonly ScrapeProgressTracker _progress;
    // ... other dependencies

    public {PhaseName}(
        ILogger<{PhaseName}> log,
        ScrapeProgressTracker progress,
        // ... persistence deps, service deps, IOptions<>
    )
    {
        _log = log;
        _progress = progress;
    }

    public async Task<int> ExecuteAsync(CancellationToken ct = default)
    {
        // 1. Begin progress tracking
        _progress.BeginPhaseProgress(totalItems);

        // 2. Process items with SharedDopPool
        var processed = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Process item
                _progress.ReportPhaseItemComplete();
                processed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Failed to process {Item}", item);
            }
        }

        _log.LogInformation("{Phase} completed: {Count} items processed", nameof({PhaseName}), processed);
        return processed;
    }
}
```

### 2. Register DI

In `FSTService/Program.cs`, add:
```csharp
builder.Services.AddSingleton<{PhaseName}>();
```

### 3. Register in Orchestrator

In the appropriate orchestrator (`ScrapeOrchestrator.cs` or `PostScrapeOrchestrator.cs`), inject and call:
```csharp
_progress.SetPhase("{phase_name}");
var count = await {phaseName}.ExecuteAsync(ct);
_log.LogInformation("{Phase}: {Count}", "{PhaseName}", count);
```

### 4. Add Progress Tracking

Ensure the phase uses:
- `_progress.BeginPhaseProgress(total)` at start
- `_progress.ReportPhaseItemComplete()` per item
- `_progress.SetSubOperation("description")` for sub-steps

### 5. Write Tests

Create `FSTService.Tests/Unit/{PhaseName}Tests.cs`:
- Test happy path (items processed correctly)
- Test empty input (no items to process)
- Test cancellation (CancellationToken respected)
- Test error recovery (individual item failure doesn't crash phase)
- Mock dependencies with NSubstitute

### 6. Verify

```bash
dotnet test FSTService.Tests\FSTService.Tests.csproj
```

Ensure coverage stays above 94%.

## Checklist

- [ ] Phase class follows canonical contract (CancellationToken, progress, error recovery)
- [ ] Uses SharedDopPool for concurrency (not raw SemaphoreSlim)
- [ ] Registered in DI (Program.cs)
- [ ] Called from orchestrator in correct phase order
- [ ] Progress tracking integrated
- [ ] Tests cover: happy path, empty, cancellation, error recovery
- [ ] Coverage ≥ 94%
- [ ] Reviewed by fst-principal-architect for consistency
