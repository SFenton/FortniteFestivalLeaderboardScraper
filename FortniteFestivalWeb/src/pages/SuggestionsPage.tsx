import { useState, useEffect, useCallback, useMemo, useRef, type CSSProperties } from 'react';
import { IoFunnel } from 'react-icons/io5';
import { Link } from 'react-router-dom';
import InfiniteScroll from 'react-infinite-scroll-component';
import { useFestival } from '../contexts/FestivalContext';
import { usePlayerData } from '../contexts/PlayerDataContext';
import { useSuggestions } from '../hooks/useSuggestions';
import { serverSongToCore, buildScoresIndex } from '../utils/suggestionAdapter';
import { InstrumentIcon, getInstrumentStatusVisual } from '../components/InstrumentIcons';
import SuggestionsFilterModal from '../components/SuggestionsFilterModal';
import type { SuggestionsFilterDraft } from '../components/SuggestionsFilterModal';
import { defaultSuggestionsFilterDraft, isSuggestionsFilterActive } from '../components/SuggestionsFilterModal';
import { InstrumentKeys } from '@festival/core/instruments';
import type { LeaderboardData } from '@festival/core/models';
import type { SuggestionCategory, SuggestionSongItem } from '@festival/core/suggestions/types';
import type { InstrumentKey } from '@festival/core/instruments';
import { shouldShowCategory, filterCategoryForInstruments } from '@festival/core/instrumentFilters';
import { globalKeyFor, getCategoryTypeId, getCategoryInstrument, perInstrumentKeyFor } from '@festival/core/suggestions/suggestionFilterConfig';
import { useSettings } from '../contexts/SettingsContext';
import type { AppSettings } from '../contexts/SettingsContext';
import { Colors, Font, Gap, Radius, Layout, MaxWidth, Size, goldFill, frostedCard } from '../theme';
import { useIsMobile } from '../hooks/useIsMobile';
import { useFabSearch } from '../contexts/FabSearchContext';
import { useScrollFade } from '../hooks/useScrollFade';
import type { InstrumentKey as ServerInstrumentKey } from '../models';

/** Clears animation styles on completion so backdrop-filter works on children. */
function FadeInDiv({ delay, hidden, children, style }: { delay: number; hidden?: boolean; children: React.ReactNode; style?: CSSProperties }) {
  const ref = useRef<HTMLDivElement>(null);
  const handleEnd = useCallback(() => {
    const el = ref.current;
    if (!el) return;
    el.style.opacity = '';
    el.style.animation = '';
  }, []);
  if (hidden) return <div style={{ opacity: 0 }}>{children}</div>;
  return (
    <div
      ref={ref}
      style={{ opacity: 0, animation: `fadeInUp 400ms ease-out ${delay}ms forwards`, ...style }}
      onAnimationEnd={handleEnd}
    >
      {children}
    </div>
  );
}

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

