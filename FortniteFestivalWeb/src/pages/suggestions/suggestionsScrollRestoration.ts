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

  const finishRestore = () => {
    if (activeRestoreKey) {
      completeSuggestionsScrollRestoration(activeRestoreKey, scrollElement.scrollTop);
    }
  };
  const cancelForUserIntent = () => {
    finishRestore();
    clearPending();
  };
  const guardRestoredScroll = () => {
    const maxScrollTop = Math.max(0, scrollElement.scrollHeight - scrollElement.clientHeight);
    if (scrollGuardTarget > maxScrollTop + 1) return;
    if (Math.abs(scrollElement.scrollTop - scrollGuardTarget) <= 1) return;
    scrollElement.scrollTo(0, scrollGuardTarget);
    if (activeRestoreKey) {
      updateSuggestionsScrollY(activeRestoreKey, scrollElement.scrollTop);
    }
    finishRestore();
    clearPending();
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
      return;
    }
    if (!restoreState.restorable) {
      scrollElement.scrollTo(0, 0);
      return;
    }

    const target = restoreState.scrollY;
    activeRestoreKey = requestedCacheKey;
    scrollGuardTarget = target;
    if (!scrollGuardAttached) {
      scrollGuardAttached = true;
      scrollElement.addEventListener('scroll', guardRestoredScroll, { passive: true });
    }
    scrollElement.scrollTo(0, target);
    updateSuggestionsScrollY(requestedCacheKey, scrollElement.scrollTop);
    if (
      scrollGuardTimeout === 0
      && Math.abs(scrollElement.scrollTop - target) <= 1
    ) {
      scrollGuardTimeout = window.setTimeout(() => {
        finishRestore();
        clearPending();
      }, RESTORE_GUARD_TIMEOUT_MS);
    }
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
  frame = done ? 0 : requestAnimationFrame(restore);
  if (!done) {
    timeouts = RESTORE_DELAYS_MS.map(delay => window.setTimeout(restore, delay));
    safetyTimeout = window.setTimeout(clearPending, RESTORE_SAFETY_TIMEOUT_MS);
  }

  return () => {
    clearPending();
  };
}
