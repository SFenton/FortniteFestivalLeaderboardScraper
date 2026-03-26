import { useMemo, type CSSProperties } from 'react';

/**
 * Memoized style factory hook.
 *
 * Replaces CSS Modules with JS-based styling backed by @festival/theme constants.
 * The factory runs once per component instance and is memoized via useMemo.
 *
 * @example
 * ```tsx
 * const styles = useStyles(() => ({
 *   card: { backgroundColor: Colors.surfaceFrosted, borderRadius: Radius.md, padding: Gap.section },
 *   title: { fontSize: Font.title, fontWeight: 700, color: Colors.textPrimary },
 * }));
 *
 * return <div style={styles.card}><h2 style={styles.title}>...</h2></div>;
 * ```
 *
 * For dynamic styles that depend on props/state, pass them as deps:
 * ```tsx
 * const styles = useStyles(() => ({
 *   row: { opacity: disabled ? 0.5 : 1 },
 * }), [disabled]);
 * ```
 */
export function useStyles<T extends Record<string, CSSProperties>>(
  factory: () => T,
  deps: readonly unknown[] = [],
): T {
  // eslint-disable-next-line react-hooks/exhaustive-deps -- deps are the caller's responsibility
  return useMemo(factory, deps);
}
