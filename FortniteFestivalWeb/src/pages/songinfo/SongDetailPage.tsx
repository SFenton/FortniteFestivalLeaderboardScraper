import { useEffect, useLayoutEffect, useState, useRef, useMemo, useCallback, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams, useSearchParams, useNavigationType, useLocation } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { IoPeople, IoStatsChart, IoTimerOutline } from 'react-icons/io5';
import { useFestival } from '../../contexts/FestivalContext';
import { useSelectedProfile, type SelectedProfile } from '../../hooks/data/useSelectedProfile';
import { useTrackedPlayer } from '../../hooks/data/useTrackedPlayer';
import { usePlayerData } from '../../contexts/PlayerDataContext';
import { api } from '../../api/client';
import {
  INSTRUMENT_KEYS,
  serverInstrumentLabel,
  serverSongSupportsInstrument,
  type PlayerBandType,
  type SelectedMemberSongScore,
  type ServerInstrumentKey as InstrumentKey,
} from '@festival/core/api';
import { Gap, Colors, Font, Layout, MaxWidth, Position, ZIndex, Display, Overflow, CssValue, PointerEvents, flexCenter, flexColumn, GridTemplate, SPINNER_FADE_MS, FADE_DURATION } from '@festival/theme';
import ArcSpinner from '../../components/common/ArcSpinner';
import Page, { PageBackground } from '../Page';
import type { PageQuickLinksConfig } from '../../components/page/PageQuickLinks';
import { useScrollContainer } from '../../contexts/ScrollContainerContext';
import SongInfoHeader from '../../components/songs/headers/SongInfoHeader';
import ScoreHistoryChart from './components/chart/ScoreHistoryChart';
import { useSettings, visibleInstruments, visiblePathInstruments } from '../../contexts/SettingsContext';
import { useIsMobile, useIsMobileChrome } from '../../hooks/ui/useIsMobile';
import { useFabSearch } from '../../contexts/FabSearchContext';
import { usePageQuickLinks, type PageQuickLinkItem } from '../../hooks/ui/usePageQuickLinks';
import { useAppliedBandComboFilter } from '../../contexts/BandFilterActionContext';
import { useStagger } from '../../hooks/ui/useStagger';
import { useScoreFilter } from '../../hooks/data/useScoreFilter';
import { useLoadPhase } from '../../hooks/data/useLoadPhase';
import { useSetPageReady } from '../../contexts/PageReadyContext';
import { useShopState } from '../../hooks/data/useShopState';
import { LoadPhase } from '@festival/core/runtime';
import PathsModal from './components/path/PathsModal';
import EmptyState from '../../components/common/EmptyState';
import CollapsePresence from '../../components/common/CollapsePresence';
import { parseApiError } from '../../utils/apiError';
import { InstrumentIcon } from '../../components/display/InstrumentIcons';
import InstrumentCard from './components/InstrumentCard';
import SongBandLeaderboardPreview from './components/SongBandLeaderboardPreview';
import IntensityCard from './components/IntensityCard';
import { songInfoSlides } from './firstRun';
import { SONG_BAND_TYPES, songBandTypeLabel } from '../../utils/songBandLeaderboards';

import { songDetailCache } from '../../api/pageCache';
import { queryKeys } from '../../api/queryKeys';
import {
  keepPreviousSongLeaderboards,
  remoteDataQueryPolicy,
} from '../../api/queryPolicy';
import { playerHistoryQueryOptions } from '../../api/remoteDataQueries';
import type { InstrumentData, SongBandData } from './songDetailTypes';
export { clearSongDetailCache } from '../../api/pageCache';

const QUICK_LINK_GLYPH_ICON_SIZE = 20;
const SONG_DETAIL_INTENSITY_QUICK_LINK_ID = 'intensity';
const SONG_DETAIL_SCORE_HISTORY_QUICK_LINK_ID = 'score-history';

type SongDetailQuickLink = PageQuickLinkItem;

function songDetailInstrumentQuickLinkId(instrument: InstrumentKey): string {
  return `instrument-${instrument}`;
}

function songDetailBandQuickLinkId(bandType: PlayerBandType): string {
  return `band-${bandType}`;
}

function createSongBandData(loading: boolean, error: string | null = null): Record<PlayerBandType, SongBandData> {
  const data = {} as Record<PlayerBandType, SongBandData>;
  for (const bandType of SONG_BAND_TYPES) {
    data[bandType] = { entries: [], loading, error };
  }
  return data;
}

