# CSS Module Migration Rules

Hard rules for converting every `.module.css` file to the `useStyles` pattern.
Reference this file on every migration pass.

---

## 1. File Structure

- **Imports** at top
- **Component logic + render** — the main thing you see when opening the file
- `export default`
- `function useStyles(...)` at the **bottom** — hoisted, reads like the old CSS module

## 2. No Magic Numbers

- Every pixel value, gap, border width, font size, weight, color, z-index, opacity, radius, letter-spacing, line-height, and duration **must** come from `@festival/theme`
- `flex: 1` and `flexShrink: 0` are **exceptions** — hardcoded 0/1 is idiomatic for flex shorthand properties
- If the constant doesn't exist yet, **add it** to the appropriate theme struct before migrating
- If a value doesn't fit an existing struct, that's a signal the struct needs splitting
- `0` is acceptable for "reset/none" values, but prefer named zeros when available: `Gap.none`, `Opacity.none`, `LineHeight.none`

## 3. No String Enums

- Size variants, states, modes → use `const enum` with numeric values (like `SpinnerSize`)
- Zero string comparisons in runtime code

## 4. No Raw Inline Styles

- Never `style={{ ... }}` with object literals in JSX
- Always `style={s.thing}` where `s` comes from `useStyles()`
- Dynamic values (like a computed color) are passed as parameters to `useStyles(param)` and included in the `useMemo` deps

## 5. Theme Structs are Single-Responsibility

- Don't dump unrelated values into one struct
- Icons → `IconSize`, stars → `StarSize`, instruments → `InstrumentSize`, etc.
- When a struct gets overloaded, split it before continuing

## 6. Backward Compatibility via Deprecated Shims

- When splitting a struct, keep the old one as a `@deprecated` re-export shim
- Existing callers aren't broken; new/migrated code uses the specific struct
- Shim gets deleted when all callers are migrated

## 7. Update All Callers

- When a prop type changes (e.g. string → enum), update **every** call site in the same pass
- Include test files — they're callers too

## 8. CSS `composes:` → Theme Factory Spreads

- `composes: frostedCard from '...'` → `...frostedCard` from `@festival/theme`
- `composes: flexCol from '...'` → `...flexColumn` from factory exports
- `composes: goldOutline from '...'` → `...goldOutline` from gold styles

## 9. CSS Variables → Direct Constants

- `var(--color-text-primary)` → `Colors.textPrimary`
- `var(--gap-md)` → `Gap.md`
- `var(--radius-xs)` → `Radius.xs`
- No CSS custom properties in useStyles — everything is a direct JS value

## 10. Keyframe Animations Stay Global

- `@keyframes spin`, `fadeInUp`, etc. remain in `keyframes.css` (imported globally)
- Referenced by name string in useStyles: `animation: 'spin 0.8s linear infinite'`
- Animation timing constants come from theme: `Spinner.duration`, `FADE_DURATION`

## 11. CSS Keywords → Enum Constants

- Never write raw CSS keyword strings (`'inline-block'`, `'center'`, `'border-box'`, `'solid'`, etc.)
- Use enums from `cssEnums.ts`:
  - `Display.inlineBlock`, `Display.flex`, `Display.none`
  - `TextAlign.center`, `TextAlign.right`
  - `BoxSizing.borderBox`
  - `BorderStyle.solid`, `BorderStyle.none`
  - `Align.center`, `Justify.between`, `Justify.end`
  - `FontStyle.italic`, `FontStyle.normal`
  - `TextTransform.uppercase`
  - `Overflow.hidden`, `Overflow.auto`, `Overflow.visible`
  - `ObjectFit.contain`, `ObjectFit.cover`
  - `Cursor.pointer`, `Cursor.text`
  - `PointerEvents.none`
  - `Position.relative`, `Position.absolute`, `Position.fixed`, `Position.sticky`
  - `FontVariant.tabularNums` (for `'tabular-nums'`), `FontVariant.normal`
  - `WordBreak.breakWord`, `WordBreak.breakAll`, `WordBreak.normal`
  - `CssValue.transparent`, `CssValue.none`, `CssValue.circle` (for `50%`), `CssValue.full` (for `100%`), `CssValue.marginCenter` (for `'0 auto'`), `CssValue.viewportFull` (for `'100vh'`)
  - `CssProp.opacity`, `CssProp.color`, `CssProp.transform`, `CssProp.backgroundColor`, `CssProp.borderColor`, `CssProp.boxShadow` — CSS property name strings for `transition()` calls
  - `GridTemplate.single` (for `1fr`), `GridTemplate.twoEqual`, `GridTemplate.threeEqual`
