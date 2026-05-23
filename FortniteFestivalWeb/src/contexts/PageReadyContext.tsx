import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { useLocation } from 'react-router-dom';

/**
 * Page-content readiness signal.
 *
 * Pages publish `true` when their main content has reached `LoadPhase.ContentIn`
 * (or otherwise considers itself "in"). The shell reads this and AND's it into
 * the mobile FAB's `ready` prop so the FAB row reveals in lockstep with the
 * page's own staggered content. Pages that do not opt in default to `true`
 * so unmigrated routes behave as before.
 */
type PageReadyContextValue = {
  pageReady: boolean;
  setPageReady: (ready: boolean) => void;
};

const PageReadyContext = createContext<PageReadyContextValue>({
  pageReady: false,
  setPageReady: () => {},
});

export function PageReadyProvider({ children }: { children: ReactNode }) {
  const location = useLocation();
  const routeKey = `${location.pathname}\u001c${location.search}`;
  // Default to false so the FAB mounts hidden and plays its right-to-left
  // fade-up stagger when the active page broadcasts ready=true. Pages with no
  // load phase opt in via `useSetPageReady(true)` immediately on mount.
  const [pageReadyState, setPageReadyState] = useState({ routeKey, ready: false });
  const pageReady = pageReadyState.routeKey === routeKey ? pageReadyState.ready : false;
  const setPageReady = useCallback((ready: boolean) => {
    setPageReadyState(previous => (
      previous.routeKey === routeKey && previous.ready === ready
        ? previous
        : { routeKey, ready }
    ));
  }, [routeKey]);
  const value = useMemo(() => ({ pageReady, setPageReady }), [pageReady, setPageReady]);
  return (
    <PageReadyContext.Provider value={value}>
      {children}
    </PageReadyContext.Provider>
  );
}

/** Read the current page-ready flag. Used by `App.tsx` to gate the mobile FAB. */
export function usePageReady(): boolean {
  return useContext(PageReadyContext).pageReady;
}

/**
 * Publish this page's content-ready state. Pass `loadPhase === LoadPhase.ContentIn`
 * (or any equivalent signal). Pages without a load phase should call
 * `useSetPageReady(true)` so the FAB reveals immediately on mount. When the
 * page unmounts the flag resets to `false` so the next route starts in a
 * known unready state and the new page's `useSetPageReady` re-broadcasts.
 */
export function useSetPageReady(ready: boolean): void {
  const { setPageReady } = useContext(PageReadyContext);
  useEffect(() => {
    setPageReady(ready);
  }, [ready, setPageReady]);
  useEffect(() => {
    return () => setPageReady(false);
  }, [setPageReady]);
}
