import React, {useCallback, useEffect, useMemo, useRef, useState} from 'react';
import {ActivityIndicator, FlatList, Image, Platform, Pressable, StyleSheet, Text, useWindowDimensions, View} from 'react-native';

import {Screen} from '../ui/Screen';
import {FrostedSurface} from '../ui/FrostedSurface';
import {useFestival} from '../app/festival/FestivalContext';
import {usePageInstrumentation} from '../app/instrumentation/usePageInstrumentation';
import type {LeaderboardData, Song} from '../core/models';
import {buildSongDisplayRow, type InstrumentQuerySettings} from '../app/songs/songFiltering';
import {SuggestionGenerator} from '../core/suggestions/suggestionGenerator';
import type {SuggestionCategory, SuggestionSongItem} from '../core/suggestions/types';
import {getInstrumentIconSource, getInstrumentStatusVisual} from '../ui/instruments/instrumentVisuals';
import {useOptionalBottomTabBarHeight} from '../navigation/useOptionalBottomTabBarHeight';

const INITIAL_BATCH = 10;
const SUBSEQUENT_BATCH = 4;

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

  const tabBarHeight = useOptionalBottomTabBarHeight();

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

  const attachUiKeys = useCallback((list: SuggestionCategory[]): SuggestionCategoryRow[] => {
    return list.map(c => ({...c, uiKey: `${c.key}:${nextUiKey.current++}`}));
  }, []);

  const visibleCategories = useMemo(() => {
    return categories.filter(c => shouldShowCategory(c.key, instrumentQuerySettings));
  }, [categories, instrumentQuerySettings]);

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

    const first = gen.getNext(INITIAL_BATCH, songs, scoresIndex);
    setCategories(attachUiKeys(first));
    setInitialLoading(false);
  }, [attachUiKeys, scoresIndex, seed, songs]);

  useEffect(() => {
    loadInitial();
  }, [loadInitial]);

  const loadMore = useCallback(() => {
    if (!songs.length) return;
    if (initialLoading || loadingMore) return;
    if (!genRef.current) return;

    setLoadingMore(true);
    const next = genRef.current.getNext(SUBSEQUENT_BATCH, songs, scoresIndex);
    if (next.length === 0) {
      genRef.current.resetForEndless();
      const again = genRef.current.getNext(SUBSEQUENT_BATCH, songs, scoresIndex);
      setCategories(cur => [...cur, ...attachUiKeys(again)]);
      setLoadingMore(false);
      return;
    }

    setCategories(cur => [...cur, ...attachUiKeys(next)]);
    setLoadingMore(false);
  }, [attachUiKeys, initialLoading, loadingMore, scoresIndex, songs]);

  const header = (
    <View style={styles.headerRow}>
      <View style={styles.headerLeft}>
        <Text style={styles.title}>Suggestions</Text>
        <Text style={styles.subtitle}>
          {songs.length ? `${songs.length} songs • ${visibleCategories.length} categories` : 'Sync songs to get started'}
        </Text>
      </View>

      <Pressable
        onPress={() => {
          const next = Math.floor(Date.now() / (1000 * 60 * 60 * 24)) + Math.floor(Math.random() * 1000);
          setSeed(next);
          logUi(`[SUGGESTIONS] regenerate seed=${next}`);
        }}
        style={({pressed}) => [styles.button, pressed && styles.buttonPressed]}
        accessibilityRole="button"
        accessibilityLabel="Regenerate suggestions"
      >
        <Text style={styles.buttonText}>Regenerate</Text>
      </Pressable>
    </View>
  );

  const footer = loadingMore ? (
    <View style={styles.loadingRow}>
      <ActivityIndicator />
    </View>
  ) : null;

  if (!songs.length) {
    return (
      <Screen>
        <View style={styles.content}>
          {header}
          <FrostedSurface style={styles.card} tint="dark" intensity={18}>
            <Text style={styles.cardTitle}>No songs yet</Text>
            <Text style={styles.body}>Go to Sync and load the song catalog first.</Text>
          </FrostedSurface>
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

  if (visibleCategories.length === 0) {
    return (
      <Screen>
        <View style={styles.content}>
          {header}
          <FrostedSurface style={styles.card} tint="dark" intensity={18}>
            <Text style={styles.cardTitle}>No Suggestions Available</Text>
            <Text style={styles.body}>Sync your scores to generate suggestions on what to play next.</Text>
          </FrostedSurface>
        </View>
      </Screen>
    );
  }

  return (
    <Screen>
      <FlatList
        style={{flex: 1, marginBottom: -tabBarHeight}}
        contentContainerStyle={[styles.content, {paddingBottom: tabBarHeight + 16}]}
        scrollIndicatorInsets={{bottom: tabBarHeight}}
        data={visibleCategories}
        keyExtractor={c => c.uiKey}
        keyboardShouldPersistTaps="handled"
        extraData={useCompactLayout}
        ListHeaderComponent={header}
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

  // Avoid nested VirtualizedList overhead for small lists, but virtualize very large categories.
  const useVirtualSongsList = cat.songs.length > 12;

  const renderSong = useCallback(({item}: {item: SuggestionSongItem}) => {
    return (
      <SuggestionSongRow
        item={item}
        useCompactLayout={props.useCompactLayout}
        song={props.songById.get(item.songId)}
        leaderboardData={props.scoresIndex[item.songId]}
        settings={props.instrumentQuerySettings}
        onOpenSong={props.onOpenSong}
      />
    );
  }, [props.instrumentQuerySettings, props.onOpenSong, props.scoresIndex, props.songById, props.useCompactLayout]);

  const songSeparator = useCallback(() => <View style={styles.songSeparator} />, []);

  return (
    <FrostedSurface style={styles.card} tint="dark" intensity={18}>
      <Text style={styles.cardTitle}>{cat.title}</Text>
      <Text style={styles.cardSubtitle}>{cat.description}</Text>

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
  item: SuggestionSongItem;
  song?: Song;
  leaderboardData?: LeaderboardData;
  settings: InstrumentQuerySettings;
  useCompactLayout: boolean;
  onOpenSong: (songId: string, title: string) => void;
}) {
  const {item, song, leaderboardData, settings} = props;

  const imageUri = song?.imagePath ?? song?.track?.au;

  const right = useMemo(() => formatRight(item), [item]);

  const row = useMemo(() => {
    if (!song) return null;
    return buildSongDisplayRow({song, leaderboardData, settings});
  }, [leaderboardData, settings, song]);

  return (
    <Pressable
      onPress={() => props.onOpenSong(item.songId, item.title)}
      style={styles.songRowPressable}
      accessibilityRole="button"
      accessibilityLabel={`Open ${item.title}`}
    >
      {({pressed}) => (
        <FrostedSurface style={[styles.songRowSurface, pressed && styles.songRowSurfacePressed]} tint="dark" intensity={12}>
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
                <Text numberOfLines={1} style={styles.songTitle}>
                  {item.title}
                </Text>
                <Text numberOfLines={1} style={styles.songMeta}>
                  {item.artist}
                </Text>
              </View>
            </View>

            <View style={styles.songRight}>
              {!props.useCompactLayout && row ? (
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
              {right ? <Text style={styles.songRightText}>{right}</Text> : null}
            </View>
          </View>
        </FrostedSurface>
      )}
    </Pressable>
  );
}, (prev, next) => (
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
    paddingHorizontal: 20,
    paddingVertical: 16,
    gap: 12,
  },
  headerRow: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: 12,
  },
  headerLeft: {
    flexGrow: 1,
    flexShrink: 1,
    gap: 4,
  },
  title: {
    color: '#FFFFFF',
    fontSize: 22,
    fontWeight: '700',
  },
  subtitle: {
    color: '#B8C0CC',
    fontSize: 14,
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
  button: {
    backgroundColor: '#223047',
    paddingVertical: 10,
    paddingHorizontal: 12,
    borderRadius: 10,
    alignItems: 'center',
    justifyContent: 'center',
  },
  buttonPressed: {
    opacity: 0.85,
  },
  buttonText: {
    color: '#FFFFFF',
    fontWeight: '700',
  },
  loadingRow: {
    paddingVertical: 18,
    alignItems: 'center',
    justifyContent: 'center',
  },
  songList: {
    gap: 8,
    marginTop: 4,
  },
  songSeparator: {
    height: 8,
  },
  songRowPressable: {},
  songRowSurface: {
    borderRadius: 10,
  },
  songRowSurfacePressed: {
    opacity: 0.92,
  },
  songRowInner: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: 10,
    paddingVertical: 10,
    paddingHorizontal: 10,
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
