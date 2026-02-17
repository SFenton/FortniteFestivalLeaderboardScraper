# React Native Codebase Cleanup Plan

> **Last updated:** February 16, 2026
>
> This document describes all the refactoring work needed to de-duplicate and standardize
> the React Native app (`FortniteFestivalRN/`). The goal is to eliminate copy-pasted code,
> extract shared components, and create a clean, maintainable architecture.

---

## Table of Contents

1. [Current Architecture Overview](#1-current-architecture-overview)
2. [Problem Inventory](#2-problem-inventory)
3. [Target Architecture](#3-target-architecture)
4. [Work Items](#4-work-items)
   - [WI-1: Create `@festival/app-screens` shared package](#wi-1-create-festivalapp-screens-shared-package)
   - [WI-2: Unify `AppNavigator` variants](#wi-2-unify-appnavigator-variants)
   - [WI-3: Create `FadeScrollView` component](#wi-3-create-fadescrollview-component)
   - [WI-4: Extract shared theme/colors](#wi-4-extract-shared-themecolors)
   - [WI-5: Extract shared style modules](#wi-5-extract-shared-style-modules)
   - [WI-6: Create `BaseSongRow` and unify song rows](#wi-6-create-basesongrow-and-unify-song-rows)
   - [WI-7: Create generic `CategoryCard`](#wi-7-create-generic-categorycard)
   - [WI-8: Extract `ToggleRow` / `ChoiceButton` controls](#wi-8-extract-togglerow--choicebutton-controls)
   - [WI-9: Create `WindowsHostScreen` generic](#wi-9-create-windowshostscreen-generic)
   - [WI-10: Create `createSubNavigator()` factory](#wi-10-create-createsubnavigator-factory)
   - [WI-11: Merge platform-split files](#wi-11-merge-platform-split-files)
   - [WI-12: Extract shared utility functions](#wi-12-extract-shared-utility-functions)
   - [WI-13: `SettingsScreen` — import `modalStyles` instead of re-defining](#wi-13-settingsscreen--import-modalstyles-instead-of-re-defining)
5. [Validation Checklist](#5-validation-checklist)
6. [Risk Notes](#6-risk-notes)

---

## 1. Current Architecture Overview

The React Native project lives at `FortniteFestivalRN/` and uses a **yarn workspaces** monorepo with these packages:

| Package | Path | Purpose | Lines (approx) |
|---------|------|---------|----------------|
| `@festival/core` | `packages/core/` | Pure TypeScript logic — models, services, persistence, formatters, suggestion generator. No React. | ~2,500 |
| `@festival/contexts` | `packages/contexts/` | React context providers (`FestivalContext`, `AuthContext`), persistence adapters | ~400 |
| `@festival/ui` | `packages/ui/` | Shared React Native UI components — cards, modals, rows, instruments, etc. | ~4,700 |
| `@festival/local-app` | `packages/local-app/` | Local-mode app entry point, screens, and navigation | ~3,700 |
| `@festival/server-app` | `packages/server-app/` | Server-mode app entry point, screens, and navigation | ~3,700 |

The two app packages (`local-app`, `server-app`) exist because the app has two modes:
- **local-app**: reads data from local file storage
- **server-app**: reads data from FSTService API

Both modes share a `FestivalContext` that abstracts the data source via adapters in `@festival/contexts`.

### Key Files by Package

**`@festival/ui` (`packages/ui/src/`)**:
| File | Lines | Description |
|------|-------|-------------|
| `instruments/InstrumentCard.tsx` | 284 | Instrument score card (mobile) |
| `instruments/InstrumentCard.windows.tsx` | 290 | Instrument score card (Windows) |
| `instruments/StatisticsInstrumentCard.tsx` | 305 | Statistics overview card per instrument |
| `instruments/instrumentVisuals.ts` | 36 | Icon sources + status color helpers |
| `songs/SongRow.tsx` | 845 | Main song list row with metadata pills |
| `suggestions/SuggestionCard.tsx` | 161 | Suggestion category card wrapper |
| `suggestions/SuggestionSongRow.tsx` | 370 | Song row within suggestion cards |
| `Modals/FilterModal.tsx` | 531 | Song list filter modal |
| `Modals/SortModal.tsx` | 367 | Song list sort modal |
| `Modals/SuggestionsFilterModal.tsx` | 259 | Suggestions filter modal |
| `Modals/modalStyles.ts` | 227 | Shared modal styles |
| `Modals/PlatformModal.tsx` | 142 | Platform-aware modal wrapper |
| `Accordion.tsx` | 114 | Collapsible section (mobile, uses reanimated) |
| `Accordion.windows.tsx` | 116 | Collapsible section (Windows, uses RN Animated) |
| `FrostedSurface.tsx` | 30 | Frosted glass card surface (default) |
| `FrostedSurface.windows.tsx` | 31 | Frosted glass card surface (Windows) |
| `FrostedSurface.android.tsx` | 84 | Frosted glass card surface (Android) |
| `FrostedSurface.ios.tsx` | 85 | Frosted glass card surface (iOS) |
| `AnimatedBackground.tsx` | 290 | Animated album art background |
| `SlidingRowsBackground.tsx` | 220 | Scrolling rows background animation |
| Other files | ~550 | Screen, PageHeader, MarqueeText, etc. |

**`@festival/local-app` (`packages/local-app/src/`)**:
| File | Lines | Identical to server-app? |
|------|-------|--------------------------|
| `screens/SongsScreen.tsx` | 709 | ✅ IDENTICAL |
| `screens/SongDetailsScreen.tsx` | 480 | ⚠️ 8 trivial line diffs |
| `screens/StatisticsScreen.tsx` | 591 | ✅ IDENTICAL |
| `screens/SuggestionsScreen.tsx` | 671 | ✅ IDENTICAL |
| `screens/SettingsScreen.tsx` | 725 | ✅ IDENTICAL |
| `screens/SyncScreen.tsx` | 210 | ✅ IDENTICAL |
| `screens/WindowsSongsHost.tsx` | 21 | ✅ IDENTICAL |
| `screens/WindowsStatisticsHost.tsx` | 21 | ✅ IDENTICAL |
| `screens/WindowsSuggestionsHost.tsx` | 21 | ✅ IDENTICAL |
| `navigation/routes.ts` | 8 | ✅ IDENTICAL |
| `navigation/SongsNavigator.tsx` | 83 | ✅ IDENTICAL |
| `navigation/StatisticsNavigator.tsx` | 87 | ✅ IDENTICAL |
| `navigation/SuggestionsNavigator.tsx` | 91 | ✅ IDENTICAL |
| `navigation/useOptionalBottomTabBarHeight.ts` | 57 | ✅ IDENTICAL |
| `navigation/windowsFlyoutUi.tsx` | 57 | ✅ IDENTICAL |
| `navigation/AppNavigator.tsx` | 474 | ⚠️ 45 line diffs |

**Total duplicated lines: ~4,305** (summing identical files that exist in both packages).

---

## 2. Problem Inventory

### P1: `local-app` ≡ `server-app` (massive duplication)

14 of 16 files are **byte-for-byte identical** between the two app packages. The two files that differ have only trivial differences:

**`SongDetailsScreen.tsx` differences (8 line diffs):**
| local-app | server-app |
|-----------|------------|
| `import React, {useEffect, useMemo, useState}` | `import React, {useCallback, useEffect, useMemo, useState}` |
| `onLayout={e => {` | `onLayout={useCallback((e) => {` |
| `return Math.abs(cur - next) >= 1 ? next : cur;` | `return Math.abs(cur - next) >= 2 ? next : cur;` |
| (end of function) | `}, [])}` |

These are trivially reconcilable — the `useCallback` wrap is an optimization and the threshold `1` vs `2` should just be `1`.

**`AppNavigator.tsx` differences (45 line diffs):**
| Aspect | local-app | server-app |
|--------|-----------|------------|
| `winRoot` background | `transparent` | `#1A0830` |
| Flyout background | `rgba(18,24,38,0.97)` | `#1A0830` |
| Flyout border | `borderRightColor: '#263244'`, `borderRightWidth: 1` | `borderRightColor: '#1E2A3A'`, `borderRightWidth: StyleSheet.hairlineWidth` |
| Flyout header | _(none)_ | Has a "Menu" header row with a `×` close button |
| Extra styles | _(none)_ | `flyoutHeader`, `flyoutHeaderTitle`, `flyoutClose`, `flyoutCloseText` |

### P2: `FadeScrollView` pattern duplicated in 5 screens

Every main screen wraps its scrollable content in the same `MaskedView` + `LinearGradient` boilerplate for fade-in/fade-out edges:

```tsx
<MaskedView
  style={styles.fadeScrollContainer}
  maskElement={
    <View style={styles.fadeMaskContainer}>
      <LinearGradient colors={['transparent', 'black']} style={styles.fadeGradient} />
      <View style={styles.fadeMaskOpaque} />
      <LinearGradient colors={['black', 'transparent']} style={styles.fadeGradient} />
    </View>
  }
>
  {/* scroll content */}
</MaskedView>
```

Plus 4 identical style definitions (`fadeScrollContainer`, `fadeMaskContainer`, `fadeMaskOpaque`, `fadeGradient`).

**Affected files:** `SongsScreen.tsx`, `StatisticsScreen.tsx`, `SuggestionsScreen.tsx`, `SettingsScreen.tsx`, `SongDetailsScreen.tsx`

### P3: Percentile pill styles duplicated 3×

These 4 style definitions are copy-pasted identically into 3 files:

```typescript
percentilePill: { backgroundColor: '#1D3A71', borderRadius: 10, paddingHorizontal: 8, paddingVertical: 3, borderWidth: 2, borderColor: 'transparent', alignItems: 'center', justifyContent: 'center' },
percentilePillGold: { backgroundColor: '#332915', borderColor: '#FFD700' },
percentilePillText: { color: '#FFFFFF', fontWeight: '800', fontSize: 12 },
percentilePillTextGold: { color: '#FFD700' },
```

**Affected files:**
- `packages/ui/src/songs/SongRow.tsx` (lines ~700-720)
- `packages/ui/src/suggestions/SuggestionSongRow.tsx` (lines ~300-320)
- `packages/local-app/src/screens/StatisticsScreen.tsx` (lines ~565-585)

### P4: `TopSongsCard` ≈ `SuggestionCard`

`TopSongsCard` is defined inline inside `StatisticsScreen.tsx` (~100 lines). It has an almost identical structure to `SuggestionCard` in `@festival/ui`:

| Feature | `SuggestionCard` | `TopSongsCard` |
|---------|------------------|----------------|
| Outer wrapper | `FrostedSurface` with `borderRadius: 12, padding: 12, gap: 8` | Identical |
| Header row | Title + description + optional instrument icon | Identical layout |
| Song list | `SuggestionSongRow` items | `TopSongRow` items |
| Virtualization | `FlatList` if `> 12` items | `FlatList` if `> 12` items |

### P5: `TopSongRow` ≈ `SuggestionSongRow` ≈ `SongRow`

Three different "song row" components exist. They all render:
`[thumbnail] [title/artist] ... [right-side metric]`

| Feature | `SongRow` (845 lines) | `SuggestionSongRow` (370 lines) | `TopSongRow` (~70 lines in StatisticsScreen) |
|---------|----------------------|---------------------------------|----------------------------------------------|
| Thumbnail | ✅ 44×44, rounded | ✅ same | ✅ same |
| Title/Artist | MarqueeText | MarqueeText | Plain `Text` with `numberOfLines={1}` |
| Right side | Instrument chips + metadata pills | Category-specific metrics | Percentile pill + star/FC text |
| Pressable | ✅ | ✅ | ✅ |
| Pressed style | `opacity: 0.85` | `opacity: 0.85` | `opacity: 0.85` |

### P6: `SettingsScreen` re-defines `modalStyles`

The following style names are defined in both `Modals/modalStyles.ts` AND `SettingsScreen.tsx` with identical or near-identical values:

- `orderRow`, `orderRowFirst`, `orderRowLast`, `orderRowSeparator`, `orderName`
- `choice`, `choiceSelected`, `choiceText`, `choiceTextSelected`
- `orderBtns`, `orderBtn`, `orderBtnText`
- `smallBtnPressed`

### P7: `ToggleRow` defined independently in 3 places

Toggle row components (a label + Switch) appear in:
- `SettingsScreen.tsx` — `ToggleRow` (with icon support) and `DescriptorToggleRow` (with description text)
- `FilterModal.tsx` — instrument toggle with icon circles
- `SuggestionsFilterModal.tsx` — similar instrument toggles

### P8: `shouldShowCategory` / `isInstrumentEnabled` duplicated

These pure functions are copy-pasted identically in:
- `StatisticsScreen.tsx` (lines 20-55)
- `SuggestionsScreen.tsx` (lines 25-55)

They map instrument keys to `settings.showLead`, `settings.showBass`, etc.

### P9: Three identical sub-navigators

`SongsNavigator.tsx` (83 lines), `StatisticsNavigator.tsx` (87 lines), `SuggestionsNavigator.tsx` (91 lines) all follow the same pattern:
1. Define a `BackChevron` component
2. Define a `*ListWrapper` component
3. Create a `Stack.Navigator` with two screens: list + SongDetails
4. Same `headerStyle`, `contentStyle`, `animation` options
5. Same `StyleSheet` definitions

### P10: Three identical Windows host components

`WindowsSongsHost.tsx`, `WindowsStatisticsHost.tsx`, `WindowsSuggestionsHost.tsx` (21 lines each) are the same pattern:
```tsx
export function WindowsXxxHost() {
  const [songId, setSongId] = useState<string | null>(null);
  const {setChromeHidden} = useWindowsFlyoutUi();
  // toggle between list view and detail view
  if (songId) return <SongDetailsView ... />;
  return <XxxScreen onOpenSong={(id) => setSongId(id)} />;
}
```

### P11: Platform-split files share 95%+ code

| File pair | Lines (mobile / windows) | What differs |
|-----------|-------------------------|--------------|
| `InstrumentCard.tsx` / `.windows.tsx` | 284 / 290 | Only `DifficultyBars` rendering: SVG `Polygon` vs `View` + `skewX` transform |
| `Accordion.tsx` / `.windows.tsx` | 114 / 116 | Animation API: `react-native-reanimated` vs RN `Animated` |
| `FrostedSurface.tsx` / `.windows.tsx` | 30 / 31 | Default `fallbackColor` opacity: `0.78` vs `0.97` |

### P12: No shared color constants

Colors are hardcoded as string literals across the entire codebase. Some commonly repeated ones:

| Color | Usage | Approx occurrences |
|-------|-------|--------------------|
| `#263244` | Border color | 10+ |
| `#0B1A2E`, `#0B1220` | Dark backgrounds | 8+ |
| `#1D3A71` | Pill backgrounds | 5+ |
| `#FFD700` | Gold accent | 8+ |
| `#1A0830` | Primary dark background | 10+ |
| `#D7DEE8` | Secondary text | 15+ |
| `#607089` | Disabled text | 5+ |
| `#9AA6B2` | Muted text | 5+ |
| `rgba(18,24,38,0.78)` | Card/frosted surface BG | 5+ |
| `#2B3B55` | Input/button borders | 10+ |
| `rgba(45,130,230,0.4)` | Primary button BG | 5+ |

---

## 3. Target Architecture

```
FortniteFestivalRN/
  packages/
    core/                              # UNCHANGED — pure logic, no React
      src/
        index.ts
        instruments.ts, models.ts, settings.ts, ...
        app/                           # formatters, filtering, statistics
        auth/, calendar/, epic/, io/, persistence/, services/, suggestions/

    contexts/                          # UNCHANGED — React contexts
      src/
        AuthContext.tsx, FestivalContext.tsx, adapters/, index.ts

    ui/                                # REFACTORED — shared component library
      src/
        index.ts

        theme/                         # NEW — centralized design tokens
          colors.ts                    #   all color constants
          spacing.ts                   #   padding/margin constants, WIN_SCROLLBAR_INSET
          typography.ts                #   font sizes, weights, text styles

        styles/                        # NEW — shared StyleSheet modules
          cardStyles.ts                #   base card border radius, padding, gap
          pillStyles.ts                #   percentilePill, percentPill, seasonPill, etc.
          songRowStyles.ts             #   thumbnail, title, meta, pressable/pressed
          listStyles.ts                #   orderRow, orderRowFirst, orderRowSeparator, etc.
          buttonStyles.ts              #   button, buttonSecondary, buttonPurple, etc.
          gridStyles.ts                #   cardGrid columns, sectionHeader, masonry

        FadeScrollView.tsx             # NEW — wraps MaskedView + LinearGradient
        Screen.tsx                     # existing
        AnimatedBackground.tsx         # existing
        SlidingRowsBackground.tsx      # existing
        CenteredEmptyStateCard.tsx     # existing
        PageHeader.tsx                 # existing
        HamburgerButton.tsx            # existing
        FestivalTextInput.tsx          # existing
        FrostedSurface.tsx             # MERGED — use Platform.select for fallbackColor
        FrostedSurface.android.tsx     # existing
        FrostedSurface.ios.tsx         # existing
        MarqueeText.tsx                # existing
        IntSlider.tsx                  # existing

        Accordion.tsx                  # SIMPLIFIED — one file, animation via hook
        useAccordionAnimation.ts       # NEW — reanimated (mobile)
        useAccordionAnimation.windows.ts  # NEW — RN Animated (Windows)

        instruments/
          instrumentVisuals.ts         # existing
          InstrumentCard.tsx           # SIMPLIFIED — one file
          DifficultyBars.tsx           # NEW — platform-split sub-component
          DifficultyBars.windows.tsx   # NEW
          StatisticsInstrumentCard.tsx  # existing

        songs/                         # REFACTORED — unified row hierarchy
          BaseSongRow.tsx              # NEW — shared base (thumbnail, title, artist, pressable)
          SongRow.tsx                  # REFACTORED — extends BaseSongRow
          SuggestionSongRow.tsx        # REFACTORED — extends BaseSongRow
          TopSongRow.tsx               # NEW — extracted from StatisticsScreen

        cards/                         # NEW — unified card components
          CategoryCard.tsx             # NEW — generic card (header + song list)
          SuggestionCard.tsx           # REFACTORED — thin wrapper

        controls/                      # NEW — unified form controls
          ToggleRow.tsx                # NEW — label + optional icon/desc + Switch
          ChoiceButton.tsx             # NEW — pill-style choice button
          InstrumentToggleRow.tsx      # NEW — instrument icon toggle (from modals)

        Modals/                        # existing (minor refactor only)
          PlatformModal.tsx
          modalStyles.ts
          FilterModal.tsx              # REFACTORED — uses InstrumentToggleRow
          SortModal.tsx
          SuggestionsFilterModal.tsx    # REFACTORED — uses InstrumentToggleRow

        useCardGrid.ts                 # existing

    app-screens/                       # NEW PACKAGE — @festival/app-screens
      package.json
      src/
        index.ts
        screens/
          SongsScreen.tsx              # moved from local-app (single copy)
          SongDetailsScreen.tsx        # unified (use useCallback, threshold=1)
          StatisticsScreen.tsx         # moved, TopSongsCard/TopSongRow removed (now in ui)
          SuggestionsScreen.tsx        # moved
          SettingsScreen.tsx           # moved, imports modalStyles instead of re-defining
          SyncScreen.tsx               # moved
        navigation/
          routes.ts                    # moved
          createSubNavigator.tsx       # NEW — factory replacing 3 navigators
          useOptionalBottomTabBarHeight.ts  # moved
          windowsFlyoutUi.tsx          # moved
          WindowsHostScreen.tsx        # NEW — generic, replaces 3 host files
        utils/
          instrumentFilters.ts         # NEW — shouldShowCategory, isInstrumentEnabled, etc.

    local-app/                         # SLIMMED — entry point + AppNavigator only
      src/
        index.tsx
        navigation/
          AppNavigator.tsx             # local-app variant

    server-app/                        # SLIMMED — entry point + AppNavigator only
      src/
        index.tsx
        navigation/
          AppNavigator.tsx             # server-app variant (flyout header + close button)
```

---

## 4. Work Items

### WI-1: Create `@festival/app-screens` shared package

**Priority:** P0 — Highest impact, eliminates ~4,300 lines of duplication.

**What to do:**
1. Create `packages/app-screens/package.json` with name `@festival/app-screens`
2. Add dependencies on `@festival/core`, `@festival/contexts`, `@festival/ui`
3. Move these files from `packages/local-app/src/` to `packages/app-screens/src/`:
   - `screens/SongsScreen.tsx`
   - `screens/StatisticsScreen.tsx`
   - `screens/SuggestionsScreen.tsx`
   - `screens/SettingsScreen.tsx`
   - `screens/SyncScreen.tsx`
   - `screens/SongDetailsScreen.tsx` (use the server-app version with `useCallback`, but keep threshold at `1`)
   - `navigation/routes.ts`
   - `navigation/SongsNavigator.tsx`
   - `navigation/StatisticsNavigator.tsx`
   - `navigation/SuggestionsNavigator.tsx`
   - `navigation/useOptionalBottomTabBarHeight.ts`
   - `navigation/windowsFlyoutUi.tsx`
4. Delete corresponding files from both `packages/local-app/` and `packages/server-app/`
5. Update `local-app/src/navigation/AppNavigator.tsx` to import from `@festival/app-screens`
6. Update `server-app/src/navigation/AppNavigator.tsx` to import from `@festival/app-screens`
7. Create `packages/app-screens/src/index.ts` exporting all screens and navigation
8. Add `@festival/app-screens` to workspace `package.json`

**Files removed:** 14 files from `local-app`, 16 files from `server-app` → replaced by 1 shared copy each.

**Risk:** Medium — import paths change significantly; all imports in AppNavigator files must be updated.

---

### WI-2: Unify `AppNavigator` variants

**Priority:** P1 — Works in tandem with WI-1.

**What to do:**

The two `AppNavigator.tsx` files differ only in:
1. **Flyout header** — server-app has a "Menu" header with `×` close button; local-app doesn't
2. **Colors** — minor differences in flyout background, border colors
3. **`winRoot` background** — `transparent` vs `#1A0830`

**Approach:** Extract a shared `BaseAppNavigator` into `@festival/app-screens` that accepts a configuration prop or uses a context to decide flyout style. Each app's `AppNavigator.tsx` becomes a thin wrapper:

```tsx
// local-app/src/navigation/AppNavigator.tsx
import {BaseAppNavigator} from '@festival/app-screens';
export function AppNavigator() {
  return <BaseAppNavigator flyoutVariant="local" />;
}
```

**Alternative:** Keep two separate `AppNavigator.tsx` files (one per app) that import everything else from `@festival/app-screens`. This is simpler and avoids over-abstracting. The files would shrink from ~475 lines to ~475 lines but with shared imports.

**Recommendation:** Use the alternative — keep two small AppNavigator files. The shared `MobileTabs`, `IOSNativeTabs`, boot overlay, and nav theme can be extracted into `@festival/app-screens/navigation/AppShell.tsx`, leaving only the `WindowsFlyout` component as the variant.

---

### WI-3: Create `FadeScrollView` component

**Priority:** P1

**What to do:**
1. Create `packages/ui/src/FadeScrollView.tsx`:
   ```tsx
   // Wraps children in MaskedView with top/bottom fade gradients.
   // Props: style, children, gradientHeight (default 32)
   export function FadeScrollView(props: {
     style?: StyleProp<ViewStyle>;
     gradientHeight?: number;
     children: React.ReactNode;
   }) { ... }
   ```
2. Export from `packages/ui/src/index.ts`
3. Replace the `MaskedView` + `LinearGradient` boilerplate in:
   - `SongsScreen.tsx` (~10 lines of JSX + 4 styles)
   - `StatisticsScreen.tsx` (~10 lines of JSX + 4 styles)
   - `SuggestionsScreen.tsx` (~10 lines of JSX + 4 styles)
   - `SettingsScreen.tsx` (~10 lines of JSX + 4 styles)
   - `SongDetailsScreen.tsx` (uses similar pattern)
4. Remove the 4 now-unused styles from each screen's `StyleSheet.create`:
   - `fadeScrollContainer`, `fadeMaskContainer`, `fadeMaskOpaque`, `fadeGradient`

**Lines saved:** ~40 JSX lines + ~20 style lines across 5 screens.

---

### WI-4: Extract shared theme/colors

**Priority:** P1

**What to do:**
1. Create `packages/ui/src/theme/colors.ts`:
   ```typescript
   export const colors = {
     // Backgrounds
     primaryDark: '#1A0830',
     cardBg: 'rgba(18,24,38,0.78)',
     darkBg: '#0B1A2E',
     inputBg: '#0B1220',
     surfaceBg: '#111827',

     // Borders
     border: '#263244',
     borderLight: '#2B3B55',
     borderNav: '#1E2A3A',

     // Text
     textPrimary: '#FFFFFF',
     textSecondary: '#D7DEE8',
     textMuted: '#9AA6B2',
     textDisabled: '#607089',
     textHint: '#8899AA',

     // Accents
     gold: '#FFD700',
     goldBg: '#332915',
     pillBg: '#1D3A71',
     primaryBlue: 'rgba(45,130,230,1)',
     primaryBlueBg: 'rgba(45,130,230,0.4)',
     primaryBlueLight: 'rgba(45,130,230,0.18)',
     purpleBg: 'rgba(124,58,237,0.4)',
     destructiveBg: 'rgba(198,40,40,0.4)',

     // Transparent
     transparent: 'transparent',
     scrimBg: 'rgba(0,0,0,0.35)',
   } as const;
   ```
2. Create `packages/ui/src/theme/spacing.ts`:
   ```typescript
   export const spacing = {
     screenPaddingHorizontal: 20,
     screenPaddingTop: 16,
     screenPaddingBottom: 4,
     cardPadding: 12,
     cardBorderRadius: 12,
     cardGap: 8,
     listSeparator: 10,
     fadeGradientHeight: 32,
   } as const;
   ```
3. Gradually update files to use `colors.xxx` instead of hardcoded strings.
   - **Do NOT do a global find-replace** — replace incrementally as you touch each file.
   - Start with the files being refactored by other WIs.

**Risk:** Low — purely additive. Add constants first and migrate consumers file-by-file.

---

### WI-5: Extract shared style modules

**Priority:** P1

**What to do:**

Create the following shared style modules in `packages/ui/src/styles/`:

#### `pillStyles.ts`
Extract from `SongRow.tsx`, `SuggestionSongRow.tsx`, `StatisticsScreen.tsx`:
- `percentilePill`, `percentilePillGold`, `percentilePillText`, `percentilePillTextGold`
- `percentPill`, `percentPillGold`, `percentPillText`, `percentPillTextGold`
- `seasonPill`, `seasonPillText`
- `diffPill`, `diffPillText`
- `fcBadge`, `fcBadgeText`

#### `songRowStyles.ts`
Extract from `SongRow.tsx`, `SuggestionSongRow.tsx`, `StatisticsScreen.tsx` (`TopSongRow`):
- `thumbWrap` / `thumb` / `thumbPlaceholder` (44×44, borderRadius 10)
- `songTitle` / `songMeta` (font styles)
- `songRowPressable` / `songRowInnerPressed` (opacity 0.85)
- `songLeft` / `songRight` layout

#### `cardStyles.ts`
Extract from `SuggestionCard.tsx`, `StatisticsScreen.tsx` (`TopSongsCard`), `StatisticsInstrumentCard.tsx`:
- `card` (borderRadius 12, padding 12, gap 8, maxWidth 1080)
- `cardHeaderRow`, `cardHeaderLeft`, `cardHeaderRight`
- `cardTitle`, `cardSubtitle`
- `cardHeaderIcon`

#### `gridStyles.ts`
Extract from `StatisticsScreen.tsx`, `SuggestionsScreen.tsx`:
- `cardGrid`, `cardGridColumnLeft`, `cardGridColumnRight`
- `masonryContainer`, `masonryColumnLeft`, `masonryColumnRight`
- `sectionHeader`, `sectionHeaderTitle`, `sectionHeaderDescription`

#### `buttonStyles.ts`
Extract from `SettingsScreen.tsx`:
- `button`, `buttonSecondary`, `buttonPurple`, `buttonDestructive`
- `buttonPressed`, `buttonDisabled`, `buttonText`

**After creating each module:**
1. Export from `packages/ui/src/index.ts`
2. Update consuming files to `import {pillStyles} from '@festival/ui'`
3. Replace local style references: `styles.percentilePill` → `pillStyles.percentilePill`
4. Remove the now-unused definitions from each file's local `StyleSheet.create`

---

### WI-6: Create `BaseSongRow` and unify song rows

**Priority:** P2

**What to do:**
1. Create `packages/ui/src/songs/BaseSongRow.tsx`:
   ```tsx
   export interface BaseSongRowProps {
     title: string;
     artist: string;
     year?: number;
     imageUri?: string;
     hideArt?: boolean;
     useMarquee?: boolean;        // true = MarqueeText, false = Text numberOfLines={1}
     onPress: () => void;
     rightContent?: React.ReactNode;
     bottomContent?: React.ReactNode;
     accessibilityLabel?: string;
   }

   export const BaseSongRow = React.memo(function BaseSongRow(props: BaseSongRowProps) {
     // Renders: Pressable > [thumbnail] [title/artist] [rightContent]
     //                       [bottomContent]
   });
   ```
2. Refactor `SongRow.tsx` to use `BaseSongRow` internally, passing instrument chips and metadata pills as `rightContent`/`bottomContent`
3. Refactor `SuggestionSongRow.tsx` to use `BaseSongRow`, passing suggestion-specific metrics as `rightContent`
4. Extract `TopSongRow` from `StatisticsScreen.tsx` into `packages/ui/src/songs/TopSongRow.tsx`, using `BaseSongRow`

**Key decisions:**
- `BaseSongRow` handles: pressable wrapper, thumbnail display, title/artist rendering, pressed opacity
- Child components handle: their specific right-side content via render prop or `rightContent` prop
- `useMarquee` defaults to `true` for `SongRow` and `SuggestionSongRow`, `false` for `TopSongRow`

**Risk:** Medium — `SongRow` is 845 lines with complex metadata rendering. Take care not to break the metadata pill layout. Test on all platforms.

---

### WI-7: Create generic `CategoryCard`

**Priority:** P2

**What to do:**
1. Create `packages/ui/src/cards/CategoryCard.tsx`:
   ```tsx
   export interface CategoryCardProps {
     title: string;
     description: string;
     instrumentKey?: InstrumentKey;
     children: React.ReactNode;     // the song list content
     style?: StyleProp<ViewStyle>;
     virtualizeThreshold?: number;   // default 12
   }

   export function CategoryCard(props: CategoryCardProps) {
     // FrostedSurface > headerRow (title + desc + icon) > children
   }
   ```
2. Refactor `SuggestionCard.tsx` to use `CategoryCard` as its base, passing `SuggestionSongRow` items as children
3. Replace `TopSongsCard` in `StatisticsScreen.tsx` with `CategoryCard` + `TopSongRow` items
4. Move `SuggestionCard.tsx` to `packages/ui/src/cards/SuggestionCard.tsx` or keep in `suggestions/`

**Lines saved:** ~80 lines of duplicated card layout from `StatisticsScreen.tsx`.

---

### WI-8: Extract `ToggleRow` / `ChoiceButton` controls

**Priority:** P2

**What to do:**
1. Create `packages/ui/src/controls/ToggleRow.tsx`:
   ```tsx
   export interface ToggleRowProps {
     label: string;
     icon?: ImageSourcePropType;
     description?: string;
     checked: boolean;
     onToggle: () => void;
     disabled?: boolean;
     first?: boolean;
     last?: boolean;
   }
   ```
   This unifies `ToggleRow` and `DescriptorToggleRow` from `SettingsScreen.tsx`.

2. Create `packages/ui/src/controls/ChoiceButton.tsx`:
   ```tsx
   export interface ChoiceButtonProps {
     label: string;
     selected: boolean;
     onPress: () => void;
   }
   ```
   Replaces the `Choice` component in `SettingsScreen.tsx`.

3. Create `packages/ui/src/controls/InstrumentToggleRow.tsx`:
   Extracts the instrument icon toggle row from `FilterModal.tsx` and `SuggestionsFilterModal.tsx`.

4. Export all from `packages/ui/src/index.ts`
5. Update `SettingsScreen.tsx`, `FilterModal.tsx`, `SuggestionsFilterModal.tsx` to use the shared components.

---

### WI-9: Create `WindowsHostScreen` generic

**Priority:** P2

**What to do:**
1. Create `packages/app-screens/src/navigation/WindowsHostScreen.tsx`:
   ```tsx
   export function WindowsHostScreen<P extends {}>(props: {
     ListComponent: React.ComponentType<P & {onOpenSong: (id: string, title: string) => void}>;
     listProps?: Omit<P, 'onOpenSong'>;
   }) {
     const [songId, setSongId] = useState<string | null>(null);
     const {setChromeHidden} = useWindowsFlyoutUi();
     // ... same pattern as current WindowsXxxHost files
   }
   ```
2. Delete `WindowsSongsHost.tsx`, `WindowsStatisticsHost.tsx`, `WindowsSuggestionsHost.tsx`
3. Update `AppNavigator.tsx` to use `WindowsHostScreen` with the appropriate list component:
   ```tsx
   const SCREEN_COMPONENTS = {
     [Routes.Songs]: () => <WindowsHostScreen ListComponent={SongsScreen} />,
     // ...
   };
   ```

**Lines saved:** ~42 lines (3 × 21 → 1 × 21).

---

### WI-10: Create `createSubNavigator()` factory

**Priority:** P2

**What to do:**
1. Create `packages/app-screens/src/navigation/createSubNavigator.tsx`:
   ```tsx
   export function createSubNavigator(
     listScreenName: string,
     ListScreen: React.ComponentType<{onOpenSong: (id: string, title: string) => void}>,
   ) {
     return function SubNavigator() {
       // Stack.Navigator with BackChevron, list screen, SongDetails screen
       // Same options as current navigators
     };
   }
   ```
2. Replace `SongsNavigator.tsx`, `StatisticsNavigator.tsx`, `SuggestionsNavigator.tsx` with:
   ```tsx
   export const SongsNavigator = createSubNavigator('SongsList', SongsScreen);
   export const StatisticsNavigator = createSubNavigator('StatisticsList', StatisticsScreen);
   export const SuggestionsNavigator = createSubNavigator('SuggestionsList', SuggestionsScreen);
   ```
3. Delete the 3 individual navigator files.

**Lines saved:** ~175 lines (3 × 85 → ~85 factory + 3 × 1-line usages).

---

### WI-11: Merge platform-split files

**Priority:** P2

#### 11a: `InstrumentCard.tsx` + `InstrumentCard.windows.tsx`

1. Extract `DifficultyBars` into its own file with platform resolution:
   - `packages/ui/src/instruments/DifficultyBars.tsx` — SVG `Polygon` version
   - `packages/ui/src/instruments/DifficultyBars.windows.tsx` — `View` + `skewX` version
2. Import `DifficultyBars` in the single `InstrumentCard.tsx` — Metro/RN will resolve the platform-specific file automatically
3. Delete `InstrumentCard.windows.tsx`

**Note:** `DifficultyBars` is also imported by `SongRow.tsx`, so it already needs to be a standalone export.

#### 11b: `Accordion.tsx` + `Accordion.windows.tsx`

1. Extract the animation hook into:
   - `packages/ui/src/useAccordionAnimation.ts` — uses `react-native-reanimated`
   - `packages/ui/src/useAccordionAnimation.windows.ts` — uses RN `Animated`
2. Keep one `Accordion.tsx` that calls `useAccordionAnimation()`
3. Delete `Accordion.windows.tsx`

#### 11c: `FrostedSurface.tsx` + `FrostedSurface.windows.tsx`

1. Merge into a single `FrostedSurface.tsx` using `Platform.select`:
   ```tsx
   const DEFAULT_FALLBACK = Platform.select({
     windows: 'rgba(18,24,38,0.97)',
     default: 'rgba(18,24,38,0.78)',
   });
   ```
2. Delete `FrostedSurface.windows.tsx`

---

### WI-12: Extract shared utility functions

**Priority:** P3

**What to do:**
1. Move `shouldShowCategory` to `@festival/core` (or `@festival/app-screens/utils/instrumentFilters.ts`):
   ```typescript
   export function shouldShowCategory(
     categoryKey: string,
     settings: {showLead: boolean; showDrums: boolean; showVocals: boolean; showBass: boolean; showProLead: boolean; showProBass: boolean},
   ): boolean { ... }
   ```
2. Move `isInstrumentEnabled` / `isInstrumentKeyVisible` to the same file.
3. Move `filterCategoryForInstruments` and `filterCategoryForInstrumentTypes` alongside them.
4. Update `StatisticsScreen.tsx` and `SuggestionsScreen.tsx` to import from the shared location.

---

### WI-13: `SettingsScreen` — import `modalStyles` instead of re-defining

**Priority:** P3

**What to do:**
1. In `SettingsScreen.tsx`, add `import {modalStyles} from '@festival/ui'`
2. Replace references to locally-defined duplicate styles with `modalStyles.xxx`:
   - `styles.orderRow` → `modalStyles.orderRow` (where they match)
   - `styles.choice` → `modalStyles.choice`
   - etc.
3. Remove the duplicate definitions from the local `StyleSheet.create`
4. Keep any Settings-specific styles that don't exist in `modalStyles`

**Note:** Some Settings styles have slight additions (e.g., `toggleLabelRow`, `toggleInstrumentIcon`) that aren't in `modalStyles`. Those stay local.

---

## 5. Validation Checklist

After completing each WI, verify:

- [ ] **TypeScript compiles cleanly** — `yarn tsc --noEmit` in the workspace root
- [ ] **Metro bundler starts** — `yarn start --reset-cache`
- [ ] **iOS builds and runs** — test on simulator
- [ ] **Android builds and runs** — test on emulator
- [ ] **Windows builds and runs** — `yarn windows`
- [ ] **All screens render correctly** — manually navigate through Songs, Suggestions, Statistics, Settings, SongDetails, Sync
- [ ] **All modals work** — Sort, Filter, SuggestionsFilter
- [ ] **Landscape/card-grid layout works** — on wide screens and tablets
- [ ] **Windows flyout navigation works** — hamburger menu opens/closes, routes switch
- [ ] **Existing tests pass** — `yarn test` (Jest tests in `__tests__/`)

---

## 6. Risk Notes

1. **WI-1 is the highest-value but highest-risk change.** Moving 14 files into a new package means updating every `import` path. Do this first and validate thoroughly before proceeding.

2. **WI-6 (BaseSongRow) has the most complexity.** `SongRow.tsx` is 845 lines with intricate metadata rendering, configurable display order, and multiple conditional pill types. Refactoring it to wrap a `BaseSongRow` requires careful testing of every metadata combination.

3. **WI-11a (InstrumentCard merge)** — `DifficultyBars` is exported from `InstrumentCard.tsx` and imported by `SongRow.tsx`. When extracting it, ensure the re-export in `@festival/ui/index.ts` still works.

4. **WI-4 (colors)** should be done incrementally — don't attempt a global find-replace. Add the constants file and migrate one file at a time, starting with files you're already modifying for other WIs.

5. **Yarn workspace resolution** — when adding `@festival/app-screens`, ensure the root `package.json` workspaces array includes it, and that Metro's `watchFolders` config picks it up. Check `metro.config.js` for any package-specific configuration.

6. **Platform file resolution** — when creating `DifficultyBars.tsx` / `.windows.tsx`, Metro resolves platform files automatically. But ensure the `index.ts` barrel export references the base filename (without platform suffix).

7. **Test the Windows build after every structural change** — the Windows build toolchain (`react-native-windows`) is the most brittle and likely to break on path changes.
