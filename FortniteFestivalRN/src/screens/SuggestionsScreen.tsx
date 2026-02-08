import React, {useCallback, useEffect, useMemo, useRef, useState} from 'react';
import {ActivityIndicator, FlatList, Image, Platform, Pressable, StyleSheet, Text, useWindowDimensions, View} from 'react-native';
import Ionicons from 'react-native-vector-icons/Ionicons';
import MaskedView from '@react-native-masked-view/masked-view';
import LinearGradient from 'react-native-linear-gradient';

import {Screen} from '../ui/Screen';
import {FrostedSurface} from '../ui/FrostedSurface';
import {CenteredEmptyStateCard} from '../ui/CenteredEmptyStateCard';
import {PageHeader} from '../ui/PageHeader';
import {MarqueeText} from '../ui/MarqueeText';
import {useFestival} from '../app/festival/FestivalContext';
import {usePageInstrumentation} from '../app/instrumentation/usePageInstrumentation';
import type {LeaderboardData, Song} from '../core/models';
import {buildSongDisplayRow, type InstrumentQuerySettings} from '../app/songs/songFiltering';
import {formatIntegerWithCommas} from '../app/format/formatters';
import {SuggestionGenerator} from '../core/suggestions/suggestionGenerator';
import type {SuggestionCategory, SuggestionSongItem} from '../core/suggestions/types';
import {getInstrumentIconSource, getInstrumentStatusVisual} from '../ui/instruments/instrumentVisuals';
import {useTabBarLayout} from '../navigation/useOptionalBottomTabBarHeight';

const INITIAL_BATCH = 10;
const SUBSEQUENT_BATCH = 4;

// Temporary: make it easy to test star_gains UI.
const FORCE_EASY_STAR_GAINS_FIRST = __DEV__;

const STAR_WHITE_ICON = require('../assets/icons/star_white.png');
const STAR_GOLD_ICON = require('../assets/icons/star_gold.png');

type SuggestionCategoryRow = SuggestionCategory & {uiKey: string};

