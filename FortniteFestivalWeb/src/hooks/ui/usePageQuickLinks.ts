import { useCallback, useEffect, useRef, useState, type ReactNode, type RefObject } from 'react';

type QuickLinkTraceEntry = {
  timestamp: number;
  event: string;
  compact: boolean;
  scrollTop: number | null;
  payload: Record<string, unknown>;
};

declare global {
  interface Window {
    __fstQuickLinksTrace?: QuickLinkTraceEntry[];
  }
}

export interface PageQuickLinkItem {
  id: string;
  label: ReactNode;
  landmarkLabel: string;
  icon?: ReactNode;
}

type UsePageQuickLinksOptions<T extends PageQuickLinkItem> = {
  items: readonly T[];
  scrollContainerRef: RefObject<HTMLElement | null>;
  isDesktopRailEnabled: boolean;
  getItemTop?: (id: string, scrollEl: HTMLElement) => number | null;
  scrollOffset?: number;
  scrollCompleteThreshold?: number;
  scrollSettleDelayMs?: number;
};

type UsePageQuickLinksResult<T extends PageQuickLinkItem> = {
  activeItemId: string | null;
  quickLinksOpen: boolean;
  openQuickLinks: () => void;
  closeQuickLinks: () => void;
  handleQuickLinkSelect: (item: T, options?: { skipScroll?: boolean; }) => void;
  registerSectionRef: (id: string, element: HTMLElement | null) => void;
};

type QuickLinkTransitionState =
  | {
    phase: 'scrolling';
    originId: string;
    targetId: string;
    lockWhileVisible: boolean;
  }
  | {
    phase: 'owned';
    targetId: string;
    lockWhileVisible: boolean;
    anchorScrollTop: number;
  };

const REACHABLE_TARGET_OWNERSHIP_PX = 96;
const QUICK_LINK_TRACE_STORAGE_KEY = 'fst:debugQuickLinks';
const MAX_QUICK_LINK_TRACE_ENTRIES = 400;

function isQuickLinksTraceEnabled(): boolean {
  if (typeof window === 'undefined') {
    return false;
  }

  try {
    return window.localStorage.getItem(QUICK_LINK_TRACE_STORAGE_KEY) === '1';
  } catch {
    return false;
  }
}

function pushQuickLinkTrace(entry: QuickLinkTraceEntry) {
  if (typeof window === 'undefined') {
    return;
  }

  const trace = window.__fstQuickLinksTrace ?? [];
  trace.push(entry);
  if (trace.length > MAX_QUICK_LINK_TRACE_ENTRIES) {
    trace.splice(0, trace.length - MAX_QUICK_LINK_TRACE_ENTRIES);
  }
  window.__fstQuickLinksTrace = trace;
}

export function getPageQuickLinkTestId(id: string): string {
  return id.replace(/[^a-z0-9]+/gi, '-').toLowerCase();
}

function getSectionScrollTop(scrollEl: HTMLElement, sectionEl: HTMLElement): number {
  const scrollRect = scrollEl.getBoundingClientRect();
  const sectionRect = sectionEl.getBoundingClientRect();
  return scrollEl.scrollTop + sectionRect.top - scrollRect.top;
}

function getQuickLinkTargetTop(scrollEl: HTMLElement, sectionEl: HTMLElement, scrollOffset: number): number {
  return Math.max(0, getSectionScrollTop(scrollEl, sectionEl) - scrollOffset);
}

function clampQuickLinkTargetTop(scrollEl: HTMLElement, itemTop: number, scrollOffset: number): number {
  const maxScrollTop = Math.max(0, scrollEl.scrollHeight - scrollEl.clientHeight);
  return Math.min(Math.max(0, itemTop - scrollOffset), maxScrollTop);
}