- This eliminates **all** `as const` casts on string literals — they are never needed

## 12. CSS Value Construction → Helper Functions

- `border: \`${Border.thick}px solid ${Colors.goldStroke}\`` → `border(Border.thick, Colors.goldStroke)`
- `padding: \`${Gap.xs}px ${Gap.sm}px\`` → `padding(Gap.xs, Gap.sm)`
- `margin: \`${Gap.md}px 0\`` → `margin(Gap.md, 0)`
- `transition: \`opacity ${TRANSITION_MS}ms ease\`` → `transition(CssProp.opacity, TRANSITION_MS)`
- Multiple transitions → `transitions(transition(CssProp.backgroundColor, FAST_FADE_MS), transition(CssProp.transform, FAST_FADE_MS))`
- `transform: 'scale(1.25)'` → `scale(MetadataSize.dotActiveScale)` — use `scale()` helper from `cssHelpers.ts`
- Never construct CSS value strings with template literals — use `border()`, `padding()`, `margin()`, `transition()`, `transitions()`, `scale()` from `cssHelpers.ts`
- **Critical**: even `\`${Gap.xs}px ${Gap.sm}px\`` is a template literal — use `padding(Gap.xs, Gap.sm)` instead

## 13. Reuse Existing Style Mixins

- Gold badge → `...goldOutline` or `...goldOutlineSkew` (not rebuilding the 8 properties manually)
- Frosted card → `...frostedCard` (not copying the background/border/shadow properties)
- Flex patterns → `...flexCenter`, `...flexColumn`, `...flexRow`, `...flexBetween`
- Fixed overlay → `...fixedFill`, `...absoluteFill`
- Vertical centering → `...centerVertical` (`top: '50%', transform: 'translateY(-50%)'`)
- Check `goldStyles.ts`, `frostedStyles.ts`, `factories.ts` before writing new style objects

## 14. Per-Size/Variant Config Lives Inside useStyles

- Don't create module-level `Record<SomeEnum, ...>` maps for size/variant configs
- Put the config object **inside** `useStyles(size)` — the hook receives the variant and returns the right styles
- This keeps all style logic in one place at the bottom of the file

## 15. Pseudo-Elements and Keyframes Stay in Minimal CSS

- `::before`, `::after`, `::placeholder` — cannot be expressed as inline styles
- Keep a **minimal** `.module.css` file containing ONLY the pseudo-element rules and component-specific `@keyframes`
- Move ALL other properties from that CSS class into `useStyles()`
- The component uses both: `className={css.thing}` for the pseudo anchor + `style={s.thing}` for all real styles
- Comment the CSS file: `/* Minimal CSS for pseudo-element that can't be inline styles. */`

## 16. Tests Assert Behavior, Not Class Names

- After migration, elements have no CSS class names — only inline styles
- Replace `container.querySelector('[class*="foo"]')` with:
  - `container.querySelector('div')` (by element type/position)
  - `element.style.backgroundColor` (by style property)
  - `screen.getByText(...)` or `screen.getByLabelText(...)` (by content/accessibility)
- For color comparisons, jsdom may convert hex to rgb — use `.toBeTruthy()` for existence checks or compare against the theme constant directly

## 17. All Durations and Timing Come From Theme

- Stagger delays → `STAGGER_INTERVAL`, `STAGGER_ENTRY_OFFSET`
- Fade durations → `FADE_DURATION`, `QUICK_FADE_MS`, `FAST_FADE_MS`
- Spinner → `Spinner.duration`, `SPINNER_FADE_MS`
- Transitions → `TRANSITION_MS`
- Debounce → `DEBOUNCE_MS`, `RESIZE_DEBOUNCE_MS`
- Never write a bare `80`, `125`, `150`, `200`, `250`, `300`, `400`, `500` as a timing value — find or add the constant in `animation.ts`

## 18. Grid Templates Are Enums

- `'1fr'` → `GridTemplate.single`
- `'1fr 1fr'` → `GridTemplate.twoEqual`
- For dynamic `repeat()` patterns (e.g. `repeat(auto-fill, minmax(...))`), build with a helper or keep as a computed string in `useStyles` — but name the parameters

## 19. Zero Values Use Named Constants

- `marginBottom: 0` → `Gap.none`
- `opacity: 0` → `Opacity.none`
- `lineHeight: 0` → `LineHeight.none`
- `padding: 0` → `Gap.none`
- `top: 0` → `Gap.none` (position offset reset)
- `margin: 0` → `Gap.none`
- **Exception**: `flex: 1`, `flexShrink: 0`, `flexGrow: 0` — bare 0/1 is idiomatic for flex, no constant needed
- Prefer named zeros for clarity: the reader knows `Gap.none` means "intentionally no gap" vs a random `0`

