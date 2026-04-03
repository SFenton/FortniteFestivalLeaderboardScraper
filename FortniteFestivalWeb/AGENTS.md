# FortniteFestivalWeb — Development Guidelines

## Stack

React 19 + TypeScript + Vite. React Router 7.1 (HashRouter). React Query (@tanstack) for server state. CSS Modules + @festival/theme for theming.

## Architecture

### Routing (`src/routes.ts`)

`/songs`, `/songs/:songId`, `/songs/:songId/:instrument`, `/player/:accountId`, `/rivals`, `/leaderboards`, `/shop`, `/suggestions`, `/compete`, `/settings` (+ nested routes)

### State Management

- 9 React Contexts: Festival, Settings, Shop, PlayerData, FirstRun, FabSearch, SearchQuery, ScrollContainer, FeatureFlags
- No Redux/Zustand — pure Context + useState
- React Query for remote data (`src/api/queryClient.ts`, `queryKeys.ts`)
- `useLocalStorageSettings()` for persistent preferences

### Page Structure (canonical)

Every page uses the `<Page>` shell:
```tsx
<Page scrollRestoreKey="feature" loadPhase={loadPhase} firstRun={firstRunConfig} before={header} after={modals}>
  {content}
</Page>
```
- Loading: `useLoadPhase()` → `ArcSpinner` → content fade-in
- Error: `<EmptyState>` with `parseApiError()`
- Empty: `<EmptyState>` with descriptive message

### Styling

- **CSS Modules** for ≥3 rules (co-located `.module.css` with component)
- **Inline styles** for <3 rules
- **Shared effects**: `effects.module.css` (`navFrosted`, `headerFrosted`, etc.)
- **Theme**: `theme.css` variables + `@festival/theme` (Size, Layout, QUERY_NARROW_GRID)
- **Animations**: `animations.module.css` for keyframes

### Hook Patterns

- Modal: `useModalState<T>(defaults)`
- Stagger: `useStaggerStyle(delay, opts)` for items; `buildStaggerStyle()` in `.map()`
- FAB actions: register in `useEffect` with `[fabSearch, ...deps]` dependency array
- Data: React Query `useQuery()` / `useQueries()`

### Component Patterns

- Common components: `PageHeader`, `EmptyState`, `FrostedCard`, `ActionPill`, `SearchBar`, `Modal`
- All display components support `style?` + `onAnimationEnd?` for stagger integration
- Modals: `{ visible, title, onClose, onApply, onReset?, children }`

### State Persistence Rules

- Navigation state (tab, sort) → URL `searchParams`
- User preferences (view mode, dismissed tips) → `localStorage`
- Remote data → React Query cache

## Testing

- **Unit**: Vitest + @testing-library/react (181 test files in `__test__/`)
- **E2E**: Playwright (17 specs in `e2e/`, 4 viewports: desktop, desktop-narrow, mobile, mobile-narrow)
- **Helpers**: `TestProviders.ts` for context wrappers
- **Run**: `npm test` (Vitest), `npx playwright test` (E2E)

## Dependencies

React 19, React Router 7.1, @tanstack/react-query 5.x, Recharts 3.x, @dnd-kit 6.x, @tanstack/react-virtual 3.x, react-i18next 16.x, KaTeX 0.16.x

## Monorepo Packages

- `@festival/core` — shared types, API client, enums, instruments
- `@festival/theme` — Size, Layout, breakpoints
- `@festival/ui-utils` — shared UI utilities
- `@festival/auth` — Epic OAuth, JWT parsing

## Web Refactoring

Active refactor tracked in `docs/refactor/PLAN.md` (18 phases). CSS migration rules in `docs/refactor/CSS_MIGRATION_RULES.md`.
