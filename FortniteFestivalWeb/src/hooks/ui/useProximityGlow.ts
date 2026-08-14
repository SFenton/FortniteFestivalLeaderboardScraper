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
 * Hover glow for frosted cards.
 *
 * Attaches a `mousemove` listener to `document.documentElement` and
 * updates CSS custom properties (`--glow-x`, `--glow-y`, `--glow-opacity`)
 * only on the frosted card under the pointer.
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
    let activeCard: HTMLElement | null = null;
    let pointerTarget: EventTarget | null = null;
    let pointerX = 0;
    let pointerY = 0;

    function onMouseMove(e: MouseEvent) {
      pointerTarget = e.target;
      pointerX = e.clientX;
      pointerY = e.clientY;
      if (rafId.current) return;          // already scheduled
      rafId.current = requestAnimationFrame(() => {
        rafId.current = 0;
        const scopes = root.querySelectorAll<HTMLElement>(SCOPE_SELECTOR);
        const scope = scopes.length > 0 ? scopes[scopes.length - 1]! : null;
        const target = pointerTarget instanceof Element
          ? pointerTarget.closest<HTMLElement>(FROSTED_SELECTOR)
          : null;
        const card = target && (!scope || scope.contains(target)) ? target : null;
        if (activeCard && activeCard !== card) {
          clearGlow(activeCard);
        }
        activeCard = card;
        if (!card) return;

        const rect = card.getBoundingClientRect();
        card.style.setProperty('--glow-x', `${pointerX - rect.left}px`);
        card.style.setProperty('--glow-y', `${pointerY - rect.top}px`);
        card.style.setProperty('--glow-opacity', '1');
        card.style.setProperty('--glow-hover', '1');
      });
    }

    function onMouseLeave() {
      cancelAnimationFrame(rafId.current);
      rafId.current = 0;
      if (activeCard) clearGlow(activeCard);
      activeCard = null;
    }

    root.addEventListener('mousemove', onMouseMove, { passive: true });
    root.addEventListener('mouseleave', onMouseLeave);
    return () => {
      cancelAnimationFrame(rafId.current);
      if (activeCard) clearGlow(activeCard);
      activeCard = null;
      root.removeEventListener('mousemove', onMouseMove);
      root.removeEventListener('mouseleave', onMouseLeave);
    };
  }, [enabled]);
}

function clearGlow(card: HTMLElement): void {
  card.style.setProperty('--glow-opacity', '0');
  card.style.setProperty('--glow-hover', '0');
}