function queryErrorMessage(error: unknown): string | null {
  if (!error) return null;
  return error instanceof Error ? error.message : 'Error';
}

type SelectedBandMember = {
  accountId: string;
  displayName: string;
};

function normalizeAccountId(accountId: string | null | undefined): string {
  return accountId?.trim().toLowerCase() ?? '';
}

function resolveSelectedBandMembers(profile: SelectedProfile | null): SelectedBandMember[] {
  if (profile?.type !== 'band') return [];

  const memberNames = new Map(profile.members.map(member => [normalizeAccountId(member.accountId), member.displayName]));
  const seen = new Set<string>();
  return profile.teamKey.split(':').flatMap(accountId => {
    const normalizedAccountId = normalizeAccountId(accountId);
    if (!normalizedAccountId || seen.has(normalizedAccountId)) return [];
    seen.add(normalizedAccountId);
    return [{
      accountId,
      displayName: memberNames.get(normalizedAccountId) || accountId.slice(0, 8),
    }];
  });
}

export default function SongDetailPage() {
  const { t } = useTranslation();
  const { songId } = useParams<{ songId: string }>();
  const [searchParams] = useSearchParams();
  const defaultInstrument = (searchParams.get('instrument') as InstrumentKey) || undefined;
  const [windowWidth, setWindowWidth] = useState(window.innerWidth);
  /* v8 ignore start — resize handler DOM event */
  useEffect(() => {
    let timer: ReturnType<typeof setTimeout>;
    const onResize = () => {
      clearTimeout(timer);
      timer = setTimeout(() => setWindowWidth(window.innerWidth), 150);
    };
    window.addEventListener('resize', onResize);
    return () => { clearTimeout(timer); window.removeEventListener('resize', onResize); };
  }, []);
  /* v8 ignore stop */
  const {
    state: { songs },
  } = useFestival();
  const { player } = useTrackedPlayer();
  const { profile } = useSelectedProfile();
  const selectedAccountId = player?.accountId;
  const selectedBandType = profile?.type === 'band' ? profile.bandType as PlayerBandType : undefined;
  const selectedTeamKey = profile?.type === 'band' ? profile.teamKey : undefined;
  const selectedBandMembers = useMemo(() => resolveSelectedBandMembers(profile), [profile]);
  const selectedBandMemberAccountIds = useMemo(() => selectedBandMembers.map(member => member.accountId), [selectedBandMembers]);
  const appliedBandComboFilter = useAppliedBandComboFilter();
  const activeBandComboId = appliedBandComboFilter && appliedBandComboFilter.bandType === selectedBandType ? appliedBandComboFilter.comboId : undefined;
  const { settings } = useSettings();
  const song = songs.find((s) => s.songId === songId);
  const configuredInstruments = visibleInstruments(settings);
  const activeInstruments = useMemo(
    () => song ? configuredInstruments.filter((instrument) => serverSongSupportsInstrument(song, instrument)) : configuredInstruments,
    [configuredInstruments, song],
  );
  const promotedSongBandType = useMemo(
    () => selectedBandType && SONG_BAND_TYPES.includes(selectedBandType) ? selectedBandType : undefined,
    [selectedBandType],
  );
  const trailingSongBandTypes = useMemo(
    () => promotedSongBandType ? SONG_BAND_TYPES.filter(bandType => bandType !== promotedSongBandType) : SONG_BAND_TYPES,
    [promotedSongBandType],
  );
  const resolvedDefaultInstrument = defaultInstrument && activeInstruments.includes(defaultInstrument) ? defaultInstrument : undefined;

  // First-run carousel
  const isMobile = useIsMobile();
  const isMobileChrome = useIsMobileChrome();
  const songInfoSlidesMemo = useMemo(() => songInfoSlides(isMobile), [isMobile]);
  const firstRunGateCtx = useMemo(() => ({ hasPlayer: !!player }), [player]);

  const activePathInstruments = visiblePathInstruments(settings);
  const canViewPaths = activePathInstruments.length > 0;
  const fabSearch = useFabSearch();
  const { filterPlayerScores, filterHistory: filterScoreHistory, leewayParam } = useScoreFilter();
  const [pathsOpen, setPathsOpen] = useState(false);
  const { isShopVisible, isShopHighlighted, isLeavingTomorrow, isShopNew, getShopUrl } = useShopState();

  // Player scores from precomputed context (already has minLeeway + validScores + rankTiers)
  const { playerData } = usePlayerData();
  const playerScores = useMemo(() => {
    if (!playerData || !songId) return [];
    return playerData.scores.filter(s => s.songId === songId);
  }, [playerData, songId]);

  const navType = useNavigationType();
  const location = useLocation();
  const cached = songId ? songDetailCache.get(songId) : undefined;
  const scoreHistoryQuery = useQuery({
    ...playerHistoryQueryOptions(selectedAccountId ?? '', songId ?? ''),
    enabled: !!selectedAccountId && !!songId,
  });
  const leaderboardsQuery = useQuery<Awaited<ReturnType<typeof api.getAllLeaderboards>>>({
    queryKey: queryKeys.allLeaderboards(songId ?? '', 10, leewayParam),
    queryFn: ({ signal }) => api.getAllLeaderboards(songId!, 10, leewayParam, { signal }),
    enabled: !!songId,
    placeholderData: keepPreviousSongLeaderboards(songId ?? ''),
    ...remoteDataQueryPolicy,
  });
  const selectedMemberScoresQuery = useQuery({
    queryKey: queryKeys.selectedMemberSongScores(
      songId ?? '',
      selectedBandMemberAccountIds,
      activeInstruments,
      leewayParam,
    ),
    queryFn: ({ signal }) => api.getSelectedMemberSongScores(
      songId!,
      [...selectedBandMemberAccountIds],
      [...activeInstruments],
      leewayParam,
      { signal },
    ),
    enabled: !!songId && selectedBandMemberAccountIds.length > 0 && activeInstruments.length > 0,
    ...remoteDataQueryPolicy,
  });
  const bandLeaderboardsQuery = useQuery({
    queryKey: queryKeys.allSongBandLeaderboards(
      songId ?? '',
      10,
      selectedAccountId,
      selectedBandType,
      selectedTeamKey,
      activeBandComboId,
    ),
    queryFn: ({ signal }) => api.getAllSongBandLeaderboards(
      songId!,
      10,
      selectedAccountId,
      selectedBandType,
      selectedTeamKey,
      activeBandComboId,
      { signal },
    ),
    enabled: !!songId,
    ...remoteDataQueryPolicy,
  });

  const scoreHistory = scoreHistoryQuery.data ?? [];
  const scoreHistoryReady = !selectedAccountId || !scoreHistoryQuery.isPending;
  const instrumentData = useMemo<Record<InstrumentKey, InstrumentData>>(() => {
    const error = queryErrorMessage(leaderboardsQuery.error);
    const nextData = Object.fromEntries(
      INSTRUMENT_KEYS.map((key) => [key, {
        entries: [],
        loading: leaderboardsQuery.isPending,
        error,
      }]),
    ) as unknown as Record<InstrumentKey, InstrumentData>;

    for (const instrument of leaderboardsQuery.data?.instruments ?? []) {
      const key = instrument.instrument as InstrumentKey;
      if (key in nextData) {
        nextData[key] = {
          entries: instrument.entries,
          loading: false,
          error: null,
          totalEntries: instrument.totalEntries,
          localEntries: instrument.localEntries,
        };
      }
    }
    return nextData;
  }, [leaderboardsQuery.data, leaderboardsQuery.error, leaderboardsQuery.isPending]);
  const bandData = useMemo<Record<PlayerBandType, SongBandData>>(() => {
    const error = queryErrorMessage(bandLeaderboardsQuery.error);
    const nextData = createSongBandData(bandLeaderboardsQuery.isPending, error);
    for (const band of bandLeaderboardsQuery.data?.bands ?? []) {
      const bandType = band.bandType as PlayerBandType;
      if (bandType in nextData) {
        nextData[bandType] = {
          entries: band.entries,
          selectedPlayerEntry: band.selectedPlayerEntry ?? null,
          selectedBandEntry: band.selectedBandEntry ?? null,
          loading: false,
          error: null,
          totalEntries: band.totalEntries,
          localEntries: band.localEntries,
        };
      }
    }
    return nextData;
  }, [bandLeaderboardsQuery.data, bandLeaderboardsQuery.error, bandLeaderboardsQuery.isPending]);
  const showLeaderboardEntryTotals = leaderboardsQuery.data?.showLeaderboardEntryTotals === true
    || bandLeaderboardsQuery.data?.showLeaderboardEntryTotals === true;
  const selectedMemberScores: SelectedMemberSongScore[] = selectedMemberScoresQuery.data?.scores ?? [];
  const selectedMemberScoresReady = selectedBandMemberAccountIds.length === 0
    || activeInstruments.length === 0
    || !selectedMemberScoresQuery.isPending;
  const mountedWithRemoteDataRef = useRef(
    !!leaderboardsQuery.data
      && (!selectedAccountId || !!scoreHistoryQuery.data)
      && !!bandLeaderboardsQuery.data
      && (
        selectedBandMemberAccountIds.length === 0
        || activeInstruments.length === 0
        || !!selectedMemberScoresQuery.data
      ),
  );
  const remoteScopeKey = [
    songId ?? '',
    leewayParam ?? '',
    selectedAccountId ?? '',
    selectedBandType ?? '',
    selectedTeamKey ?? '',
    activeBandComboId ?? '',
    selectedBandMemberAccountIds.join(','),
    activeInstruments.join(','),
  ].join(':');
  const initialRemoteScopeKeyRef = useRef(remoteScopeKey);
  const openPaths = useCallback(() => {
    if (canViewPaths) {
      setPathsOpen(true);
    }
  }, [canViewPaths]);

  /* v8 ignore start — FAB registration callback */
  // Register openPaths for the FAB
  useEffect(() => {
    fabSearch.registerSongDetailActions(canViewPaths ? { openPaths } : null);
    return () => fabSearch.registerSongDetailActions(null);
  }, [canViewPaths, fabSearch, openPaths]);
  /* v8 ignore stop */

  useEffect(() => {
    if (!canViewPaths && pathsOpen) {
      setPathsOpen(false);
    }
  }, [canViewPaths, pathsOpen]);

  const shopUrl = song ? getShopUrl(song.songId) : undefined;
  const showShop = isShopVisible && !!shopUrl;

  // No player means no player-specific data to wait for
  const playerDataReady = !player || scoreHistoryReady;
  const instrumentsReady = activeInstruments.every((k) => !instrumentData[k].loading);
  const bandsReady = SONG_BAND_TYPES.every((bandType) => !bandData[bandType].loading);
  const allReady = playerDataReady && instrumentsReady && bandsReady && selectedMemberScoresReady;
  const hasShownRemoteDataRef = useRef(mountedWithRemoteDataRef.current);
  useEffect(() => {
    if (allReady) hasShownRemoteDataRef.current = true;
  }, [allReady]);
  const transitionReady = allReady || hasShownRemoteDataRef.current;

  // Apply invalid score filtering
  const filteredScoreHistory = useMemo(() => {
    if (!songId) return scoreHistory;
    // Hoist the lookup — songId is constant across all entries, no need to
    // linear-scan the songs array for every history entry.
    const instMap = songs.find(s => s.songId === songId)?.maxScores;
    if (!instMap) return scoreHistory;
    return scoreHistory.filter(h =>
      filterScoreHistory(songId, h.instrument, [h]).length > 0,
    );
  }, [songId, scoreHistory, filterScoreHistory, songs]);

  const filteredPlayerScores = useMemo(() => {
    return filterPlayerScores(playerScores);
  }, [playerScores, filterPlayerScores]);

  const filteredSelectedMemberScores = useMemo(() => {
    return selectedMemberScores.flatMap((score) => {
      const filtered = filterPlayerScores([score])[0];
      return filtered ? [{ ...score, ...filtered }] : [];
    });
  }, [filterPlayerScores, selectedMemberScores]);

  const selectedMemberScoresByInstrument = useMemo(() => {
    const byAccountAndInstrument = new Map<string, SelectedMemberSongScore>();
    for (const score of filteredSelectedMemberScores) {
      byAccountAndInstrument.set(`${normalizeAccountId(score.accountId)}:${score.instrument}`, score);
    }

    const result = {} as Record<InstrumentKey, SelectedMemberSongScore[]>;
    for (const instrument of activeInstruments) {
      const rows: SelectedMemberSongScore[] = [];
      for (const member of selectedBandMembers) {
        const score = byAccountAndInstrument.get(`${normalizeAccountId(member.accountId)}:${instrument}`);
        if (score) rows.push({ ...score, displayName: score.displayName || member.displayName });
      }
      result[instrument] = rows;
    }
    return result;
  }, [activeInstruments, filteredSelectedMemberScores, selectedBandMembers]);

  const showScoreHistoryChart = !!player && scoreHistoryReady && filteredScoreHistory.length > 0;

  const allErrored = activeInstruments.length > 0
    && activeInstruments.every((k) => instrumentData[k].error && !instrumentData[k].loading);

  // Compute a global score width so season pills align across all sections
  const globalScoreWidth = useMemo(() => {
    let maxLen = 1;
    for (const inst of activeInstruments) {
      for (const e of instrumentData[inst].entries) {
        maxLen = Math.max(maxLen, e.score.toLocaleString().length);
      }
    }
    for (const s of filteredPlayerScores) {
      maxLen = Math.max(maxLen, s.score.toLocaleString().length);
    }
    for (const s of filteredSelectedMemberScores) {
      maxLen = Math.max(maxLen, s.score.toLocaleString().length);
    }
    for (const h of filteredScoreHistory) {
      maxLen = Math.max(maxLen, h.newScore.toLocaleString().length);
    }
    for (const bandType of SONG_BAND_TYPES) {
      for (const e of bandData[bandType].entries) {
        maxLen = Math.max(maxLen, e.score.toLocaleString().length);
      }
      const selectedEntry = bandData[bandType].selectedBandEntry ?? bandData[bandType].selectedPlayerEntry;
      if (selectedEntry) {
        maxLen = Math.max(maxLen, selectedEntry.score.toLocaleString().length);
      }
    }
    return `${maxLen}ch`;
  }, [activeInstruments, instrumentData, filteredPlayerScores, filteredSelectedMemberScores, filteredScoreHistory, bandData]);

  // Transition: spinner fade-out → staggered content fade-in
  // phase: 'loading' | 'spinnerOut' | 'contentIn'
  const allCached = initialRemoteScopeKeyRef.current === remoteScopeKey
    && !!cached
    && mountedWithRemoteDataRef.current;
  // Skip animations when all data is already cached (return visit, layout remount, etc.).
  // Frozen at mount time — the cache getting written mid-lifecycle should not flip this.
  const skipAnimRef = useRef(allCached);
  const skipAnim = skipAnimRef.current;
  const { phase } = useLoadPhase(transitionReady, { skipAnimation: allCached });
  useSetPageReady(phase === LoadPhase.ContentIn);
  const { forDelay: stagger, clearAnim } = useStagger(!skipAnim);
  const hasFab = useIsMobile();

  // Header stagger: always mount the header, control visibility via CSS (matches LeaderboardPage).
  // Mobile / cached → undefined (visible immediately). Loading → opacity:0. ContentIn → fadeInUp.
  const headerStagger: CSSProperties | undefined = hasFab || skipAnim
    ? undefined
    : phase === LoadPhase.ContentIn
      ? { opacity: 0, animation: `fadeInUp ${FADE_DURATION}ms ease-out forwards` }
      : { opacity: 0 };
  const userScrolledRef = useRef(false);
  const scrollContainerRef = useScrollContainer();

  // Cache scroll position on scroll.
  useEffect(() => {
    const scrollEl = scrollContainerRef.current;
    if (!scrollEl) return;
    const onScroll = () => {
      userScrolledRef.current = true;
      if (songId) {
        const entry = songDetailCache.get(songId);
        if (entry) entry.scrollTop = scrollEl.scrollTop;
      }
    };
    scrollEl.addEventListener('scroll', onScroll, { passive: true });
    return () => scrollEl.removeEventListener('scroll', onScroll);
  }, [songId, scrollContainerRef]);

  const hasScrolled = useRef(false);

  // Reset scroll tracking when song or instrument changes
  useEffect(() => {
    hasScrolled.current = false;
    userScrolledRef.current = false;
  }, [songId, defaultInstrument]);

  // Ensure a navigation entry exists once the page is ready.
  useEffect(() => {
    if (!songId || !allReady) return;
    songDetailCache.set(songId, {
      scrollTop: scrollContainerRef.current?.scrollTop ?? 0,
    });
  }, [allReady, songId, scrollContainerRef]);

  // Restore scroll position when returning from cache (not on fresh PUSH navigations)
  useLayoutEffect(() => {
    if (navType === 'PUSH' || !allCached || !songId) return;
    const saved = songDetailCache.get(songId);
    /* v8 ignore start — scroll restore */
  if (saved && saved.scrollTop > 0) {
      scrollContainerRef.current?.scrollTo(0, saved.scrollTop);
    }
  // eslint-disable-next-line react-hooks/exhaustive-deps -- mount-only scroll restore
  }, []);
  /* v8 ignore stop */

  // Scroll to the instrument card when arriving with ?instrument= and autoScroll state
  /* v8 ignore start — DOM scroll positioning */
  const autoScroll = !!(location.state as Record<string, unknown> | null)?.autoScroll;
  useEffect(() => {
    if (phase !== LoadPhase.ContentIn || !resolvedDefaultInstrument || hasScrolled.current || !autoScroll) return;
    hasScrolled.current = true;
    // Wait for stagger animations to complete before measuring position
    const id = setTimeout(() => {
      if (userScrolledRef.current) return;
      const target = document.getElementById(`player-score-${resolvedDefaultInstrument}`)
        ?? document.getElementById(`instrument-card-${resolvedDefaultInstrument}`);
      if (!target) return;
      const targetRect = target.getBoundingClientRect();
      const nav = document.querySelector('nav');
      const navHeight = nav ? nav.getBoundingClientRect().height : 0;
      const scrollEl = scrollContainerRef.current;
      const scrollRect = scrollEl?.getBoundingClientRect();
      const padding = 24;
      const desiredBottom = (scrollRect ? scrollRect.bottom : window.innerHeight) - navHeight - padding;
      const scrollBy = targetRect.bottom - desiredBottom;
      if (scrollBy > 0 && scrollEl) scrollEl.scrollTo({ top: scrollEl.scrollTop + scrollBy, behavior: 'smooth' });
    }, 1500);
    return () => clearTimeout(id);
  // eslint-disable-next-line react-hooks/exhaustive-deps -- autoScroll frozen at mount
  }, [phase, resolvedDefaultInstrument]);
  /* v8 ignore stop */

  const styles = useSongDetailStyles();
  const promotedBandSectionCount = promotedSongBandType ? 1 : 0;
  const instrumentRows = Math.ceil(activeInstruments.length / 2);
  const getInstrumentBaseDelay = useCallback((cardIndex: number) => {
    const rowIndex = Math.floor(cardIndex / 2);
    return 300 + (promotedBandSectionCount + rowIndex) * 150;
  }, [promotedBandSectionCount]);
  const getTrailingBandBaseDelay = useCallback((bandIndex: number) => (
    450 + (promotedBandSectionCount + instrumentRows) * 150 + bandIndex * 150
  ), [instrumentRows, promotedBandSectionCount]);
  const renderSongBandPreview = useCallback((bandType: PlayerBandType, baseDelay: number) => (
    <SongBandLeaderboardPreview
      key={bandType}
      songId={songId!}
      bandType={bandType}
      data={bandData[bandType]}
      selectedAccountId={selectedAccountId}
      showLeaderboardEntryTotals={showLeaderboardEntryTotals}
      baseDelay={baseDelay}
      skipAnimation={skipAnim}
    />
  ), [bandData, selectedAccountId, showLeaderboardEntryTotals, skipAnim, songId]);

  const quickLinkItems = useMemo<SongDetailQuickLink[]>(() => {
    if (phase !== LoadPhase.ContentIn || allErrored) return [];

    const intensityLabel = t('songDetail.quickLinksIntensity', 'Intensity');
    const items: SongDetailQuickLink[] = [{
      id: SONG_DETAIL_INTENSITY_QUICK_LINK_ID,
      label: intensityLabel,
      landmarkLabel: t('songDetail.quickLinksIntensityLandmark', 'Song Intensity'),
      icon: <IoStatsChart size={QUICK_LINK_GLYPH_ICON_SIZE} />,
    }];

    if (showScoreHistoryChart) {
      const scoreHistoryLabel = t('songDetail.scoreHistory');
      items.push({
        id: SONG_DETAIL_SCORE_HISTORY_QUICK_LINK_ID,
        label: scoreHistoryLabel,
        landmarkLabel: scoreHistoryLabel,
        icon: <IoTimerOutline size={QUICK_LINK_GLYPH_ICON_SIZE} />,
      });
    }

    if (promotedSongBandType) {
      const bandLabel = songBandTypeLabel(promotedSongBandType, t);
      items.push({
        id: songDetailBandQuickLinkId(promotedSongBandType),
        label: bandLabel,
        landmarkLabel: t('songDetail.quickLinksBandLandmark', { band: bandLabel, defaultValue: `${bandLabel} Band Leaderboard` }),
        icon: <IoPeople size={QUICK_LINK_GLYPH_ICON_SIZE} />,
      });
    }

    for (const instrument of activeInstruments) {
      const instrumentLabel = serverInstrumentLabel(instrument);
      items.push({
        id: songDetailInstrumentQuickLinkId(instrument),
        label: instrumentLabel,
        landmarkLabel: t('songDetail.quickLinksInstrumentLandmark', { instrument: instrumentLabel, defaultValue: `${instrumentLabel} Leaderboard` }),
        icon: <InstrumentIcon instrument={instrument} sig={song?.sig} size={QUICK_LINK_GLYPH_ICON_SIZE} />,
      });
    }

    for (const bandType of trailingSongBandTypes) {
      const bandLabel = songBandTypeLabel(bandType, t);
      items.push({
        id: songDetailBandQuickLinkId(bandType),
        label: bandLabel,
        landmarkLabel: t('songDetail.quickLinksBandLandmark', { band: bandLabel, defaultValue: `${bandLabel} Band Leaderboard` }),
        icon: <IoPeople size={QUICK_LINK_GLYPH_ICON_SIZE} />,
      });
    }

    return items;
  }, [activeInstruments, allErrored, phase, promotedSongBandType, showScoreHistoryChart, song?.sig, t, trailingSongBandTypes]);

  const quickLinksTitle = t('songDetail.quickLinks', 'Quick Links');
  const {
    activeItemId,
    quickLinksOpen,
    openQuickLinks,
    closeQuickLinks,
    handleQuickLinkSelect,
    registerSectionRef,
  } = usePageQuickLinks<SongDetailQuickLink>({
    items: quickLinkItems,
    scrollContainerRef,
    isDesktopRailEnabled: false,
  });

  const handleModalQuickLinkSelect = useCallback((item: SongDetailQuickLink) => {
    closeQuickLinks();
    handleQuickLinkSelect(item);
  }, [closeQuickLinks, handleQuickLinkSelect]);

  const pageQuickLinks = useMemo<PageQuickLinksConfig | undefined>(() => {
    if (!isMobile || quickLinkItems.length < 2) return undefined;

    return {
      title: quickLinksTitle,
      items: quickLinkItems,
      activeItemId,
      visible: quickLinksOpen,
      onOpen: openQuickLinks,
      onClose: closeQuickLinks,
      onSelect: (item) => handleModalQuickLinkSelect(item as SongDetailQuickLink),
      testIdPrefix: 'song-detail',
    };
  }, [activeItemId, closeQuickLinks, handleModalQuickLinkSelect, isMobile, openQuickLinks, quickLinkItems, quickLinksOpen, quickLinksTitle]);

  if (!songId) {
    return <div style={styles.center}>{t('songDetail.songNotFound')}</div>;
  }

  return (
    <Page
      scrollDeps={[phase, activeInstruments.length, SONG_BAND_TYPES.length, quickLinkItems.length]}
      variant="withBgClip"
      fabSpacer={phase === LoadPhase.ContentIn && allErrored ? 'none' : 'end'}
      quickLinks={pageQuickLinks}
      firstRun={{ key: 'songinfo', label: t('nav.songInfo', 'Song Info'), slides: songInfoSlidesMemo, gateContext: firstRunGateCtx }}
      background={<PageBackground src={song?.albumArt} />}
      before={
        <div style={headerStagger} onAnimationEnd={clearAnim}>
          <SongInfoHeader
            song={song}
            songId={songId!}
            collapsed
            onOpenPaths={!isMobileChrome && canViewPaths ? openPaths : undefined}
            shopUrl={!isMobileChrome && showShop ? shopUrl : undefined}
            shopPulse={showShop && song ? isShopHighlighted(song.songId) : false}
            shopLeavingTomorrow={showShop && song ? isLeavingTomorrow(song.songId) : false}
            shopNew={showShop && song ? isShopNew(song.songId) : false}
            hideBackground
          />
        </div>
      }
      after={<>
        {/* v8 ignore start -- songId always truthy from route params */}
        {songId && canViewPaths && <PathsModal visible={pathsOpen} songId={songId} generationId={song?.pathArtifactGenerationId} sig={song?.sig} onClose={() => setPathsOpen(false)} />}
        {/* v8 ignore stop */}
      </>}
    >
      {phase !== LoadPhase.ContentIn && (
        <div
          style={{ ...styles.spinnerOverlay,
            ...(phase === LoadPhase.SpinnerOut ? styles.spinnerFadeOut : {}),
          }}
        >
          <ArcSpinner />
        </div>
      )}
      {phase === LoadPhase.ContentIn && allErrored && (() => {
        const parsed = parseApiError(String(instrumentData[activeInstruments[0]!].error));
        return <EmptyState fullPage title={parsed.title} subtitle={parsed.subtitle} style={stagger(200)} onAnimationEnd={clearAnim} />;
      })()}
      {phase === LoadPhase.ContentIn && !allErrored && (
        <div style={styles.container}>
          <div ref={(element) => registerSectionRef(SONG_DETAIL_INTENSITY_QUICK_LINK_ID, element)}>
            <IntensityCard
              song={song}
              sig={song?.sig}
              style={{ ...stagger(100), marginBottom: Gap.section }}
              onAnimationEnd={clearAnim}
            />
          </div>
          <CollapsePresence visible={showScoreHistoryChart} testId="song-detail-score-history-collapse">
            {showScoreHistoryChart ? (
              <div ref={(element) => registerSectionRef(SONG_DETAIL_SCORE_HISTORY_QUICK_LINK_ID, element)} style={{ ...stagger(150), marginBottom: Gap.section }} onAnimationEnd={clearAnim}>
                <ScoreHistoryChart
                  songId={songId}
                  accountId={player.accountId}
                  playerName={player.displayName}
                  defaultInstrument={resolvedDefaultInstrument}
                  history={filteredScoreHistory}
                  visibleInstruments={activeInstruments}
                  skipAnimation={skipAnim}
                  scoreWidth={globalScoreWidth}
                  sig={song?.sig}
                />
              </div>
            ) : null}
          </CollapsePresence>
          {promotedSongBandType && (
            <div ref={(element) => registerSectionRef(songDetailBandQuickLinkId(promotedSongBandType), element)} data-testid="song-detail-promoted-band-sections" style={styles.promotedBandSections}>
              {renderSongBandPreview(promotedSongBandType, 300)}
            </div>
          )}
          <div data-testid="song-detail-instrument-grid" style={styles.instrumentGrid}>
            {activeInstruments.map((inst, idx) => {
              const baseDelay = getInstrumentBaseDelay(idx);
              return (
                  <div key={inst} id={`instrument-card-${inst}`} ref={(element) => registerSectionRef(songDetailInstrumentQuickLinkId(inst), element)}>
                    <InstrumentCard
                      songId={songId}
                      instrument={inst}
                      baseDelay={baseDelay}
                      windowWidth={windowWidth}
                      singleColumn={activeInstruments.length <= 1}
                      playerScore={filteredPlayerScores.find((s) => s.instrument === inst)}
                      playerName={player?.displayName}
                      playerAccountId={player?.accountId}
                      spotlightScores={selectedMemberScoresByInstrument[inst] ?? []}
                      prefetchedEntries={instrumentData[inst].entries}
                      prefetchedError={instrumentData[inst].error}
                      totalEntries={instrumentData[inst].totalEntries}
                      localEntries={instrumentData[inst].localEntries}
                      showLeaderboardEntryTotals={showLeaderboardEntryTotals}
                      skipAnimation={skipAnim}
                      scoreWidth={globalScoreWidth}
                      sig={song?.sig}
                    />
                  </div>
              );
            })}
          </div>
          {trailingSongBandTypes.length > 0 && (
            <div style={styles.bandSections}>
              {trailingSongBandTypes.map((bandType, idx) => (
                <div key={bandType} ref={(element) => registerSectionRef(songDetailBandQuickLinkId(bandType), element)}>
                  {renderSongBandPreview(bandType, getTrailingBandBaseDelay(idx))}
                </div>
              ))}
            </div>
          )}
        </div>
      )}
    </Page>
  );
}

