import { useState, useEffect, useCallback, useMemo, useRef, type CSSProperties } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { IoFunnel } from 'react-icons/io5';
import { ActionPill } from '../../components/common/ActionPill';
import { useFestival } from '../../contexts/FestivalContext';
import { usePlayerData } from '../../contexts/PlayerDataContext';
import { useBandFilterAction } from '../../contexts/BandFilterActionContext';
import {
  SUGGESTIONS_CATEGORY_LIMIT,
  useSuggestions,
} from '../../hooks/data/useSuggestions';
import { suggestionsSlides } from './firstRun';
import { serverSongToCore, buildScoresIndex } from '../../utils/suggestionAdapter';
import SuggestionsFilterModal from './modals/SuggestionsFilterModal';
import type { SuggestionsFilterDraft } from './modals/SuggestionsFilterModal';
import { defaultSuggestionsFilterDraft, isSuggestionsFilterActive } from './modals/SuggestionsFilterModal';
import { buildBandSuggestionSource } from './bandSuggestions';
import { shouldShowCategory, filterCategoryForInstruments } from '@festival/core/config';
import { useSettings } from '../../contexts/SettingsContext';
import { api } from '../../api/client';
import { queryKeys } from '../../api/queryKeys';
import { isBandFilterForSelectedProfile } from '../../state/bandFilter';
import type { SelectedBandProfile } from '../../state/selectedProfile';
import {
  Size, Gap, Layout, MaxWidth, Colors, Font, Weight, Radius, Spinner, SpinnerSize,
  CssValue,
  fixedFill, flexCenter, flexColumn, padding,
  FADE_DURATION, SCROLL_PREFETCH_PX,
} from '@festival/theme';
import { LoadPhase } from '@festival/core/runtime';
import Page from '../Page';
import { useScrollContainer } from '../../contexts/ScrollContainerContext';
import EmptyState from '../../components/common/EmptyState';
import { buildStaggerStyle, clearStaggerStyle } from '../../hooks/ui/useStaggerStyle';
import PageHeader from '../../components/common/PageHeader';
import { useIsMobile, useIsMobileChrome } from '../../hooks/ui/useIsMobile';
import { useFabSearch } from '../../contexts/FabSearchContext';
import { usePageTransition } from '../../hooks/ui/usePageTransition';
import { useSetPageReady } from '../../contexts/PageReadyContext';
import { useModalState } from '../../hooks/ui/useModalState';
import PressableButton from '../../components/common/PressableButton';
import { SuggestionsLoadSentinel } from './components/SuggestionsLoadSentinel';
import {
  VirtualizedSuggestionsList,
  type VisibleSuggestionRow,
} from './components/VirtualizedSuggestionsList';
import {
  loadSuggestionsFilter,
  saveSuggestionsFilter,
  buildEffectiveInstrumentSettings,
  shouldShowCategoryType,
  filterCategoryForInstrumentTypes,
  computeEffectiveSeason,
  buildAlbumArtMap,
} from './suggestionsHelpers';

export { beginSuggestionsScrollRestoration } from './suggestionsScrollRestoration';

type SuggestionsPageProps = {
  accountId?: string;
  selectedBand?: SelectedBandProfile | null;
};
type SuggestionsMode = 'solo' | 'band';
const noopBandComboApply = () => {};
const noopBandComboReset = () => {};

