---
name: add-feature-flag
description: "Add an end-to-end feature flag across FSTService and FortniteFestivalWeb. Use when gating a feature behind a toggle that can be enabled/disabled via config. Covers FeatureOptions, appsettings, FeatureFlagsContext, FeatureGate, and environment variables."
argument-hint: "Name of the feature flag (e.g., 'Difficulty')"
---

# Add Feature Flag

## When to Use

- Gating a new feature behind a toggle
- Creating a gradual rollout mechanism
- Adding a dev-only feature that shouldn't ship to production yet

## Procedure

### 1. FSTService — Add Flag

In `FSTService/FeatureOptions.cs`:
```csharp
public class FeatureOptions
{
    // ... existing flags
    public bool {FlagName} { get; set; }
}
```

### 2. FSTService — Set Defaults

In `FSTService/appsettings.json`:
```json
{
  "Features": {
    "{FlagName}": false
  }
}
```

In `FSTService/appsettings.Development.json`:
```json
{
  "Features": {
    "{FlagName}": true
  }
}
```

### 3. FSTService — Expose via API

The feature flags are already served by `FeatureEndpoints.cs` at `GET /api/features`. Verify the new flag appears in the response.

### 4. FortniteFestivalWeb — Consume Flag

In `FortniteFestivalWeb/src/contexts/FeatureFlagsContext.tsx`, add the flag:
```typescript
export interface FeatureFlags {
  // ... existing flags
  {flagName}: boolean;
}
```

### 5. FortniteFestivalWeb — Gate Content

Use `<FeatureGate>` to conditionally render:
```tsx
import { FeatureGate } from '../components/routing/FeatureGate';

<FeatureGate flag="{flagName}">
  <ProtectedContent />
</FeatureGate>
```

Or use the hook for conditional logic:
```tsx
const { {flagName} } = useFeatureFlags();
if (!{flagName}) return null;
```

### 6. Docker/Deploy — Environment Variable

The flag can be overridden via environment variable:
```
Features__{FlagName}=true
```

In `deploy/docker-compose.yml`, add to fstservice environment:
```yaml
- Features__{FlagName}=${FEATURE_{FLAGNAME}:-false}
```

### 7. Write Tests

- FSTService: Test flag appears in `/api/features` response
- Web: Test `FeatureGate` renders/hides based on flag
- E2E: Test feature visibility with flag on/off

## Checklist

- [ ] Flag added to `FeatureOptions.cs`
- [ ] Default set in `appsettings.json` (false) and `appsettings.Development.json` (true)
- [ ] Flag exposed via `GET /api/features`
- [ ] `FeatureFlagsContext.tsx` updated with new flag
- [ ] `FeatureGate` or hook used to gate content
- [ ] Environment variable override works
- [ ] Docker Compose updated
- [ ] Tests for both service and web
