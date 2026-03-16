import { useState, useEffect, useCallback, useMemo, useRef, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { IoFunnel } from 'react-icons/io5';
import { Link } from 'react-router-dom';
import InfiniteScroll from 'react-infinite-scroll-component';
import { useFestival } from '../contexts/FestivalContext';
import { usePlayerData } from '../contexts/PlayerDataContext';
import { useSuggestions } from '../hooks/useSuggestions';
import { serverSongToCore, buildScoresIndex } from '../utils/suggestionAdapter';
import { InstrumentIcon, getInstrumentStatusVisual } from '../components/InstrumentIcons';
import AlbumArt from '../components/AlbumArt';
import SeasonPill from '../components/SeasonPill';
import SuggestionsFilterModal from '../components/modals/SuggestionsFilterModal';
import type { SuggestionsFilterDraft } from '../components/modals/SuggestionsFilterModal';
import { defaultSuggestionsFilterDraft, isSuggestionsFilterActive } from '../components/modals/SuggestionsFilterModal';
import { InstrumentKeys } from '@festival/core/instruments';
import type { LeaderboardData } from '@festival/core/models';
import type { SuggestionCategory, SuggestionSongItem } from '@festival/core/suggestions/types';
import type { InstrumentKey } from '@festival/core/instruments';
import { shouldShowCategory, filterCategoryForInstruments } from '@festival/core/instrumentFilters';
import { globalKeyFor, getCategoryTypeId, getCategoryInstrument, perInstrumentKeyFor } from '@festival/core/suggestions/suggestionFilterConfig';
import { useSettings } from '../contexts/SettingsContext';
import type { AppSettings } from '../contexts/SettingsContext';
import { Colors, Font, Gap, Radius, Layout, MaxWidth, Size, goldFill, goldOutline, goldOutlineSkew, frostedCard } from '@festival/theme';
import s from './SuggestionsPage.module.css';
import { estimateVisibleCount } from '../utils/stagger';
import { useIsMobile, useIsMobileChrome } from '../hooks/useIsMobile';
import { useFabSearch } from '../contexts/FabSearchContext';
import { useScrollFade } from '../hooks/useScrollFade';
import { useStaggerRush } from '../hooks/useStaggerRush';
import { useLoadPhase } from '../hooks/useLoadPhase';
import FadeInDiv from '../components/FadeInDiv';
import type { InstrumentKey as ServerInstrumentKey } from '../models';

const CORE_TO_SERVER_INSTRUMENT: Record<InstrumentKey, ServerInstrumentKey> = {
  guitar: 'Solo_Guitar',
  bass: 'Solo_Bass',
  drums: 'Solo_Drums',
  vocals: 'Solo_Vocals',
  pro_guitar: 'Solo_PeripheralGuitar',
  pro_bass: 'Solo_PeripheralBass',
};

const FILTER_STORAGE_KEY = 'fst-suggestions-filter';

function loadSuggestionsFilter(): SuggestionsFilterDraft {
  try {
    const raw = localStorage.getItem(FILTER_STORAGE_KEY);
    if (raw) {
      const parsed = JSON.parse(raw);
      return { ...defaultSuggestionsFilterDraft(), ...parsed };
    }
  } catch { /* ignore */ }
  return defaultSuggestionsFilterDraft();
}

function saveSuggestionsFilter(draft: SuggestionsFilterDraft) {
  localStorage.setItem(FILTER_STORAGE_KEY, JSON.stringify(draft));
}

type InstrumentShowSettings = {
  showLead: boolean;
  showBass: boolean;
  showDrums: boolean;
  showVocals: boolean;
  showProLead: boolean;
  showProBass: boolean;
};

function buildEffectiveInstrumentSettings(filter: SuggestionsFilterDraft, appSettings: AppSettings): InstrumentShowSettings {
  return {
    showLead: appSettings.showLead && filter.suggestionsLeadFilter,
    showBass: appSettings.showBass && filter.suggestionsBassFilter,
    showDrums: appSettings.showDrums && filter.suggestionsDrumsFilter,
    showVocals: appSettings.showVocals && filter.suggestionsVocalsFilter,
    showProLead: appSettings.showProLead && filter.suggestionsProLeadFilter,
    showProBass: appSettings.showProBass && filter.suggestionsProBassFilter,
  };
}

function shouldShowCategoryType(categoryKey: string, filter: SuggestionsFilterDraft): boolean {
  const typeId = getCategoryTypeId(categoryKey);
  if (!typeId) return true;
  return filter[globalKeyFor(typeId)] ?? true;
}

function filterCategoryForInstrumentTypes(
  cat: SuggestionCategory,
  filter: SuggestionsFilterDraft,
): SuggestionCategory | null {
  const typeId = getCategoryTypeId(cat.key);
  if (!typeId) return cat;
  const catInstrument = getCategoryInstrument(cat.key);
  if (catInstrument) {
    const pk = perInstrumentKeyFor(catInstrument, typeId);
    return (filter[pk] ?? true) ? cat : null;
  }
  const filtered = cat.songs.filter(s => {
    if (!s.instrumentKey) return true;
    const pk = perInstrumentKeyFor(s.instrumentKey, typeId);
    return filter[pk] ?? true;
  });
  if (filtered.length === 0) return null;
  if (filtered.length === cat.songs.length) return cat;
  return { ...cat, songs: filtered };
}

type Props = { accountId: string };

let _suggestionsHasRendered = false;

export default function SuggestionsPage({ accountId }: Props) {
  const { t } = useTranslation();
  const { settings: appSettings } = useSettings();
  const {
    state: { songs, currentSeason, isLoading },
  } = useFestival();

  const { playerData, playerLoading } = usePlayerData();
  const isMobile = useIsMobile();
  const isMobileChrome = useIsMobileChrome();

  const coreSongs = useMemo(
    () => (playerData ? songs.map(serverSongToCore) : []),
    [songs, playerData],
  );
  const scoresIndex = useMemo(
    () => (playerData ? buildScoresIndex(playerData.scores) : {}),
    [playerData],
  );

  const albumArtMap = useMemo(() => {
    const m = new Map<string, string>();
    for (const s of songs) {
      if (s.albumArt) m.set(s.songId, s.albumArt);
    }
    return m;
  }, [songs]);

  const scrollRef = useRef<HTMLDivElement>(null);
  const listRef = useRef<HTMLDivElement>(null);

  // Use server-provided season, fall back to highest season in player scores
  const effectiveSeason = useMemo(() => {
    if (currentSeason > 0) return currentSeason;
    if (!playerData) return 0;
    let max = 0;
    for (const s of playerData.scores) {
      if (s.season != null && s.season > max) max = s.season;
    }
    return max;
  }, [currentSeason, playerData]);

  const { categories, loadMore, hasMore } = useSuggestions(accountId, coreSongs, scoresIndex, effectiveSeason);

  // Suggestions filter state
  const [filterSettings, setFilterSettings] = useState<SuggestionsFilterDraft>(loadSuggestionsFilter);
  const [showFilter, setShowFilter] = useState(false);
  const [filterDraft, setFilterDraft] = useState<SuggestionsFilterDraft>(() => ({ ...filterSettings }));

  useEffect(() => { saveSuggestionsFilter(filterSettings); }, [filterSettings]);

  const openFilter = () => {
    setFilterDraft({ ...filterSettings });
    setShowFilter(true);
  };
  const applyFilter = () => {
    setFilterSettings(filterDraft);
    setShowFilter(false);
  };
  const resetFilter = () => {
    const defaults = defaultSuggestionsFilterDraft();
    setFilterDraft(defaults);
    setFilterSettings(defaults);
    setShowFilter(false);
  };

  const filtersActive = isSuggestionsFilterActive(filterSettings);

  // Register suggestions filter for FAB
  const fabSearch = useFabSearch();
  const openFilterRef = useRef(openFilter);
  openFilterRef.current = openFilter;
  useEffect(() => {
    fabSearch.registerSuggestionsActions({ openFilter: () => openFilterRef.current() });
  }, [fabSearch]);

  const instrumentVisibility = useMemo(() => ({
    showLead: appSettings.showLead,
    showBass: appSettings.showBass,
    showDrums: appSettings.showDrums,
    showVocals: appSettings.showVocals,
    showProLead: appSettings.showProLead,
    showProBass: appSettings.showProBass,
  }), [appSettings.showLead, appSettings.showBass, appSettings.showDrums, appSettings.showVocals, appSettings.showProLead, appSettings.showProBass]);

  const visibleCategories = useMemo(() => {
    const instSettings = buildEffectiveInstrumentSettings(filterSettings, appSettings);
    return categories
      .filter(c => shouldShowCategory(c.key, instSettings))
      .filter(c => shouldShowCategoryType(c.key, filterSettings))
      .map(c => filterCategoryForInstruments(c, instSettings))
      .filter((c): c is SuggestionCategory => c !== null)
      .map(c => filterCategoryForInstrumentTypes(c, filterSettings))
      .filter((c): c is SuggestionCategory => c !== null);
  }, [categories, filterSettings, appSettings]);

  // When filters hide most generated content, InfiniteScroll fires loadMore
  // once, the new categories all get filtered out, visible-count and scroll
  // height don't change, so InfiniteScroll never fires again — "Loading more"
  // gets stuck.
  //
  // Solution: track consecutive batches that produce zero new visible
  // categories.  After each render where raw categories grew but visible
  // didn't, schedule another loadMore.  Stop after MAX_STALE consecutive
  // empty batches and hide the loader.
  const [filterExhausted, setFilterExhausted] = useState(false);
  const prevRawRef = useRef(categories.length);
  const prevVisibleRef = useRef(visibleCategories.length);
  const staleCountRef = useRef(0);
  const MAX_STALE = 15;
  const MIN_VISIBLE = 4;

  useEffect(() => {
    setFilterExhausted(false);
    staleCountRef.current = 0;
    prevRawRef.current = categories.length;
    prevVisibleRef.current = 0;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [filterSettings]);

  useEffect(() => {
    if (!hasMore || filterExhausted || categories.length === 0) return;

    const rawGrew = categories.length > prevRawRef.current;
    const visibleGrew = visibleCategories.length > prevVisibleRef.current;
    prevRawRef.current = categories.length;

    if (visibleGrew) {
      staleCountRef.current = 0;
      prevVisibleRef.current = visibleCategories.length;
      // If we have enough visible content, let InfiniteScroll take over
      if (visibleCategories.length >= MIN_VISIBLE) return;
    } else if (rawGrew) {
      staleCountRef.current++;
      if (staleCountRef.current >= MAX_STALE) {
        setFilterExhausted(true);
        return;
      }
    } else {
      return; // nothing changed
    }

    // Either visible is still too sparse OR raw grew with nothing new visible.
    // Use setTimeout to yield to the browser between batches and avoid a hot loop.
    const id = setTimeout(() => loadMore(), 100);
    return () => clearTimeout(id);
  }, [categories.length, visibleCategories.length, hasMore, filterExhausted, loadMore]);

  const effectiveHasMore = hasMore && !filterExhausted;

  // If InfiniteScroll's scrollable target isn't overflowing, scroll events
  // never fire and `next` is never called.  Detect this after each render
  // and pump another batch so the container eventually becomes scrollable.
  useEffect(() => {
    if (!effectiveHasMore) return;
    const el = scrollRef.current;
    if (!el) return;
    // Wait briefly so the DOM has updated with the latest content.
    const id = setTimeout(() => {
      if (el.scrollHeight <= el.clientHeight) {
        loadMore();
      }
    }, 100);
    return () => clearTimeout(id);
  }, [visibleCategories.length, effectiveHasMore, loadMore]);

  const filteredLoadMore = useCallback(() => {
    if (filterExhausted) return;
    loadMore();
  }, [loadMore, filterExhausted]);

  // ── Spinner → staggered-content transition ──
  const dataReady = !(isLoading || playerLoading) || categories.length > 0;
  const skipAnimRef = useRef(_suggestionsHasRendered);
  const skipAnim = skipAnimRef.current;
  _suggestionsHasRendered = true;
  const { phase } = useLoadPhase(dataReady, { skipAnimation: skipAnim });

  // Per-card scroll fade
  const updateCardFade = useScrollFade(scrollRef, listRef, [phase, visibleCategories]);

  const rushOnScroll = useStaggerRush(scrollRef);
  const handleScroll = useCallback(() => {
    updateCardFade();
    rushOnScroll();
  }, [updateCardFade, rushOnScroll]);

  // Track how many category cards have already been revealed so that newly
  // loaded batches get their own stagger starting from delay 0.
  const revealedCountRef = useRef(0);

  const getCardDelay = (index: number): number | null => {
    if (skipAnim) return -1;                                   // skip all animation
    if (phase !== 'contentIn') return null;                   // hidden behind spinner
    if (index < revealedCountRef.current) return -1;          // already visible, no animation
    const offset = index - revealedCountRef.current;
    const maxVisible = estimateVisibleCount(200);
    if (offset >= maxVisible) return -1;                      // beyond viewport, show instantly
    return offset * 125;
  };

  // After each render, mark all current cards as revealed.
  useEffect(() => {
    if (phase === 'contentIn') {
      revealedCountRef.current = visibleCategories.length;
    }
  }, [visibleCategories.length, phase]);

  if (!playerData && !playerLoading && categories.length === 0) {
    return <div className={s.center}>{t('common.couldNotLoadPlayer')}</div>;
  }

  if (categories.length === 0 && !hasMore) {
    return (
      <div className={s.page}>
        <div className={s.container}>
          <div className={s.emptyState}>
            <div className={s.emptyTitle}>{t('suggestions.noSuggestions')}</div>
            <div className={s.emptySubtitle}>
              The service may be down unexpectedly. Please refresh to try again.
            </div>
          </div>
        </div>
        <SuggestionsFilterModal
          visible={showFilter}
          draft={filterDraft}
          savedDraft={filterSettings}
          instrumentVisibility={instrumentVisibility}
          onChange={setFilterDraft}
          onCancel={() => setShowFilter(false)}
          onReset={resetFilter}
          onApply={applyFilter}
        />
      </div>
    );
  }

  const headerStagger: React.CSSProperties = phase === 'contentIn' && !skipAnim
    ? { opacity: 0, animation: 'fadeInUp 400ms ease-out forwards' }
    : skipAnim ? {} : { opacity: 0 };

  return (
    <div className={s.page}>
      {/* Spinner overlay — visible during loading & spinnerOut */}
      {phase !== 'contentIn' && (
        <div
          className={s.spinnerOverlay} style={{
            ...(phase === 'spinnerOut'
              ? { animation: 'fadeOut 500ms ease-out forwards' }
              : {}),
          }}
        >
          <div className={s.arcSpinner} />
        </div>
      )}
      {!isMobileChrome && (
      <div className={s.header}>
        <div className={s.container}>
          <div style={{ ...s.headerRow, ...headerStagger }}>
            <button
              style={{ ...s.iconBtn, ...(filtersActive ? s.iconBtnActive : {}), width: 'auto', paddingLeft: Gap.xl, paddingRight: Gap.xl, gap: Gap.md }}
              onClick={openFilter}
              title={t('common.filter')}
              aria-label={t('common.filterSuggestions')}
            >
              <IoFunnel size={18} />
              <span style={{ fontSize: Font.sm, fontWeight: 600, whiteSpace: 'nowrap' }}>{t('common.filter')}</span>
            </button>
          </div>
        </div>
      </div>
      )}
      <div id="suggestions-scroll" ref={scrollRef} onScroll={handleScroll} className={s.scrollArea}>
      <div style={{ ...s.container, ...(isMobile ? { paddingTop: Gap.sm } : {}) }}>
        {visibleCategories.length === 0 && (categories.length > 0 || !effectiveHasMore) ? (
          <div className={s.emptyState}>
            <div className={s.emptyTitle}>{t('suggestions.noSuggestions')}</div>
            <div className={s.emptySubtitle}>
              {filtersActive
                ? t('suggestions.noSuggestionsFiltered')
                : t('suggestions.playSongsFirst')}
            </div>
          </div>
        ) : (
          <InfiniteScroll
            dataLength={visibleCategories.length}
            next={filteredLoadMore}
            hasMore={effectiveHasMore}
            loader={phase === 'contentIn' ? <div className={s.loader}><div className={s.loaderSpinner} /></div> : <></>}
            scrollThreshold="600px"
            scrollableTarget="suggestions-scroll"
            style={{ overflow: 'visible' }}
          >
            <div ref={listRef} style={{ paddingTop: Gap.lg }}>
            {visibleCategories.map((cat, idx) => {
              const delay = getCardDelay(idx);
              if (delay === -1) {
                // Already visible — render without animation wrapper
                return <CategoryCard key={`${idx}-${cat.key}`} category={cat} albumArtMap={albumArtMap} scoresIndex={scoresIndex} />;
              }
              return (
                <FadeInDiv key={`${idx}-${cat.key}`} delay={delay ?? 0} hidden={delay === null}>
                  <CategoryCard category={cat} albumArtMap={albumArtMap} scoresIndex={scoresIndex} />
                </FadeInDiv>
              );
            })}
            </div>
          </InfiniteScroll>
        )}
      </div>
      </div>

      <SuggestionsFilterModal
        visible={showFilter}
        draft={filterDraft}
        savedDraft={filterSettings}
        instrumentVisibility={instrumentVisibility}
        onChange={setFilterDraft}
        onCancel={() => setShowFilter(false)}
        onReset={resetFilter}
        onApply={applyFilter}
      />
    </div>
  );
}

function CategoryCard({
  category,
  albumArtMap,
  scoresIndex,
}: {
  category: SuggestionCategory;
  albumArtMap: Map<string, string>;
  scoresIndex: Record<string, LeaderboardData>;
}) {
  const catInstrument = getCatInstrument(category.key);
  return (
    <div style={s.card}>
      <div className={s.cardHeader}>
        <div className={s.cardHeaderRow}>
          <div>
            <span className={s.cardTitle}>{category.title}</span>
            <span className={s.cardDesc}>{category.description}</span>
          </div>
          {catInstrument && (
            <InstrumentIcon instrument={catInstrument} size={36} />
          )}
        </div>
      </div>
      <div className={s.songList}>
        {category.songs.map((song) => (
          <SongRow
            key={`${song.songId}-${song.instrumentKey ?? 'any'}`}
            song={song}
            categoryKey={category.key}
            albumArt={albumArtMap.get(song.songId)}
            leaderboardData={scoresIndex[song.songId]}
          />
        ))}
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Category-key classification helpers (mirrors mobile SuggestionSongRow logic)
// ---------------------------------------------------------------------------

function getCatInstrument(key: string): InstrumentKey | null {
  const prefixes = ['unfc_', 'unplayed_', 'almost_elite_', 'pct_push_', 'stale_', 'pct_improve_', 'improve_rankings_'];
  let remainder: string | null = null;
  for (const p of prefixes) {
    if (key.startsWith(p)) {
      remainder = key.substring(p.length);
      break;
    }
  }
  if (!remainder) return null;
  const known: InstrumentKey[] = ['pro_guitar', 'pro_bass', 'guitar', 'bass', 'drums', 'vocals'];
  for (const k of known) {
    if (remainder === k || remainder.startsWith(`${k}_`)) return k;
  }
  return null;
}

type RowLayout =
  | 'instrumentChips'   // default: 6 colored instrument status circles
  | 'singleInstrument'  // FC/near-FC/gold-push/star-gains: single instrument icon
  | 'percentile'        // almost_elite/pct_push/pct_improve: percentile pill + instrument icon
  | 'season'            // stale songs: season pill
  | 'unfcAccuracy'      // unfc_*: bold accuracy %
  | 'hidden';           // variety/artist/samename_title/unplayed_any: nothing

function getRowLayout(categoryKey: string): RowLayout {
  const k = categoryKey.toLowerCase();
  if (k.startsWith('variety_pack') || k.startsWith('artist_sampler_') || k.startsWith('artist_unplayed_')
    || k.startsWith('unplayed_')
    || (k.startsWith('samename_') && !k.startsWith('samename_nearfc_'))) return 'hidden';
  if (k.startsWith('unfc_')) return 'unfcAccuracy';
  if (k.startsWith('stale_')) return 'season';
  if (k.startsWith('almost_elite') || k.startsWith('pct_push') || k.startsWith('pct_improve') || k.startsWith('same_pct') || k.startsWith('improve_rankings')) return 'percentile';
  if (k.startsWith('near_fc') || k.startsWith('almost_six_star') || k.startsWith('more_stars')
    || k.startsWith('first_plays_mixed') || k.startsWith('star_gains')
    || k.startsWith('samename_nearfc_')) return 'singleInstrument';
  return 'instrumentChips';
}

function SongRow({
  song,
  categoryKey,
  albumArt,
  leaderboardData,
}: {
  song: SuggestionSongItem;
  categoryKey: string;
  albumArt?: string;
  leaderboardData?: LeaderboardData;
}) {
  const layout = getRowLayout(categoryKey);
  const isMobile = useIsMobile();
  const starCount = song.stars ?? 0;
  const isGold = starCount >= 6;
  const displayStars = isGold ? 5 : starCount;
  const showStars = layout === 'singleInstrument' && categoryKey.startsWith('star_gains') && starCount > 0;
  const showStarPngs = showStars && isMobile;

  // Instrument from the song item, or inferred from the category key
  const instrument = song.instrumentKey ?? getCatInstrument(categoryKey);
  const songUrl = instrument
    ? `/songs/${song.songId}?instrument=${CORE_TO_SERVER_INSTRUMENT[instrument]}`
    : `/songs/${song.songId}`;

  const starSrc = isGold ? `${import.meta.env.BASE_URL}star_gold.png` : `${import.meta.env.BASE_URL}star_white.png`;

  return (
    <Link to={songUrl} style={showStarPngs ? { ...s.row, flexDirection: 'column' as const, alignItems: 'stretch' as const } : s.row}>
      <div style={showStarPngs ? s.rowMainLine : { display: 'contents' }}>
        <AlbumArt src={albumArt} size={Size.thumb} />
        <div className={s.rowText}>
          <span className={s.rowTitle}>{song.title}</span>
          <span className={s.rowArtist}>{song.artist}{song.year ? ` · ${song.year}` : ''}</span>
        </div>
        <RightContent song={song} layout={layout} leaderboardData={leaderboardData} starCount={showStars && !showStarPngs ? displayStars : 0} starSrc={starSrc} />
      </div>
      {/* Star gains: PNG stars centered below on mobile */}
      {showStarPngs && (
        <div className={s.starPngRow}>
          {Array.from({ length: displayStars }, (_, i) => (
            <img key={i} src={starSrc} alt="★" className={s.starPngImg} />
          ))}
        </div>
      )}
    </Link>
  );
}

function RightContent({
  song,
  layout,
  leaderboardData,
  starCount = 0,
  starSrc,
}: {
  song: SuggestionSongItem;
  layout: RowLayout;
  leaderboardData?: LeaderboardData;
  starCount?: number;
  starSrc?: string;
}) {
  if (layout === 'hidden') return null;

  if (layout === 'unfcAccuracy') {
    const pct = song.percent;
    const display = typeof pct === 'number' && pct > 0
      ? `${Math.max(0, Math.min(99, Math.floor(pct)))}%`
      : null;
    if (!display || typeof pct !== 'number') return null;
    const t = Math.min(Math.max(pct / 100, 0), 1);
    const r = Math.round(220 * (1 - t) + 46 * t);
    const g = Math.round(40 * (1 - t) + 204 * t);
    const b = Math.round(40 * (1 - t) + 113 * t);
    return <span style={{ ...s.unfcPct, color: `rgb(${r},${g},${b})` }}>{display}</span>;
  }

  if (layout === 'season') {
    // Look up the season from leaderboard data for the specific instrument, or max across all
    let season = 0;
    if (leaderboardData) {
      if (song.instrumentKey) {
        const tr = (leaderboardData as Record<string, unknown>)[song.instrumentKey] as { seasonAchieved?: number } | undefined;
        season = tr?.seasonAchieved ?? 0;
      } else {
        for (const ins of InstrumentKeys) {
          const tr = (leaderboardData as Record<string, unknown>)[ins] as { seasonAchieved?: number } | undefined;
          if (tr && (tr.seasonAchieved ?? 0) > season) season = tr.seasonAchieved!;
        }
      }
    }
    return season > 0 ? <SeasonPill season={season} /> : null;
  }

  if (layout === 'percentile') {
    const display = song.percentileDisplay;
    const isTop1 = display === 'Top 1%';
    const isTop5 = display === 'Top 2%' || display === 'Top 3%' || display === 'Top 4%' || display === 'Top 5%';
    const pillStyle = isTop1 ? s.percentileBadgeTop1 : isTop5 ? s.percentileBadgeTop5 : s.percentilePill;
    return (
      <div className={s.badges}>
        {display && (
          <span style={pillStyle}>
            {display}
          </span>
        )}
        {song.instrumentKey && (
          <InstrumentIcon instrument={song.instrumentKey} size={28} />
        )}
      </div>
    );
  }

  if (layout === 'singleInstrument') {
    return song.instrumentKey ? (
      <div className={s.badges}>
        {starCount > 0 && starSrc && (
          <span className={s.starPngInlineRow}>
            {Array.from({ length: starCount }, (_, i) => (
              <img key={i} src={starSrc} alt="★" className={s.starPngImg} />
            ))}
          </span>
        )}
        <InstrumentIcon instrument={song.instrumentKey} size={28} />
      </div>
    ) : null;
  }

  // instrumentChips: show 6 colored circles
  return (
    <div className={s.instrumentChipsRow}>
      {InstrumentKeys.map((ins) => {
        const tr = leaderboardData ? (leaderboardData as Record<string, unknown>)[ins] as { numStars?: number; isFullCombo?: boolean } | undefined : undefined;
        const hasScore = !!tr && (tr.numStars ?? 0) > 0;
        const isFC = !!tr?.isFullCombo;
        const { fill, stroke } = getInstrumentStatusVisual(hasScore, isFC);
        return (
          <div
            key={ins}
            style={{
              ...s.instrumentChip,
              backgroundColor: fill,
              borderColor: stroke,
            }}
          >
            <InstrumentIcon instrument={ins} size={20} />
          </div>
        );
      })}
    </div>
  );
}

