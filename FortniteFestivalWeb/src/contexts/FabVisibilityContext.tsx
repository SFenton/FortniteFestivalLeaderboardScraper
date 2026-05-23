import { createContext, useContext, type ReactNode } from 'react';

/**
 * Mobile FAB visibility gate.
 *
 * Shell-level state that should hide every mobile FAB (e.g. mobile notifications
 * drawer is open, or we're not in mobile chrome) is broadcast through this
 * context so App-owned and page-owned FAB wrappers share the same visibility gate.
 */
const FabVisibilityContext = createContext<{ mobileFabHidden: boolean }>({ mobileFabHidden: false });

export function FabVisibilityProvider({ mobileFabHidden, children }: { mobileFabHidden: boolean; children: ReactNode }) {
  return (
    <FabVisibilityContext.Provider value={{ mobileFabHidden }}>
      {children}
    </FabVisibilityContext.Provider>
  );
}

export function useFabVisibility() {
  return useContext(FabVisibilityContext);
}
