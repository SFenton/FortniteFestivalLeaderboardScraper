import { useMemo, useCallback, useState, useLayoutEffect, useRef } from 'react';
import { useFirstRunContext } from '../../contexts/FirstRunContext';
import type { FirstRunSlideDef, FirstRunGateContext } from '../../firstRun/types';

type UseFirstRunResult = {
  /** The unseen slides to display (gate-filtered + unseen-filtered). */
  slides: FirstRunSlideDef[];
  /** True when there are unseen slides to show (includes exit animation period). */
  show: boolean;
  /** Dismiss the carousel — marks slides as seen and begins exit animation. */
  dismiss: () => void;
  /** Called by FirstRunCarousel after its exit animation completes. */
  onExitComplete: () => void;
};

/**
 * Hook for pages to check whether their first-run carousel should show.
 * Call after useRegisterFirstRun.
 *
 * @param pageKey - The page key used when registering slides.
 * @param gateCtx - Context object evaluated against each slide's gate predicate.
 */
export function useFirstRun(pageKey: string, gateCtx: FirstRunGateContext): UseFirstRunResult {
  const { enabled, getUnseenSlides, getAllSlides, markSeen, setActiveCarousel, activeCarouselKey, registeredPages } = useFirstRunContext();
  const [dismissed, setDismissed] = useState(false);
  const [closing, setClosing] = useState(false);

  const computedSlides = useMemo(
    () => {
      if (!enabled) return [];
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
    [pageKey, gateCtx, getUnseenSlides, getAllSlides, dismissed, enabled, registeredPages],
  );

  // Freeze slides during exit animation so the carousel doesn't unmount early
  const slidesRef = useRef(computedSlides);
  if (!closing) slidesRef.current = computedSlides;
  const slides = closing ? slidesRef.current : computedSlides;

  const show = slides.length > 0;

  // Keep context's activeCarouselKey in sync with this page's carousel visibility.
  // useLayoutEffect ensures this runs before paint, preventing a changelog flash.
  /* v8 ignore start -- layout synchronization + cleanup branches not exercisable via renderHook */
  useLayoutEffect(() => {
    if (show && !closing) {
      setActiveCarousel(pageKey);
    } else if (activeCarouselKey === pageKey) {
      setActiveCarousel(null);
    }
    return () => {
      if (activeCarouselKey === pageKey) {
        setActiveCarousel(null);
      }
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps -- only react to show/closing changes and unmount
  }, [show, closing, pageKey]);
  /* v8 ignore stop */

  const dismiss = useCallback(() => {
    if (computedSlides.length > 0) {
      markSeen(computedSlides);
    }
    setActiveCarousel(null);
    setClosing(true);
  }, [computedSlides, markSeen, setActiveCarousel]);

  const onExitComplete = useCallback(() => {
    setClosing(false);
    setDismissed(true);
  }, []);

  return useMemo(() => ({
    slides,
    show,
    dismiss,
    onExitComplete,
  }), [slides, show, dismiss, onExitComplete]);
}

type UseFirstRunReplayResult = {
  /** All slides for the page (bypasses gates + seen state). */
  slides: FirstRunSlideDef[];
  /** Whether the replay carousel is open (includes exit animation period). */
  show: boolean;
  /** Open the replay carousel. */
  open: () => void;
  /** Close the replay carousel (marks all as seen, begins exit animation). */
  dismiss: () => void;
  /** Called by FirstRunCarousel after its exit animation completes. */
  onExitComplete: () => void;
};

/**
 * Hook for Settings page to replay a page's full first-run carousel.
 * Bypasses gates and seen state — shows all slides.
 */
export function useFirstRunReplay(pageKey: string): UseFirstRunReplayResult {
  const { enabled, getAllSlides, resetPage, markSeen, registeredPages } = useFirstRunContext();
  const [showReplay, setShowReplay] = useState(false);

  // registeredPages forces re-evaluation after registration effects fire
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const slides = useMemo(() => enabled ? getAllSlides(pageKey) : [], [pageKey, getAllSlides, enabled, registeredPages]);

  const open = useCallback(() => {
    resetPage(pageKey);
    setShowReplay(true);
  }, [pageKey, resetPage]);

  const dismiss = useCallback(() => {
    if (slides.length > 0) {
      markSeen(slides);
    }
  }, [slides, markSeen]);

  const onExitComplete = useCallback(() => {
    setShowReplay(false);
  }, []);

  return useMemo(() => ({
    slides,
    show: showReplay,
    open,
    dismiss,
    onExitComplete,
  }), [slides, showReplay, open, dismiss, onExitComplete]);
}
