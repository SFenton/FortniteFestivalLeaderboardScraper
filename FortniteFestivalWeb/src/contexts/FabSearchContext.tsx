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
  registerLeaderboardActions: (actions: { openMetric: () => void; openInstrument: () => void }) => void;
  openLeaderboardMetric: () => void;
  openLeaderboardInstrument: () => void;
  registerRivalsActions: (actions: { toggleTab: () => void }) => void;
  rivalsToggleTab: () => void;
  rivalsActiveTab: 'song' | 'leaderboard';
  setRivalsActiveTab: (tab: 'song' | 'leaderboard') => void;
  registerBandActions: (actions: { openFilter: () => void }) => void;
  openBandFilter: () => void;
  registerPlayerQuickLinks: (action: { openQuickLinks: () => void } | null) => void;
  openPlayerQuickLinks: () => void;
  hasPlayerQuickLinks: boolean;
  registerPlayerPageSelect: (action: { displayName: string; onSelect: () => void } | null) => void;
  playerPageSelect: { displayName: string; onSelect: () => void } | null;
};

const FabSearchContext = createContext<FabSearchContextType>({
  registerActions: () => {}, openSort: () => {}, openFilter: () => {},
  registerSuggestionsActions: () => {}, openSuggestionsFilter: () => {},
  registerPlayerHistoryActions: () => {}, openPlayerHistorySort: () => {},
  registerSongDetailActions: () => {}, openPaths: () => {},
  registerShopActions: () => {}, shopToggleView: () => {}, shopViewMode: 'grid', setShopViewMode: () => {},
  registerLeaderboardActions: () => {}, openLeaderboardMetric: () => {}, openLeaderboardInstrument: () => {},
  registerRivalsActions: () => {}, rivalsToggleTab: () => {}, rivalsActiveTab: 'song', setRivalsActiveTab: () => {},
  registerBandActions: () => {}, openBandFilter: () => {},
  registerPlayerQuickLinks: () => {}, openPlayerQuickLinks: () => {}, hasPlayerQuickLinks: false,
  registerPlayerPageSelect: () => {}, playerPageSelect: null,
});

export function FabSearchProvider({ children }: { children: ReactNode }) {
  const actionsRef = useRef<{ openSort: () => void; openFilter: () => void }>({ openSort: () => {}, openFilter: () => {} });
  const suggestionsActionsRef = useRef<{ openFilter: () => void }>({ openFilter: () => {} });
  const playerHistoryActionsRef = useRef<{ openSort: () => void }>({ openSort: () => {} });
  const songDetailActionsRef = useRef<{ openPaths: () => void }>({ openPaths: () => {} });
  const shopActionsRef = useRef<{ toggleView: () => void }>({ toggleView: () => {} });
  const leaderboardActionsRef = useRef<{ openMetric: () => void; openInstrument: () => void }>({ openMetric: () => {}, openInstrument: () => {} });
  const rivalsActionsRef = useRef<{ toggleTab: () => void }>({ toggleTab: () => {} });
  const bandActionsRef = useRef<{ openFilter: () => void }>({ openFilter: () => {} });
  const playerQuickLinksRef = useRef<() => void>(() => {});

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

  const registerLeaderboardActions = useCallback((actions: { openMetric: () => void; openInstrument: () => void }) => {
    leaderboardActionsRef.current = actions;
  }, []);

  const registerRivalsActions = useCallback((actions: { toggleTab: () => void }) => {
    rivalsActionsRef.current = actions;
  }, []);

  const registerBandActions = useCallback((actions: { openFilter: () => void }) => {
    bandActionsRef.current = actions;
  }, []);

  const [hasPlayerQuickLinks, setHasPlayerQuickLinks] = useState(false);
  const registerPlayerQuickLinks = useCallback((action: { openQuickLinks: () => void } | null) => {
    playerQuickLinksRef.current = action?.openQuickLinks ?? (() => {});
    setHasPlayerQuickLinks(!!action);
  }, []);

  const openSort = useCallback(() => actionsRef.current.openSort(), []);
  const openFilter = useCallback(() => actionsRef.current.openFilter(), []);
  const openSuggestionsFilter = useCallback(() => suggestionsActionsRef.current.openFilter(), []);
  const openPlayerHistorySort = useCallback(() => playerHistoryActionsRef.current.openSort(), []);
  const openPaths = useCallback(() => songDetailActionsRef.current.openPaths(), []);
  const shopToggleView = useCallback(() => shopActionsRef.current.toggleView(), []);
  const openLeaderboardMetric = useCallback(() => leaderboardActionsRef.current.openMetric(), []);
  const openLeaderboardInstrument = useCallback(() => leaderboardActionsRef.current.openInstrument(), []);
  const rivalsToggleTab = useCallback(() => rivalsActionsRef.current.toggleTab(), []);
  const openBandFilter = useCallback(() => bandActionsRef.current.openFilter(), []);
  const openPlayerQuickLinks = useCallback(() => playerQuickLinksRef.current(), []);

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
    registerLeaderboardActions, openLeaderboardMetric, openLeaderboardInstrument,
    registerRivalsActions, rivalsToggleTab, rivalsActiveTab, setRivalsActiveTab,
    registerBandActions, openBandFilter,
    registerPlayerQuickLinks, openPlayerQuickLinks, hasPlayerQuickLinks,
    registerPlayerPageSelect, playerPageSelect,
  }), [registerActions, openSort, openFilter,
    registerSuggestionsActions, openSuggestionsFilter,
    registerPlayerHistoryActions, openPlayerHistorySort,
    registerSongDetailActions, openPaths,
    registerShopActions, shopToggleView, shopViewMode, setShopViewMode,
    registerLeaderboardActions, openLeaderboardMetric, openLeaderboardInstrument,
    registerRivalsActions, rivalsToggleTab, rivalsActiveTab, setRivalsActiveTab,
    registerBandActions, openBandFilter,
    registerPlayerQuickLinks, openPlayerQuickLinks, hasPlayerQuickLinks,
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
