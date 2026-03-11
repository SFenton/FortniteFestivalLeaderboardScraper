import { createContext, useContext, useState, useCallback, useRef, type ReactNode } from 'react';

type FabSearchContextType = {
  query: string;
  setQuery: (q: string) => void;
  registerActions: (actions: { openSort: () => void; openFilter: () => void }) => void;
  openSort: () => void;
  openFilter: () => void;
  registerSuggestionsActions: (actions: { openFilter: () => void }) => void;
  openSuggestionsFilter: () => void;
};

const FabSearchContext = createContext<FabSearchContextType>({
  query: '', setQuery: () => {},
  registerActions: () => {}, openSort: () => {}, openFilter: () => {},
  registerSuggestionsActions: () => {}, openSuggestionsFilter: () => {},
});

export function FabSearchProvider({ children }: { children: ReactNode }) {
  const [query, setQueryState] = useState('');
  const setQuery = useCallback((q: string) => setQueryState(q), []);
  const actionsRef = useRef<{ openSort: () => void; openFilter: () => void }>({ openSort: () => {}, openFilter: () => {} });
  const suggestionsActionsRef = useRef<{ openFilter: () => void }>({ openFilter: () => {} });

  const registerActions = useCallback((actions: { openSort: () => void; openFilter: () => void }) => {
    actionsRef.current = actions;
  }, []);

  const registerSuggestionsActions = useCallback((actions: { openFilter: () => void }) => {
    suggestionsActionsRef.current = actions;
  }, []);

  const openSort = useCallback(() => actionsRef.current.openSort(), []);
  const openFilter = useCallback(() => actionsRef.current.openFilter(), []);
  const openSuggestionsFilter = useCallback(() => suggestionsActionsRef.current.openFilter(), []);

  return (
    <FabSearchContext.Provider value={{ query, setQuery, registerActions, openSort, openFilter, registerSuggestionsActions, openSuggestionsFilter }}>
      {children}
    </FabSearchContext.Provider>
  );
}

export function useFabSearch() {
  return useContext(FabSearchContext);
}
