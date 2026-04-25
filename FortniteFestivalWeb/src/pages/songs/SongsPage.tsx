/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useState, useMemo, useEffect, useRef, useCallback, type CSSProperties, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useLocation, useNavigationType } from 'react-router-dom';
import { useVirtualizer } from '@tanstack/react-virtual';
import { IoCompass } from 'react-icons/io5';
import { staggerDelay, estimateVisibleCount, IS_PAGE_RELOAD } from '@festival/ui-utils';
import { useStaggerStyle, buildStaggerStyle, clearStaggerStyle } from '../../hooks/ui/useStaggerStyle';
import { useContainerWidth } from '../../hooks/ui/useContainerWidth';
import { useFestival } from '../../contexts/FestivalContext';
import { usePlayerData } from '../../contexts/PlayerDataContext';
import { useSettings } from '../../contexts/SettingsContext';
import { useIsMobile, useIsMobileChrome, useIsWideDesktop } from '../../hooks/ui/useIsMobile';
import { useFabSearch } from '../../contexts/FabSearchContext';
import { useSearchQuery } from '../../contexts/SearchQueryContext';
import { clearScrollCache } from '../../hooks/ui/useScrollRestore';
import { useFilteredSongs } from '../../hooks/data/useFilteredSongs';
import { useScoreFilter } from '../../hooks/data/useScoreFilter';
import { useShopState } from '../../hooks/data/useShopState';
import { useShop } from '../../contexts/ShopContext';
import { useModalState } from '../../hooks/ui/useModalState';
import { songSlides } from './firstRun';
import { type PlayerScore, type ServerInstrumentKey as InstrumentKey, DEFAULT_INSTRUMENT } from '@festival/core/api/serverTypes';
import { accuracyBgColor, maxScoreColor, LoadPhase } from '@festival/core';
import { Gap, Colors, Font, Layout, MetadataSize, BoxSizing, CssValue, Display, TextAlign, Weight, Border, Radius, Size, FADE_DURATION, STAGGER_INTERVAL, border, flexCenter, padding } from '@festival/theme';
import { LoadGate } from '../../components/page/LoadGate';
import Page from '../Page';
import { useScrollContainer } from '../../contexts/ScrollContainerContext';
import SyncBanner from '../../components/page/SyncBanner';
import SyncCompleteBanner from '../../components/page/SyncCompleteBanner';
import CollapseOnExit from '../../components/page/CollapseOnExit';
import EmptyState from '../../components/common/EmptyState';
import { ActionPill } from '../../components/common/ActionPill';
import { parseApiError } from '../../utils/apiError';
import PageHeader from '../../components/common/PageHeader';
import PageHeaderTransition from '../../components/common/PageHeaderTransition';
import { SongRow } from './components/SongRow';
import { SongsToolbar } from './components/SongsToolbar';
import { visibleInstruments } from '../../contexts/SettingsContext';
import { DEFAULT_SONGS_SCROLL_OFFSET, getPageQuickLinkTestId, usePageQuickLinks, type PageQuickLinkItem } from '../../hooks/ui/usePageQuickLinks';
import type { PageQuickLinksConfig } from '../../components/page/PageQuickLinks';
import SortModal from './modals/SortModal';
import type { SortDraft } from './modals/SortModal';
import FilterModal from './modals/FilterModal';
import type { FilterDraft } from './modals/FilterModal';
import DifficultyBars from '../../components/songs/metadata/DifficultyBars';
import DifficultyPill from '../../components/songs/metadata/DifficultyPill';
import MiniStars from '../../components/songs/metadata/MiniStars';
import PercentilePill from '../../components/songs/metadata/PercentilePill';
import SeasonPill from '../../components/songs/metadata/SeasonPill';
import {
  type SongSettings,
  type SongSortMode,
  defaultSongSettings,
  defaultSongFilters,
  loadSongSettings,
  normalizeSongSettings,
  saveSongSettings,
  SONG_SETTINGS_CHANGED_EVENT,
  isFilterActive,
} from '../../utils/songSettings';
import { resolveCompactRowMode } from './layoutMode';
import { hasVisitedPage, markPageVisited } from '../../hooks/ui/usePageTransition';
import { buildSongQuickLinkSections, type SongQuickLinkSection } from './songQuickLinks';

/**
 * Estimated minimum width (px) for each metadata element in desktop row layout.
 * Includes the element itself plus its share of the gap.
 * Used to predict whether all elements fit before rendering.
 */
const METADATA_MIN_WIDTH: Record<string, number> = {
  score: 94,        // ScorePill (78px) + gap share
  percentage: 72,   // AccuracyDisplay (~55px) + gap share
  percentile: 96,   // PercentilePill (~80px) + gap share
  stars: 120,       // 5 gold stars (~104px) + gap share
  seasonachieved: 48, // SeasonPill (~32px) + gap share
  intensity: 44,    // DifficultyBars (~28px) + gap share
  difficulty: 44,   // DifficultyPill (~28px) + gap share
  maxdistance: 76,  // PercentilePill with % (~60px) + gap share
  maxscorediff: 92,  // PercentilePill with -XXX,XXX (~76px) + gap share
};

/** Fixed overhead: row padding (32px) + SongInfo (albumArt 48 + gap 16 + min title 150) + gap to metadata (16). */
const ROW_FIXED_OVERHEAD = 262;

/** Safety buffer (px) so compact mode fires before any metadata could clip. */
const ROW_WIDTH_BUFFER = 60;

const SONG_SORT_LABEL_KEYS: Record<SongSortMode, string> = {
  title: 'sort.title',
  artist: 'sort.artist',
  year: 'sort.year',
  duration: 'sort.duration',
  shop: 'sort.itemShop',
  hasfc: 'sort.hasFC',
  lastplayed: 'sort.lastPlayed',
  score: 'sort.score',
  percentage: 'sort.percentage',
  percentile: 'sort.percentile',
  stars: 'sort.stars',
  seasonachieved: 'sort.seasonAchieved',
  intensity: 'sort.intensity',
  difficulty: 'sort.difficulty',
  maxdistance: 'sort.maxDistance',
  maxscorediff: 'sort.maxScoreDiff',
};

type SongQuickLink = PageQuickLinkItem & {
  rowIndex: number;
};

