import { createContext, useContext, useState, useCallback, useRef, useMemo, type ReactNode } from 'react';

type SongsFabActions = {
  openSort: () => void;
  openFilter: () => void;
  sortActive?: boolean;
  filterActive?: boolean;
};

type SuggestionsFabActions = {
  openFilter: () => void;
  filterActive?: boolean;
};

type FabSearchContextType = {
  registerActions: (actions: SongsFabActions | null) => void;
  openSort: () => void;
  openFilter: () => void;
  songsActionsReady: boolean;
  songsSortActive: boolean;
  songsFilterActive: boolean;
  registerSuggestionsActions: (actions: SuggestionsFabActions | null) => void;
  openSuggestionsFilter: () => void;
  suggestionsActionsReady: boolean;
  suggestionsFilterActive: boolean;
  registerPlayerHistoryActions: (actions: { openSort: () => void } | null) => void;
  openPlayerHistorySort: () => void;
  playerHistoryActionsReady: boolean;
  registerSongDetailActions: (actions: { openPaths: () => void } | null) => void;
  openPaths: () => void;
  songDetailActionsReady: boolean;
  registerShopActions: (actions: { toggleView: () => void } | null) => void;
  shopToggleView: () => void;
  shopActionsReady: boolean;
  shopViewMode: 'grid' | 'list';
  setShopViewMode: (mode: 'grid' | 'list') => void;
  registerLeaderboardActions: (actions: { openMetric?: () => void; openInstrument?: () => void } | null) => void;
  openLeaderboardMetric: () => void;
  openLeaderboardInstrument: () => void;
  leaderboardMetricReady: boolean;
  leaderboardInstrumentReady: boolean;
  registerRivalsActions: (actions: { toggleTab?: () => void; findRival?: () => void } | null) => void;
  rivalsToggleTab: () => void;
  rivalsFindRival: () => void;
  rivalsToggleTabReady: boolean;
  rivalsFindRivalReady: boolean;
  rivalsActiveTab: 'song' | 'leaderboard';
  setRivalsActiveTab: (tab: 'song' | 'leaderboard') => void;
  registerBandActions: (actions: { openFilter: () => void } | null) => void;
  openBandFilter: () => void;
  bandActionsReady: boolean;
  registerPlayerQuickLinks: (action: { openQuickLinks: () => void } | null) => void;
  openPlayerQuickLinks: () => void;
  hasPlayerQuickLinks: boolean;
  registerPlayerPageSelect: (action: { displayName: string; onSelect: () => void } | null) => void;
  playerPageSelect: { displayName: string; onSelect: () => void } | null;
  registerBandPageSelect: (action: { onSelect: () => void } | null) => void;
  bandPageSelect: { onSelect: () => void } | null;
};

const FabSearchContext = createContext<FabSearchContextType>({
  registerActions: () => {}, openSort: () => {}, openFilter: () => {}, songsActionsReady: false, songsSortActive: false, songsFilterActive: false,
  registerSuggestionsActions: () => {}, openSuggestionsFilter: () => {}, suggestionsActionsReady: false, suggestionsFilterActive: false,
  registerPlayerHistoryActions: () => {}, openPlayerHistorySort: () => {}, playerHistoryActionsReady: false,
  registerSongDetailActions: () => {}, openPaths: () => {}, songDetailActionsReady: false,
  registerShopActions: () => {}, shopToggleView: () => {}, shopActionsReady: false, shopViewMode: 'grid', setShopViewMode: () => {},
  registerLeaderboardActions: () => {}, openLeaderboardMetric: () => {}, openLeaderboardInstrument: () => {}, leaderboardMetricReady: false, leaderboardInstrumentReady: false,
  registerRivalsActions: () => {}, rivalsToggleTab: () => {}, rivalsFindRival: () => {}, rivalsToggleTabReady: false, rivalsFindRivalReady: false, rivalsActiveTab: 'song', setRivalsActiveTab: () => {},
  registerBandActions: () => {}, openBandFilter: () => {}, bandActionsReady: false,
  registerPlayerQuickLinks: () => {}, openPlayerQuickLinks: () => {}, hasPlayerQuickLinks: false,
  registerPlayerPageSelect: () => {}, playerPageSelect: null,
  registerBandPageSelect: () => {}, bandPageSelect: null,
});