function resolveQuickLinkItemTop(
  itemId: string,
  sectionRefs: Map<string, HTMLElement>,
  scrollEl: HTMLElement,
  getItemTop?: (id: string, scrollEl: HTMLElement) => number | null,
): number | null {
  const sectionEl = sectionRefs.get(itemId);
  if (sectionEl) {
    return getSectionScrollTop(scrollEl, sectionEl);
  }

  return getItemTop?.(itemId, scrollEl) ?? null;
}

function isQuickLinkItemVisible<T extends PageQuickLinkItem>(
  itemId: string,
  items: readonly T[],
  sectionRefs: Map<string, HTMLElement>,
  scrollEl: HTMLElement,
  getItemTop?: (id: string, scrollEl: HTMLElement) => number | null,
): boolean {
  const sectionEl = sectionRefs.get(itemId);
  if (sectionEl) {
    const scrollRect = scrollEl.getBoundingClientRect();
    const sectionRect = sectionEl.getBoundingClientRect();
    return sectionRect.bottom > scrollRect.top && sectionRect.top < scrollRect.bottom;
  }

  const itemIndex = items.findIndex((item) => item.id === itemId);
  if (itemIndex === -1) return false;

  const itemTop = resolveQuickLinkItemTop(itemId, sectionRefs, scrollEl, getItemTop);
  if (itemTop == null) return false;

  let itemBottom = scrollEl.scrollHeight;
  for (let nextIndex = itemIndex + 1; nextIndex < items.length; nextIndex += 1) {
    const nextItemTop = resolveQuickLinkItemTop(items[nextIndex]!.id, sectionRefs, scrollEl, getItemTop);
    if (nextItemTop != null) {
      itemBottom = nextItemTop;
      break;
    }
  }

  return itemBottom > scrollEl.scrollTop && itemTop < scrollEl.scrollTop + scrollEl.clientHeight;
}

function isQuickLinkTargetReachable(
  scrollEl: HTMLElement,
  itemTop: number,
  scrollOffset: number,
): boolean {
  const targetRelativeTop = itemTop - scrollEl.scrollTop;
  return targetRelativeTop >= -REACHABLE_TARGET_OWNERSHIP_PX
    && targetRelativeTop <= scrollOffset + REACHABLE_TARGET_OWNERSHIP_PX;
}

function resolveActiveQuickLink<T extends PageQuickLinkItem>(
  items: readonly T[],
  sectionRefs: Map<string, HTMLElement>,
  scrollEl: HTMLElement,
  scrollOffset: number,
  getItemTop?: (id: string, scrollEl: HTMLElement) => number | null,
  preferredItemId?: string | null,
): string | null {
  if (items.length === 0) return null;

  if (preferredItemId && isQuickLinkItemVisible(preferredItemId, items, sectionRefs, scrollEl, getItemTop)) {
    return preferredItemId;
  }

  const threshold = scrollEl.scrollTop + scrollOffset + 1;
  let active = items[0]!.id;

  for (const item of items) {
    const itemTop = resolveQuickLinkItemTop(item.id, sectionRefs, scrollEl, getItemTop);
    if (itemTop == null) continue;
    if (itemTop <= threshold) {
      active = item.id;
      continue;
    }
    break;
  }

  return active;
}