## 20. Transition Property Names Are Enums

- Never write `transition('opacity', ...)` — use `transition(CssProp.opacity, ...)`
- Available: `CssProp.opacity`, `.color`, `.transform`, `.backgroundColor`, `.borderColor`, `.boxShadow`, `.width`, `.height`, `.all`
- For multiple transitions: `transitions(transition(CssProp.backgroundColor, ms), transition(CssProp.color, ms))`

## 21. CSS Compound Values Use Helpers

- `margin: '0 auto'` → `CssValue.marginCenter`
- `transform: 'scale(1)'` → `scale(1)` from `cssHelpers.ts`
- `transform: 'scale(0)'` → `scale(0)`
- `transform: \`scale(${DOT_ACTIVE_SCALE})\`` → `scale(MetadataSize.dotActiveScale)` — scale factors belong in theme
- Never define local constants for values that belong in theme (e.g. `const DOT_ACTIVE_SCALE = 1.25` → `MetadataSize.dotActiveScale`)

## 22. Viewport and Layout Magic Values Belong in Theme

- `'100vh'` → `CssValue.viewportFull`
- `'40vh'` → `Layout.pageMessageMinHeight` — named for its purpose
- `'60vh'` → `Layout.errorFallbackMinHeight`
- `calc(100vh - 200px)` → `calc(100vh - ${Layout.shellChromeHeight}px)` — comment explains the subtraction (header + nav + padding)
- Viewport-relative strings that appear in more than one file → add to `Layout` with a descriptive name
- Computed viewport strings (`calc(...)`) stay in `useStyles` but reference named constants for the magic part

## 23. Every `useStyles` Hook, Every File

- **All** migrated components must have a `function useStyles(...)` at the bottom — even if the styles are static
- Class components (like ErrorBoundary) use a module-level `const styles: Record<string, CSSProperties>` instead, since hooks aren't available
- No module-level `const hiddenStyle = {...}` — put it inside `useStyles` or the static `styles` object
- This is a consistency rule: when you open any migrated file, you always find `useStyles` at the bottom

## 24. All User-Visible Strings Must Be Translated

- Never hardcode English text in JSX: `"Something went wrong"` → `t('common.error')`
- Error boundaries (class components) use `i18next.t()` directly since `useTranslation()` hook isn't available
- Functional components use `const { t } = useTranslation()` from `react-i18next`
- New translation keys go in `src/i18n/en.json` under the appropriate section (`common`, `error`, etc.)
- If a string exists in en.json already, reuse it: `common.error`, `common.reload`, `common.cancel`, etc.

## 25. Font Weights Are Theme Constants

- `fontWeight: 400` → `Weight.normal`
- `fontWeight: 600` → `Weight.semibold`
- `fontWeight: 700` → `Weight.bold`
- `fontWeight: 800` → `Weight.heavy`
- Never write a bare number for font-weight

## 26. Shadows Are Theme Constants

- `boxShadow: '0 4px 12px rgba(0, 0, 0, 0.4)'` → `Shadow.tooltip`
- `boxShadow: '0 8px 24px rgba(0, 0, 0, 0.5)'` → `Shadow.elevated`
- Frosted card shadow is inside `frostedCard` mixin — don't duplicate
- New shadows belong in `Shadow` struct in `spacing.ts`

## 27. Common Centering Patterns Are Factories

- `top: '50%', transform: 'translateY(-50%)'` → `...centerVertical` from `factories.ts`
- `position: absolute, inset: 0` → `...absoluteFill`
- `position: fixed, inset: 0` → `...fixedFill`
- `display: flex, alignItems: center, justifyContent: center` → `...flexCenter`
- When a 2+ property pattern appears in multiple files, extract a factory

## 28. No `as const` Casts in useStyles

- Style objects use `as CSSProperties`, never `as const`
- `as const` hides missing enum usage — e.g. `position: 'relative' as const` should be `position: Position.relative`
- If TypeScript needs a type assertion, use `as CSSProperties` from React
- If you find yourself writing `as const` on a string value, that value needs an enum constant

## 29. Transform Values Use Helper Functions

