import React, {useCallback, useEffect, useMemo, useRef, useState} from 'react';
import {ActivityIndicator, FlatList, Pressable, StyleSheet, Text, useWindowDimensions, View} from 'react-native';
import Ionicons from 'react-native-vector-icons/Ionicons';
import MaskedView from '@react-native-masked-view/masked-view';
import LinearGradient from 'react-native-linear-gradient';

import {Screen} from '../ui/Screen';
import {FrostedSurface} from '../ui/FrostedSurface';
import {CenteredEmptyStateCard} from '../ui/CenteredEmptyStateCard';
import {PageHeader} from '../ui/PageHeader';
import {useFestival} from '../app/festival/FestivalContext';
import {usePageInstrumentation} from '../app/instrumentation/usePageInstrumentation';
import type {Song} from '../core/models';
import {type InstrumentShowSettings} from '../app/songs/songFiltering';
import {SuggestionGenerator} from '../core/suggestions/suggestionGenerator';
import type {SuggestionCategory} from '../core/suggestions/types';
import type {InstrumentKey} from '../core/instruments';
import {useTabBarLayout} from '../navigation/useOptionalBottomTabBarHeight';
import {SuggestionCard} from '../ui/suggestions/SuggestionCard';

const INITIAL_BATCH = 10;
const SUBSEQUENT_BATCH = 4;

// Temporary: make it easy to test star_gains UI.
const FORCE_EASY_STAR_GAINS_FIRST = __DEV__;

type SuggestionCategoryRow = SuggestionCategory & {uiKey: string};

const shouldShowCategory = (categoryKey: string, settings: {showLead: boolean; showDrums: boolean; showVocals: boolean; showBass: boolean; showProLead: boolean; showProBass: boolean}): boolean => {
  const key = categoryKey.toLowerCase();
  if (key.includes('pro_guitar') || key.includes('prolead') || key.includes('pro_lead')) return settings.showProLead;
  if (key.includes('pro_bass') || key.includes('probass')) return settings.showProBass;
  if (key.includes('guitar') || key.includes('lead')) return settings.showLead;
  if (key.includes('bass')) return settings.showBass;
  if (key.includes('drums')) return settings.showDrums;
  if (key.includes('vocals') || key.includes('vocal')) return settings.showVocals;
  return true;
};

const isInstrumentKeyVisible = (instrumentKey: InstrumentKey, settings: {showLead: boolean; showDrums: boolean; showVocals: boolean; showBass: boolean; showProLead: boolean; showProBass: boolean}): boolean => {
  switch (instrumentKey) {
    case 'guitar': return settings.showLead;
    case 'bass': return settings.showBass;
    case 'drums': return settings.showDrums;
    case 'vocals': return settings.showVocals;
    case 'pro_guitar': return settings.showProLead;
    case 'pro_bass': return settings.showProBass;
    default: return true;
  }
};

/** Filter songs within a category to remove items for hidden instruments, then drop the category if empty. */
const filterCategoryForInstruments = (cat: SuggestionCategory, settings: {showLead: boolean; showDrums: boolean; showVocals: boolean; showBass: boolean; showProLead: boolean; showProBass: boolean}): SuggestionCategory | null => {
  // Single-instrument categories are filtered by shouldShowCategory via key.
  // For multi-instrument categories, filter individual song items.
  const filtered = cat.songs.filter(s => !s.instrumentKey || isInstrumentKeyVisible(s.instrumentKey, settings));
  if (filtered.length === 0) return null;
  if (filtered.length === cat.songs.length) return cat;
  return {...cat, songs: filtered};
};

