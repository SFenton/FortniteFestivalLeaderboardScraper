import React, {useCallback, useEffect, useMemo, useRef, useState} from 'react';
import {ActivityIndicator, FlatList, Platform, Pressable, ScrollView, StyleSheet, Text, View, useWindowDimensions} from 'react-native';
import Ionicons from 'react-native-vector-icons/Ionicons';


import {Screen, FrostedSurface, CenteredEmptyStateCard, PageHeader, HamburgerButton, SuggestionCard, useCardGrid, SuggestionsFilterModal, defaultSuggestionsInstrumentFilters, WIN_SCROLLBAR_INSET, FadeScrollView, Colors, gridStyles, Layout, Gap, Font, LineHeight, Radius, Opacity} from '@festival/ui';
import type {SuggestionsInstrumentFilters} from '@festival/ui';
import {useFestival, usePageInstrumentation} from '@festival/contexts';
import type {Song, InstrumentShowSettings, SuggestionCategory, InstrumentKey, SuggestionTypeId} from '@festival/core';
import {SuggestionGenerator, SUGGESTION_TYPES, getCategoryTypeId, getCategoryInstrument, globalKeyFor, perInstrumentKeyFor, isInstrumentVisible, shouldShowCategory, filterCategoryForInstruments} from '@festival/core';
import {useTabBarLayout} from '../navigation/useOptionalBottomTabBarHeight';
import {useWindowsFlyoutUi} from '../navigation/windowsFlyoutUi';

const INITIAL_BATCH = 10;
const SUBSEQUENT_BATCH = 4;

// Temporary: make it easy to test star_gains UI.
const FORCE_EASY_STAR_GAINS_FIRST = __DEV__;

type SuggestionCategoryRow = SuggestionCategory & {uiKey: string};

const shouldShowCategoryType = (categoryKey: string, settingsObj: Record<string, any>): boolean => {
  const typeId = getCategoryTypeId(categoryKey);
  if (!typeId) return true;
  return settingsObj[globalKeyFor(typeId)] ?? true;
};

const isPerInstrumentTypeEnabled = (settingsObj: Record<string, any>, instrument: InstrumentKey, typeId: SuggestionTypeId): boolean => {
  return settingsObj[perInstrumentKeyFor(instrument, typeId)] ?? true;
};

/** Filter a category based on per-instrument type settings. Drops the category entirely if no songs remain. */
const filterCategoryForInstrumentTypes = (cat: SuggestionCategory, settingsObj: Record<string, any>): SuggestionCategory | null => {
  const typeId = getCategoryTypeId(cat.key);
  if (!typeId) return cat;

  // Single-instrument category (key encodes a specific instrument)
  const catInstrument = getCategoryInstrument(cat.key);
  if (catInstrument) {
    return isPerInstrumentTypeEnabled(settingsObj, catInstrument, typeId) ? cat : null;
  }

  // Multi-instrument or instrument-agnostic: filter individual songs
  const filtered = cat.songs.filter(s => {
    if (!s.instrumentKey) return true;
    return isPerInstrumentTypeEnabled(settingsObj, s.instrumentKey, typeId);
  });
  if (filtered.length === 0) return null;
  if (filtered.length === cat.songs.length) return cat;
  return {...cat, songs: filtered};
};

