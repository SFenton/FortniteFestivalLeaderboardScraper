import { createContext, useContext, useState, useCallback, useRef, useMemo, type ReactNode } from 'react';

type FabSearchContextType = {
  registerActions: (actions: { openSort: () => void; openFilter: () => void }) => void;
  openSort: () => void;
  openFilter: () => void;
  registerSuggestionsActions: (actions: { openFilter: () => void }) => void;
  openSuggestionsFilter: () => void;
  registerPlayerHistoryActions: (actions: { openSort: () => void }) => void;
  openPlayerHistorySort: () => void;
  registerSongDetailActions: (actions: { openPaths: () => void }) => void;
  openPaths: () => void;
  registerShopActions: (actions: { toggleView: () => void }) => void;
  shopToggleView: () => void;
  shopViewMode: 'grid' | 'list';
  setShopViewMode: (mode: 'grid' | 'list') => void;
  registerLeaderboardActions: (actions: { openMetric: () => void }) => void;
  openLeaderboardMetric: () => void;
  registerRivalsActions: (actions: { toggleTab: () => void }) => void;
  rivalsToggleTab: () => void;
  rivalsActiveTab: 'song' | 'leaderboard';
  setRivalsActiveTab: (tab: 'song' | 'leaderboard') => void;
  registerPlayerPageSelect: (action: { displayName: string; onSelect: () => void } | null) => void;
  playerPageSelect: { displayName: string; onSelect: () => void } | null;
};

const FabSearchContext = createContext<FabSearchContextType>({
  registerActions: () => {}, openSort: () => {}, openFilter: () => {},
  registerSuggestionsActions: () => {}, openSuggestionsFilter: () => {},
  registerPlayerHistoryActions: () => {}, openPlayerHistorySort: () => {},
  registerSongDetailActions: () => {}, openPaths: () => {},
  registerShopActions: () => {}, shopToggleView: () => {}, shopViewMode: 'grid', setShopViewMode: () => {},
  registerLeaderboardActions: () => {}, openLeaderboardMetric: () => {},
  registerRivalsActions: () => {}, rivalsToggleTab: () => {}, rivalsActiveTab: 'song', setRivalsActiveTab: () => {},
  registerPlayerPageSelect: () => {}, playerPageSelect: null,
});

export function FabSearchProvider({ children }: { children: ReactNode }) {
  const actionsRef = useRef<{ openSort: () => void; openFilter: () => void }>({ openSort: () => {}, openFilter: () => {} });
  const suggestionsActionsRef = useRef<{ openFilter: () => void }>({ openFilter: () => {} });
  const playerHistoryActionsRef = useRef<{ openSort: () => void }>({ openSort: () => {} });
  const songDetailActionsRef = useRef<{ openPaths: () => void }>({ openPaths: () => {} });
  const shopActionsRef = useRef<{ toggleView: () => void }>({ toggleView: () => {} });
  const leaderboardActionsRef = useRef<{ openMetric: () => void }>({ openMetric: () => {} });
  const rivalsActionsRef = useRef<{ toggleTab: () => void }>({ toggleTab: () => {} });

  const registerActions = useCallback((actions: { openSort: () => void; openFilter: () => void }) => {
    actionsRef.current = actions;
  }, []);

  const registerSuggestionsActions = useCallback((actions: { openFilter: () => void }) => {
    suggestionsActionsRef.current = actions;
  }, []);

  const registerPlayerHistoryActions = useCallback((actions: { openSort: () => void }) => {
    playerHistoryActionsRef.current = actions;
  }, []);

  const registerSongDetailActions = useCallback((actions: { openPaths: () => void }) => {
    songDetailActionsRef.current = actions;
  }, []);

  const registerShopActions = useCallback((actions: { toggleView: () => void }) => {
    shopActionsRef.current = actions;
  }, []);

  const registerLeaderboardActions = useCallback((actions: { openMetric: () => void }) => {
    leaderboardActionsRef.current = actions;
  }, []);

  const registerRivalsActions = useCallback((actions: { toggleTab: () => void }) => {
    rivalsActionsRef.current = actions;
  }, []);

  const openSort = useCallback(() => actionsRef.current.openSort(), []);
  const openFilter = useCallback(() => actionsRef.current.openFilter(), []);
  const openSuggestionsFilter = useCallback(() => suggestionsActionsRef.current.openFilter(), []);
  const openPlayerHistorySort = useCallback(() => playerHistoryActionsRef.current.openSort(), []);
  const openPaths = useCallback(() => songDetailActionsRef.current.openPaths(), []);
  const shopToggleView = useCallback(() => shopActionsRef.current.toggleView(), []);
  const openLeaderboardMetric = useCallback(() => leaderboardActionsRef.current.openMetric(), []);
  const rivalsToggleTab = useCallback(() => rivalsActionsRef.current.toggleTab(), []);

  const [shopViewMode, setShopViewMode] = useState<'grid' | 'list'>('grid');
  const [rivalsActiveTab, setRivalsActiveTab] = useState<'song' | 'leaderboard'>('song');

  const [playerPageSelect, setPlayerPageSelect] = useState<{ displayName: string; onSelect: () => void } | null>(null);
  const registerPlayerPageSelect = useCallback((action: { displayName: string; onSelect: () => void } | null) => {
    setPlayerPageSelect(action);
  }, []);

  const value = useMemo<FabSearchContextType>(() => ({
    registerActions, openSort, openFilter,
    registerSuggestionsActions, openSuggestionsFilter,
    registerPlayerHistoryActions, openPlayerHistorySort,
    registerSongDetailActions, openPaths,
    registerShopActions, shopToggleView, shopViewMode, setShopViewMode,
    registerLeaderboardActions, openLeaderboardMetric,
    registerRivalsActions, rivalsToggleTab, rivalsActiveTab, setRivalsActiveTab,
    registerPlayerPageSelect, playerPageSelect,
  }), [registerActions, openSort, openFilter,
    registerSuggestionsActions, openSuggestionsFilter,
    registerPlayerHistoryActions, openPlayerHistorySort,
    registerSongDetailActions, openPaths,
    registerShopActions, shopToggleView, shopViewMode, setShopViewMode,
    registerLeaderboardActions, openLeaderboardMetric,
    registerRivalsActions, rivalsToggleTab, rivalsActiveTab, setRivalsActiveTab,
    registerPlayerPageSelect, playerPageSelect]);

  return (
    <FabSearchContext.Provider value={value}>
      {children}
    </FabSearchContext.Provider>
  );
}

export function useFabSearch() {
  return useContext(FabSearchContext);
}

export function usePlayerPageSelect() {
  const { playerPageSelect, registerPlayerPageSelect } = useContext(FabSearchContext);
  return { playerPageSelect, registerPlayerPageSelect };
}