export default function SuggestionsPage({ accountId, selectedBand = null }: SuggestionsPageProps) {
  const { t } = useTranslation();
  const { settings: appSettings } = useSettings();
  const mode: SuggestionsMode = selectedBand ? 'band' : 'solo';

  const firstRunGateCtx = useMemo(() => ({ hasPlayer: true }), []);
  const {
    state: { songs, currentSeason, isLoading },
  } = useFestival();

  const { playerData, playerLoading } = usePlayerData();
  const isMobile = useIsMobile();
  const isMobileChrome = useIsMobileChrome();

  
  const coreSongs = useMemo(
    () => (mode === 'solo' && playerData ? songs.map(serverSongToCore) : []),
    [songs, playerData, mode],
  );
  const soloScoresIndex = useMemo(
    () => (mode === 'solo' && playerData ? buildScoresIndex(playerData.scores) : {}),
    [playerData, mode],
  );
  

  const albumArtMap = useMemo(() => buildAlbumArtMap(songs), [songs]);

  const scrollContainerRef = useScrollContainer();

  // Use server-provided season, fall back to highest season in player scores
  const effectiveSeason = useMemo(
    () => computeEffectiveSeason(currentSeason, playerData?.scores ?? null),
    [currentSeason, playerData],
  );

  const bandFilterAction = useBandFilterAction();
  const appliedBandComboFilter = bandFilterAction.appliedFilter ?? null;
  const selectedBandComboFilter = selectedBand && isBandFilterForSelectedProfile(appliedBandComboFilter, selectedBand)
    ? appliedBandComboFilter
    : null;
  const activeBandComboId = selectedBandComboFilter?.comboId;
  const bandComboFilterProps = useMemo(() => {
    if (!isMobile || mode !== 'band' || !selectedBand) return undefined;
    return {
      selectedBand,
      appliedAssignments: selectedBandComboFilter?.assignments ?? [],
      onApply: bandFilterAction.onApplyFilter ?? noopBandComboApply,
      onReset: bandFilterAction.onResetFilter ?? noopBandComboReset,
    };
  }, [bandFilterAction.onApplyFilter, bandFilterAction.onResetFilter, isMobile, mode, selectedBand, selectedBandComboFilter?.assignments]);

  const bandSongRowsQuery = useQuery({
    queryKey: queryKeys.bandSongRows(selectedBand?.bandType ?? '', selectedBand?.teamKey ?? '', activeBandComboId),
    queryFn: ({ signal }) => api.getBandSongRows(selectedBand!.bandType, selectedBand!.teamKey, activeBandComboId, { signal }),
    enabled: !!selectedBand,
    staleTime: 5 * 60 * 1000,
  });

  const bandSource = useMemo(
    () => selectedBand
      ? buildBandSuggestionSource({
          songs,
          performances: bandSongRowsQuery.data?.entries ?? [],
          bandType: selectedBand.bandType,
          comboId: activeBandComboId,
          currentSeason: effectiveSeason,
        })
      : null,
    [activeBandComboId, bandSongRowsQuery.data?.entries, effectiveSeason, selectedBand, songs],
  );

  const bandIdentity = selectedBand
    ? `${selectedBand.bandType}|${selectedBand.teamKey}|${activeBandComboId ?? 'overall'}`
    : 'solo';
  const suggestionCacheKey = mode === 'band' ? `band:${bandIdentity}` : `solo:${accountId ?? ''}`;
  const suggestionSongs = mode === 'band' ? (bandSource?.songs ?? []) : coreSongs;
  const scoresIndex = mode === 'band' ? (bandSource?.scoresIndex ?? {}) : soloScoresIndex;
  const suggestions = useSuggestions(
    accountId ?? '',
    suggestionSongs,
    scoresIndex,
    effectiveSeason,
    {
      mode,
      cacheKey: suggestionCacheKey,
      sourceReady: mode === 'band'
        ? !isLoading && !bandSongRowsQuery.isLoading
        : !!accountId && !!playerData,
      bandComboId: activeBandComboId,
    },
  );
  const {
    categories,
    mixKey,
    loadMore,
    hasMore,
    limitReached,
    loadTriggerCount,
    startNewMix,
    resetScrollPosition,
  } = suggestions;

  // Suggestions filter state
  const [filterSettings, setFilterSettings] = useState<SuggestionsFilterDraft>(loadSuggestionsFilter);
  const filterModal = useModalState<SuggestionsFilterDraft>(defaultSuggestionsFilterDraft);

  useEffect(() => { saveSuggestionsFilter(filterSettings); }, [filterSettings]);
  
  const openFilter = () => {
    filterModal.open({ ...filterSettings });
  };
  const applyFilter = () => {
    setFilterSettings(filterModal.draft);
    filterModal.close();
  };
  const resetFilter = () => {
    filterModal.reset();
  };
  

  const filtersActive = isSuggestionsFilterActive(filterSettings, mode);

  
  const { registerSuggestionsActions } = useFabSearch();
  const openFilterRef = useRef(openFilter);
  openFilterRef.current = openFilter;
  useEffect(() => {
    registerSuggestionsActions({ openFilter: () => openFilterRef.current(), filterActive: filtersActive });
    return () => registerSuggestionsActions(null);
  }, [filtersActive, registerSuggestionsActions]);
  

  const instrumentVisibility = useMemo(() => ({
    showLead: appSettings.showLead,
    showBass: appSettings.showBass,
    showDrums: appSettings.showDrums,
    showVocals: appSettings.showVocals,
    showProLead: appSettings.showProLead,
    showProBass: appSettings.showProBass,
    showPeripheralVocals: appSettings.showPeripheralVocals,
    showPeripheralCymbals: appSettings.showPeripheralCymbals,
    showPeripheralDrums: appSettings.showPeripheralDrums,
  }), [appSettings.showLead, appSettings.showBass, appSettings.showDrums, appSettings.showVocals, appSettings.showProLead, appSettings.showProBass, appSettings.showPeripheralVocals, appSettings.showPeripheralCymbals, appSettings.showPeripheralDrums]);
  
  const effectiveInstrumentSettings = useMemo(
    () => buildEffectiveInstrumentSettings(filterSettings, appSettings),
    [appSettings, filterSettings],
  );
  const virtualizationMeasurementKey = useMemo(
    () => JSON.stringify([filterSettings, effectiveInstrumentSettings]),
    [effectiveInstrumentSettings, filterSettings],
  );
  const visibilityCacheRef = useRef<{
    key: string;
    sourceLength: number;
    rows: VisibleSuggestionRow[];
  }>({ key: '', sourceLength: 0, rows: [] });
  const visibilityCacheKey = `${mixKey ?? suggestionCacheKey}:${virtualizationMeasurementKey}`;
  const visibleCategories = useMemo<VisibleSuggestionRow[]>(() => {
    const cachedVisibility = visibilityCacheRef.current;
    const canAppend = cachedVisibility.key === visibilityCacheKey
      && cachedVisibility.sourceLength <= categories.length;
    const rows = canAppend ? [...cachedVisibility.rows] : [];
    const startIndex = canAppend ? cachedVisibility.sourceLength : 0;
    for (let sourceIndex = startIndex; sourceIndex < categories.length; sourceIndex += 1) {
      const category = categories[sourceIndex]!;
      if (!shouldShowCategory(category.key, effectiveInstrumentSettings)) continue;
      if (!shouldShowCategoryType(category.key, filterSettings)) continue;
      const instrumentFiltered = filterCategoryForInstruments(
        category,
        effectiveInstrumentSettings,
      );
      if (!instrumentFiltered) continue;
      const typeFiltered = filterCategoryForInstrumentTypes(instrumentFiltered, filterSettings);
      if (!typeFiltered) continue;
      rows.push({
        id: `${mixKey ?? suggestionCacheKey}:${sourceIndex}:${category.key}`,
        sourceIndex,
        category: typeFiltered,
      });
    }
    visibilityCacheRef.current = {
      key: visibilityCacheKey,
      sourceLength: categories.length,
      rows,
    };
    return rows;
  }, [
    categories,
    effectiveInstrumentSettings,
    filterSettings,
    visibilityCacheKey,
  ]);
  

  // Track how many category cards have already been revealed so that newly
  // loaded batches get their own stagger starting from delay 0.
  const revealedCountRef = useRef(0);
  const previousFilterSettingsRef = useRef(filterSettings);
  const previousMixKeyRef = useRef(mixKey);
  const MIN_VISIBLE = 4;

  useEffect(() => {
    const filterChanged = previousFilterSettingsRef.current !== filterSettings;
    const identityChanged = previousMixKeyRef.current !== mixKey;
    previousFilterSettingsRef.current = filterSettings;
    previousMixKeyRef.current = mixKey;
    if (!filterChanged && !identityChanged) return;

    revealedCountRef.current = 0;
    resetScrollPosition();
    scrollContainerRef.current?.scrollTo(0, 0);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [filterSettings, mixKey, resetScrollPosition]);

  
  useEffect(() => {
    if (!hasMore || categories.length === 0 || visibleCategories.length >= MIN_VISIBLE) return;

    const id = setTimeout(() => loadMore(), 100);
    return () => clearTimeout(id);
  }, [categories.length, filterSettings, hasMore, loadMore, mixKey, visibleCategories.length]);

  
  const filteredLoadMore = useCallback(() => {
    loadMore();
  }, [loadMore]);

  const dataReady = mode === 'band'
    ? (!isLoading && !bandSongRowsQuery.isLoading) || categories.length > 0
    : !(isLoading || playerLoading) || categories.length > 0;
  const hasCachedData = categories.length > 0;
  const { phase, shouldStagger } = usePageTransition(`suggestions:${mode}:${accountId ?? ''}:${bandIdentity}`, dataReady, hasCachedData);
  useSetPageReady(phase === LoadPhase.ContentIn);
  const skipAnim = !shouldStagger;
  
  useEffect(() => {
    if (phase === LoadPhase.ContentIn) {
      revealedCountRef.current = visibleCategories.length;
    }
  }, [visibleCategories.length, phase]);
  const handleStartNewMix = useCallback(() => {
    revealedCountRef.current = 0;
    startNewMix();
  }, [startNewMix]);
  

  
  if (mode === 'solo' && !playerData && !playerLoading && categories.length === 0) {
    return <EmptyState fullPage title={t('common.couldNotLoadPlayer')} subtitle={t('common.serviceDown')} style={buildStaggerStyle(200)} onAnimationEnd={clearStaggerStyle} />;
  }

  if (categories.length === 0 && !hasMore) {
    return (
      <div style={suggestionsStyles.page}>
        <div style={suggestionsStyles.container}>
          <EmptyState
            fullPage
            title={t('suggestions.noSuggestions')}
            subtitle={t('suggestions.serviceDown')}
          />
        </div>
        <SuggestionsFilterModal
          visible={filterModal.visible}
          draft={filterModal.draft}
          savedDraft={filterSettings}
          mode={mode}
          instrumentVisibility={instrumentVisibility}
          bandComboFilter={bandComboFilterProps}
          onChange={filterModal.setDraft}
          onCancel={filterModal.close}
          onReset={resetFilter}
          onApply={applyFilter}
        />
      </div>
    );
  }
  

  
  const headerStagger: React.CSSProperties = phase === LoadPhase.ContentIn && !skipAnim
    ? { opacity: 0, animation: `fadeInUp ${FADE_DURATION}ms ease-out forwards` }
    : skipAnim ? {} : { opacity: 0 };
  const bandTypeForCards = mode === 'band' ? selectedBand?.bandType : undefined;
  const showEmptyState = visibleCategories.length === 0 && (categories.length > 0 || !hasMore);

  return (
    <Page
      scrollDeps={[phase, visibleCategories.length, mixKey]}
      firstRun={{ key: 'suggestions', label: t('nav.suggestions'), slides: suggestionsSlides, gateContext: firstRunGateCtx }}
      loadPhase={phase}
      before={<>
        {!isMobileChrome && (
          <PageHeader
            title={t('nav.suggestions')}
            style={headerStagger}
            actions={
              <div style={headerStagger}>
                <ActionPill
                  icon={<IoFunnel size={Size.iconFab} />}
                  label={t('common.filter')}
                  onClick={openFilter}
                  active={filtersActive}
                />
              </div>
            }
          />
        )}
      </>}
      after={<>
        <SuggestionsFilterModal
          visible={filterModal.visible}
          draft={filterModal.draft}
          savedDraft={filterSettings}
          mode={mode}
          instrumentVisibility={instrumentVisibility}
          bandComboFilter={bandComboFilterProps}
          onChange={filterModal.setDraft}
          onCancel={filterModal.close}
          onReset={resetFilter}
          onApply={applyFilter}
        />
      </>}
    >
        {showEmptyState && (
          <EmptyState
            fullPage={!limitReached}
            title={t('suggestions.noSuggestions')}
            subtitle={filtersActive
              ? t('suggestions.noSuggestionsFiltered')
              : t('suggestions.playSongsFirst')}
            style={buildStaggerStyle(skipAnim ? null : 200)}
            onAnimationEnd={clearStaggerStyle}
          />
        )}
        <VirtualizedSuggestionsList
          rows={visibleCategories}
          phase={phase}
          skipAnimation={skipAnim}
          revealedCount={revealedCountRef.current}
          identity={mixKey}
          measurementKey={virtualizationMeasurementKey}
          categoryLimit={SUGGESTIONS_CATEGORY_LIMIT}
          generatedCategoryCount={categories.length}
          loadTriggerCount={loadTriggerCount}
          scrollContainerRef={scrollContainerRef}
          albumArtMap={albumArtMap}
          scoresIndex={scoresIndex}
          bandType={bandTypeForCards}
        />
        {limitReached && (
          <div data-testid="suggestions-mix-limit" style={suggestionsStyles.mixLimit}>
            <div style={suggestionsStyles.mixLimitMessage}>
              {t('suggestions.mixLimitReached')}
            </div>
            <PressableButton
              data-testid="suggestions-start-new-mix"
              style={suggestionsStyles.mixLimitButton}
              onPress={handleStartNewMix}
            >
              {t('suggestions.startNewMix')}
            </PressableButton>
          </div>
        )}
        {hasMore && phase === LoadPhase.ContentIn && (
          <div style={suggestionsStyles.loader}><div style={suggestionsStyles.loaderSpinner} /></div>
        )}
        <SuggestionsLoadSentinel
          rootRef={scrollContainerRef}
          disabled={!hasMore || phase !== LoadPhase.ContentIn}
          triggerKey={`${mixKey ?? suggestionCacheKey}:${categories.length}`}
          prefetchPx={SCROLL_PREFETCH_PX}
          onLoadMore={filteredLoadMore}
          fallbackLabel={t('suggestions.loadMore')}
        />
    </Page>
  );
}

const suggestionsStyles = {
  center: {
    ...flexCenter,
    minHeight: CssValue.viewportFull,
  } as CSSProperties,
  page: {} as CSSProperties,
  container: {
    maxWidth: MaxWidth.card,
    margin: CssValue.marginCenter,
    padding: padding(Layout.paddingTop, Layout.paddingHorizontal),
  } as CSSProperties,
  spinnerOverlay: {
    ...fixedFill,
    zIndex: 2,
    ...flexCenter,
  } as CSSProperties,
  loader: {
    ...flexCenter,
    padding: padding(Gap.section, Gap.none),
  } as CSSProperties,
  loaderSpinner: {
    width: Spinner[SpinnerSize.MD].size,
    height: Spinner[SpinnerSize.MD].size,
    borderStyle: 'solid' as const,
    borderWidth: Spinner[SpinnerSize.MD].border,
    borderColor: Spinner.trackColor,
    borderTopColor: Colors.accentPurple,
    borderRadius: CssValue.circle,
    animation: `spin ${Spinner.duration} linear infinite`,
  } as CSSProperties,
  mixLimit: {
    ...flexColumn,
    alignItems: 'center',
    gap: Gap.lg,
    padding: padding(Gap.section, Gap.xl),
    textAlign: 'center',
  } as CSSProperties,
  mixLimitMessage: {
    color: Colors.textSecondary,
    fontSize: Font.md,
    fontWeight: Weight.semibold,
  } as CSSProperties,
  mixLimitButton: {
    border: 0,
    borderRadius: Radius.full,
    padding: padding(Gap.md, Gap.xl),
    backgroundColor: Colors.accentPurple,
    color: Colors.textPrimary,
    fontSize: Font.md,
    fontWeight: Weight.bold,
    cursor: 'pointer',
  } as CSSProperties,
};