function useSongDetailStyles() {
  return useMemo(() => ({
    container: {
      maxWidth: MaxWidth.card,
      margin: CssValue.marginCenter,
      paddingTop: Gap.none,
      paddingBottom: Layout.paddingTop,
    } as CSSProperties,
    instrumentGrid: {
      display: Display.grid,
      gridTemplateColumns: GridTemplate.autoFillInstrument,
      gap: `${Gap.section}px ${Gap.md}px`,
      overflow: Overflow.hidden,
    } as CSSProperties,
    bandSections: {
      ...flexColumn,
      gap: Gap.section,
      marginTop: Gap.section,
    } as CSSProperties,
    promotedBandSections: {
      ...flexColumn,
      gap: Gap.section,
      marginBottom: Gap.section,
    } as CSSProperties,
    center: {
      ...flexCenter,
      minHeight: CssValue.viewportFull,
      color: Colors.textSecondary,
      backgroundColor: Colors.backgroundApp,
      fontSize: Font.lg,
    } as CSSProperties,
    spinnerOverlay: {
      position: Position.fixed,
      inset: 0,
      zIndex: ZIndex.overlay,
      ...flexCenter,
    } as CSSProperties,
    spinnerFadeOut: {
      animation: `fadeOut ${SPINNER_FADE_MS}ms ease-out forwards`,
      pointerEvents: PointerEvents.none,
    } as CSSProperties,
  }), []);
}