const noop = () => {};
const defaultSongsActions: SongsFabActions = { openSort: noop, openFilter: noop, sortActive: false, filterActive: false };
const defaultSuggestionsActions: SuggestionsFabActions = { openFilter: noop, filterActive: false };
const defaultPlayerHistoryActions = { openSort: noop };
const defaultSongDetailActions = { openPaths: noop };
const defaultShopActions = { toggleView: noop };
const defaultLeaderboardActions = { openMetric: noop, openInstrument: noop };
const defaultRivalsActions = { toggleTab: noop, findRival: noop };
const defaultBandActions = { openFilter: noop };

export function FabSearchProvider({ children }: { children: ReactNode }) {
  const actionsRef = useRef<SongsFabActions>(defaultSongsActions);
  const [songsActionState, setSongsActionState] = useState({ ready: false, sortActive: false, filterActive: false });
  const suggestionsActionsRef = useRef<SuggestionsFabActions>(defaultSuggestionsActions);
  const [suggestionsActionState, setSuggestionsActionState] = useState({ ready: false, filterActive: false });
  const playerHistoryActionsRef = useRef<{ openSort: () => void }>(defaultPlayerHistoryActions);
  const [playerHistoryActionsReady, setPlayerHistoryActionsReady] = useState(false);
  const songDetailActionsRef = useRef<{ openPaths: () => void }>(defaultSongDetailActions);
  const [songDetailActionsReady, setSongDetailActionsReady] = useState(false);
  const shopActionsRef = useRef<{ toggleView: () => void }>(defaultShopActions);
  const [shopActionsReady, setShopActionsReady] = useState(false);
  const leaderboardActionsRef = useRef<{ openMetric: () => void; openInstrument: () => void }>(defaultLeaderboardActions);
  const [leaderboardActionState, setLeaderboardActionState] = useState({ metricReady: false, instrumentReady: false });
  const rivalsActionsRef = useRef<{ toggleTab: () => void; findRival: () => void }>(defaultRivalsActions);
  const [rivalsActionState, setRivalsActionState] = useState({ toggleTabReady: false, findRivalReady: false });
  const bandActionsRef = useRef<{ openFilter: () => void }>(defaultBandActions);
  const [bandActionsReady, setBandActionsReady] = useState(false);
  const playerQuickLinksRef = useRef<() => void>(noop);

  const registerActions = useCallback((actions: SongsFabActions | null) => {
    actionsRef.current = actions ?? defaultSongsActions;
    const nextState = { ready: !!actions, sortActive: !!actions?.sortActive, filterActive: !!actions?.filterActive };
    setSongsActionState(previous => (
      previous.ready === nextState.ready && previous.sortActive === nextState.sortActive && previous.filterActive === nextState.filterActive
        ? previous
        : nextState
    ));
  }, []);

  const registerSuggestionsActions = useCallback((actions: SuggestionsFabActions | null) => {
    suggestionsActionsRef.current = actions ?? defaultSuggestionsActions;
    const nextState = { ready: !!actions, filterActive: !!actions?.filterActive };
    setSuggestionsActionState(previous => (
      previous.ready === nextState.ready && previous.filterActive === nextState.filterActive
        ? previous
        : nextState
    ));
  }, []);

  const registerPlayerHistoryActions = useCallback((actions: { openSort: () => void } | null) => {
    playerHistoryActionsRef.current = actions ?? defaultPlayerHistoryActions;
    setPlayerHistoryActionsReady(!!actions);
  }, []);

  const registerSongDetailActions = useCallback((actions: { openPaths: () => void } | null) => {
    songDetailActionsRef.current = actions ?? defaultSongDetailActions;
    setSongDetailActionsReady(!!actions);
  }, []);

  const registerShopActions = useCallback((actions: { toggleView: () => void } | null) => {
    shopActionsRef.current = actions ?? defaultShopActions;
    setShopActionsReady(!!actions);
  }, []);

  const registerLeaderboardActions = useCallback((actions: { openMetric?: () => void; openInstrument?: () => void } | null) => {
    leaderboardActionsRef.current = {
      openMetric: actions?.openMetric ?? noop,
      openInstrument: actions?.openInstrument ?? noop,
    };
    const nextState = { metricReady: !!actions?.openMetric, instrumentReady: !!actions?.openInstrument };
    setLeaderboardActionState(previous => (
      previous.metricReady === nextState.metricReady && previous.instrumentReady === nextState.instrumentReady
        ? previous
        : nextState
    ));
  }, []);

  const registerRivalsActions = useCallback((actions: { toggleTab?: () => void; findRival?: () => void } | null) => {
    rivalsActionsRef.current = {
      toggleTab: actions?.toggleTab ?? noop,
      findRival: actions?.findRival ?? noop,
    };
    const nextState = { toggleTabReady: !!actions?.toggleTab, findRivalReady: !!actions?.findRival };
    setRivalsActionState(previous => (
      previous.toggleTabReady === nextState.toggleTabReady && previous.findRivalReady === nextState.findRivalReady
        ? previous
        : nextState
    ));
  }, []);

  const registerBandActions = useCallback((actions: { openFilter: () => void } | null) => {
    bandActionsRef.current = actions ?? defaultBandActions;
    setBandActionsReady(!!actions);
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
  const rivalsFindRival = useCallback(() => rivalsActionsRef.current.findRival(), []);
  const openBandFilter = useCallback(() => bandActionsRef.current.openFilter(), []);
  const openPlayerQuickLinks = useCallback(() => playerQuickLinksRef.current(), []);

  const [shopViewMode, setShopViewMode] = useState<'grid' | 'list'>('grid');
  const [rivalsActiveTab, setRivalsActiveTab] = useState<'song' | 'leaderboard'>('song');

  const [playerPageSelect, setPlayerPageSelect] = useState<{ displayName: string; onSelect: () => void } | null>(null);
  const registerPlayerPageSelect = useCallback((action: { displayName: string; onSelect: () => void } | null) => {
    setPlayerPageSelect(action);
  }, []);

  const [bandPageSelect, setBandPageSelect] = useState<{ onSelect: () => void } | null>(null);
  const registerBandPageSelect = useCallback((action: { onSelect: () => void } | null) => {
    setBandPageSelect(action);
  }, []);

  const value = useMemo<FabSearchContextType>(() => ({
    registerActions, openSort, openFilter, songsActionsReady: songsActionState.ready, songsSortActive: songsActionState.sortActive, songsFilterActive: songsActionState.filterActive,
    registerSuggestionsActions, openSuggestionsFilter, suggestionsActionsReady: suggestionsActionState.ready, suggestionsFilterActive: suggestionsActionState.filterActive,
    registerPlayerHistoryActions, openPlayerHistorySort, playerHistoryActionsReady,
    registerSongDetailActions, openPaths, songDetailActionsReady,
    registerShopActions, shopToggleView, shopActionsReady, shopViewMode, setShopViewMode,
    registerLeaderboardActions, openLeaderboardMetric, openLeaderboardInstrument, leaderboardMetricReady: leaderboardActionState.metricReady, leaderboardInstrumentReady: leaderboardActionState.instrumentReady,
    registerRivalsActions, rivalsToggleTab, rivalsFindRival, rivalsToggleTabReady: rivalsActionState.toggleTabReady, rivalsFindRivalReady: rivalsActionState.findRivalReady, rivalsActiveTab, setRivalsActiveTab,
    registerBandActions, openBandFilter, bandActionsReady,
    registerPlayerQuickLinks, openPlayerQuickLinks, hasPlayerQuickLinks,
    registerPlayerPageSelect, playerPageSelect,
    registerBandPageSelect, bandPageSelect,
  }), [registerActions, openSort, openFilter, songsActionState.ready, songsActionState.sortActive, songsActionState.filterActive,
    registerSuggestionsActions, openSuggestionsFilter, suggestionsActionState.ready, suggestionsActionState.filterActive,
    registerPlayerHistoryActions, openPlayerHistorySort, playerHistoryActionsReady,
    registerSongDetailActions, openPaths, songDetailActionsReady,
    registerShopActions, shopToggleView, shopActionsReady, shopViewMode, setShopViewMode,
    registerLeaderboardActions, openLeaderboardMetric, openLeaderboardInstrument, leaderboardActionState.metricReady, leaderboardActionState.instrumentReady,
    registerRivalsActions, rivalsToggleTab, rivalsFindRival, rivalsActionState.toggleTabReady, rivalsActionState.findRivalReady, rivalsActiveTab, setRivalsActiveTab,
    registerBandActions, openBandFilter, bandActionsReady,
    registerPlayerQuickLinks, openPlayerQuickLinks, hasPlayerQuickLinks,
    registerPlayerPageSelect, playerPageSelect,
    registerBandPageSelect, bandPageSelect]);

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

export function useBandPageSelect() {
  const { bandPageSelect, registerBandPageSelect } = useContext(FabSearchContext);
  return { bandPageSelect, registerBandPageSelect };
}