const shouldShowCategory = (categoryKey: string, settings: {queryLead: boolean; queryDrums: boolean; queryVocals: boolean; queryBass: boolean; queryProLead: boolean; queryProBass: boolean}): boolean => {
  const key = categoryKey.toLowerCase();
  if (key.includes('pro_guitar') || key.includes('prolead') || key.includes('pro_lead')) return settings.queryProLead;
  if (key.includes('pro_bass') || key.includes('probass')) return settings.queryProBass;
  if (key.includes('guitar') || key.includes('lead')) return settings.queryLead;
  if (key.includes('bass')) return settings.queryBass;
  if (key.includes('drums')) return settings.queryDrums;
  if (key.includes('vocals') || key.includes('vocal')) return settings.queryVocals;
  return true;
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

  const instrumentQuerySettings = useMemo<InstrumentQuerySettings>(() => ({
    queryLead: settings.queryLead,
    queryBass: settings.queryBass,
    queryDrums: settings.queryDrums,
    queryVocals: settings.queryVocals,
    queryProLead: settings.queryProLead,
    queryProBass: settings.queryProBass,
  }), [
    settings.queryBass,
    settings.queryDrums,
    settings.queryLead,
    settings.queryProBass,
    settings.queryProLead,
    settings.queryVocals,
  ]);

  const songById = useMemo(() => {
    const m = new Map<string, Song>();
    for (const s of songs) m.set(s.track.su, s);
    return m;
  }, [songs]);

  // Seed changes regenerate suggestions. Use a daily seed by default to keep it stable.
  const [seed, setSeed] = useState<number>(() => Math.floor(Date.now() / (1000 * 60 * 60 * 24)));

  const genRef = useRef<SuggestionGenerator | null>(null);
  const nextUiKey = useRef(0);
  const [initialLoading, setInitialLoading] = useState(false);
  const [loadingMore, setLoadingMore] = useState(false);
  const [categories, setCategories] = useState<SuggestionCategoryRow[]>([]);

  const listRef = useRef<FlatList<SuggestionCategoryRow> | null>(null);

  const onRegenerate = useCallback(() => {
    // Jump back to the top so the user immediately sees the new first categories.
    listRef.current?.scrollToOffset({offset: 0, animated: false});

    const next = Math.floor(Date.now() / (1000 * 60 * 60 * 24)) + Math.floor(Math.random() * 1000);
    setSeed(next);
    logUi(`[SUGGESTIONS] regenerate seed=${next}`);
  }, [logUi]);

  const attachUiKeys = useCallback((list: SuggestionCategory[]): SuggestionCategoryRow[] => {
    return list.map(c => ({...c, uiKey: `${c.key}:${nextUiKey.current++}`}));
  }, []);

  const visibleCategories = useMemo(() => {
    return categories.filter(c => shouldShowCategory(c.key, instrumentQuerySettings));
  }, [categories, instrumentQuerySettings]);

  const filterForEnabledInstruments = useCallback((list: SuggestionCategory[]) => {
    return list.filter(c => shouldShowCategory(c.key, instrumentQuerySettings));
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

    const gen = new SuggestionGenerator({seed, disableSkipping: false});
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
      logUi(`[SUGGESTIONS] init songs=${songs.length} scores=${scoresCount} seed=${seed} generated=${picked.length}`);
    }
    setInitialLoading(false);
  }, [attachUiKeys, filterForEnabledInstruments, logUi, scoresIndex, seed, songs]);

  useEffect(() => {
    loadInitial();
  }, [loadInitial]);

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

    const debug = __DEV__
      ? `songs=${songs.length} scores=${Object.keys(scoresIndex ?? {}).length} categories=${categories.length} visible=${visibleCategories.length} hasEverSyncedScores=${String(settings.hasEverSyncedScores)}
lead=${String(settings.queryLead)} bass=${String(settings.queryBass)} drums=${String(settings.queryDrums)} vocals=${String(settings.queryVocals)} proLead=${String(settings.queryProLead)} proBass=${String(settings.queryProBass)}`
      : '';

    return (
      <Screen>
        <View style={styles.content}>
          {header}
          <CenteredEmptyStateCard title="No Suggestions Available" body={emptyBody}>
            {__DEV__ ? <Text style={styles.debugText}>{debug}</Text> : null}
          </CenteredEmptyStateCard>
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

function SuggestionCard(props: {
  cat: SuggestionCategoryRow;
  useCompactLayout: boolean;
  songById: ReadonlyMap<string, Song>;
  scoresIndex: Readonly<Record<string, LeaderboardData | undefined>>;
  instrumentQuerySettings: InstrumentQuerySettings;
  onOpenSong: (songId: string, title: string) => void;
}) {
  const {cat} = props;

  const catInstrumentKey = useMemo(() => {
    // First-play categories are emitted as `unplayed_{instrument}` and have
    // variants like `unplayed_{instrument}_decade_00`.
    // Close-FC categories are emitted as `unfc_{instrument}` with decade
    // variants like `unfc_{instrument}_decade_00`.
    let suffix: string | undefined;
    if (cat.key.startsWith('unplayed_')) {
      suffix = cat.key.slice('unplayed_'.length);
      if (suffix === 'any' || suffix.startsWith('any_')) return undefined;
    } else if (cat.key.startsWith('unfc_')) {
      suffix = cat.key.slice('unfc_'.length);
    }

    if (!suffix) return undefined;

    const known: Array<'pro_guitar' | 'pro_bass' | 'guitar' | 'bass' | 'drums' | 'vocals'> = ['pro_guitar', 'pro_bass', 'guitar', 'bass', 'drums', 'vocals'];
    for (const k of known) {
      if (suffix === k || suffix.startsWith(`${k}_`)) return k;
    }

    return undefined;
  }, [cat.key]);

  // Avoid nested VirtualizedList overhead for small lists, but virtualize very large categories.
  const useVirtualSongsList = cat.songs.length > 12;

  const renderSong = useCallback(({item}: {item: SuggestionSongItem}) => {
    return (
      <SuggestionSongRow
        categoryKey={cat.key}
        item={item}
        useCompactLayout={props.useCompactLayout}
        song={props.songById.get(item.songId)}
        leaderboardData={props.scoresIndex[item.songId]}
        settings={props.instrumentQuerySettings}
        onOpenSong={props.onOpenSong}
      />
    );
  }, [cat.key, props.instrumentQuerySettings, props.onOpenSong, props.scoresIndex, props.songById, props.useCompactLayout]);

  const songSeparator = useCallback(() => <View style={styles.songSeparator} />, []);

  return (
    <FrostedSurface style={styles.card} tint="dark" intensity={18}>
      <View style={styles.cardHeaderRow}>
        <View style={styles.cardHeaderLeft}>
          <Text style={styles.cardTitle} numberOfLines={1}>
            {cat.title}
          </Text>
          <Text style={styles.cardSubtitle}>{cat.description}</Text>
        </View>

        {catInstrumentKey ? (
          <View style={styles.cardHeaderRight}>
            <Image source={getInstrumentIconSource(catInstrumentKey)} style={styles.cardHeaderIcon} resizeMode="contain" />
          </View>
        ) : null}
      </View>

      <View style={styles.songList}>
        {useVirtualSongsList ? (
          <FlatList
            data={cat.songs}
            keyExtractor={s => `${cat.key}:${s.songId}`}
            renderItem={renderSong}
            ItemSeparatorComponent={songSeparator}
            scrollEnabled={false}
            keyboardShouldPersistTaps="handled"
            removeClippedSubviews={Platform.OS === 'android'}
            initialNumToRender={8}
            maxToRenderPerBatch={6}
            updateCellsBatchingPeriod={24}
            windowSize={5}
          />
        ) : (
          cat.songs.map(s => (
            <SuggestionSongRow
              key={`${cat.key}:${s.songId}`}
              categoryKey={cat.key}
              item={s}
              useCompactLayout={props.useCompactLayout}
              song={props.songById.get(s.songId)}
              leaderboardData={props.scoresIndex[s.songId]}
              settings={props.instrumentQuerySettings}
              onOpenSong={props.onOpenSong}
            />
          ))
        )}
      </View>
    </FrostedSurface>
  );
}

const SuggestionSongRow = React.memo(function SuggestionSongRow(props: {
  categoryKey: string;
  item: SuggestionSongItem;
  song?: Song;
  leaderboardData?: LeaderboardData;
  settings: InstrumentQuerySettings;
  useCompactLayout: boolean;
  onOpenSong: (songId: string, title: string) => void;
}) {
  const {item, song, leaderboardData, settings} = props;

  const year = song?.track?.ry;

  const imageUri = song?.imagePath ?? song?.track?.au;

  const isUnfcCategory = props.categoryKey.startsWith('unfc_');
  const isFcTheseNextCategory = props.categoryKey.startsWith('near_fc_any');
  const isNearFcRelaxedCategory = props.categoryKey.startsWith('near_fc_relaxed');
  const isGoldStarPushCategory = props.categoryKey.startsWith('almost_six_star') || props.categoryKey.startsWith('more_stars');
  const isFirstPlaysMixedCategory = props.categoryKey.startsWith('first_plays_mixed');
  const isVarietyPackCategory = props.categoryKey === 'variety_pack';
  const isArtistSamplerCategory = props.categoryKey.startsWith('artist_sampler_');
  const isArtistUnplayedCategory = props.categoryKey.startsWith('artist_unplayed_');
  const isSameNameNearFcCategory = props.categoryKey.startsWith('samename_nearfc_');
  const isSameNameTitleCategory = props.categoryKey.startsWith('samename_') && !isSameNameNearFcCategory;
  const isUnplayedAnyCategory = props.categoryKey === 'unplayed_any' || props.categoryKey.startsWith('unplayed_any_decade_');
  const isStarGainsCategory = props.categoryKey.startsWith('star_gains');

  const right = useMemo(() => {
    if (isUnfcCategory) return '';
    return formatRight(item);
  }, [isUnfcCategory, item]);

  const unfcPercent = useMemo(() => {
    if (!isUnfcCategory) return undefined;
    if (typeof item.percent !== 'number' || !Number.isFinite(item.percent)) return undefined;
    // Show the leading two integer digits (no decimals). This category is always < 100%.
    const pctInt = Math.max(0, Math.min(99, Math.floor(item.percent)));
    return String(pctInt).padStart(2, '0');
  }, [isUnfcCategory, item.percent]);

  const showUnfcBadge = unfcPercent != null;

  const rightInstrumentKey = (isFcTheseNextCategory || isNearFcRelaxedCategory || isGoldStarPushCategory || isFirstPlaysMixedCategory || isStarGainsCategory) ? item.instrumentKey : undefined;
  const rightInstrumentKeyFinal = (rightInstrumentKey || (isSameNameNearFcCategory ? item.instrumentKey : undefined));
  const showRightInstrumentIcon = !!rightInstrumentKeyFinal;

  const starGainsStarCount = useMemo(() => {
    if (!isStarGainsCategory) return 0;

    const instr = rightInstrumentKeyFinal;
    const tr = instr && leaderboardData ? (leaderboardData as any)[instr] : undefined;
    const nFromTracker = tr?.numStars;
    if (typeof nFromTracker === 'number' && Number.isFinite(nFromTracker) && nFromTracker > 0) {
      return Math.max(0, Math.min(6, Math.floor(nFromTracker)));
    }

    if (typeof item.stars !== 'number' || !Number.isFinite(item.stars) || item.stars <= 0) return 0;
    return Math.max(0, Math.min(6, Math.floor(item.stars)));
  }, [isStarGainsCategory, item.stars, leaderboardData, rightInstrumentKeyFinal]);

  const starGainsStarsVisual = useMemo(() => {
    if (!isStarGainsCategory || starGainsStarCount <= 0) return null;
    const allGold = starGainsStarCount >= 6;
    const displayCount = allGold ? 5 : Math.max(1, starGainsStarCount);
    const source = allGold ? STAR_GOLD_ICON : STAR_WHITE_ICON;
    const instr = rightInstrumentKeyFinal;
    const tr = instr && leaderboardData ? (leaderboardData as any)[instr] : undefined;
    const scoreValue = tr?.initialized ? tr?.maxScore : undefined;
    const scoreDisplay = typeof scoreValue === 'number' && Number.isFinite(scoreValue) ? formatIntegerWithCommas(scoreValue) : '';
    return {displayCount, source, scoreDisplay};
  }, [isStarGainsCategory, leaderboardData, rightInstrumentKeyFinal, starGainsStarCount]);

  const row = useMemo(() => {
    if (!song) return null;
    return buildSongDisplayRow({song, leaderboardData, settings});
  }, [leaderboardData, settings, song]);

  const hideRightSideCompletely = isVarietyPackCategory || isArtistSamplerCategory || isArtistUnplayedCategory || isSameNameTitleCategory || isUnplayedAnyCategory;

  return (
    <Pressable
      onPress={() => props.onOpenSong(item.songId, item.title)}
      style={styles.songRowPressable}
      accessibilityRole="button"
      accessibilityLabel={`Open ${item.title}`}
    >
      {({pressed}) => (
        <View style={[styles.songRowPressable, pressed && styles.songRowInnerPressed]}>
          <View style={styles.songRowInner}>
            <View style={styles.songLeft}>
              <View style={styles.thumbWrap}>
                {imageUri ? (
                  <Image source={{uri: imageUri}} style={styles.thumb} resizeMode="cover" />
                ) : (
                  <View style={styles.thumbPlaceholder} />
                )}
              </View>

              <View style={styles.songRowText}>
                <MarqueeText text={item.title} textStyle={styles.songTitle} />
                <MarqueeText text={`${item.artist}${item.artist && year ? ' • ' : ''}${year ?? ''}`} textStyle={styles.songMeta} />
              </View>
            </View>

            <View style={styles.songRight}>
              {!hideRightSideCompletely && !showUnfcBadge && !showRightInstrumentIcon && !props.useCompactLayout && row ? (
                <View style={styles.instrumentRow}>
                  {row.instrumentStatuses.map(s => {
                    const {fill, stroke} = getInstrumentStatusVisual({hasScore: s.hasScore, isFullCombo: s.isFullCombo});
                    const opacity = s.isEnabled ? 1 : 0.35;
                    return (
                      <View key={s.instrumentKey} style={[styles.instrumentChip, {backgroundColor: fill, borderColor: stroke, opacity}]}>
                        <Image source={getInstrumentIconSource(s.instrumentKey)} style={styles.instrumentIcon} resizeMode="contain" />
                      </View>
                    );
                  })}
                </View>
              ) : null}
              {hideRightSideCompletely ? null : showRightInstrumentIcon ? (
                <View style={styles.songRightSingle}>
                  <Image source={getInstrumentIconSource(rightInstrumentKeyFinal)} style={styles.fcTheseInstrumentIcon} resizeMode="contain" />
                </View>
              ) : showUnfcBadge ? (
                <View style={styles.songRightSingle}>
                  <Text style={styles.unfcPctText}>{unfcPercent}%</Text>
                </View>
              ) : right ? (
                <View style={styles.songRightSingle}>
                  <Text style={styles.songRightText}>{right}</Text>
                </View>
              ) : null}
            </View>
          </View>
          {starGainsStarsVisual ? (
            <View style={styles.starGainsStarsRow}>
              <View style={styles.starGainsStarsInner}>
                {Array.from({length: starGainsStarsVisual.displayCount}).map((_, i) => (
                  <Image key={i} source={starGainsStarsVisual.source} style={styles.starGainsStarIcon} resizeMode="contain" />
                ))}
                {starGainsStarsVisual.scoreDisplay ? (
                  <Text style={styles.starGainsScoreText}>• {starGainsStarsVisual.scoreDisplay}</Text>
                ) : null}
              </View>
            </View>
          ) : null}
        </View>
      )}
    </Pressable>
  );
}, (prev, next) => (
  prev.categoryKey === next.categoryKey &&
  prev.item === next.item &&
  prev.song === next.song &&
  prev.leaderboardData === next.leaderboardData &&
  prev.settings === next.settings &&
  prev.useCompactLayout === next.useCompactLayout &&
  prev.onOpenSong === next.onOpenSong
));

function formatRight(item: SuggestionSongItem): string {
  const parts: string[] = [];

  if (typeof item.percent === 'number' && Number.isFinite(item.percent) && item.percent > 0) {
    parts.push(`${item.percent.toFixed(2)}%`);
  }

  if (typeof item.stars === 'number' && Number.isFinite(item.stars) && item.stars > 0) {
    const displayStars = item.stars >= 6 ? 6 : item.stars;
    parts.push(`${displayStars}★`);
  }

  if (item.fullCombo) {
    parts.push('FC');
  }

  return parts.join(' • ');
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
  cardHeaderRow: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
  },
  cardHeaderLeft: {
    flex: 1,
    minWidth: 0,
    gap: 4,
  },
  cardHeaderRight: {
    flexShrink: 0,
    paddingLeft: 12,
    justifyContent: 'center',
    alignItems: 'center',
  },
  cardTitle: {
    color: '#FFFFFF',
    fontSize: 16,
    fontWeight: '700',
  },
  cardHeaderIcon: {
    width: 28,
    height: 28,
    opacity: 0.92,
  },
  cardSubtitle: {
    color: '#D7DEE8',
    fontSize: 13,
    lineHeight: 18,
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
  songList: {
    gap: 8,
    marginTop: 4,
  },
  songSeparator: {
    height: 8,
  },
  songRowPressable: {},
  debugText: {
    color: '#9CA3AF',
    fontSize: 12,
    lineHeight: 16,
    textAlign: 'center',
  },
  songRowInner: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: 10,
    paddingVertical: 10,
    paddingHorizontal: 10,
  },
  songRowInnerPressed: {
    opacity: 0.85,
  },
  songLeft: {
    flexGrow: 1,
    flexShrink: 1,
    minWidth: 0,
    flexDirection: 'row',
    alignItems: 'center',
    gap: 10,
  },
  thumbWrap: {
    width: 44,
    height: 44,
    borderRadius: 10,
    overflow: 'hidden',
    backgroundColor: '#0F172A',
  },
  thumb: {
    width: '100%',
    height: '100%',
  },
  thumbPlaceholder: {
    width: '100%',
    height: '100%',
    backgroundColor: '#111827',
  },
  songRowText: {
    flexGrow: 1,
    flexShrink: 1,
    minWidth: 0,
    gap: 2,
  },
  songRight: {
    flexShrink: 0,
    alignItems: 'flex-end',
    justifyContent: 'center',
    gap: 4,
  },
  songRightSingle: {
    height: 24,
    justifyContent: 'center',
    alignItems: 'center',
  },
  unfcPctText: {
    color: '#FFFFFF',
    fontSize: 16,
    fontWeight: '800',
    letterSpacing: 0.2,
    minWidth: 56,
    includeFontPadding: false,
    lineHeight: 18,
    textAlign: 'center',
    textAlignVertical: 'center',
  },
  fcTheseInstrumentIcon: {
    width: 24,
    height: 24,
    opacity: 0.92,
    alignSelf: 'center',
  },
  starGainsStarsRow: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    gap: 4,
    paddingBottom: 6,
    paddingHorizontal: 10,
  },
  starGainsStarsInner: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
  },
  starGainsStarIcon: {
    width: 14,
    height: 14,
    opacity: 0.95,
  },
  starGainsScoreText: {
    marginLeft: 8,
    color: '#D7DEE8',
    fontSize: 12,
    fontWeight: '700',
  },
  songTitle: {
    color: '#FFFFFF',
    fontWeight: '700',
    fontSize: 14,
  },
  songMeta: {
    color: '#9AA6B2',
    fontSize: 12,
  },
  songRightText: {
    color: '#D7DEE8',
    fontSize: 12,
    fontWeight: '700',
  },
  instrumentRow: {
    flexDirection: 'row',
    gap: 6,
    alignItems: 'center',
    justifyContent: 'flex-end',
  },
  instrumentChip: {
    width: 28,
    height: 28,
    borderRadius: 14,
    borderWidth: 2,
    alignItems: 'center',
    justifyContent: 'center',
  },
  instrumentIcon: {
    width: 18,
    height: 18,
  },
});
