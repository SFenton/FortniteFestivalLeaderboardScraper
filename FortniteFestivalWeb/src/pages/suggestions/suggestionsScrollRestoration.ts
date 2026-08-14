import {
  completeSuggestionsScrollRestoration,
  getSuggestionsScrollRestoreState,
  updateSuggestionsScrollY,
} from './suggestionsSessionCache';

const RESTORE_DELAYS_MS = [100, 300, 750, 1_500, 3_000];
const RESTORE_GUARD_TIMEOUT_MS = 1_000;
const RESTORE_SAFETY_TIMEOUT_MS = 4_000;

export function beginSuggestionsScrollRestoration(scrollElement: HTMLElement): () => void {
  let observer: MutationObserver | null = null;
  let timeouts: number[] = [];
  let scrollGuardAttached = false;
  let scrollGuardTimeout = 0;
  let scrollGuardTarget = 0;
  let userIntentGuardsAttached = false;
  let done = false;
  let frame = 0;
  let activeRestoreKey: string | null = null;
  let safetyTimeout = 0;
  const queueRestoreFrame = () => {
    if (done || frame) return;
    frame = requestAnimationFrame(() => {
      frame = 0;
      restore();
    });
  };

  const finishRestore = () => {
    if (activeRestoreKey) {
      completeSuggestionsScrollRestoration(activeRestoreKey, scrollElement.scrollTop);
    }
  };
  const cancelForUserIntent = () => {
    finishRestore();
    clearPending();
  };
  const scheduleStableCompletion = (reset = false) => {
    if (scrollGuardTimeout !== 0 && !reset) return;
    window.clearTimeout(scrollGuardTimeout);
    scrollGuardTimeout = window.setTimeout(() => {
      scrollGuardTimeout = 0;
      finishRestore();
      clearPending();
    }, RESTORE_GUARD_TIMEOUT_MS);
  };
  const ensureScrollGuard = () => {
    if (scrollGuardAttached) return;
    scrollGuardAttached = true;
    scrollElement.addEventListener('scroll', guardRestoredScroll, { passive: true });
  };
  const guardRestoredScroll = () => {
    const maxScrollTop = Math.max(0, scrollElement.scrollHeight - scrollElement.clientHeight);
    if (scrollGuardTarget > maxScrollTop + 1) return;
    if (Math.abs(scrollElement.scrollTop - scrollGuardTarget) > 1) {
      scrollElement.scrollTo(0, scrollGuardTarget);
      if (activeRestoreKey) {
        updateSuggestionsScrollY(activeRestoreKey, scrollElement.scrollTop);
      }
      scheduleStableCompletion(true);
      return;
    }
    scheduleStableCompletion();
  };
  const clearPending = () => {
    done = true;
    if (frame) {
      cancelAnimationFrame(frame);
      frame = 0;
    }
    observer?.disconnect();
    observer = null;
    if (scrollGuardAttached) {
      scrollElement.removeEventListener('scroll', guardRestoredScroll);
      scrollGuardAttached = false;
    }
    if (userIntentGuardsAttached) {
      window.removeEventListener('wheel', cancelForUserIntent, true);
      window.removeEventListener('touchstart', cancelForUserIntent, true);
      window.removeEventListener('pointerdown', cancelForUserIntent, true);
      window.removeEventListener('keydown', cancelForUserIntent, true);
      userIntentGuardsAttached = false;
    }
    window.clearTimeout(scrollGuardTimeout);
    scrollGuardTimeout = 0;
    window.clearTimeout(safetyTimeout);
    safetyTimeout = 0;
    for (const timeout of timeouts) window.clearTimeout(timeout);
    timeouts = [];
  };
  const restore = () => {
    if (done) return;
    const list = scrollElement.querySelector<HTMLElement>('[data-testid="suggestions-list"]');
    const requestedCacheKey = list?.dataset.suggestionsCacheKey ?? null;
    const restoreState = getSuggestionsScrollRestoreState(requestedCacheKey);
    if (!requestedCacheKey || !restoreState.matches) {
      scrollElement.scrollTo(0, 0);
      queueRestoreFrame();
      return;
    }
    if (!restoreState.restorable) {
      activeRestoreKey = requestedCacheKey;
      scrollGuardTarget = 0;
      ensureScrollGuard();
      const previousScrollTop = scrollElement.scrollTop;
      scrollElement.scrollTo(0, 0);
      updateSuggestionsScrollY(requestedCacheKey, scrollElement.scrollTop);
      scheduleStableCompletion(previousScrollTop > 1);
      queueRestoreFrame();
      return;
    }

    const target = restoreState.scrollY;
    activeRestoreKey = requestedCacheKey;
    scrollGuardTarget = target;
    ensureScrollGuard();
    const previousScrollTop = scrollElement.scrollTop;
    scrollElement.scrollTo(0, target);
    updateSuggestionsScrollY(requestedCacheKey, scrollElement.scrollTop);
    if (Math.abs(scrollElement.scrollTop - target) <= 1) {
      scheduleStableCompletion(Math.abs(previousScrollTop - target) > 1);
    }
    queueRestoreFrame();
  };

  observer = new MutationObserver(restore);
  observer.observe(scrollElement, {
    attributeFilter: ['data-suggestions-cache-key'],
    attributes: true,
    childList: true,
    subtree: true,
  });
  userIntentGuardsAttached = true;
  window.addEventListener('wheel', cancelForUserIntent, { capture: true, passive: true });
  window.addEventListener('touchstart', cancelForUserIntent, { capture: true, passive: true });
  window.addEventListener('pointerdown', cancelForUserIntent, { capture: true, passive: true });
  window.addEventListener('keydown', cancelForUserIntent, true);
  restore();
  if (!done) {
    timeouts = RESTORE_DELAYS_MS.map(delay => window.setTimeout(restore, delay));
    safetyTimeout = window.setTimeout(() => {
      finishRestore();
      clearPending();
    }, RESTORE_SAFETY_TIMEOUT_MS);
  }

  return () => {
    clearPending();
  };
}
