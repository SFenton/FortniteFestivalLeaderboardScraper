import { createContext, useContext, useRef, useState, useCallback, type ReactNode, type RefObject } from 'react';

/* ── Scroll container ── */
const ScrollContainerContext = createContext<RefObject<HTMLDivElement | null>>({ current: null });

/** Returns the ref to the full-width scroll container element in the app shell. */
export function useScrollContainer(): RefObject<HTMLDivElement | null> {
  return useContext(ScrollContainerContext);
}

/* ── Page-header portal target ── */
interface HeaderPortalValue {
  node: HTMLDivElement | null;
  setNode: (el: HTMLDivElement | null) => void;
}
const HeaderPortalContext = createContext<HeaderPortalValue>({ node: null, setNode: () => {} });

/** Returns the portal target DOM node (or null before mount). */
export function useHeaderPortal(): HTMLDivElement | null {
  return useContext(HeaderPortalContext).node;
}

/** Returns a ref callback to assign to the portal target div. */
export function useHeaderPortalRef(): (el: HTMLDivElement | null) => void {
  return useContext(HeaderPortalContext).setNode;
}

/* ── Combined provider ── */
export function ScrollContainerProvider({ children }: { children: ReactNode }) {
  const scrollRef = useRef<HTMLDivElement>(null);
  const [portalNode, setPortalNode] = useState<HTMLDivElement | null>(null);
  const portalValue: HeaderPortalValue = { node: portalNode, setNode: setPortalNode };
  return (
    <ScrollContainerContext.Provider value={scrollRef}>
      <HeaderPortalContext.Provider value={portalValue}>
        {children}
      </HeaderPortalContext.Provider>
    </ScrollContainerContext.Provider>
  );
}

/**
 * Hook for App.tsx to get refs to assign to the DOM elements.
 */
export function useShellRefs() {
  const scrollRef = useScrollContainer();
  const setPortalNode = useHeaderPortalRef();
  // Stable ref callback for the portal target div
  const portalRefCallback = useCallback((el: HTMLDivElement | null) => {
    setPortalNode(el);
  }, [setPortalNode]);
  return { scrollRef, portalRefCallback };
}
