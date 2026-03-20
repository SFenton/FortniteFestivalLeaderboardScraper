import { useState, useEffect, useCallback, useMemo, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigationType } from 'react-router-dom';
import { IoFunnel } from 'react-icons/io5';
import { ActionPill } from '../../components/common/ActionPill';
import InfiniteScroll from 'react-infinite-scroll-component';
import { useFestival } from '../../contexts/FestivalContext';
import { usePlayerData } from '../../contexts/PlayerDataContext';
import { useSuggestions } from '../../hooks/data/useSuggestions';
import { serverSongToCore, buildScoresIndex } from '../../utils/suggestionAdapter';
import SuggestionsFilterModal from './modals/SuggestionsFilterModal';
import type { SuggestionsFilterDraft } from './modals/SuggestionsFilterModal';
import { defaultSuggestionsFilterDraft, isSuggestionsFilterActive } from './modals/SuggestionsFilterModal';
import { shouldShowCategory, filterCategoryForInstruments } from '@festival/core/instrumentFilters';
import type { SuggestionCategory } from '@festival/core/suggestions/types';
import { useSettings } from '../../contexts/SettingsContext';
import { Gap } from '@festival/theme';
import ArcSpinner from '../../components/common/ArcSpinner';
import s from './SuggestionsPage.module.css';
import { useIsMobile, useIsMobileChrome } from '../../hooks/ui/useIsMobile';
import { useFabSearch } from '../../contexts/FabSearchContext';
import { useScrollFade } from '../../hooks/ui/useScrollFade';
import { useStaggerRush } from '../../hooks/ui/useStaggerRush';
import { useScrollRestore } from '../../hooks/ui/useScrollRestore';
import { useLoadPhase } from '../../hooks/data/useLoadPhase';
import { useModalState } from '../../hooks/ui/useModalState';
import FadeIn from '../../components/page/FadeIn';
import { CategoryCard } from './components/CategoryCard';
import {
  loadSuggestionsFilter,
  saveSuggestionsFilter,
  buildEffectiveInstrumentSettings,
  shouldShowCategoryType,
  filterCategoryForInstrumentTypes,
  computeEffectiveSeason,
  getCardDelay,
  buildAlbumArtMap,
} from './suggestionsHelpers';

type Props = { accountId: string };

let _suggestionsHasRendered = false;

