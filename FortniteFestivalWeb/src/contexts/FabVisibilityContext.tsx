import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from 'react';

/**
 * Mobile FAB visibility gate.
 *
 * Shell-level state that should hide every mobile FAB (e.g. mobile notifications
 * drawer is open, or we're not in mobile chrome) is broadcast through this
 * context so App-owned and page-owned FAB wrappers share the same visibility gate.
 * Rendered FAB surfaces also register here so pages can reserve bottom clearance
 * only while a fixed mobile surface actually exists.
 */
type FabVisibilityContextValue = {
  mobileFabHidden: boolean;
  hasMobileFabSurface: boolean;
  setMobileFabSurfacePresence: (id: string, present: boolean) => void;
};

const FabVisibilityContext = createContext<FabVisibilityContextValue>({
  mobileFabHidden: false,
  hasMobileFabSurface: false,
  setMobileFabSurfacePresence: () => {},
});

export function FabVisibilityProvider({ mobileFabHidden, children }: { mobileFabHidden: boolean; children: ReactNode }) {
  const [mobileFabSurfaceIds, setMobileFabSurfaceIds] = useState<Set<string>>(() => new Set());
  const setMobileFabSurfacePresence = useCallback((id: string, present: boolean) => {
    setMobileFabSurfaceIds(current => {
      if (current.has(id) === present) return current;
      const next = new Set(current);
      if (present) next.add(id);
      else next.delete(id);
      return next;
    });
  }, []);
  const value = useMemo(() => ({
    mobileFabHidden,
    hasMobileFabSurface: mobileFabSurfaceIds.size > 0,
    setMobileFabSurfacePresence,
  }), [mobileFabHidden, mobileFabSurfaceIds, setMobileFabSurfacePresence]);

  return (
    <FabVisibilityContext.Provider value={value}>
      {children}
    </FabVisibilityContext.Provider>
  );
}

export function useFabVisibility() {
  return useContext(FabVisibilityContext);
}
