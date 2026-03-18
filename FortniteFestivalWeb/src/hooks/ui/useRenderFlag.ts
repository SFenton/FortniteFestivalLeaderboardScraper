import { useRef } from 'react';

/**
 * Module-level render tracking for skip-animation decisions.
 *
 * Replaces the repeated pattern:
 *   let _hasRendered = false;
 *   function Page() {
 *     const skipAnimRef = useRef(_hasRendered);
 *     const skipAnim = skipAnimRef.current;
 *     _hasRendered = true;
 *   }
 *
 * Usage:
 *   const rendered = createRenderFlag();
 *   function Page() {
 *     const skipAnim = rendered();
 *   }
 */
export function createRenderFlag(): () => boolean {
  let hasRendered = false;
  return function useRenderFlag(): boolean {
    // eslint-disable-next-line react-hooks/rules-of-hooks
    const ref = useRef(hasRendered);
    hasRendered = true;
    return ref.current;
  };
}
