import { useMemo, useCallback, useState } from 'react';
import { useFirstRunContext } from '../../contexts/FirstRunContext';
import type { FirstRunSlideDef, FirstRunGateContext } from '../../firstRun/types';

type UseFirstRunResult = {
  /** The unseen slides to display (gate-filtered + unseen-filtered). */
  slides: FirstRunSlideDef[];
  /** True when there are unseen slides to show. */
  show: boolean;
  /** Dismiss the carousel — marks all currently-shown slides as seen. */
  dismiss: () => void;
};

/**
 * Hook for pages to check whether their first-run carousel should show.
 * Call after useRegisterFirstRun.
 *
 * @param pageKey - The page key used when registering slides.
 * @param gateCtx - Context object evaluated against each slide's gate predicate.
 */
export function useFirstRun(pageKey: string, gateCtx: FirstRunGateContext): UseFirstRunResult {
  const { getUnseenSlides, getAllSlides, markSeen, registeredPages } = useFirstRunContext();
  const [dismissed, setDismissed] = useState(false);

  const slides = useMemo(
    () => {
      if (dismissed) return [];
      if (gateCtx.ready === false) return [];
      if (gateCtx.alwaysShow) {
        const all = getAllSlides(pageKey);
        return all.filter(s => !s.gate || s.gate(gateCtx));
      }
      return getUnseenSlides(pageKey, gateCtx);
    },
    // registeredPages forces re-evaluation after registration effects fire
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [pageKey, gateCtx, getUnseenSlides, getAllSlides, dismissed, registeredPages],
  );

  const dismiss = useCallback(() => {
    if (slides.length > 0) {
      markSeen(slides);
    }
    setDismissed(true);
  }, [slides, markSeen]);

  return useMemo(() => ({
    slides,
    show: slides.length > 0,
    dismiss,
  }), [slides, dismiss]);
}

type UseFirstRunReplayResult = {
  /** All slides for the page (bypasses gates + seen state). */
  slides: FirstRunSlideDef[];
  /** Whether the replay carousel is open. */
  show: boolean;
  /** Open the replay carousel. */
  open: () => void;
  /** Close the replay carousel (marks all as seen). */
  dismiss: () => void;
};

/**
 * Hook for Settings page to replay a page's full first-run carousel.
 * Bypasses gates and seen state — shows all slides.
 */
export function useFirstRunReplay(pageKey: string): UseFirstRunReplayResult {
  const { getAllSlides, resetPage, markSeen, registeredPages } = useFirstRunContext();
  const [showReplay, setShowReplay] = useState(false);

  // registeredPages forces re-evaluation after registration effects fire
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const slides = useMemo(() => getAllSlides(pageKey), [pageKey, getAllSlides, registeredPages]);

  const open = useCallback(() => {
    resetPage(pageKey);
    setShowReplay(true);
  }, [pageKey, resetPage]);

  const dismiss = useCallback(() => {
    if (slides.length > 0) {
      markSeen(slides);
    }
    setShowReplay(false);
  }, [slides, markSeen]);

  return useMemo(() => ({
    slides,
    show: showReplay,
    open,
    dismiss,
  }), [slides, showReplay, open, dismiss]);
}