const SONG_QUICK_LINK_PERCENTAGE_PILL_STYLE: CSSProperties = {
  padding: padding(Gap.xs, Gap.sm),
  borderRadius: Radius.xs,
  display: Display.inlineBlock,
  textAlign: TextAlign.center,
  boxSizing: BoxSizing.borderBox,
  minWidth: MetadataSize.accuracyPillMinWidth,
  fontWeight: Weight.semibold,
  color: Colors.textPrimary,
  border: border(Border.thick, CssValue.transparent),
};

function getSongQuickLinkBucketKey(id: string): string {
  const separator = id.indexOf(':');
  return separator >= 0 ? id.slice(separator + 1) : id;
}

function getPercentileQuickLinkTier(bucketKey: string): 'top1' | 'top5' | 'default' {
  const bucket = Number(bucketKey);
  if (!Number.isFinite(bucket)) return 'default';
  if (bucket <= 1) return 'top1';
  if (bucket <= 5) return 'top5';
  return 'default';
}

function getPercentageQuickLinkValue(bucketKey: string): number | null {
  switch (bucketKey) {
    case '100':
      return 100;
    case '99':
      return 99;
    case '98':
      return 98;
    case '95':
      return 96;
    case '90':
      return 92;
    case 'lt90':
      return 89;
    default:
      return null;
  }
}

function getMaxDistanceQuickLinkValue(bucketKey: string): number | null {
  switch (bucketKey) {
    case '100':
      return 100;
    case '99':
      return 99.5;
    case '98':
      return 98.5;
    case '95':
      return 96;
    case '90':
      return 92;
    case 'lt90':
      return 89;
    default:
      return null;
  }
}

function getMaxScoreDiffQuickLinkValue(bucketKey: string): number | null {
  switch (bucketKey) {
    case 'max':
      return 100;
    case 'lt1k':
      return 99.8;
    case 'lt5k':
      return 99.1;
    case 'lt10k':
      return 98.3;
    case 'lt25k':
      return 96;
    case 'lt50k':
      return 92;
    case 'gte50k':
      return 85;
    default:
      return null;
  }
}

function renderSongQuickLinkLabel(sortMode: SongSortMode, section: Pick<SongQuickLinkSection, 'id' | 'label'>, isWideDesktop = false): ReactNode {
  const bucketKey = getSongQuickLinkBucketKey(section.id);

  if (isWideDesktop) {
    if (sortMode === 'percentile') {
      return bucketKey === 'no-rank' ? section.label : `Top ${section.label}`;
    }

    if (sortMode !== 'intensity') {
      return section.label;
    }
  }

  if (sortMode === 'percentile') {
    if (bucketKey === 'no-rank') {
      return section.label;
    }

    return <PercentilePill display={`Top ${section.label}`} tier={getPercentileQuickLinkTier(bucketKey)} />;
  }

  if (sortMode === 'percentage') {
    const bucketValue = getPercentageQuickLinkValue(bucketKey);
    if (bucketValue == null) {
      return section.label;
    }

    return (
      <span style={{ ...SONG_QUICK_LINK_PERCENTAGE_PILL_STYLE, backgroundColor: accuracyBgColor(bucketValue) }}>
        {section.label}
      </span>
    );
  }

  if (sortMode === 'stars') {
    const stars = Number(bucketKey);
    if (!Number.isFinite(stars) || stars <= 0) {
      return section.label;
    }

    return <MiniStars starsCount={stars} isFullCombo={false} align="start" />;
  }

  if (sortMode === 'seasonachieved') {
    const season = Number(bucketKey.replace(/^s/i, ''));
    if (!Number.isFinite(season) || season <= 0) {
      return section.label;
    }

    return <SeasonPill season={season} />;
  }

  if (sortMode === 'intensity') {
    const intensity = Number(bucketKey);
    if (!Number.isFinite(intensity) || intensity < 0) {
      return section.label;
    }

    return <DifficultyBars level={intensity} raw />;
  }

  if (sortMode === 'difficulty') {
    const difficulty = Number(bucketKey);
    if (!Number.isFinite(difficulty) || difficulty < 0) {
      return section.label;
    }

    return <DifficultyPill difficulty={difficulty} />;
  }

  if (sortMode === 'maxdistance') {
    const bucketValue = getMaxDistanceQuickLinkValue(bucketKey);
    if (bucketValue == null) {
      return section.label;
    }

    return <PercentilePill display={section.label} color={maxScoreColor(bucketValue)} />;
  }

  if (sortMode === 'maxscorediff') {
    const bucketValue = getMaxScoreDiffQuickLinkValue(bucketKey);
    if (bucketValue == null) {
      return section.label;
    }

    return <PercentilePill display={section.label} color={maxScoreColor(bucketValue)} />;
  }

  return section.label;
}

function renderSongSectionLabel(sortMode: SongSortMode, section: SongQuickLinkSection, sectionLabelStyle: CSSProperties): ReactNode {
  const label = renderSongQuickLinkLabel(sortMode, section);
  return typeof label === 'string'
    ? <span style={sectionLabelStyle}>{label}</span>
    : label;
}

function getMinDesktopRowWidth(visibleKeys: string[], sortMode?: string): number {
  let width = ROW_FIXED_OVERHEAD;
  for (const key of visibleKeys) {
    if (key === 'score' && (sortMode === 'maxdistance' || sortMode === 'maxscorediff')) {
      width += 192;  // dual score: 78 + 6 + ~8 + 6 + 78 = 176px + 16px gap share
    } else {
      width += METADATA_MIN_WIDTH[key] ?? 80;
    }
  }
  return width + ROW_WIDTH_BUFFER;
}

