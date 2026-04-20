import { useCallback, useEffect, useRef, useState, type ReactNode, type RefObject } from 'react';

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
    if (!isDesktopRailEnabled || !scrollEl || items.length === 0) {
      clearQuickLinkTransition();
      setActiveItemId(null);
      return;
    }

    const syncActive = () => {
      const naturalActive = resolveActiveQuickLink(items, sectionRefs.current, scrollEl, scrollOffset, getItemTop);
      const transitionState = quickLinkTransitionRef.current;
      if (!transitionState) {
        setActiveItemId(naturalActive);
        return;
      }

      const targetVisible = isQuickLinkItemVisible(transitionState.targetId, items, sectionRefs.current, scrollEl, getItemTop);
      const targetItemTop = resolveQuickLinkItemTop(transitionState.targetId, sectionRefs.current, scrollEl, getItemTop);

      if (transitionState.phase === 'scrolling') {
        if (targetItemTop == null) {
          clearQuickLinkTransition();
          setActiveItemId(naturalActive);
          return;
        }

        const targetTop = clampQuickLinkTargetTop(scrollEl, targetItemTop, scrollOffset);
        if (Math.abs(scrollEl.scrollTop - targetTop) <= scrollCompleteThreshold) {
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
            if (Math.abs(scrollEl.scrollTop - settledTargetTop) > scrollCompleteThreshold) {
              return;
            }

            quickLinkTransitionRef.current = {
              phase: 'owned',
              targetId: currentTransitionState.targetId,
              lockWhileVisible: currentTransitionState.lockWhileVisible,
              anchorScrollTop: scrollEl.scrollTop,
            };
            setActiveItemId(currentTransitionState.targetId);
          }, scrollSettleDelayMs);
        } else {
          clearQuickLinkSettleTimer();
        }

        const originStillExists = items.some((item) => item.id === transitionState.originId);
        setActiveItemId(originStillExists ? transitionState.originId : naturalActive);
        return;
      }

      if (transitionState.lockWhileVisible) {
        if (targetVisible) {
          setActiveItemId(transitionState.targetId);
          return;
        }

        clearQuickLinkTransition();
        setActiveItemId(naturalActive);
        return;
      }

      if (targetVisible && Math.abs(scrollEl.scrollTop - transitionState.anchorScrollTop) <= scrollCompleteThreshold) {
        setActiveItemId(transitionState.targetId);
        return;
      }

      if (targetItemTop != null) {
        const targetRelativeTop = targetItemTop - scrollEl.scrollTop;
        if (targetRelativeTop >= -REACHABLE_TARGET_OWNERSHIP_PX && targetRelativeTop <= scrollOffset + REACHABLE_TARGET_OWNERSHIP_PX) {
          setActiveItemId(transitionState.targetId);
          return;
        }

        clearQuickLinkTransition();
        setActiveItemId(naturalActive);
        return;
      }

      clearQuickLinkTransition();
      setActiveItemId(naturalActive);
    };

    syncActive();
    scrollEl.addEventListener('scroll', syncActive, { passive: true });
    window.addEventListener('resize', syncActive);
    return () => {
      clearQuickLinkSettleTimer();
      scrollEl.removeEventListener('scroll', syncActive);
      window.removeEventListener('resize', syncActive);
    };
  }, [clearQuickLinkSettleTimer, clearQuickLinkTransition, getItemTop, isDesktopRailEnabled, items, scrollCompleteThreshold, scrollContainerRef, scrollOffset, scrollSettleDelayMs]);

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

    if (!isDesktopRailEnabled) {
      clearQuickLinkTransition();
      setActiveItemId(item.id);
      if (!options?.skipScroll) {
        scrollEl.scrollTo({ top: nextTop, behavior: 'smooth' });
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
      setActiveItemId(item.id);
      return;
    }

    clearQuickLinkSettleTimer();
    quickLinkTransitionRef.current = {
      phase: 'scrolling',
      originId,
      targetId: item.id,
      lockWhileVisible,
    };
    setActiveItemId(originId);
    if (!options?.skipScroll) {
      scrollEl.scrollTo({ top: nextTop, behavior: 'smooth' });
    }
  }, [activeItemId, clearQuickLinkSettleTimer, clearQuickLinkTransition, getItemTop, isDesktopRailEnabled, items, scrollCompleteThreshold, scrollContainerRef, scrollOffset]);

  return {
    activeItemId,
    quickLinksOpen,
    openQuickLinks,
    closeQuickLinks,
    handleQuickLinkSelect,
    registerSectionRef,
  };
}