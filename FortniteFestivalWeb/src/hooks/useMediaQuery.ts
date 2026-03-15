import { useSyncExternalStore } from 'react';

/**
 * Subscribe to a CSS media query and re-render only when the boolean result
 * changes.  Built on `useSyncExternalStore` for tear-free reads.
 *
 * @example
 *   const isWide = useMediaQuery('(min-width: 768px)');
 */
export function useMediaQuery(query: string): boolean {
  return useSyncExternalStore(
    (callback) => {
      const mql = window.matchMedia(query);
      mql.addEventListener('change', callback);
      return () => mql.removeEventListener('change', callback);
    },
    () => window.matchMedia(query).matches,
    () => false,
  );
}
