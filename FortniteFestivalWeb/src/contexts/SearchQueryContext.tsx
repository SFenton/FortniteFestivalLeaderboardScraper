/**
 * Search query context — only consumed by components that display/edit the query.
 * Separated from FabSearchContext so page action registrations don't re-render on every keystroke.
 */
import { createContext, useContext, useState, useCallback, useMemo, type ReactNode } from 'react';

type SearchQueryContextType = {
  query: string;
  setQuery: (q: string) => void;
};

const SearchQueryContext = createContext<SearchQueryContextType>({
  query: '', setQuery: () => {},
});

export function SearchQueryProvider({ children }: { children: ReactNode }) {
  const [query, setQueryState] = useState('');
  const setQuery = useCallback((q: string) => setQueryState(q), []);

  const value = useMemo(() => ({ query, setQuery }), [query, setQuery]);

  return (
    <SearchQueryContext.Provider value={value}>
      {children}
    </SearchQueryContext.Provider>
  );
}

export function useSearchQuery() {
  return useContext(SearchQueryContext);
}