export function usePageQuickLinks<T extends PageQuickLinkItem>({
  items,
  scrollContainerRef,
  isDesktopRailEnabled,
  getItemTop,
  scrollOffset = 8,
  scrollCompleteThreshold = 8,
  scrollSettleDelayMs = 120,
}: UsePageQuickLinksOptions<T>): UsePageQuickLinksResult<T> {
  const [activeItemId, setActiveItemId] = useState<string | null>(null);
  const [quickLinksOpen, setQuickLinksOpen] = useState(false);
  const sectionRefs = useRef(new Map<string, HTMLElement>());
  const quickLinkTransitionRef = useRef<QuickLinkTransitionState | null>(null);
  const quickLinkSettleTimerRef = useRef<number | null>(null);
  const loggedActiveItemIdRef = useRef<string | null>(null);

  const emitQuickLinkDebug = useCallback((event: string, payload: Record<string, unknown>) => {
    if (!isQuickLinksTraceEnabled()) {
      return;
    }

    const scrollEl = scrollContainerRef.current;
    const entry = {
      timestamp: Date.now(),
      event,
      compact: !isDesktopRailEnabled,
      scrollTop: scrollEl?.scrollTop ?? null,
      payload,
    } satisfies QuickLinkTraceEntry;

    pushQuickLinkTrace(entry);
    console.log('[quick-links]', JSON.stringify(entry));
  }, [isDesktopRailEnabled, scrollContainerRef]);

  const setLoggedActiveItemId = useCallback((nextId: string | null, reason: string, payload?: Record<string, unknown>) => {
    const previousId = loggedActiveItemIdRef.current;
    if (previousId !== nextId) {
      emitQuickLinkDebug('active-change', {
        reason,
        previousId,
        nextId,
        ...payload,
      });
      loggedActiveItemIdRef.current = nextId;
    }

    setActiveItemId(nextId);
  }, [emitQuickLinkDebug]);

  const clearQuickLinkSettleTimer = useCallback(() => {
    if (quickLinkSettleTimerRef.current === null) return;
    window.clearTimeout(quickLinkSettleTimerRef.current);
    quickLinkSettleTimerRef.current = null;
  }, []);

  const clearQuickLinkTransition = useCallback(() => {
    clearQuickLinkSettleTimer();
    quickLinkTransitionRef.current = null;
  }, [clearQuickLinkSettleTimer]);

  useEffect(() => () => {
    clearQuickLinkSettleTimer();
  }, [clearQuickLinkSettleTimer]);

  const registerSectionRef = useCallback((id: string, element: HTMLElement | null) => {
    if (element) {
      sectionRefs.current.set(id, element);
      return;
    }
    sectionRefs.current.delete(id);
  }, []);

  const openQuickLinks = useCallback(() => {
    if (items.length > 0) {
      setQuickLinksOpen(true);
    }
  }, [items.length]);

  const closeQuickLinks = useCallback(() => {
    setQuickLinksOpen(false);
  }, []);

  useEffect(() => {
    if (isDesktopRailEnabled || items.length === 0) {
      setQuickLinksOpen(false);
    }
  }, [isDesktopRailEnabled, items.length]);

  useEffect(() => {
    const scrollEl = scrollContainerRef.current;
    if (!scrollEl || items.length === 0) {
      clearQuickLinkTransition();
      setLoggedActiveItemId(null, 'no-scroll-or-items');
      return;
    }

    const syncActive = () => {
      const naturalActive = resolveActiveQuickLink(items, sectionRefs.current, scrollEl, scrollOffset, getItemTop);
      const transitionState = quickLinkTransitionRef.current;

      emitQuickLinkDebug('sync-snapshot', {
        activeItemId,
        naturalActive,
        threshold: scrollEl.scrollTop + scrollOffset + 1,
        transitionState,
        items: items.map((item) => ({
          id: item.id,
          top: resolveQuickLinkItemTop(item.id, sectionRefs.current, scrollEl, getItemTop),
          visible: isQuickLinkItemVisible(item.id, items, sectionRefs.current, scrollEl, getItemTop),
          hasSectionRef: sectionRefs.current.has(item.id),
        })),
      });

      if (!transitionState) {
        setLoggedActiveItemId(naturalActive, 'natural-scroll');
        return;
      }

      const targetVisible = isQuickLinkItemVisible(transitionState.targetId, items, sectionRefs.current, scrollEl, getItemTop);
      const targetItemTop = resolveQuickLinkItemTop(transitionState.targetId, sectionRefs.current, scrollEl, getItemTop);

      if (transitionState.phase === 'scrolling') {
        if (targetItemTop == null) {
          clearQuickLinkTransition();
          setLoggedActiveItemId(naturalActive, 'scrolling-target-missing');
          return;
        }

        const targetTop = clampQuickLinkTargetTop(scrollEl, targetItemTop, scrollOffset);
        const targetReached = Math.abs(scrollEl.scrollTop - targetTop) <= scrollCompleteThreshold
          || (targetVisible && isQuickLinkTargetReachable(scrollEl, targetItemTop, scrollOffset));

        if (targetReached) {
          clearQuickLinkSettleTimer();
          quickLinkSettleTimerRef.current = window.setTimeout(() => {
            quickLinkSettleTimerRef.current = null;

            const currentTransitionState = quickLinkTransitionRef.current;
            if (!currentTransitionState
              || currentTransitionState.phase !== 'scrolling'
              || currentTransitionState.targetId !== transitionState.targetId) {
              return;
            }

            const settledItemTop = resolveQuickLinkItemTop(currentTransitionState.targetId, sectionRefs.current, scrollEl, getItemTop);
            if (settledItemTop == null) {
              return;
            }

            const settledTargetTop = clampQuickLinkTargetTop(scrollEl, settledItemTop, scrollOffset);
            const settledTargetVisible = isQuickLinkItemVisible(currentTransitionState.targetId, items, sectionRefs.current, scrollEl, getItemTop);
            const settledTargetReached = Math.abs(scrollEl.scrollTop - settledTargetTop) <= scrollCompleteThreshold
              || (settledTargetVisible && isQuickLinkTargetReachable(scrollEl, settledItemTop, scrollOffset));

            if (!settledTargetReached) {
              return;
            }

            quickLinkTransitionRef.current = {
              phase: 'owned',
              targetId: currentTransitionState.targetId,
              lockWhileVisible: currentTransitionState.lockWhileVisible,
              anchorScrollTop: scrollEl.scrollTop,
            };
            setLoggedActiveItemId(currentTransitionState.targetId, 'scroll-settled-arrival');
          }, scrollSettleDelayMs);
        } else {
          clearQuickLinkSettleTimer();
        }

        const originStillExists = items.some((item) => item.id === transitionState.originId);
        setLoggedActiveItemId(originStillExists ? transitionState.originId : naturalActive, 'scroll-origin-hold');
        return;
      }

      if (transitionState.lockWhileVisible) {
        if (targetVisible) {
          setLoggedActiveItemId(transitionState.targetId, 'owned-visible-lock');
          return;
        }

        clearQuickLinkTransition();
        setLoggedActiveItemId(naturalActive, 'owned-visible-release');
        return;
      }

      if (targetVisible && Math.abs(scrollEl.scrollTop - transitionState.anchorScrollTop) <= scrollCompleteThreshold) {
        setLoggedActiveItemId(transitionState.targetId, 'owned-anchor-hold');
        return;
      }

      if (targetItemTop != null) {
        const targetRelativeTop = targetItemTop - scrollEl.scrollTop;
        if (targetRelativeTop >= -REACHABLE_TARGET_OWNERSHIP_PX && targetRelativeTop <= scrollOffset + REACHABLE_TARGET_OWNERSHIP_PX) {
          setLoggedActiveItemId(transitionState.targetId, 'owned-reachable-band');
          return;
        }

        clearQuickLinkTransition();
        setLoggedActiveItemId(naturalActive, 'owned-release-natural');
        return;
      }

      clearQuickLinkTransition();
      setLoggedActiveItemId(naturalActive, 'owned-target-missing');
    };

    syncActive();
    scrollEl.addEventListener('scroll', syncActive, { passive: true });
    window.addEventListener('resize', syncActive);
    return () => {
      clearQuickLinkSettleTimer();
      scrollEl.removeEventListener('scroll', syncActive);
      window.removeEventListener('resize', syncActive);
    };
  }, [clearQuickLinkSettleTimer, clearQuickLinkTransition, getItemTop, isDesktopRailEnabled, items, scrollCompleteThreshold, scrollContainerRef, scrollOffset, scrollSettleDelayMs, setLoggedActiveItemId]);

  const handleQuickLinkSelect = useCallback((item: T, options?: { skipScroll?: boolean; }) => {
    const scrollEl = scrollContainerRef.current;
    const sectionEl = sectionRefs.current.get(item.id);
    const itemTop = scrollEl
      ? (sectionEl ? getSectionScrollTop(scrollEl, sectionEl) : getItemTop?.(item.id, scrollEl))
      : null;
    if (!scrollEl || itemTop == null) return;

    const maxScrollTop = Math.max(0, scrollEl.scrollHeight - scrollEl.clientHeight);
    const rawTargetTop = Math.max(0, itemTop - scrollOffset);
    const nextTop = Math.min(rawTargetTop, maxScrollTop);
    const lockWhileVisible = rawTargetTop > maxScrollTop;

    emitQuickLinkDebug('click-intent', {
      targetId: item.id,
      skipScroll: !!options?.skipScroll,
      itemTop,
      nextTop,
      lockWhileVisible,
    });

    if (!isDesktopRailEnabled) {
      clearQuickLinkSettleTimer();
      if (Math.abs(scrollEl.scrollTop - nextTop) <= scrollCompleteThreshold) {
        quickLinkTransitionRef.current = {
          phase: 'owned',
          targetId: item.id,
          lockWhileVisible,
          anchorScrollTop: scrollEl.scrollTop,
        };
        setLoggedActiveItemId(item.id, 'compact-click-immediate-arrival');
        return;
      }

      quickLinkTransitionRef.current = {
        phase: 'scrolling',
        originId: item.id,
        targetId: item.id,
        lockWhileVisible,
      };
      setLoggedActiveItemId(item.id, 'compact-click-hold');
      if (!options?.skipScroll) {
        scrollEl.scrollTo({ top: nextTop, behavior: 'smooth' });
        if (Math.abs(scrollEl.scrollTop - nextTop) <= scrollCompleteThreshold) {
          quickLinkTransitionRef.current = {
            phase: 'owned',
            targetId: item.id,
            lockWhileVisible,
            anchorScrollTop: scrollEl.scrollTop,
          };
        }
      }
      return;
    }

    const naturalActive = resolveActiveQuickLink(items, sectionRefs.current, scrollEl, scrollOffset, getItemTop);
    const originId = activeItemId && items.some((entry) => entry.id === activeItemId)
      ? activeItemId
      : (naturalActive ?? item.id);

    if (Math.abs(scrollEl.scrollTop - nextTop) <= scrollCompleteThreshold) {
      clearQuickLinkSettleTimer();
      quickLinkTransitionRef.current = {
        phase: 'owned',
        targetId: item.id,
        lockWhileVisible,
        anchorScrollTop: scrollEl.scrollTop,
      };
      setLoggedActiveItemId(item.id, 'desktop-click-immediate-arrival');
      return;
    }

    clearQuickLinkSettleTimer();
    quickLinkTransitionRef.current = {
      phase: 'scrolling',
      originId,
      targetId: item.id,
      lockWhileVisible,
    };
    setLoggedActiveItemId(originId, 'desktop-click-origin-hold', { targetId: item.id });
    if (!options?.skipScroll) {
      scrollEl.scrollTo({ top: nextTop, behavior: 'smooth' });
    }
  }, [activeItemId, clearQuickLinkSettleTimer, clearQuickLinkTransition, emitQuickLinkDebug, getItemTop, isDesktopRailEnabled, items, scrollCompleteThreshold, scrollContainerRef, scrollOffset, setLoggedActiveItemId]);

  return {
    activeItemId,
    quickLinksOpen,
    openQuickLinks,
    closeQuickLinks,
    handleQuickLinkSelect,
    registerSectionRef,
  };
}