export function SuggestionsScreen(props: {onOpenSong?: (songId: string, title: string) => void}) {
  usePageInstrumentation('Suggestions');

  const {height: tabBarHeight, marginBottom: tabBarMargin} = useTabBarLayout();

  const {width} = useWindowDimensions();
  const useCompactLayout = width < 900;

  const {
    state: {songs, scoresIndex, settings},
    actions: {logUi},
  } = useFestival();

  const instrumentQuerySettings = useMemo<InstrumentShowSettings>(() => ({
    showLead: settings.showLead,
    showBass: settings.showBass,
    showDrums: settings.showDrums,
    showVocals: settings.showVocals,
    showProLead: settings.showProLead,
    showProBass: settings.showProBass,
  }), [
    settings.showBass,
    settings.showDrums,
    settings.showLead,
    settings.showProBass,
    settings.showProLead,
    settings.showVocals,
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

  const attachUiKeys = useCallback((list: SuggestionCategory[]): SuggestionCategoryRow[] => {
    return list.map(c => ({...c, uiKey: `${c.key}:${nextUiKey.current++}`}));
  }, []);

  const visibleCategories = useMemo(() => {
    return categories
      .filter(c => shouldShowCategory(c.key, instrumentQuerySettings))
      .map(c => filterCategoryForInstruments(c, instrumentQuerySettings))
      .filter((c): c is SuggestionCategoryRow => c !== null);
  }, [categories, instrumentQuerySettings]);

  const filterForEnabledInstruments = useCallback((list: SuggestionCategory[]) => {
    return list
      .filter(c => shouldShowCategory(c.key, instrumentQuerySettings))
      .map(c => filterCategoryForInstruments(c, instrumentQuerySettings))
      .filter((c): c is SuggestionCategory => c !== null);
  }, [instrumentQuerySettings]);

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

    // Scroll after loadInitial so the FlatList already has new data when the
    // native thread processes the scroll command.
    listRef.current?.scrollToOffset({offset: 0, animated: false});

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

  const header = (
    <PageHeader
      title="Suggestions"
      right={
        canRegenerate ? (
          <Pressable
            onPress={onRegenerate}
            style={({pressed}) => [styles.regenBtn, pressed && styles.regenBtnPressed]}
            accessibilityRole="button"
            accessibilityLabel="Regenerate suggestions"
          >
            <Ionicons name="refresh" size={22} color="#FFFFFF" />
          </Pressable>
        ) : undefined
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

        <MaskedView
          style={styles.fadeScrollContainer}
          maskElement={
            <View style={styles.fadeMaskContainer}>
              <LinearGradient
                colors={['transparent', 'black']}
                style={styles.fadeGradient}
              />
              <View style={styles.fadeMaskOpaque} />
              <LinearGradient
                colors={['black', 'transparent']}
                style={styles.fadeGradient}
              />
            </View>
          }
        >
          <FlatList
            ref={listRef}
            style={{flex: 1, marginBottom: tabBarMargin}}
            contentContainerStyle={{paddingTop: 32, paddingBottom: tabBarHeight + 16}}
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
                instrumentQuerySettings={instrumentQuerySettings}
                onOpenSong={(songId, title) => {
                  logUi(`[SUGGESTIONS] open ${songId} '${title}' (${cat.key})`);
                  props.onOpenSong?.(songId, title);
                }}
              />
            )}
          />
        </MaskedView>
      </View>
    </Screen>
  );
}

const styles = StyleSheet.create({
  content: {
    flex: 1,
    paddingHorizontal: 20,
    paddingTop: 16,
    paddingBottom: 4,
    gap: 12,
  },
  fadeScrollContainer: {
    flex: 1,
  },
  fadeMaskContainer: {
    flex: 1,
  },
  fadeMaskOpaque: {
    flex: 1,
    backgroundColor: '#000000',
  },
  fadeGradient: {
    height: 32,
  },
  card: {
    borderRadius: 12,
    padding: 12,
    gap: 8,
  },
  cardTitle: {
    color: '#FFFFFF',
    fontSize: 16,
    fontWeight: '700',
  },
  body: {
    color: '#D7DEE8',
    fontSize: 14,
    lineHeight: 20,
  },
  regenBtn: {
    paddingHorizontal: 8,
    paddingVertical: 8,
    alignItems: 'center',
    justifyContent: 'center',
  },
  regenBtnPressed: {
    opacity: 0.85,
  },
  loadingRow: {
    paddingVertical: 18,
    alignItems: 'center',
    justifyContent: 'center',
  },
  categorySeparator: {
    height: 10,
  },
  debugText: {
    color: '#9CA3AF',
    fontSize: 12,
    lineHeight: 16,
    textAlign: 'center',
  },
});