- `'scale(1)'` → `scale(1)` from `cssHelpers.ts`
- `'scale(0.95)'` → `scale(MODAL_SCALE_ENTER)` — scale factors are animation constants
- `'scale(0.9)'` → `scale(PILL_SCALE_HIDDEN)`
- `'translateY(10px)'` → `translateY(MODAL_SLIDE_OFFSET)`
- `'scale(0.95) translateY(10px)'` → `scaleTranslateY(MODAL_SCALE_ENTER, MODAL_SLIDE_OFFSET)`
- `'scale(1) translateY(0)'` → `scaleTranslateY(1, 0)`
- Named constants: `MODAL_SCALE_ENTER` (0.95), `PILL_SCALE_HIDDEN` (0.9), `MODAL_SLIDE_OFFSET` (10px)

## 30. Easing Curves Are Named Constants

- `'cubic-bezier(0.4, 0, 0.2, 1)'` → `EASE_SMOOTH` from `animation.ts`
- `'ease'` is the default for `transition()` — no constant needed
- `'ease-out'` / `'ease-in'` / `'linear'` are standard CSS and don't need constants
- Custom cubic-bezier curves → add a named constant in `animation.ts`

## 31. WhiteSpace and Isolation Are Enums

- `whiteSpace: 'nowrap' as const` → `WhiteSpace.nowrap`
- `isolation: 'isolate' as const` → `Isolation.isolate`
- These are CSS keywords that must use the enum pattern (rule 11)

## 32. Modal/Popup Shared Factories

- `composes: modalOverlay` → `...modalOverlay` — fixed fullscreen dark scrim
- `composes: modalCard` → `...modalCard` — frosted glass dialog body with blur
- `composes: btnPrimary` → `...btnPrimary` — blue chip action button
- `composes: btnDanger` → `...btnDanger` — red danger action button
- `composes: purpleGlass` → `...purpleGlass` — purple branded glass surface
- All from `frostedStyles.ts` via `@festival/theme`

## 33. Layout Magic Numbers Get Descriptive Names

- `maxWidth: 340` → `Layout.confirmMaxWidth`
- `maxWidth: 520` → `Layout.changelogMaxWidth`
- `maxHeight: '80vh'` → `Layout.changelogMaxHeight`
- `maxHeight: 240` → `Layout.dropdownMaxHeight`
- `'calc(100% + 4px)'` → `calc(100% + ${Layout.dropdownGap}px)`
- `width/height: 32` (close button) → `Layout.closeBtnSize`
- Every layout-specific magic number gets a descriptive name in `Layout`

## 34. Phase Comparisons Use Enums

- `phase === 'contentIn'` → `phase === LoadPhase.ContentIn` from `@festival/core`
- `phase === 'spinnerOut'` → `phase === LoadPhase.SpinnerOut`
- `loadPhase === 'loading'` → `loadPhase === LoadPhase.Loading`
- Never compare load/sync phases as raw strings — the enum exists for type safety

## 35. Transition Durations Are Named Constants

- `transition(CssProp.color, 150)` → `transition(CssProp.color, NAV_TRANSITION_MS)` — hover/nav state
- `transition(CssProp.all, 150)` → `transition(CssProp.all, NAV_TRANSITION_MS)`
- `transition(CssProp.backgroundColor, 250)` → `transition(CssProp.backgroundColor, LINK_TRANSITION_MS)` — sidebar links
- `transition(CssProp.gridTemplateRows, 200)` → `transition(CssProp.gridTemplateRows, FAST_FADE_MS)` — accordion
- Available: `NAV_TRANSITION_MS` (150), `FAST_FADE_MS` (200), `LINK_TRANSITION_MS` (250), `TRANSITION_MS` (300), `FADE_DURATION` (400), `SPINNER_FADE_MS` (500)
- **Never write a bare number as a transition duration**

## 36. Stagger Offsets Use Named Constants or Arithmetic

- `450, 300, 150, 0` → `MODAL_STAGGER_MS * 3, * 2, * 1, 0`
- `baseDelay + 80 + i * 60` → `baseDelay + STAGGER_ENTRY_OFFSET + i * STAGGER_ROW_MS`
- Available: `STAGGER_INTERVAL` (125), `STAGGER_ENTRY_OFFSET` (80), `STAGGER_ROW_MS` (60), `MODAL_STAGGER_MS` (150)

## 37. Inline fadeOut/fadeIn Animations Use useStyles

- `style={phase === 'spinnerOut' ? { animation: 'fadeOut 500ms ...' } : undefined}` → move to `useStyles(isSpinnerOut)`
- Animation string uses `SPINNER_FADE_MS` or `FADE_DURATION` for timing
- The `useStyles` hook receives the boolean and conditionally includes the animation property
