---
name: new-web-page
description: "Scaffold a new page in FortniteFestivalWeb. Use when adding a new route, page component, CSS module, tests, and navigation integration. Follows the canonical Page shell pattern."
argument-hint: "Name of the new page (e.g., 'StatisticsPage')"
---

# New Web Page

## When to Use

- Adding a new page/route to FortniteFestivalWeb
- Creating a new feature area with its own URL

## Prerequisites

Read the web consistency registry: `/memories/repo/architecture/web-consistency-registry.md`

## Procedure

### 1. Add Route

In `FortniteFestivalWeb/src/routes.ts`:
```typescript
export const ROUTES = {
  // ... existing routes
  NEW_FEATURE: '/new-feature',
};
```

### 2. Create Page Component

Create `FortniteFestivalWeb/src/pages/{feature}/{FeatureName}Page.tsx`:

```tsx
import { useLoadPhase } from '../../hooks/ui/useLoadPhase';
import { Page } from '../Page';
import { PageHeader } from '../../components/common/PageHeader';
import { EmptyState } from '../../components/common/EmptyState';
import { ArcSpinner } from '../../components/common/ArcSpinner';
import { parseApiError } from '../../utils/formatters';
import { useStaggerStyle } from '../../hooks/ui/useStaggerStyle';
import s from './{FeatureName}Page.module.css';

export function {FeatureName}Page() {
  const { phase: loadPhase, shouldStagger } = useLoadPhase();
  // Data fetching hooks here

  const headerStagger = useStaggerStyle(0, { skip: !shouldStagger });

  return (
    <Page
      scrollRestoreKey="{feature}"
      loadPhase={loadPhase}
      before={
        <PageHeader
          title="{Feature Title}"
          style={headerStagger.style}
          onAnimationEnd={headerStagger.onAnimationEnd}
        />
      }
    >
      {/* Page content */}
    </Page>
  );
}
```

### 3. Create CSS Module

Create `FortniteFestivalWeb/src/pages/{feature}/{FeatureName}Page.module.css`:
```css
/* Feature-specific styles (only if ≥3 rules needed) */
```

### 4. Register Route

In `FortniteFestivalWeb/src/App.tsx`, add the route:
```tsx
<Route path={ROUTES.NEW_FEATURE} element={<{FeatureName}Page />} />
```

### 5. Add Navigation

Add to sidebar/FAB navigation as appropriate:
- Desktop: `PinnedSidebar` menu item
- Mobile: FAB action or bottom nav

### 6. Write Tests

Create `FortniteFestivalWeb/__test__/pages/{feature}/{FeatureName}Page.test.tsx`:
- Test rendering with loading state
- Test error state display
- Test empty state display
- Test with data

Create E2E spec `FortniteFestivalWeb/e2e/{feature}.fre.spec.ts`:
- Test across 4 viewports
- Test navigation to page
- Test core functionality

### 7. Verify

```bash
cd FortniteFestivalWeb && npm test
cd FortniteFestivalWeb && npx playwright test
```

## Checklist

- [ ] Route added to `routes.ts`
- [ ] Page uses `<Page>` shell with `scrollRestoreKey`
- [ ] Loading state: `useLoadPhase()` → `ArcSpinner`
- [ ] Error state: `<EmptyState>` with `parseApiError()`
- [ ] Stagger animation integrated
- [ ] CSS module co-located (if ≥3 rules)
- [ ] Route registered in `App.tsx`
- [ ] Navigation integrated (sidebar/FAB)
- [ ] Unit tests (Vitest)
- [ ] E2E tests (Playwright, 4 viewports)
- [ ] Reviewed by web-principal-architect and web-principal-designer
