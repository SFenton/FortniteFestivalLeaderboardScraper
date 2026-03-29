import { useEffect, useRef, type RefObject } from 'react';

const DEFAULT_RADIUS = 250;

/** Attribute selector matching elements with the --frosted-card CSS custom property marker. */
const FROSTED_SELECTOR = '[style*="--frosted-card"]';

/**
 * Proximity-based glow for frosted cards.
 *
 * Attaches a `mousemove` listener to the container element and updates
 * CSS custom properties (`--glow-x`, `--glow-y`, `--glow-opacity`) on
 * every frosted card child.  Cards are discovered automatically via the
 * `--frosted-card` CSS custom property set by the `frostedCard` theme
 * mixin — no className or hook wiring needed on individual cards.
 *
 * Cards within `radius` pixels of the cursor show the spotlight — even
 * when the cursor is in the gap between cards.
 *
 * All work happens in a single rAF callback per frame — zero React
 * re-renders.  When `enabled` is false, no listeners are attached.
 *
 * Desktop-only: the CSS `@media (hover: none)` rule hides the
 * `::before` pseudo-element on touch devices regardless.
 */
export function useProximityGlow(
  containerRef: RefObject<HTMLElement | null>,
  enabled: boolean,
  radius = DEFAULT_RADIUS,
): void {
  const rafId = useRef(0);

  useEffect(() => {
    const container = containerRef.current;
    if (!enabled || !container) return;

    function onMouseMove(e: MouseEvent) {
      if (rafId.current) return;          // already scheduled
      rafId.current = requestAnimationFrame(() => {
        rafId.current = 0;
        const cards = container!.querySelectorAll<HTMLElement>(FROSTED_SELECTOR);
        const mx = e.clientX;
        const my = e.clientY;
        for (const card of cards) {
          const r = card.getBoundingClientRect();
          // Local coordinates (can be negative / beyond bounds — that's the point)
          const lx = mx - r.left;
          const ly = my - r.top;

          // Shortest distance from mouse to card rect (0 when inside)
          const cx = Math.max(r.left, Math.min(mx, r.right));
          const cy = Math.max(r.top, Math.min(my, r.bottom));
          const dist = Math.sqrt((mx - cx) ** 2 + (my - cy) ** 2);

          if (dist <= radius) {
            card.style.setProperty('--glow-x', `${lx}px`);
            card.style.setProperty('--glow-y', `${ly}px`);
            card.style.setProperty('--glow-opacity', String(1 - dist / radius));
          } else {
            card.style.setProperty('--glow-opacity', '0');
          }
        }
      });
    }

    function onMouseLeave() {
      cancelAnimationFrame(rafId.current);
      rafId.current = 0;
      const cards = container!.querySelectorAll<HTMLElement>(FROSTED_SELECTOR);
      for (const card of cards) {
        card.style.setProperty('--glow-opacity', '0');
      }
    }

    container.addEventListener('mousemove', onMouseMove, { passive: true });
    container.addEventListener('mouseleave', onMouseLeave);
    return () => {
      cancelAnimationFrame(rafId.current);
      container.removeEventListener('mousemove', onMouseMove);
      container.removeEventListener('mouseleave', onMouseLeave);
    };
  }, [containerRef, enabled, radius]);
}
