import { useEffect, useRef } from 'react';

// Proximity radius — kept for when proximity mode is re-enabled.
// const DEFAULT_RADIUS = 250;
// /** Read --glow-size from :root and parse to a number (px). Falls back to DEFAULT_RADIUS. */
// function getGlowRadius(): number {
//   const raw = getComputedStyle(document.documentElement).getPropertyValue('--glow-size').trim();
//   const parsed = parseInt(raw, 10);
//   return parsed > 0 ? parsed : DEFAULT_RADIUS;
// }

/** Attribute selector matching elements with the --frosted-card CSS custom property marker. */
const FROSTED_SELECTOR = '[style*="--frosted-card"]';

/** Attribute selector for an exclusive glow scope container. */
const SCOPE_SELECTOR = '[data-glow-scope]';

/**
 * Proximity-based glow for frosted cards.
 *
 * Attaches a `mousemove` listener to `document.documentElement` and
 * updates CSS custom properties (`--glow-x`, `--glow-y`, `--glow-opacity`)
 * on every frosted card in the viewport.  Cards are discovered automatically
 * via the `--frosted-card` CSS custom property set by the `frostedCard`
 * theme mixin — no className or hook wiring needed on individual cards.
 *
 * Covers all regions of the app (content, sidebar, header) because the
 * listener is on the document root.
 *
 * All work happens in a single rAF callback per frame — zero React
 * re-renders.  When `enabled` is false, no listeners are attached.
 *
 * Desktop-only: the CSS `@media (hover: none)` rule hides the
 * `::before` pseudo-element on touch devices regardless.
 */
export function useProximityGlow(enabled: boolean): void {
  const rafId = useRef(0);

  useEffect(() => {
    if (!enabled) return;

    const root = document.documentElement;
    // const radius = getGlowRadius();  // unused while proximity mode is off

    function onMouseMove(e: MouseEvent) {
      if (rafId.current) return;          // already scheduled
      rafId.current = requestAnimationFrame(() => {
        rafId.current = 0;
        const cards = root.querySelectorAll<HTMLElement>(FROSTED_SELECTOR);
        const scope = root.querySelector<HTMLElement>(SCOPE_SELECTOR);
        const mx = e.clientX;
        const my = e.clientY;
        for (const card of cards) {
          // When a glow scope is active, suppress painting on cards outside it
          if (scope && !scope.contains(card)) {
            card.style.setProperty('--glow-opacity', '0');
            card.style.setProperty('--glow-hover', '0');
            continue;
          }
          const r = card.getBoundingClientRect();
          // Local coordinates (can be negative / beyond bounds — that's the point)
          const lx = mx - r.left;
          const ly = my - r.top;

          // Shortest distance from mouse to card rect (0 when inside)
          const cx = Math.max(r.left, Math.min(mx, r.right));
          const cy = Math.max(r.top, Math.min(my, r.bottom));
          const dist = Math.sqrt((mx - cx) ** 2 + (my - cy) ** 2);

          if (dist === 0) {
            card.style.setProperty('--glow-x', `${lx}px`);
            card.style.setProperty('--glow-y', `${ly}px`);
            card.style.setProperty('--glow-opacity', '1');
            card.style.setProperty('--glow-hover', '1');
          } else {
            card.style.setProperty('--glow-opacity', '0');
            card.style.setProperty('--glow-hover', '0');
          }
        }
      });
    }

    function onMouseLeave() {
      cancelAnimationFrame(rafId.current);
      rafId.current = 0;
      const cards = root.querySelectorAll<HTMLElement>(FROSTED_SELECTOR);
      for (const card of cards) {
        card.style.setProperty('--glow-opacity', '0');
        card.style.setProperty('--glow-hover', '0');
      }
    }

    root.addEventListener('mousemove', onMouseMove, { passive: true });
    root.addEventListener('mouseleave', onMouseLeave);
    return () => {
      cancelAnimationFrame(rafId.current);
      root.removeEventListener('mousemove', onMouseMove);
      root.removeEventListener('mouseleave', onMouseLeave);
    };
  }, [enabled]);
}