export default function SongsPage() {
  const { t } = useTranslation();
  const {
    state: { songs, isLoading, error },
  } = useFestival();
  const { settings: appSettings } = useSettings();
  const isMobile = useIsMobile();
  const isMobileChrome = useIsMobileChrome();
  const isWideDesktop = useIsWideDesktop();
  const [settings, setSettings] = useState<SongSettings>(loadSongSettings);
  
  // Filter metadata keys by visibility settings (computed early for container-width detection)
  /* v8 ignore start � metadata visibility: settings-dependent presentation filter */
  const visibleMetadataOrder = useMemo(() => {
    const hidden = new Set<string>();
    if (!appSettings.metadataShowScore) hidden.add('score');
    if (!appSettings.metadataShowPercentage) hidden.add('percentage');
    if (!appSettings.metadataShowPercentile) hidden.add('percentile');
    if (!appSettings.metadataShowSeasonAchieved) hidden.add('seasonachieved');
    if (!appSettings.metadataShowIntensity) hidden.add('intensity');
    if (!appSettings.metadataShowGameDifficulty) hidden.add('difficulty');
    if (!appSettings.metadataShowStars) hidden.add('stars');
    if (!appSettings.metadataShowLastPlayed || settings.sortMode !== 'lastplayed') hidden.add('lastplayed');

    let order: string[];
    if (appSettings.songRowVisualOrderEnabled) {
      order = hidden.size === 0
        ? appSettings.songRowVisualOrder
        : appSettings.songRowVisualOrder.filter(k => !hidden.has(k));
    } else {
      order = hidden.size === 0
        ? settings.metadataOrder
        : settings.metadataOrder.filter(k => !hidden.has(k));
    }

    // Auto-inject maxdistance/maxscorediff only when max-score sort is active
    if (settings.sortMode === 'maxdistance' && !order.includes('maxdistance')) {
      order = [...order, 'maxdistance'];
    }
    if (settings.sortMode === 'maxscorediff' && !order.includes('maxscorediff')) {
      order = [...order, 'maxscorediff'];
    }
    if (settings.sortMode === 'lastplayed' && !order.includes('lastplayed')) {
      order = [...order, 'lastplayed'];
    }
    return order;
  }, [
    settings.metadataOrder,
    settings.sortMode,
    appSettings.songRowVisualOrderEnabled,
    appSettings.songRowVisualOrder,
    appSettings.metadataShowScore,
    appSettings.metadataShowPercentage,
    appSettings.metadataShowPercentile,
    appSettings.metadataShowSeasonAchieved,
    appSettings.metadataShowIntensity,
    appSettings.metadataShowGameDifficulty,
    appSettings.metadataShowStars,
    appSettings.metadataShowLastPlayed,
  ]);
  /* v8 ignore stop */
  
  // Container-width-aware compact row detection.
  // Keep the breakpoint decision stable with hysteresis so the virtualized list
  // does not bounce between 68px and 122px rows while resizing near the cutoff.
  const containerRef = useRef<HTMLDivElement>(null);
  const containerWidth = useContainerWidth(containerRef);
  const minDesktopWidth = useMemo(
    () => getMinDesktopRowWidth(visibleMetadataOrder, settings.sortMode),
    [visibleMetadataOrder, settings.sortMode],
  );
  const compactRowRef = useRef(typeof window !== 'undefined' ? window.innerWidth <= 1100 : false);
  const effectiveRowWidth = containerWidth > 0
    ? containerWidth
    : (typeof window !== 'undefined' ? window.innerWidth : minDesktopWidth);
  const isCompactRow = resolveCompactRowMode(effectiveRowWidth, minDesktopWidth, compactRowRef.current);
  const songRowMobile = isMobile || isCompactRow;

  useEffect(() => {
    compactRowRef.current = isCompactRow;
  }, [isCompactRow]);

  const songsSlidesMemo = useMemo(() => songSlides(isMobileChrome), [isMobileChrome]);

  const fabSearch = useFabSearch();
  const searchQuery = useSearchQuery();
  const staggerRushRef = useRef<(() => void) | undefined>(undefined);
  const resetRush = useCallback(() => staggerRushRef.current?.(), []);
  const navType = useNavigationType();
  const location = useLocation();
  const forceRestagger = !!(location.state as Record<string, unknown> | null)?.restagger;
  const isBackNav = navType === 'POP' && !!(location.state as Record<string, unknown> | null) && !IS_PAGE_RELOAD;

  const [search, setSearchLocal] = useState(searchQuery.query);
  const setSearch = useCallback((q: string) => {
    setSearchLocal(q);
    searchQuery.setQuery(q);
  // eslint-disable-next-line react-hooks/exhaustive-deps -- searchQuery is context, stable ref
  }, [fabSearch]);
  const effectiveSearch = isMobileChrome ? searchQuery.query : search;
  const [debouncedSearch, setDebouncedSearch] = useState(effectiveSearch);
  useEffect(() => {
    const id = setTimeout(() => setDebouncedSearch(effectiveSearch), 250);
    return () => clearTimeout(id);
  }, [effectiveSearch]);

  // Re-sync settings from localStorage when changed externally (e.g. PlayerPage card clicks)
  const savingRef = useRef(false);
  /* v8 ignore start � external settings sync event listener */
  useEffect(() => {
    const sync = () => {
      if (savingRef.current) return; // ignore self-triggered events
      const fresh = loadSongSettings();
      setSettings(fresh);
      setInstrument(fresh.instrument ?? DEFAULT_INSTRUMENT);
    };
    window.addEventListener(SONG_SETTINGS_CHANGED_EVENT, sync);
    return () => window.removeEventListener(SONG_SETTINGS_CHANGED_EVENT, sync);
  }, []);
  /* v8 ignore stop */
  const [instrument, setInstrument] = useState<InstrumentKey>(
    () => settings.instrument ?? DEFAULT_INSTRUMENT,
  );

  // Sort/Filter modal state
  const sortModal = useModalState<SortDraft>(() => ({
    sortMode: settings.sortMode,
    sortAscending: settings.sortAscending,
    metadataOrder: settings.metadataOrder,
    instrumentOrder: settings.instrumentOrder,
  }));
  const filterModal = useModalState<FilterDraft>(() => ({
    ...settings.filters,
    instrumentFilter: settings.instrument,
  }));

  // Persist settings on change
  useEffect(() => { savingRef.current = true; saveSongSettings(settings); savingRef.current = false; }, [settings]);

  const openSort = () => {
    sortModal.open({
      sortMode: settings.sortMode,
      sortAscending: settings.sortAscending,
      metadataOrder: settings.metadataOrder,
      instrumentOrder: settings.instrumentOrder,
    });
  };
  const applySort = () => {
    setSettings(s => ({ ...s, ...sortModal.draft }));
    sortModal.close();
  };
  const resetSort = () => {
    const d = defaultSongSettings();
    sortModal.setDraft({ sortMode: d.sortMode, sortAscending: d.sortAscending, metadataOrder: d.metadataOrder, instrumentOrder: d.instrumentOrder });
  };

  const openFilter = () => {
    filterModal.open({ ...settings.filters, instrumentFilter: settings.instrument });
  };
  const applyFilter = () => {
    const { instrumentFilter, ...filters } = filterModal.draft;
    setInstrument(instrumentFilter ?? DEFAULT_INSTRUMENT);
    setSettings(s => normalizeSongSettings({ ...s, filters, instrument: instrumentFilter }));
    filterModal.close();
  };
  const resetFilter = () => {
    filterModal.setDraft({ ...defaultSongFilters(), instrumentFilter: null });
  };

  const sortActive = settings.sortMode !== 'title' || !settings.sortAscending;

  // Register sort/filter actions for FAB — uses refs so latest closures are always captured
  const openSortRef = useRef(openSort);
  const openFilterRef = useRef(openFilter);
  openSortRef.current = openSort;
  openFilterRef.current = openFilter;
  /* v8 ignore start � FAB action registration callbacks */
  useEffect(() => {
    fabSearch.registerActions({ openSort: () => openSortRef.current(), openFilter: () => openFilterRef.current() });
  }, [fabSearch]);
  /* v8 ignore stop */
  const { playerData, playerLoading, isSyncing, syncPhase, backfillProgress, historyProgress, rivalsProgress, entriesFound, itemsCompleted, totalItems, currentSongName, seasonsQueried, rivalsFound, isThrottled, throttleStatusKey, pendingRankUpdate, estimatedRankUpdateMinutes, probeStatusKey, nextRetrySeconds, justCompleted: ctxJustCompleted, clearCompleted: ctxClearCompleted, syncBannerDismissed, dismissSyncBanner } = usePlayerData();
  const [showCompleteBanner, setShowCompleteBanner] = useState(false);

  // Show completion banner when sync finishes (unless already globally dismissed)
  useEffect(() => {
    if (ctxJustCompleted) {
      ctxClearCompleted();
      if (!syncBannerDismissed) setShowCompleteBanner(true);
    }
  }, [ctxJustCompleted, ctxClearCompleted, syncBannerDismissed]);

  // Hide local banner when dismissed globally from another page
  useEffect(() => {
    if (syncBannerDismissed) setShowCompleteBanner(false);
  }, [syncBannerDismissed]);

  // Sync banner collapse animation state (matches PlayerContent pattern)
  const bannerVisible = isSyncing || (!syncBannerDismissed && showCompleteBanner);
  const [bannerCollapsed, setBannerCollapsed] = useState(!bannerVisible);
  useEffect(() => { if (bannerVisible) setBannerCollapsed(false); }, [bannerVisible]);

  const shopCtx = useShop();
  const { isShopHighlighted, isLeavingTomorrow, isShopVisible } = useShopState();
  const filtersActive = isFilterActive(settings.filters, settings.instrument, isShopVisible) || settings.instrument != null;
  const firstRunGateCtx = useMemo(() => ({ hasPlayer: !!playerData, shopHighlightEnabled: isShopVisible && !appSettings.disableShopHighlighting }), [playerData, isShopVisible, appSettings.disableShopHighlighting]);

  const { isScoreValid, enabled: scoreFilterEnabled, leeway: userLeeway, getFilteredRank, getFilteredTotal } = useScoreFilter();

  // Apply invalid-score substitution/dropping (same logic as filterPlayerScores but
  // also builds an invalidity map for the UI indicator, and exempts instruments where
  // the overThreshold filter is active so the user can inspect the raw values).
  /* v8 ignore start � effectiveScores: invalid-score substitution */
  type InvalidReason = 'fallback' | 'no-fallback' | 'over-threshold';
  const { effectiveScores, invalidScoreMap } = useMemo(() => {
    const empty = { effectiveScores: [] as PlayerScore[], invalidScoreMap: new Map<string, Map<InstrumentKey, InvalidReason>>() };
    if (!playerData) return empty;
    if (!scoreFilterEnabled) return { effectiveScores: playerData.scores, invalidScoreMap: empty.invalidScoreMap };

    const overThreshold = settings.filters.overThreshold ?? {};
    const scores: PlayerScore[] = [];
    const invalids = new Map<string, Map<InstrumentKey, InvalidReason>>();

    for (const s of playerData.scores) {
      const inst = s.instrument as InstrumentKey;
      const scoreInvalid = s.minLeeway != null ? s.minLeeway > userLeeway : !isScoreValid(s.songId, inst, s.score);
      if (scoreInvalid) {
        // When overThreshold filter is active, pass through raw score but mark as over-threshold
        if (overThreshold[inst]) {
          scores.push(s);
          let byInst = invalids.get(s.songId);
          if (!byInst) { byInst = new Map(); invalids.set(s.songId, byInst); }
          byInst.set(inst, 'over-threshold');
        } else if (s.validScores && s.validScores.length > 0) {
          // New path: find best fallback from validScores where minLeeway <= userLeeway
          const fallback = s.validScores.find(v => v.minLeeway <= userLeeway);
          if (fallback) {
            const filteredRank = getFilteredRank(fallback.rankTiers);
            const filteredTotal = getFilteredTotal(s.songId, inst, s.totalEntries);
            scores.push({
              ...s,
              score: fallback.score,
              accuracy: fallback.accuracy ?? s.accuracy,
              isFullCombo: fallback.fc ?? s.isFullCombo,
              stars: fallback.stars ?? s.stars,
              rank: filteredRank ?? s.rank,
              totalEntries: filteredTotal ?? s.totalEntries,
            });
            let byInst = invalids.get(s.songId);
            if (!byInst) { byInst = new Map(); invalids.set(s.songId, byInst); }
            byInst.set(inst, 'fallback');
          } else {
            let byInst = invalids.get(s.songId);
            if (!byInst) { byInst = new Map(); invalids.set(s.songId, byInst); }
            byInst.set(inst, 'no-fallback');
          }
        } else if (s.validScore != null) {
          // Legacy path: substitute with server-provided fallback values
          scores.push({
            ...s,
            score: s.validScore,
            rank: s.validRank ?? 0,
            accuracy: s.validAccuracy ?? s.accuracy,
            isFullCombo: s.validIsFullCombo ?? s.isFullCombo,
            stars: s.validStars ?? s.stars,
            totalEntries: s.validTotalEntries ?? s.totalEntries,
          });
          let byInst = invalids.get(s.songId);
          if (!byInst) { byInst = new Map(); invalids.set(s.songId, byInst); }
          byInst.set(inst, 'fallback');
        } else {
          // Invalid with no fallback � drop from effective scores
          let byInst = invalids.get(s.songId);
          if (!byInst) { byInst = new Map(); invalids.set(s.songId, byInst); }
          byInst.set(inst, 'no-fallback');
        }
      } else {
        scores.push(s);
      }
    }
    return { effectiveScores: scores, invalidScoreMap: invalids };
  }, [playerData, scoreFilterEnabled, isScoreValid, userLeeway, getFilteredRank, getFilteredTotal, settings.filters.overThreshold]);
  /* v8 ignore stop */

  // Build lookup: songId ? PlayerScore for the selected instrument
  /* v8 ignore start � scoreMap: instrument filter loop */
  const scoreMap = useMemo(() => {
    if (!playerData) return new Map<string, PlayerScore>();
    const map = new Map<string, PlayerScore>();
    for (const s of effectiveScores) {
      if (s.instrument === instrument) {
        map.set(s.songId, s);
      }
    }
    return map;
  }, [playerData, effectiveScores, instrument]);
  /* v8 ignore stop */

  /* v8 ignore start � allScoreMap: multi-instrument lookup */
  // Build a per-song, per-instrument lookup for filter logic
  const allScoreMap = useMemo(() => {
    if (!playerData) return new Map<string, Map<InstrumentKey, PlayerScore>>();
    const map = new Map<string, Map<InstrumentKey, PlayerScore>>();
    for (const sc of effectiveScores) {
      let byInst = map.get(sc.songId);
      if (!byInst) {
        byInst = new Map();
        map.set(sc.songId, byInst);
      }
      byInst.set(sc.instrument as InstrumentKey, sc);
    }
    return map;
  }, [playerData, effectiveScores]);
  /* v8 ignore stop */

  const filtered = useFilteredSongs({
    songs,
    search: debouncedSearch,
    sortMode: settings.sortMode,
    sortAscending: settings.sortAscending,
    filters: settings.filters,
    instrument: settings.instrument,
    scoreMap,
    allScoreMap,
    shopSongIds: shopCtx.shopSongIds,
    leavingTomorrowIds: shopCtx.leavingTomorrowIds,
    isScoreValid,
    filterInvalidScoresEnabled: scoreFilterEnabled,
    shopVisible: isShopVisible,
  });

  const sectionModel = useMemo(() => buildSongQuickLinkSections({
    songs: filtered,
    sortMode: settings.sortMode,
    instrument: settings.instrument,
    scoreMap,
    allScoreMap,
    shopSongIds: shopCtx.shopSongIds,
    leavingTomorrowIds: shopCtx.leavingTomorrowIds,
    t,
  }), [allScoreMap, filtered, scoreMap, settings.instrument, settings.sortMode, shopCtx.leavingTomorrowIds, shopCtx.shopSongIds, t]);

  const hasQuickLinkSections = sectionModel.sections.length >= 2;
  const sortLabel = t(SONG_SORT_LABEL_KEYS[settings.sortMode] ?? 'sort.title');
  const quickLinksTitle = t('songs.quickLinksTitle', { sort: sortLabel });
  const quickLinkItems = useMemo<SongQuickLink[]>(() => {
    if (!hasQuickLinkSections) {
      return [];
    }

    return sectionModel.sections.map((section) => ({
      id: section.id,
      label: renderSongQuickLinkLabel(settings.sortMode, section, isWideDesktop),
      landmarkLabel: section.landmarkLabel,
      icon: null,
      rowIndex: section.rowIndex,
    }));
  }, [hasQuickLinkSections, isWideDesktop, sectionModel.sections, settings.sortMode]);

  const hasPlayer = !!playerData;

  const enabledInstruments = useMemo(
    () => visibleInstruments(appSettings),
    [appSettings],
  );

  // Derive available seasons from player scores
  const availableSeasons = useMemo(() => {
    if (!playerData) return [];
    const set = new Set<number>();
    for (const s of playerData.scores) {
      if (s.season != null) set.add(s.season);
    }
    return Array.from(set).sort((a, b) => a - b);
  }, [playerData]);

  // ── Spinner → staggered-content transition ──
  const dataReady = !isLoading && !playerLoading;
  // Capture "first visit this session" before marking as rendered
  const skipAnimRef = useRef((hasVisitedPage('songs') || isBackNav) && !forceRestagger);
  const skipAnim = skipAnimRef.current;
  useEffect(() => { markPageVisited('songs'); }, []);
  // Skip spinner + stagger only when data is actually available
  const [loadPhase, setLoadPhase] = useState<LoadPhase>(
    dataReady ? LoadPhase.ContentIn : LoadPhase.Loading,
  );
  // Track whether the initial load phase was set via settings change (not mount)
  const isSettingsChangeRef = useRef(false);
  // Whether to run stagger animation on the current contentIn transition
  const [shouldStagger, setShouldStagger] = useState(!skipAnim);

  // Track whether the toolbar has been shown at least once (initial load complete)
  const toolbarShownRef = useRef(skipAnim);
  if (loadPhase === LoadPhase.ContentIn) toolbarShownRef.current = true;

  // Fingerprint of sort/filter/search settings — when it changes, re-stagger the list
  const settingsKey = `${settings.sortMode}|${settings.sortAscending}|${instrument}|${JSON.stringify(settings.filters)}|${debouncedSearch}`;
  const prevSettingsKeyRef = useRef(settingsKey);

  /* v8 ignore start � animation: stagger/re-stagger effects */
  useEffect(() => {
    if (prevSettingsKeyRef.current === settingsKey) return;
    prevSettingsKeyRef.current = settingsKey;
    // Only re-stagger if we were already showing content
    if (loadPhase === LoadPhase.ContentIn) {
      isSettingsChangeRef.current = true;
      resetRush();
      setShouldStagger(true);
      setLoadPhase(LoadPhase.SpinnerOut);
    }
  }, [settingsKey, loadPhase]);

  // Show spinner while the user is typing ahead of the debounce
  const searchLoadingRef = useRef(false);
  useEffect(() => {
    if (effectiveSearch !== debouncedSearch && (loadPhase === LoadPhase.ContentIn || loadPhase === LoadPhase.SpinnerOut)) {
      searchLoadingRef.current = true;
      setLoadPhase(LoadPhase.Loading);
    }
  }, [effectiveSearch, debouncedSearch, loadPhase]);

  useEffect(() => {
    if (!dataReady || loadPhase !== LoadPhase.Loading) return;
    // Hold the spinner briefly so it feels intentional after search debounce
    if (searchLoadingRef.current) {
      searchLoadingRef.current = false;
      const id = setTimeout(() => { resetRush(); setShouldStagger(true); setLoadPhase(LoadPhase.SpinnerOut); }, 300);
      return () => clearTimeout(id);
    }
    // On revisit (skipAnim) skip the spinner-out delay and jump straight to content
    if (skipAnimRef.current) {
      setLoadPhase(LoadPhase.ContentIn);
    } else {
      resetRush();
      setShouldStagger(true);
      setLoadPhase(LoadPhase.SpinnerOut);
    }
  }, [dataReady, loadPhase]);

  useEffect(() => {
    if (loadPhase !== LoadPhase.SpinnerOut) return;
    const id = setTimeout(() => {
      setLoadPhase(LoadPhase.ContentIn);
    }, 500);
    return () => clearTimeout(id);
  }, [loadPhase]);

  // Turn off stagger after all animations finish
  const maxVisibleRows = useMemo(() => estimateVisibleCount(songRowMobile ? 120 : 72), [songRowMobile]);
  const getRowStaggerDelay = useCallback((rowIndex: number): number | undefined => {
    if (!shouldStagger || rowIndex >= maxVisibleRows) return undefined;
    return staggerDelay(rowIndex, 125, maxVisibleRows) ?? maxVisibleRows * 125;
  }, [maxVisibleRows, shouldStagger]);
  const desktopRailRevealDelayMs = useMemo(
    () => shouldStagger ? ((maxVisibleRows + 1) * STAGGER_INTERVAL) + FADE_DURATION : 0,
    [maxVisibleRows, shouldStagger],
  );
  const staggerRetireDelayMs = useMemo(
    () => desktopRailRevealDelayMs + (isWideDesktop && hasQuickLinkSections ? FADE_DURATION : 0),
    [desktopRailRevealDelayMs, hasQuickLinkSections, isWideDesktop],
  );
  useEffect(() => {
    if (loadPhase !== LoadPhase.ContentIn || !shouldStagger) return;
    const totalAnimTime = staggerRetireDelayMs;
    const id = setTimeout(() => setShouldStagger(false), totalAnimTime);
    return () => clearTimeout(id);
  }, [loadPhase, shouldStagger, staggerRetireDelayMs]);
  /* v8 ignore stop */

  const scrollContainerRef = useScrollContainer();

  // Scroll to top when content transitions in after a settings change (not on initial mount or back nav)
  /* v8 ignore start � scroll reset on settings change */
  useEffect(() => {
    if (loadPhase === LoadPhase.ContentIn && isSettingsChangeRef.current) {
      isSettingsChangeRef.current = false;
      scrollContainerRef.current?.scrollTo(0, 0);
      clearScrollCache('songs');
    }
  }, [loadPhase, scrollContainerRef]);
  /* v8 ignore stop */

  // -- Virtual list --
  const SONG_ROW_HEIGHT = songRowMobile ? 122 : 68;
  const SECTION_ROW_HEIGHT = songRowMobile ? 44 : 52;
  const VIRTUAL_ROW_GAP = 2;
  const listParentRef = useRef<HTMLDivElement>(null);
  const [listScrollMargin, setListScrollMargin] = useState(0);
  const resolveListScrollMargin = useCallback((scrollEl: HTMLElement | null = scrollContainerRef.current) => {
    const listEl = listParentRef.current;
    if (!scrollEl || !listEl) {
      return 0;
    }

    const scrollRect = scrollEl.getBoundingClientRect();
    const listRect = listEl.getBoundingClientRect();
    return Math.max(0, scrollEl.scrollTop + listRect.top - scrollRect.top);
  }, [scrollContainerRef]);

  useEffect(() => {
    if (loadPhase !== LoadPhase.ContentIn || filtered.length === 0) {
      setListScrollMargin(0);
      return;
    }

    const updateListScrollMargin = () => {
      const nextMargin = Math.round(resolveListScrollMargin());
      setListScrollMargin((current) => current === nextMargin ? current : nextMargin);
    };

    updateListScrollMargin();

    const resizeObserver = typeof ResizeObserver !== 'undefined'
      ? new ResizeObserver(updateListScrollMargin)
      : null;
    if (containerRef.current) {
      resizeObserver?.observe(containerRef.current);
    }
    if (listParentRef.current) {
      resizeObserver?.observe(listParentRef.current);
    }
    if (scrollContainerRef.current) {
      resizeObserver?.observe(scrollContainerRef.current);
    }
    window.addEventListener('resize', updateListScrollMargin);

    return () => {
      resizeObserver?.disconnect();
      window.removeEventListener('resize', updateListScrollMargin);
    };
  }, [filtered.length, loadPhase, resolveListScrollMargin, scrollContainerRef]);

  const quickLinkTopById = useMemo(() => {
    const offsets = new Map<string, number>();
    if (!hasQuickLinkSections) {
      return offsets;
    }

    let offset = 0;
    for (const row of sectionModel.rows) {
      if (row.type === 'section') {
        offsets.set(row.section.id, offset);
      }
      offset += (row.type === 'section' ? SECTION_ROW_HEIGHT : SONG_ROW_HEIGHT) + VIRTUAL_ROW_GAP;
    }

    return offsets;
  }, [SECTION_ROW_HEIGHT, SONG_ROW_HEIGHT, hasQuickLinkSections, sectionModel.rows]);
  const getQuickLinkItemTop = useCallback((id: string, scrollEl: HTMLElement) => {
    const rowTop = quickLinkTopById.get(id);
    if (rowTop == null) {
      return null;
    }

    return resolveListScrollMargin(scrollEl) + rowTop;
  }, [quickLinkTopById, resolveListScrollMargin]);

  const {
    activeItemId,
    quickLinksOpen,
    openQuickLinks,
    closeQuickLinks,
    handleQuickLinkSelect,
    registerSectionRef,
  } = usePageQuickLinks<SongQuickLink>({
    items: quickLinkItems,
    scrollContainerRef,
    isDesktopRailEnabled: isWideDesktop,
    getItemTop: getQuickLinkItemTop,
  });

  const virtualizer = useVirtualizer({
    count: loadPhase === LoadPhase.ContentIn ? sectionModel.rows.length : 0,
    estimateSize: (index) => sectionModel.rows[index]?.type === 'section' ? SECTION_ROW_HEIGHT : SONG_ROW_HEIGHT,
    overscan: 8,
    gap: VIRTUAL_ROW_GAP,
    getScrollElement: () => scrollContainerRef.current,
    scrollMargin: listScrollMargin,
    scrollPaddingStart: DEFAULT_SONGS_SCROLL_OFFSET,
  });

  const handleSongQuickLinkSelect = useCallback((link: SongQuickLink) => {
    handleQuickLinkSelect(link, { skipScroll: true });
    virtualizer.scrollToIndex(link.rowIndex, { align: 'start', behavior: 'smooth' });
  }, [handleQuickLinkSelect, virtualizer]);

  const handleModalQuickLinkSelect = useCallback((link: SongQuickLink) => {
    handleSongQuickLinkSelect(link);
    closeQuickLinks();
  }, [closeQuickLinks, handleSongQuickLinkSelect]);

  const pageQuickLinks = useMemo<PageQuickLinksConfig | undefined>(() => {
    if (quickLinkItems.length < 2 || loadPhase !== LoadPhase.ContentIn) {
      return undefined;
    }

    return {
      title: quickLinksTitle,
      items: quickLinkItems,
      activeItemId,
      visible: quickLinksOpen,
      onOpen: openQuickLinks,
      onClose: closeQuickLinks,
      desktopRailRevealDelayMs: isWideDesktop ? desktopRailRevealDelayMs : 0,
      onSelect: (item) => {
        const nextItem = item as SongQuickLink;
        if (isWideDesktop) {
          handleSongQuickLinkSelect(nextItem);
          return;
        }
        handleModalQuickLinkSelect(nextItem);
      },
      testIdPrefix: 'songs',
    };
  }, [activeItemId, closeQuickLinks, desktopRailRevealDelayMs, handleModalQuickLinkSelect, handleSongQuickLinkSelect, isWideDesktop, loadPhase, openQuickLinks, quickLinkItems, quickLinksOpen, quickLinksTitle]);

  const quickLinksButtonLabel = t('songs.quickLinksButton');
  const compactQuickLinksAction = !isWideDesktop && pageQuickLinks
    ? (
      <ActionPill
        icon={<IoCompass size={Size.iconAction} />}
        label={quickLinksButtonLabel}
        onClick={openQuickLinks}
      />
    )
    : undefined;

  const emptyStagger = useStaggerStyle(200, { skip: !shouldStagger });
  const songsStyles = useSongsStyles();
  const showMobilePageHeader = !isMobileChrome || appSettings.showButtonsInHeaderMobile;

  if (error) {
    const parsed = parseApiError(error);
    return <EmptyState fullPage title={parsed.title} subtitle={parsed.subtitle} style={buildStaggerStyle(200)} onAnimationEnd={clearStaggerStyle} />;
  }

  return (
    <Page
      scrollRestoreKey="songs"
      scrollDeps={[loadPhase, filtered, quickLinkItems.length]}
      staggerRushRef={staggerRushRef}
      containerStyle={{ paddingTop: Layout.paddingTop }}
      firstRun={{ key: 'songs', label: t('nav.songs'), slides: songsSlidesMemo, gateContext: firstRunGateCtx }}
      fabSpacer={isMobileChrome ? 'fixed' : 'end'}
      quickLinks={pageQuickLinks}
      before={<>
        <LoadGate phase={loadPhase} overlay>
          {isMobileChrome ? (
            <PageHeaderTransition visible={showMobilePageHeader}>
              <PageHeader
                actions={compactQuickLinksAction}
              />
            </PageHeaderTransition>
          ) : (
            <PageHeader
              title={
                <div style={{ visibility: (toolbarShownRef.current || loadPhase === LoadPhase.ContentIn) ? 'visible' : 'hidden' } as CSSProperties}>
                  <SongsToolbar
                    search={search}
                    onSearchChange={setSearch}
                    instrument={settings.instrument}
                    sortActive={sortActive}
                    filtersActive={filtersActive}
                    hasSongs={songs.length > 0 && !isLoading}
                    hasPlayer={hasPlayer}
                    filteredCount={filtered.length}
                    totalCount={songs.length}
                    onOpenSort={openSort}
                    onOpenFilter={openFilter}
                  />
                </div>
              }
              actionsAlign="start"
              actions={compactQuickLinksAction}
            />
          )}
        </LoadGate>
      </>}
      after={<>
        <SortModal
          visible={sortModal.visible}
          draft={sortModal.draft}
          savedDraft={{
            sortMode: settings.sortMode,
            sortAscending: settings.sortAscending,
            metadataOrder: settings.metadataOrder,
            instrumentOrder: settings.instrumentOrder,
          }}
          instrumentFilter={settings.instrument}
          hasPlayer={!!playerData}
          hideItemShop={!isShopVisible}
          metadataVisibility={{
            score: appSettings.metadataShowScore,
            percentage: appSettings.metadataShowPercentage,
            percentile: appSettings.metadataShowPercentile,
            seasonachieved: appSettings.metadataShowSeasonAchieved,
            intensity: appSettings.metadataShowIntensity,
            difficulty: appSettings.metadataShowGameDifficulty,
            stars: appSettings.metadataShowStars,
            lastplayed: appSettings.metadataShowLastPlayed,
          }}
          songRowVisualOrderEnabled={appSettings.songRowVisualOrderEnabled}
          onChange={sortModal.setDraft}
          onCancel={sortModal.close}
          onReset={resetSort}
          onApply={applySort}
        />
        <FilterModal
          visible={filterModal.visible}
          draft={filterModal.draft}
          savedDraft={{ ...settings.filters, instrumentFilter: settings.instrument }}
          availableSeasons={availableSeasons}
          onChange={filterModal.setDraft}
          onCancel={filterModal.close}
          onReset={resetFilter}
          onApply={applyFilter}
        />
      </>}
    >
      <div ref={containerRef} style={songsStyles.container}>
        {(bannerVisible || !bannerCollapsed) && (
          <CollapseOnExit show={bannerVisible} onCollapsed={() => setBannerCollapsed(true)}>
            {isSyncing ? (
              <SyncBanner
                phase={syncPhase}
                backfillProgress={backfillProgress}
                historyProgress={historyProgress}
                rivalsProgress={rivalsProgress}
                itemsCompleted={itemsCompleted}
                totalItems={totalItems}
                entriesFound={entriesFound}
                currentSongName={currentSongName}
                seasonsQueried={seasonsQueried}
                rivalsFound={rivalsFound}
                isThrottled={isThrottled}
                throttleStatusKey={throttleStatusKey}
                probeStatusKey={probeStatusKey}
                nextRetrySeconds={nextRetrySeconds}
              />
            ) : showCompleteBanner ? (
              <SyncCompleteBanner
                onDismissed={() => { setShowCompleteBanner(false); dismissSyncBanner(); }}
                pendingRankUpdate={pendingRankUpdate}
                estimatedRankUpdateMinutes={estimatedRankUpdateMinutes}
              />
            ) : null}
          </CollapseOnExit>
        )}
        {loadPhase === LoadPhase.ContentIn && filtered.length === 0 ? (
          <EmptyState
            fullPage
            title={t('songs.noResults')}
            subtitle={filtersActive ? t('songs.noResultsSubtitle') : t('common.serviceDown')}
            style={emptyStagger.style}
            onAnimationEnd={emptyStagger.onAnimationEnd}
          />
        ) : (
          <div
            ref={listParentRef}
            data-testid="songs-virtual-list"
            style={{ ...songsStyles.list,
              height: virtualizer.getTotalSize(),
              position: 'relative',
            }}
          >
            {loadPhase === LoadPhase.ContentIn && virtualizer.getVirtualItems().map((virtualRow) => {
                const row = sectionModel.rows[virtualRow.index]!;
                const rowDelay = getRowStaggerDelay(virtualRow.index);
                return (
                  <div
                    key={row.type === 'section' ? `section:${row.section.id}` : `song:${row.song.songId}`}
                    style={{
                      position: 'absolute',
                      top: 0,
                      left: 0,
                      width: '100%',
                      transform: `translateY(${virtualRow.start - virtualizer.options.scrollMargin}px)`,
                    }}
                    ref={virtualizer.measureElement}
                    data-index={virtualRow.index}
                  >
                    {row.type === 'section' ? (
                      <div
                        ref={(element) => registerSectionRef(row.section.id, element)}
                        data-testid={`songs-section-${getPageQuickLinkTestId(row.section.id)}`}
                        aria-label={row.section.landmarkLabel}
                        style={{
                          ...songsStyles.sectionRow,
                          paddingTop: row.section.rowIndex === 0 ? 0 : Gap.lg,
                          ...buildStaggerStyle(rowDelay),
                        }}
                        onAnimationEnd={clearStaggerStyle}
                      >
                        <div style={songsStyles.sectionDivider} />
                        <div style={songsStyles.sectionLabelRow}>
                          {renderSongSectionLabel(settings.sortMode, row.section, songsStyles.sectionLabel)}
                        </div>
                      </div>
                    ) : (
                      <SongRow
                        song={row.song}
                        score={hasPlayer ? scoreMap.get(row.song.songId) : undefined}
                        instrument={instrument}
                        instrumentFilter={settings.instrument}
                        allScoreMap={hasPlayer ? allScoreMap.get(row.song.songId) : undefined}
                        showInstrumentIcons={hasPlayer && !appSettings.songsHideInstrumentIcons}
                        enabledInstruments={enabledInstruments}
                        metadataOrder={visibleMetadataOrder}
                        sortMode={settings.sortMode}
                        isMobile={songRowMobile}
                        staggerDelay={rowDelay}
                        shopHighlight={isShopHighlighted(row.song.songId)}
                        shopHighlightRed={isLeavingTomorrow(row.song.songId)}
                        invalidInstruments={invalidScoreMap.get(row.song.songId)}
                        containerWidth={containerWidth}
                      />
                    )}
                  </div>
                );
            })}
          </div>
        )}
      </div>
    </Page>
  );
}

function useSongsStyles() {
  return useMemo(() => ({
    container: {
      width: CssValue.full,
      boxSizing: BoxSizing.borderBox,
    } as CSSProperties,
    list: {
      paddingTop: Gap.lg,
    } as CSSProperties,
    sectionRow: {
      display: 'flex',
      flexDirection: 'column' as const,
      gap: Gap.sm,
      width: '100%',
      boxSizing: BoxSizing.borderBox,
    } as CSSProperties,
    sectionDivider: {
      width: '100%',
      height: 1,
    } as CSSProperties,
    sectionLabelRow: {
      display: 'flex',
      alignItems: 'center',
      padding: padding(0, Gap.sm),
    } as CSSProperties,
    sectionLabel: {
      color: Colors.textSecondary,
      fontSize: Font.md,
      fontWeight: 700,
      letterSpacing: '0.08em',
      textTransform: 'uppercase' as const,
    } as CSSProperties,
    center: {
      ...flexCenter,
      minHeight: CssValue.viewportFull,
      color: Colors.textSecondary,
      backgroundColor: Colors.backgroundApp,
      fontSize: Font.lg,
    } as CSSProperties,
  }), []);
}

/** Render a single metadata element for the given key. Mirrors mobile renderMetadataElement. */