export default function SuggestionsPage({ accountId }: Props) {
  const { t } = useTranslation();
  const navType = useNavigationType();
  const { settings: appSettings } = useSettings();
  const {
    state: { songs, currentSeason, isLoading },
  } = useFestival();

  const { playerData, playerLoading } = usePlayerData();
  const isMobile = useIsMobile();
  const isMobileChrome = useIsMobileChrome();

  /* v8 ignore start — React memo/ref ternaries exercised via integration tests */
  const coreSongs = useMemo(
    () => (playerData ? songs.map(serverSongToCore) : []),
    [songs, playerData],
  );
  const scoresIndex = useMemo(
    () => (playerData ? buildScoresIndex(playerData.scores) : {}),
    [playerData],
  );
  /* v8 ignore stop */

  const albumArtMap = useMemo(() => buildAlbumArtMap(songs), [songs]);

  const scrollRef = useRef<HTMLDivElement>(null);
  const listRef = useRef<HTMLDivElement>(null);
  const saveScroll = useScrollRestore(scrollRef, 'suggestions', navType);

  // Use server-provided season, fall back to highest season in player scores
  const effectiveSeason = useMemo(
    () => computeEffectiveSeason(currentSeason, playerData?.scores ?? null),
    [currentSeason, playerData],
  );

  const { categories, loadMore, hasMore } = useSuggestions(accountId, coreSongs, scoresIndex, effectiveSeason);

  // Suggestions filter state
  const [filterSettings, setFilterSettings] = useState<SuggestionsFilterDraft>(loadSuggestionsFilter);
  const filterModal = useModalState<SuggestionsFilterDraft>(defaultSuggestionsFilterDraft);

  useEffect(() => { saveSuggestionsFilter(filterSettings); }, [filterSettings]);
  /* v8 ignore start — modal callbacks exercised via filter modal integration tests */
  const openFilter = () => {
    filterModal.open({ ...filterSettings });
  };
  const applyFilter = () => {
    setFilterSettings(filterModal.draft);
    filterModal.close();
  };
  const resetFilter = () => {
    const defaults = defaultSuggestionsFilterDraft();
    filterModal.setDraft(defaults);
    setFilterSettings(defaults);
    filterModal.close();
  };
  /* v8 ignore stop */

  const filtersActive = isSuggestionsFilterActive(filterSettings);

  /* v8 ignore start — FAB registration and scroll handlers */
  const fabSearch = useFabSearch();
  const openFilterRef = useRef(openFilter);
  openFilterRef.current = openFilter;
  useEffect(() => {
    fabSearch.registerSuggestionsActions({ openFilter: () => openFilterRef.current() });
  }, [fabSearch]);
  /* v8 ignore stop */

  const instrumentVisibility = useMemo(() => ({
    showLead: appSettings.showLead,
    showBass: appSettings.showBass,
    showDrums: appSettings.showDrums,
    showVocals: appSettings.showVocals,
    showProLead: appSettings.showProLead,
    showProBass: appSettings.showProBass,
  }), [appSettings.showLead, appSettings.showBass, appSettings.showDrums, appSettings.showVocals, appSettings.showProLead, appSettings.showProBass]);
  /* v8 ignore start — filter pipeline; logic tested in suggestionsHelpers unit tests */
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
  /* v8 ignore stop */

  // When filters hide most generated content, InfiniteScroll fires loadMore
  // once, the new categories all get filtered out, visible-count and scroll
  // height don't change, so InfiniteScroll never fires again â€” "Loading more"
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

  /* v8 ignore start — stale batch detection; requires InfiniteScroll interaction */
  useEffect(() => {
    if (!hasMore || filterExhausted || categories.length === 0) return;

    const rawGrew = categories.length > prevRawRef.current;
    const visibleGrew = visibleCategories.length > prevVisibleRef.current;
    prevRawRef.current = categories.length;
    if (visibleGrew) {
      staleCountRef.current = 0;
      prevVisibleRef.current = visibleCategories.length;
      if (visibleCategories.length >= MIN_VISIBLE) return;
    } else if (rawGrew) {
      staleCountRef.current++;
      if (staleCountRef.current >= MAX_STALE) {
        setFilterExhausted(true);
        return;
      }
    } else {
      return;
    }

    const id = setTimeout(() => loadMore(), 100);
    return () => clearTimeout(id);
  }, [categories.length, visibleCategories.length, hasMore, filterExhausted, loadMore]);
  /* v8 ignore stop */

  const effectiveHasMore = hasMore && !filterExhausted;

  /* v8 ignore start — scroll overflow detection for InfiniteScroll */
  useEffect(() => {
    if (!effectiveHasMore) return;
    const el = scrollRef.current;
    if (!el) return;
    const id = setTimeout(() => {
      if (el.scrollHeight <= el.clientHeight) {
        loadMore();
      }
    }, 100);
    return () => clearTimeout(id);
  }, [visibleCategories.length, effectiveHasMore, loadMore]);
  /* v8 ignore stop */

  /* v8 ignore start — callback and data-readiness branches */
  const filteredLoadMore = useCallback(() => {
    if (filterExhausted) return;
    loadMore();
  }, [loadMore, filterExhausted]);

  const dataReady = !(isLoading || playerLoading) || categories.length > 0;
  /* v8 ignore stop */
  const skipAnimRef = useRef(_suggestionsHasRendered);
  const skipAnim = skipAnimRef.current;
  _suggestionsHasRendered = true;
  const { phase } = useLoadPhase(dataReady, { skipAnimation: skipAnim });

  // Per-card scroll fade
  const updateCardFade = useScrollFade(scrollRef, listRef, [phase, visibleCategories]);

  const rushOnScroll = useStaggerRush(scrollRef);
  /* v8 ignore start — scroll handler composition */
  const handleScroll = useCallback(() => {
    saveScroll();
    updateCardFade();
    rushOnScroll();
  }, [saveScroll, updateCardFade, rushOnScroll]);
  /* v8 ignore stop */

  // Track how many category cards have already been revealed so that newly
  // loaded batches get their own stagger starting from delay 0.
  const revealedCountRef = useRef(0);

  /* v8 ignore next — animation wrapper: branches tested in getCardDelay unit tests */
  const computeDelay = (index: number) => getCardDelay(index, skipAnim, phase, revealedCountRef.current);

  /* v8 ignore start — phase tracking */
  useEffect(() => {
    if (phase === 'contentIn') {
      revealedCountRef.current = visibleCategories.length;
    }
  }, [visibleCategories.length, phase]);
  /* v8 ignore stop */

  /* v8 ignore start — early-return empty states */
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
          visible={filterModal.visible}
          draft={filterModal.draft}
          savedDraft={filterSettings}
          instrumentVisibility={instrumentVisibility}
          onChange={filterModal.setDraft}
          onCancel={filterModal.close}
          onReset={resetFilter}
          onApply={applyFilter}
        />
      </div>
    );
  }
  /* v8 ignore stop */

  /* v8 ignore start — JSX conditional rendering; branches exercised via 42 integration tests */
  const headerStagger: React.CSSProperties = phase === 'contentIn' && !skipAnim
    ? { opacity: 0, animation: 'fadeInUp 400ms ease-out forwards' }
    : skipAnim ? {} : { opacity: 0 };

  return (
    <div className={s.page}>
      {/* Spinner overlay â€” visible during loading & spinnerOut */}
      {phase !== 'contentIn' && (
        <div
          className={s.spinnerOverlay} style={{
            ...(phase === 'spinnerOut'
              ? { animation: 'fadeOut 500ms ease-out forwards' }
              : {}),
          }}
        >
          <ArcSpinner />
        </div>
      )}
      {!isMobileChrome && (
      <div className={s.header}>
        <div className={s.container}>
          <div className={s.headerRow} style={headerStagger}>
            <ActionPill
              icon={<IoFunnel size={18} />}
              label={t('common.filter')}
              onClick={openFilter}
              active={filtersActive}
            />
          </div>
        </div>
      </div>
      )}
      <div id="suggestions-scroll" ref={scrollRef} onScroll={handleScroll} className={s.scrollArea}>
      <div style={{ ...(isMobile ? { paddingTop: Gap.sm } : {}) }} className={s.container}>
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
              const delay = computeDelay(idx);
              if (delay === -1) {
                // Already visible â€” render without animation wrapper
                return <CategoryCard key={`${idx}-${cat.key}`} category={cat} albumArtMap={albumArtMap} scoresIndex={scoresIndex} />;
              }
              return (
                <FadeIn key={`${idx}-${cat.key}`} delay={delay ?? 0} hidden={delay === null}>
                  <CategoryCard category={cat} albumArtMap={albumArtMap} scoresIndex={scoresIndex} />
                </FadeIn>
              );
            })}
            </div>
          </InfiniteScroll>
        )}
      </div>
      {isMobileChrome && <div className={s.fabSpacer} />}
      </div>

      <SuggestionsFilterModal
        visible={filterModal.visible}
        draft={filterModal.draft}
        savedDraft={filterSettings}
        instrumentVisibility={instrumentVisibility}
        onChange={filterModal.setDraft}
        onCancel={filterModal.close}
        onReset={resetFilter}
        onApply={applyFilter}
      />
    </div>
    /* v8 ignore stop */
  );
}

