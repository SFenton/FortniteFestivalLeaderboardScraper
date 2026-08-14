import { useCallback, useEffect, useLayoutEffect, useRef } from 'react';
import { useLocation } from 'react-router-dom';
import { IS_PAGE_RELOAD } from '@festival/ui-utils';
import { useScrollContainer } from '../../contexts/ScrollContainerContext';
import { markCurrentSuggestionsScrollRestorable } from '../../pages/suggestions/suggestionsSessionCache';
import { normalizeRoutePathname, RoutePatterns, Routes } from '../../routes';
import type { PreserveShellScrollState } from '../../utils/quietNavigation';

const consumedPreserveShellScrollKeys = new Set<string>();

type SuggestionsScrollModule = {
  beginSuggestionsScrollRestoration: (
    scrollElement: HTMLElement,
  ) => () => void;
};

export default function ShellScrollRestoration({
  layoutKey,
  loadSuggestionsPage,
}: {
  layoutKey: 'standard' | 'wide';
  loadSuggestionsPage: () => Promise<SuggestionsScrollModule>;
}) {
  const location = useLocation();
  const { key: locationKey, pathname } = location;
  const routePathname = normalizeRoutePathname(pathname);
  const preserveShellScrollKey = (
    location.state as PreserveShellScrollState | null
  )?.preserveShellScrollKey;
  const scrollContainerRef = useScrollContainer();
  const previousLayoutKeyRef = useRef(layoutKey);
  const previousPathnameRef = useRef(routePathname);
  const suggestionsRestoreCleanupRef = useRef<(() => void) | null>(null);
  const suggestionsRestoreFrameRef = useRef(0);
  const suggestionsRestoreRequestRef = useRef(0);

  const stopSuggestionsRestoration = useCallback(() => {
    suggestionsRestoreRequestRef.current += 1;
    cancelAnimationFrame(suggestionsRestoreFrameRef.current);
    suggestionsRestoreFrameRef.current = 0;
    suggestionsRestoreCleanupRef.current?.();
    suggestionsRestoreCleanupRef.current = null;
  }, []);

  const startSuggestionsRestoration = useCallback(() => {
    stopSuggestionsRestoration();
    const request = suggestionsRestoreRequestRef.current;
    void loadSuggestionsPage().then(({ beginSuggestionsScrollRestoration }) => {
      if (request !== suggestionsRestoreRequestRef.current) return;
      const scrollElement = scrollContainerRef.current;
      if (scrollElement) {
        suggestionsRestoreCleanupRef.current =
          beginSuggestionsScrollRestoration(scrollElement);
        return;
      }
      suggestionsRestoreFrameRef.current = requestAnimationFrame(() => {
        suggestionsRestoreFrameRef.current = 0;
        if (request !== suggestionsRestoreRequestRef.current) return;
        const nextScrollElement = scrollContainerRef.current;
        if (nextScrollElement) {
          suggestionsRestoreCleanupRef.current =
            beginSuggestionsScrollRestoration(nextScrollElement);
        }
      });
    });
  }, [
    loadSuggestionsPage,
    scrollContainerRef,
    stopSuggestionsRestoration,
  ]);

  useEffect(() => {
    if ('scrollRestoration' in history) {
      history.scrollRestoration = 'manual';
    }
  }, []);

  useLayoutEffect(() => {
    const layoutChanged = previousLayoutKeyRef.current !== layoutKey;
    previousLayoutKeyRef.current = layoutKey;
    if (layoutChanged && routePathname === Routes.suggestions) {
      markCurrentSuggestionsScrollRestorable();
    }
  }, [layoutKey, routePathname]);

  useLayoutEffect(() => {
    const previousPathname = previousPathnameRef.current;
    previousPathnameRef.current = routePathname;
    if (
      previousPathname === Routes.suggestions
      && routePathname !== Routes.suggestions
    ) {
      markCurrentSuggestionsScrollRestorable();
    }
  }, [routePathname]);

  useEffect(() => {
    if (
      preserveShellScrollKey
      && !consumedPreserveShellScrollKeys.has(preserveShellScrollKey)
    ) {
      consumedPreserveShellScrollKeys.add(preserveShellScrollKey);
      return;
    }
    if (routePathname === Routes.suggestions) return;
    if (!IS_PAGE_RELOAD) {
      if (routePathname === Routes.songs) return;
      if (RoutePatterns.songDetail.test(routePathname)) return;
    }
    scrollContainerRef.current?.scrollTo(0, 0);
  }, [preserveShellScrollKey, routePathname, scrollContainerRef]);

  useLayoutEffect(() => {
    if (routePathname === Routes.suggestions && !preserveShellScrollKey) {
      startSuggestionsRestoration();
    } else {
      stopSuggestionsRestoration();
    }
  }, [
    layoutKey,
    locationKey,
    preserveShellScrollKey,
    routePathname,
    startSuggestionsRestoration,
    stopSuggestionsRestoration,
  ]);

  return null;
}