export default function SuggestionsPage({ accountId }: Props) {
  const { settings: appSettings } = useSettings();
  const {
    state: { songs, isLoading },
  } = useFestival();

  const { playerData, playerLoading } = usePlayerData();
  const isMobile = useIsMobile();

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

  const { categories, loadMore, hasMore } = useSuggestions(accountId, coreSongs, scoresIndex);

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
  useEffect(() => {
    fabSearch.registerSuggestionsActions({ openFilter });
  });

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
    // Schedule another loadMore so the user isn't stuck.
    const id = requestAnimationFrame(() => loadMore());
    return () => cancelAnimationFrame(id);
  }, [categories.length, visibleCategories.length, hasMore, filterExhausted, loadMore]);

  const effectiveHasMore = hasMore && !filterExhausted;

  const filteredLoadMore = useCallback(() => {
    if (filterExhausted) return;
    loadMore();
  }, [loadMore, filterExhausted]);

  // ── Spinner → staggered-content transition ──
  const dataReady = !(isLoading || playerLoading) || categories.length > 0;
  const [phase, setPhase] = useState<'loading' | 'spinnerOut' | 'contentIn'>(
    dataReady ? 'contentIn' : 'loading',
  );

  useEffect(() => {
    if (!dataReady || phase !== 'loading') return;
    setPhase('spinnerOut');
  }, [dataReady, phase]);

  useEffect(() => {
    if (phase !== 'spinnerOut') return;
    const id = setTimeout(() => setPhase('contentIn'), 500);
    return () => clearTimeout(id);
  }, [phase]);

  // Per-card scroll fade
  const updateCardFade = useScrollFade(scrollRef, listRef, [phase, visibleCategories]);

  const handleScroll = useCallback(() => {
    updateCardFade();
  }, [updateCardFade]);

  // Track how many category cards have already been revealed so that newly
  // loaded batches get their own stagger starting from delay 0.
  const revealedCountRef = useRef(0);

  const getCardDelay = (index: number): number | null => {
    if (phase !== 'contentIn') return null;                   // hidden behind spinner
    if (index < revealedCountRef.current) return -1;          // already visible, no animation
    const offset = index - revealedCountRef.current;
    return offset * 125;
  };

  // After each render, mark all current cards as revealed.
  useEffect(() => {
    if (phase === 'contentIn') {
      revealedCountRef.current = visibleCategories.length;
    }
  }, [visibleCategories.length, phase]);

  if (!playerData && !playerLoading && categories.length === 0) {
    return <div style={styles.center}>Could not load player data.</div>;
  }

  if (categories.length === 0 && !hasMore) {
    return (
      <div style={styles.page}>
        <div style={styles.container}>
          <div style={styles.emptyState}>
            <div style={styles.emptyTitle}>No Suggestions Available</div>
            <div style={styles.emptySubtitle}>
              The service may be down unexpectedly. Please refresh to try again.
            </div>
          </div>
        </div>
        <SuggestionsFilterModal
          visible={showFilter}
          draft={filterDraft}
          instrumentVisibility={instrumentVisibility}
          onChange={setFilterDraft}
          onCancel={() => setShowFilter(false)}
          onReset={resetFilter}
          onApply={applyFilter}
        />
      </div>
    );
  }

  const headerStagger: React.CSSProperties = phase === 'contentIn'
    ? { opacity: 0, animation: 'fadeInUp 400ms ease-out forwards' }
    : { opacity: 0 };

  return (
    <div style={styles.page}>
      {/* Spinner overlay — visible during loading & spinnerOut */}
      {phase !== 'contentIn' && (
        <div
          style={{
            ...styles.spinnerOverlay,
            ...(phase === 'spinnerOut'
              ? { animation: 'fadeOut 500ms ease-out forwards' }
              : {}),
          }}
        >
          <div style={styles.arcSpinner} />
        </div>
      )}
      {!isMobile && (
      <div style={styles.header}>
        <div style={styles.container}>
          <div style={{ ...styles.headerRow, ...headerStagger }}>
            <button
              style={{ ...styles.iconBtn, ...(filtersActive ? styles.iconBtnActive : {}) }}
              onClick={openFilter}
              title="Filter"
              aria-label="Filter suggestions"
            >
              <IoFunnel size={18} />
              {filtersActive && <span style={styles.filterDot} />}
            </button>
          </div>
        </div>
      </div>
      )}
      <div id="suggestions-scroll" ref={scrollRef} onScroll={handleScroll} style={styles.scrollArea}>
      <div style={{ ...styles.container, ...(isMobile ? { paddingTop: Gap.sm } : {}) }}>
        {visibleCategories.length === 0 && (categories.length > 0 || !effectiveHasMore) ? (
          <div style={styles.emptyState}>
            <div style={styles.emptyTitle}>No Suggestions Available</div>
            <div style={styles.emptySubtitle}>
              {filtersActive
                ? 'Try changing your filters to see more suggestions.'
                : 'Play some songs first!'}
            </div>
          </div>
        ) : (
          <InfiniteScroll
            dataLength={visibleCategories.length}
            next={filteredLoadMore}
            hasMore={effectiveHasMore}
            loader={<div style={styles.loader}><div style={styles.loaderSpinner} /></div>}
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
    <div style={styles.card}>
      <div style={styles.cardHeader}>
        <div style={styles.cardHeaderRow}>
          <div>
            <span style={styles.cardTitle}>{category.title}</span>
            <span style={styles.cardDesc}>{category.description}</span>
          </div>
          {catInstrument && (
            <InstrumentIcon instrument={catInstrument} size={36} />
          )}
        </div>
      </div>
      <div style={styles.songList}>
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
  const prefixes = ['unfc_', 'unplayed_', 'almost_elite_', 'pct_push_'];
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
  | 'percentile'        // almost_elite/pct_push: percentile pill + instrument icon
  | 'unfcAccuracy'      // unfc_*: bold accuracy %
  | 'hidden';           // variety/artist/samename_title/unplayed_any: nothing

function getRowLayout(categoryKey: string): RowLayout {
  const k = categoryKey.toLowerCase();
  if (k.startsWith('variety_pack') || k.startsWith('artist_sampler_') || k.startsWith('artist_unplayed_')
    || k.startsWith('unplayed_')
    || (k.startsWith('samename_') && !k.startsWith('samename_nearfc_'))) return 'hidden';
  if (k.startsWith('unfc_')) return 'unfcAccuracy';
  if (k.startsWith('almost_elite') || k.startsWith('pct_push')) return 'percentile';
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
  const starCount = song.stars ?? 0;
  const isGold = starCount >= 6;
  const displayStars = isGold ? 5 : starCount;

  // Instrument from the song item, or inferred from the category key
  const instrument = song.instrumentKey ?? getCatInstrument(categoryKey);
  const songUrl = instrument
    ? `/songs/${song.songId}?instrument=${CORE_TO_SERVER_INSTRUMENT[instrument]}`
    : `/songs/${song.songId}`;

  return (
    <Link to={songUrl} style={styles.row}>
      {albumArt ? (
        <img src={albumArt} alt="" style={styles.thumb} loading="lazy" />
      ) : (
        <div style={{ ...styles.thumb, ...styles.thumbPlaceholder }} />
      )}
      <div style={styles.rowText}>
        <span style={styles.rowTitle}>{song.title}</span>
        <span style={styles.rowArtist}>{song.artist}</span>
        {/* Star gains: show stars + score beneath title */}
        {layout === 'singleInstrument' && categoryKey.startsWith('star_gains') && starCount > 0 && (
          <span style={{ ...styles.starRow, ...(isGold ? { color: Colors.gold } : {}) }}>
            {'★'.repeat(displayStars)}
          </span>
        )}
      </div>
      <RightContent song={song} layout={layout} leaderboardData={leaderboardData} />
    </Link>
  );
}

function RightContent({
  song,
  layout,
  leaderboardData,
}: {
  song: SuggestionSongItem;
  layout: RowLayout;
  leaderboardData?: LeaderboardData;
}) {
  if (layout === 'hidden') return null;

  if (layout === 'unfcAccuracy') {
    const pct = song.percent;
    const display = typeof pct === 'number' && pct > 0
      ? `${Math.max(0, Math.min(99, Math.floor(pct)))}%`
      : null;
    return display ? <span style={styles.unfcPct}>{display}</span> : null;
  }

  if (layout === 'percentile') {
    const display = song.percentileDisplay;
    const isTop5 = display === 'Top 1%' || display === 'Top 2%' || display === 'Top 3%' || display === 'Top 4%' || display === 'Top 5%';
    return (
      <div style={styles.badges}>
        {display && (
          <span style={{ ...styles.percentilePill, ...(isTop5 ? styles.percentilePillGold : {}) }}>
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
      <div style={styles.badges}>
        <InstrumentIcon instrument={song.instrumentKey} size={28} />
      </div>
    ) : null;
  }

  // instrumentChips: show 6 colored circles
  return (
    <div style={styles.instrumentChipsRow}>
      {InstrumentKeys.map((ins) => {
        const tr = leaderboardData ? (leaderboardData as Record<string, unknown>)[ins] as { numStars?: number; isFullCombo?: boolean } | undefined : undefined;
        const hasScore = !!tr && (tr.numStars ?? 0) > 0;
        const isFC = !!tr?.isFullCombo;
        const { fill, stroke } = getInstrumentStatusVisual(hasScore, isFC);
        return (
          <div
            key={ins}
            style={{
              ...styles.instrumentChip,
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

const styles: Record<string, React.CSSProperties> = {
  page: {
    height: '100%',
    display: 'flex',
    flexDirection: 'column' as const,
    color: Colors.textPrimary,
    fontFamily:
      "-apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif",
  },
  header: {
    flexShrink: 0,
    zIndex: 10,
  },
  bottomToolbar: {
    flexShrink: 0,
    zIndex: 10,
  },
  bottomToolbarInner: {
    display: 'flex',
    width: '100%',
    maxWidth: MaxWidth.card,
    margin: '0 auto',
    padding: `${Gap.md}px ${Layout.paddingHorizontal}px`,
    boxSizing: 'border-box' as const,
  },
  scrollArea: {
    flex: 1,
    overflowY: 'auto' as const,
  },
  container: {
    maxWidth: MaxWidth.card,
    margin: '0 auto',
    padding: `${Layout.paddingTop}px ${Layout.paddingHorizontal}px`,
  },
  center: {
    display: 'flex',
    justifyContent: 'center',
    alignItems: 'center',
    minHeight: '100vh',
  },
  arcSpinner: {
    width: 48,
    height: 48,
    border: '4px solid rgba(255,255,255,0.10)',
    borderTopColor: Colors.accentPurple,
    borderRadius: '50%',
    animation: 'spin 0.8s linear infinite',
  },
  spinnerOverlay: {
    position: 'fixed' as const,
    inset: 0,
    zIndex: 2,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
  },
  heading: {
    fontSize: Font.title,
    fontWeight: 700,
    margin: 0,
  },
  headerRow: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
  },
  iconBtn: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    position: 'relative' as const,
    width: Size.control,
    height: Size.control,
    borderRadius: Radius.xs,
    ...frostedCard,
    color: Colors.textTertiary,
    cursor: 'pointer',
  },
  iconBtnMobile: {
    width: 44,
    height: 44,
  },
  iconBtnActive: {
    borderColor: Colors.accentBlue,
    color: Colors.accentBlue,
    backgroundColor: Colors.chipSelectedBgSubtle,
  },
  filterDot: {
    position: 'absolute' as const,
    top: 4,
    right: 4,
    width: 6,
    height: 6,
    borderRadius: '50%',
    backgroundColor: Colors.accentBlue,
  },
  empty: {
    color: Colors.textTertiary,
    fontSize: Font.md,
  },
  emptyState: {
    display: 'flex',
    flexDirection: 'column' as const,
    alignItems: 'center',
    justifyContent: 'center',
    height: 'calc(100dvh - 250px)',
    textAlign: 'center' as const,
  },
  emptyTitle: {
    fontSize: Font.xl,
    fontWeight: 700,
    color: Colors.textPrimary,
    marginBottom: Gap.md,
  },
  emptySubtitle: {
    fontSize: Font.md,
    color: Colors.textMuted,
  },
  loader: {
    display: 'flex',
    justifyContent: 'center',
    padding: `${Gap.section}px 0`,
  },
  loaderSpinner: {
    width: 28,
    height: 28,
    border: '3px solid rgba(255,255,255,0.10)',
    borderTopColor: Colors.accentPurple,
    borderRadius: '50%',
    animation: 'spin 0.8s linear infinite',
  },
  card: {
    ...frostedCard,
    borderRadius: Radius.lg,
    marginBottom: Gap.section,
    overflow: 'hidden',
  },
  cardHeader: {
    padding: `${Gap.xl}px ${Gap.section}px`,
    borderBottom: `1px solid ${Colors.borderSubtle}`,
  },
  cardHeaderRow: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: Gap.xl,
  },
  cardTitle: {
    display: 'block',
    fontSize: Font.lg,
    fontWeight: 700,
    color: Colors.textPrimary,
    marginBottom: Gap.xs,
  },
  cardDesc: {
    display: 'block',
    fontSize: Font.sm,
    color: Colors.textTertiary,
  },
  songList: {
    display: 'flex',
    flexDirection: 'column' as const,
  },
  row: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.xl,
    padding: `${Gap.lg}px ${Gap.section}px`,
    borderBottom: `1px solid ${Colors.borderSubtle}`,
    textDecoration: 'none',
    color: 'inherit',
    transition: 'background-color 0.12s',
  },
  rowText: {
    flex: 1,
    minWidth: 0,
    display: 'flex',
    flexDirection: 'column' as const,
    gap: Gap.xs,
  },
  rowTitle: {
    fontSize: Font.md,
    fontWeight: 600,
    color: Colors.textPrimary,
    whiteSpace: 'nowrap' as const,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
  },
  rowArtist: {
    fontSize: Font.sm,
    color: Colors.textTertiary,
  },
  badges: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.md,
    flexShrink: 0,
  },
  instrumentChipsRow: {
    display: 'flex',
    alignItems: 'center',
    gap: 6,
    flexShrink: 0,
  },
  instrumentChip: {
    width: 34,
    height: 34,
    borderRadius: 17,
    border: '2px solid',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
  },
  unfcPct: {
    fontSize: Font.lg,
    fontWeight: 800,
    color: Colors.textPrimary,
    minWidth: 48,
    textAlign: 'center' as const,
    flexShrink: 0,
  },
  starRow: {
    fontSize: Font.xs,
    color: Colors.textTertiary,
    marginTop: Gap.xs,
  },
  percentilePill: {
    fontSize: Font.xs,
    padding: `${Gap.xs}px ${Gap.md}px`,
    borderRadius: Radius.xs,
    backgroundColor: Colors.surfaceSubtle,
    color: Colors.textMuted,
  },
  percentilePillGold: goldFill,
  thumb: {
    width: Size.thumb,
    height: Size.thumb,
    borderRadius: Radius.xs,
    objectFit: 'cover' as const,
    flexShrink: 0,
  },
  thumbPlaceholder: {
    backgroundColor: Colors.purplePlaceholder,
  },
};
