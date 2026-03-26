import { createContext, useContext, useRef, type ReactNode, type RefObject } from 'react';

const ScrollContainerContext = createContext<RefObject<HTMLDivElement | null>>({ current: null });

export function ScrollContainerProvider({ children }: { children: ReactNode }) {
  const ref = useRef<HTMLDivElement>(null);
  return (
    <ScrollContainerContext.Provider value={ref}>
      {children}
    </ScrollContainerContext.Provider>
  );
}

/** Returns the ref to the scroll container element that wraps all page content. */
export function useScrollContainer(): RefObject<HTMLDivElement | null> {
  return useContext(ScrollContainerContext);
}