export function SuggestionsScreen(props: {onOpenSong?: (songId: string, title: string) => void}) {
  usePageInstrumentation('Suggestions');

  const {openFlyout} = useWindowsFlyoutUi();
  const hamburger = Platform.OS === 'windows' ? <HamburgerButton onPress={openFlyout} /> : undefined;

  const {height: tabBarHeight, marginBottom: tabBarMargin} = useTabBarLayout();

  const {width} = useWindowDimensions();
  const isCardGrid = useCardGrid();
  const useCompactLayout = width < 900;

  const {
    state: {songs, scoresIndex, settings},
    actions,
  } = useFestival();
  const {logUi} = actions;

  const instrumentVisibility = useMemo<InstrumentShowSettings>(() => ({
    showLead: settings.showLead,
    showBass: settings.showBass,
    showDrums: settings.showDrums,
    showVocals: settings.showVocals,
    showProLead: settings.showProLead,
    showProBass: settings.showProBass,
  }), [
    settings.showLead,
    settings.showBass,
    settings.showDrums,
    settings.showVocals,
    settings.showProLead,
    settings.showProBass,
  ]);

  // Merge app-level visibility and suggestion filters: an instrument only
  // appears if enabled in BOTH places.
  const effectiveInstrumentSettings = useMemo<InstrumentShowSettings>(() => ({
    showLead: settings.showLead && settings.suggestionsLeadFilter,
    showBass: settings.showBass && settings.suggestionsBassFilter,
    showDrums: settings.showDrums && settings.suggestionsDrumsFilter,
    showVocals: settings.showVocals && settings.suggestionsVocalsFilter,
    showProLead: settings.showProLead && settings.suggestionsProLeadFilter,
    showProBass: settings.showProBass && settings.suggestionsProBassFilter,
  }), [
    settings.showLead, settings.suggestionsLeadFilter,
    settings.showBass, settings.suggestionsBassFilter,
    settings.showDrums, settings.suggestionsDrumsFilter,
    settings.showVocals, settings.suggestionsVocalsFilter,
    settings.showProLead, settings.suggestionsProLeadFilter,
    settings.showProBass, settings.suggestionsProBassFilter,
  ]);

  const songById = useMemo(() => {
    const m = new Map<string, Song>();
    for (const s of songs) m.set(s.track.su, s);
    return m;
  }, [songs]);

  // Seed changes regenerate suggestions. Use a daily seed by default to keep it stable.
  const [seed, setSeed] = useState<number>(() => Math.floor(Date.now() / (1000 * 60 * 60 * 24)));
  const seedRef = useRef(seed);

  const genRef = useRef<SuggestionGenerator | null>(null);
  const nextUiKey = useRef(0);
  const [initialLoading, setInitialLoading] = useState(false);
  const [loadingMore, setLoadingMore] = useState(false);
  const [categories, setCategories] = useState<SuggestionCategoryRow[]>([]);

  const listRef = useRef<FlatList<SuggestionCategoryRow> | null>(null);
  const scrollRef = useRef<ScrollView | null>(null);

  // Suggestions filter modal state
  const [filterVisible, setFilterVisible] = useState(false);
  const [filterDraft, setFilterDraft] = useState<SuggestionsInstrumentFilters>(() => {
    const defaults = defaultSuggestionsInstrumentFilters();
    const draft = {} as Record<string, boolean>;
    for (const k of Object.keys(defaults)) {
      draft[k] = (settings as any)[k] ?? (defaults as any)[k];
    }
    return draft as unknown as SuggestionsInstrumentFilters;
  });

  const attachUiKeys = useCallback((list: SuggestionCategory[]): SuggestionCategoryRow[] => {
    return list.map(c => ({...c, uiKey: `${c.key}:${nextUiKey.current++}`}));
  }, []);

  const categoryTypeSettings = useMemo(() => {
    const obj: Record<string, boolean> = {};
    for (const {id} of SUGGESTION_TYPES) {
      const gk = globalKeyFor(id);
      obj[gk] = (settings as any)[gk] ?? true;
    }
    return obj;
  }, [
    // eslint-disable-next-line react-hooks/exhaustive-deps
    ...SUGGESTION_TYPES.map(({id}) => (settings as any)[globalKeyFor(id)]),
  ]);

  const visibleCategories = useMemo(() => {
    return categories
      .filter(c => shouldShowCategory(c.key, effectiveInstrumentSettings))
      .filter(c => shouldShowCategoryType(c.key, categoryTypeSettings))
      .map(c => filterCategoryForInstruments(c, effectiveInstrumentSettings))
      .filter((c): c is SuggestionCategoryRow => c !== null)
      .map(c => filterCategoryForInstrumentTypes(c, settings as any) as SuggestionCategoryRow | null)
      .filter((c): c is SuggestionCategoryRow => c !== null);
  }, [categories, effectiveInstrumentSettings, categoryTypeSettings, settings]);

  const filterForEnabledInstruments = useCallback((list: SuggestionCategory[]) => {
    return list
      .filter(c => shouldShowCategory(c.key, effectiveInstrumentSettings))
      .filter(c => shouldShowCategoryType(c.key, categoryTypeSettings))
      .map(c => filterCategoryForInstruments(c, effectiveInstrumentSettings))
      .filter((c): c is SuggestionCategory => c !== null)
      .map(c => filterCategoryForInstrumentTypes(c, settings as any))
      .filter((c): c is SuggestionCategory => c !== null);
  }, [effectiveInstrumentSettings, categoryTypeSettings, settings]);

  const canRegenerate = songs.length > 0 && settings.hasEverSyncedScores && visibleCategories.length > 0;

  const loadInitial = useCallback(() => {
    if (!songs.length) {
      genRef.current = null;
      nextUiKey.current = 0;
      setCategories([]);
      setInitialLoading(false);
      setLoadingMore(false);
      return;
    }

    setInitialLoading(true);
    setLoadingMore(false);

    const gen = new SuggestionGenerator({seed: seedRef.current, disableSkipping: false});
    genRef.current = gen;

    nextUiKey.current = 0;

    // Because we filter by enabled instruments at the UI layer, the initial
    // random batch can sometimes be entirely filtered out. Generate until we
    // have enough visible categories (or we exhaust options).
    const picked: SuggestionCategory[] = [];
    let safety = 0;
    while ((picked.length < INITIAL_BATCH || (FORCE_EASY_STAR_GAINS_FIRST && !picked.some(c => c.key === 'star_gains'))) && safety < 60) {
      safety++;
      const need = INITIAL_BATCH - picked.length;
      const chunk = gen.getNext(Math.max(need, 3), songs, scoresIndex);
      if (chunk.length === 0) break;
      picked.push(...filterForEnabledInstruments(chunk));
    }

    let firstPage = picked.slice(0, INITIAL_BATCH);
    if (FORCE_EASY_STAR_GAINS_FIRST) {
      const idx = firstPage.findIndex(c => c.key === 'star_gains');
      if (idx > 0) {
        const [gains] = firstPage.splice(idx, 1);
        firstPage = [gains, ...firstPage];
      }
    }

    setCategories(attachUiKeys(firstPage));

    if (__DEV__) {
      const scoresCount = Object.keys(scoresIndex ?? {}).length;
      logUi(`[SUGGESTIONS] init songs=${songs.length} scores=${scoresCount} seed=${seedRef.current} generated=${picked.length}`);
    }
    setInitialLoading(false);
  }, [attachUiKeys, filterForEnabledInstruments, logUi, scoresIndex, songs]);

  useEffect(() => {
    loadInitial();
  }, [loadInitial]);

  const onRegenerate = useCallback(() => {
    // Update the seed ref first so loadInitial() picks it up immediately.
    const next = Math.floor(Date.now() / (1000 * 60 * 60 * 24)) + Math.floor(Math.random() * 1000);
    seedRef.current = next;
    setSeed(next);

    // Generate new suggestions directly instead of waiting for the useEffect
    // to detect the seed change.  This keeps the new categories, the seed
    // update, and the scroll-to-top in a single React batch so the user never
    // sees a flash of stale or intermediate data.
    loadInitial();

    // Scroll after loadInitial so the list already has new data when the
    // native thread processes the scroll command.
    listRef.current?.scrollToOffset({offset: 0, animated: false});
    scrollRef.current?.scrollTo({y: 0, animated: false});

    logUi(`[SUGGESTIONS] regenerate seed=${next}`);
  }, [loadInitial, logUi]);

  const loadMore = useCallback(() => {
    if (!songs.length) return;
    if (initialLoading || loadingMore) return;
    if (!genRef.current) return;

    setLoadingMore(true);

    const picked: SuggestionCategory[] = [];
    let safety = 0;
    while (picked.length < SUBSEQUENT_BATCH && safety < 30) {
      safety++;
      const need = SUBSEQUENT_BATCH - picked.length;
      const chunk = genRef.current.getNext(Math.max(need, 2), songs, scoresIndex);
      if (chunk.length === 0) {
        genRef.current.resetForEndless();
        continue;
      }
      picked.push(...filterForEnabledInstruments(chunk));
    }

    if (picked.length > 0) {
      setCategories(cur => [...cur, ...attachUiKeys(picked)]);
    }
    setLoadingMore(false);
  }, [attachUiKeys, filterForEnabledInstruments, initialLoading, loadingMore, scoresIndex, songs]);

  // Masonry: split items into two independent columns for tablet grid mode.
  const [masonryLeft, masonryRight] = useMemo(() => {
    if (!isCardGrid) return [[], []] as [SuggestionCategoryRow[], SuggestionCategoryRow[]];
    const left: SuggestionCategoryRow[] = [];
    const right: SuggestionCategoryRow[] = [];
    visibleCategories.forEach((item, i) => {
      if (i % 2 === 0) left.push(item);
      else right.push(item);
    });
    return [left, right];
  }, [isCardGrid, visibleCategories]);

  const handleMasonryScroll = useCallback((e: any) => {
    const {layoutMeasurement, contentOffset, contentSize} = e.nativeEvent;
    if (layoutMeasurement.height + contentOffset.y >= contentSize.height - 300) {
      loadMore();
    }
  }, [loadMore]);

  const onOpenFilter = useCallback(() => {
    const defaults = defaultSuggestionsInstrumentFilters();
    const draft = {} as Record<string, boolean>;
    for (const k of Object.keys(defaults)) {
      draft[k] = (settings as any)[k] ?? (defaults as any)[k];
    }
    setFilterDraft(draft as unknown as SuggestionsInstrumentFilters);
    setFilterVisible(true);
  }, [settings]);

  const onCancelFilter = useCallback(() => setFilterVisible(false), []);

  const onResetFilter = useCallback(() => {
    const defaults = defaultSuggestionsInstrumentFilters();
    setFilterDraft(defaults);
    actions.setSettings({...settings, ...defaults});
    setFilterVisible(false);
  }, [actions, settings]);

  const onApplyFilter = useCallback(() => {
    actions.setSettings({...settings, ...filterDraft});
    setFilterVisible(false);
  }, [actions, filterDraft, settings]);

  const isFilterActive = useMemo(() => {
    const defaults = defaultSuggestionsInstrumentFilters();
    return Object.keys(defaults).some(
      k => ((settings as any)[k] ?? (defaults as any)[k]) !== (defaults as any)[k],
    );
  }, [settings]);
  const filterIconColor = isFilterActive ? Colors.accentBlue : Colors.textSecondary;

  const showFilterButton = songs.length > 0 && !initialLoading && settings.hasEverSyncedScores && visibleCategories.length > 0;

  const header = (
    <PageHeader
      title="Suggestions"
      left={hamburger}
      right={
        <View style={styles.headerActions}>
          {showFilterButton ? (
            <Pressable
              onPress={onOpenFilter}
              style={({pressed}) => [styles.regenBtn, pressed && styles.regenBtnPressed]}
              accessibilityRole="button"
              accessibilityLabel="Filter suggestions"
            >
              <Ionicons name="funnel" size={18} color={filterIconColor} />
            </Pressable>
          ) : null}
          {canRegenerate ? (
            <Pressable
              onPress={onRegenerate}
              style={({pressed}) => [styles.regenBtn, pressed && styles.regenBtnPressed]}
              accessibilityRole="button"
              accessibilityLabel="Regenerate suggestions"
            >
              <Ionicons name="refresh" size={22} color={Colors.textPrimary} />
            </Pressable>
          ) : null}
        </View>
      }
    />
  );

  const footer = loadingMore ? (
    <View style={styles.loadingRow}>
      <ActivityIndicator />
    </View>
  ) : null;

  const showNeverSyncedScoresMessage = !settings.hasEverSyncedScores;

  const categorySeparator = useCallback(() => <View style={styles.categorySeparator} />, []);

  if (!songs.length) {
    return (
      <Screen>
        <View style={styles.content}>
          {header}
          <CenteredEmptyStateCard title="No songs yet" body="Songs haven't loaded yet. Check Settings." />
        </View>
        <SuggestionsFilterModal
          visible={filterVisible}
          draft={filterDraft}
          instrumentVisibility={instrumentVisibility}
          onChange={setFilterDraft}
          onCancel={onCancelFilter}
          onReset={onResetFilter}
          onApply={onApplyFilter}
        />
      </Screen>
    );
  }

  if (initialLoading) {
    return (
      <Screen>
        <View style={styles.content}>
          {header}
          <View style={styles.loadingRow}>
            <ActivityIndicator />
          </View>
        </View>
        <SuggestionsFilterModal
          visible={filterVisible}
          draft={filterDraft}
          instrumentVisibility={instrumentVisibility}
          onChange={setFilterDraft}
          onCancel={onCancelFilter}
          onReset={onResetFilter}
          onApply={onApplyFilter}
        />
      </Screen>
    );
  }

  if (visibleCategories.length === 0 || !settings.hasEverSyncedScores) {
    const emptyBody = settings.hasEverSyncedScores
      ? 'Sync your scores to generate suggestions on what to play next.'
      : 'Sync your scores to see your Fortnite Festival stats.';

    return (
      <Screen>
        <View style={styles.content}>
          {header}
          <CenteredEmptyStateCard title="No Suggestions Available" body={emptyBody} />
        </View>
        <SuggestionsFilterModal
          visible={filterVisible}
          draft={filterDraft}
          instrumentVisibility={instrumentVisibility}
          onChange={setFilterDraft}
          onCancel={onCancelFilter}
          onReset={onResetFilter}
          onApply={onApplyFilter}
        />
      </Screen>
    );
  }

  return (
    <Screen>
      <View style={styles.content}>
        {header}

        {showNeverSyncedScoresMessage ? (
          <FrostedSurface style={styles.card} tint="dark" intensity={18}>
            <Text style={styles.cardTitle}>Scores not synced</Text>
            <Text style={styles.body}>Sync your scores to see your Fortnite Festival stats.</Text>
          </FrostedSurface>
        ) : null}

        <FadeScrollView>
          {isCardGrid ? (
            <ScrollView
              ref={scrollRef}
              style={{flex: 1, marginBottom: tabBarMargin}}
              contentContainerStyle={{paddingTop: 32, paddingBottom: tabBarHeight + 16, paddingRight: WIN_SCROLLBAR_INSET}}
              scrollIndicatorInsets={{bottom: tabBarHeight}}
              showsVerticalScrollIndicator={false}
              showsHorizontalScrollIndicator={false}
              keyboardShouldPersistTaps="handled"
              onScroll={handleMasonryScroll}
              scrollEventThrottle={200}
            >
              <View style={gridStyles.cardGrid}>
                <View style={gridStyles.cardGridColumnLeft}>
                  {masonryLeft.map(cat => (
                    <SuggestionCard
                      key={cat.uiKey}
                      cat={cat}
                      useCompactLayout={useCompactLayout}
                      songById={songById}
                      scoresIndex={scoresIndex}
                      instrumentQuerySettings={effectiveInstrumentSettings}
                      onOpenSong={(songId, title) => {
                        logUi(`[SUGGESTIONS] open ${songId} '${title}' (${cat.key})`);
                        props.onOpenSong?.(songId, title);
                      }}
                    />
                  ))}
                </View>
                {isCardGrid ? (
                  <View style={gridStyles.cardGridColumnRight}>
                    {masonryRight.map(cat => (
                      <SuggestionCard
                        key={cat.uiKey}
                        cat={cat}
                        useCompactLayout={useCompactLayout}
                        songById={songById}
                        scoresIndex={scoresIndex}
                        instrumentQuerySettings={effectiveInstrumentSettings}
                        onOpenSong={(songId, title) => {
                          logUi(`[SUGGESTIONS] open ${songId} '${title}' (${cat.key})`);
                          props.onOpenSong?.(songId, title);
                        }}
                      />
                    ))}
                  </View>
                ) : null}
              </View>
              {footer}
            </ScrollView>
          ) : (
            <FlatList
              ref={listRef}
              style={{flex: 1, marginBottom: tabBarMargin}}
              contentContainerStyle={{paddingTop: 32, paddingBottom: tabBarHeight + 16, paddingRight: WIN_SCROLLBAR_INSET}}
              scrollIndicatorInsets={{bottom: tabBarHeight}}
              showsVerticalScrollIndicator={false}
              showsHorizontalScrollIndicator={false}
              data={visibleCategories}
              keyExtractor={c => c.uiKey}
              keyboardShouldPersistTaps="handled"
              extraData={useCompactLayout}
              ItemSeparatorComponent={categorySeparator}
              ListFooterComponent={footer}
              onEndReachedThreshold={0.4}
              onEndReached={loadMore}
              renderItem={({item: cat}) => (
                <SuggestionCard
                  cat={cat}
                  useCompactLayout={useCompactLayout}
                  songById={songById}
                  scoresIndex={scoresIndex}
                  instrumentQuerySettings={effectiveInstrumentSettings}
                  onOpenSong={(songId, title) => {
                    logUi(`[SUGGESTIONS] open ${songId} '${title}' (${cat.key})`);
                    props.onOpenSong?.(songId, title);
                  }}
                />
              )}
            />
          )}
        </FadeScrollView>
      </View>

      <SuggestionsFilterModal
        visible={filterVisible}
        draft={filterDraft}
        instrumentVisibility={instrumentVisibility}
        onChange={setFilterDraft}
        onCancel={onCancelFilter}
        onReset={onResetFilter}
        onApply={onApplyFilter}
      />
    </Screen>
  );
}

const styles = StyleSheet.create({
  content: {
    flex: 1,
    paddingHorizontal: Layout.paddingHorizontal,
    paddingTop: Layout.paddingTop,
    paddingBottom: Layout.paddingBottom,
    gap: Gap.xl,
  },

  card: {
    borderRadius: Radius.md,
    padding: Gap.xl,
    gap: Gap.md,
  },
  cardTitle: {
    color: Colors.textPrimary,
    fontSize: Font.lg,
    fontWeight: '700',
  },
  body: {
    color: Colors.textSecondary,
    fontSize: Font.md,
    lineHeight: LineHeight.lg,
  },
  regenBtn: {
    paddingHorizontal: Gap.md,
    paddingVertical: Gap.md,
    alignItems: 'center',
    justifyContent: 'center',
  },
  regenBtnPressed: {
    opacity: Opacity.pressed,
  },
  headerActions: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: Gap.sm,
  },
  loadingRow: {
    paddingVertical: 18,
    alignItems: 'center',
    justifyContent: 'center',
  },
  categorySeparator: {
    height: Gap.lg,
  },

  debugText: {
    color: Colors.textMutedCaption,
    fontSize: Font.sm,
    lineHeight: LineHeight.sm,
    textAlign: 'center',
  },
});
