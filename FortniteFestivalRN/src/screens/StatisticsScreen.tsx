import React, {useCallback, useMemo} from 'react';
import {FlatList, Image, Pressable, StyleSheet, Text, View} from 'react-native';

import type {InstrumentKey} from '../core/instruments';
import type {Song} from '../core/models';
import {useFestival} from '../app/festival/FestivalContext';
import {formatScoreCompact} from '../app/format/formatters';
import {usePageInstrumentation} from '../app/instrumentation/usePageInstrumentation';
import {buildInstrumentStats, buildTopSongCategories, type InstrumentDetailedStats} from '../app/statistics/statistics';
import type {SuggestionCategory} from '../core/suggestions/types';
import {getInstrumentIconSource} from '../ui/instruments/instrumentVisuals';
import {Screen} from '../ui/Screen';
import {FrostedSurface} from '../ui/FrostedSurface';
import {useOptionalBottomTabBarHeight} from '../navigation/useOptionalBottomTabBarHeight';

const TOP_SONGS_VIRTUALIZE_THRESHOLD = 12;

const shouldShowCategory = (
  categoryKey: string,
  settings: {queryLead: boolean; queryDrums: boolean; queryVocals: boolean; queryBass: boolean; queryProLead: boolean; queryProBass: boolean},
): boolean => {
  const key = categoryKey.toLowerCase();
  if (key.includes('pro_guitar') || key.includes('prolead') || key.includes('pro_lead')) return settings.queryProLead;
  if (key.includes('pro_bass') || key.includes('probass')) return settings.queryProBass;
  if (key.includes('guitar') || key.includes('lead')) return settings.queryLead;
  if (key.includes('bass')) return settings.queryBass;
  if (key.includes('drums')) return settings.queryDrums;
  if (key.includes('vocals') || key.includes('vocal')) return settings.queryVocals;
  return true;
};

const isInstrumentEnabled = (
  instrument: InstrumentKey,
  settings: {queryLead: boolean; queryDrums: boolean; queryVocals: boolean; queryBass: boolean; queryProLead: boolean; queryProBass: boolean},
): boolean => {
  switch (instrument) {
    case 'guitar':
      return settings.queryLead;
    case 'bass':
      return settings.queryBass;
    case 'drums':
      return settings.queryDrums;
    case 'vocals':
      return settings.queryVocals;
    case 'pro_guitar':
      return settings.queryProLead;
    case 'pro_bass':
      return settings.queryProBass;
    default:
      return true;
  }
};

type StatsListItem =
  | {type: 'instrument'; key: string; stats: InstrumentDetailedStats}
  | {type: 'top'; key: string; cat: SuggestionCategory};

export function StatisticsScreen(props: {onOpenSong?: (songId: string, title: string) => void}) {
  usePageInstrumentation('Statistics');

  const tabBarHeight = useOptionalBottomTabBarHeight();

  const {onOpenSong} = props;

  const {
    state: {songs, scoresIndex, settings},
  } = useFestival();

  const instrumentQuerySettings = useMemo(() => ({
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

  const boards = useMemo(() => {
    return Object.values(scoresIndex).filter(Boolean) as any[];
  }, [scoresIndex]);

  const instrumentStats = useMemo(() => {
    const totalSongsInLibrary = songs.length > 0 ? songs.length : boards.length;
    const stats = buildInstrumentStats({boards, totalSongsInLibrary});
    return stats.filter(s => isInstrumentEnabled(s.instrumentKey, instrumentQuerySettings));
  }, [boards, instrumentQuerySettings, songs.length]);

  const topCategories = useMemo(() => {
    const cats = buildTopSongCategories({boards});
    return cats.filter(c => shouldShowCategory(c.key, instrumentQuerySettings));
  }, [boards, instrumentQuerySettings]);

  const hasAnyScores = Object.keys(scoresIndex).length > 0;

  const header = useMemo(() => (
    <>
      <Text style={styles.title}>Statistics</Text>
      <Text style={styles.subtitle}>
        {songs.length ? `${songs.length} songs • ${boards.length} score rows` : `${boards.length} score rows`}
      </Text>
    </>
  ), [boards.length, songs.length]);

  const listStyle = useMemo(() => ({flex: 1, marginBottom: -tabBarHeight}), [tabBarHeight]);
  const listContentStyle = useMemo(() => [styles.content, {paddingBottom: tabBarHeight + 16}], [tabBarHeight]);
  const scrollInsets = useMemo(() => ({bottom: tabBarHeight}), [tabBarHeight]);

  const data = useMemo<StatsListItem[]>(() => {
    const items: StatsListItem[] = [];
    for (const s of instrumentStats) items.push({type: 'instrument', key: `inst:${s.instrumentKey}`, stats: s});
    for (const c of topCategories) items.push({type: 'top', key: `top:${c.key}`, cat: c});
    return items;
  }, [instrumentStats, topCategories]);

  const renderItem = useCallback(({item}: {item: StatsListItem}) => {
    if (item.type === 'instrument') return <InstrumentCard stats={item.stats} />;
    return <TopSongsCard cat={item.cat} songById={songById} onOpenSong={onOpenSong} />;
  }, [onOpenSong, songById]);

  if (!hasAnyScores) {
    return (
      <Screen>
        <View style={styles.content}>
          <Text style={styles.title}>Statistics</Text>
          <FrostedSurface style={styles.emptyState} tint="dark" intensity={18}>
            <Text style={styles.emptyTitle}>No Statistics Available</Text>
            <Text style={styles.emptyBody}>Sync your scores to see your Fortnite Festival stats.</Text>
          </FrostedSurface>
        </View>
      </Screen>
    );
  }

  return (
    <Screen>
      <FlatList
        style={listStyle}
        contentContainerStyle={listContentStyle}
        scrollIndicatorInsets={scrollInsets}
        keyboardShouldPersistTaps="handled"
        data={data}
        keyExtractor={i => i.key}
        renderItem={renderItem}
        ListHeaderComponent={header}
        removeClippedSubviews
        initialNumToRender={8}
        maxToRenderPerBatch={6}
        updateCellsBatchingPeriod={24}
        windowSize={7}
      />
    </Screen>
  );
}

const InstrumentCard = React.memo(function InstrumentCard(props: {stats: InstrumentDetailedStats}) {
  const s = props.stats;

  const pctTotal =
    s.top1PercentCount +
    s.top5PercentCount +
    s.top10PercentCount +
    s.top25PercentCount +
    s.top50PercentCount +
    s.below50PercentCount;

  return (
    <FrostedSurface style={styles.card} tint="dark" intensity={18}>
      <View style={styles.cardHeaderRow}>
        <Image source={getInstrumentIconSource(s.instrumentKey)} style={styles.instrumentIcon} />
        <View style={styles.cardHeaderText}>
          <Text style={styles.cardTitle}>{s.instrumentLabel}</Text>
          <Text style={styles.cardSubtitle}>
            {s.songsPlayed} of {s.totalSongsInLibrary} songs played ({s.completionPercent.toFixed(1)}%)
          </Text>
        </View>
      </View>

      <View style={styles.statsGrid}>
        <StatCell label="FCs" value={`${s.fcCount} (${s.fcPercent.toFixed(1)}%)`} />
        <StatCell label="Gold Stars" value={`${s.goldStarCount}`} />
        <StatCell label="5 Stars" value={`${s.fiveStarCount}`} />
        <StatCell label="4 Stars" value={`${s.fourStarCount}`} />
        <StatCell label="Avg Accuracy" value={`${s.averageAccuracy.toFixed(2)}%`} />
        <StatCell label="Best Accuracy" value={`${s.bestAccuracy.toFixed(2)}%`} />
        <StatCell label="Perfect Scores" value={`${s.perfectScoreCount}`} />
        <StatCell label="Avg Stars" value={`${s.averageStars.toFixed(2)}`} />
        <StatCell label="Total Score" value={formatScoreCompact(s.totalScore)} />
        <StatCell label="Highest Score" value={formatScoreCompact(s.highestScore)} />
        <StatCell label="Best Rank" value={s.bestRank > 0 ? s.bestRankFormatted : '—'} />
        <StatCell label="Weighted Percentile" value={s.weightedPercentileFormatted !== 'N/A' ? s.weightedPercentileFormatted : '—'} />
      </View>

      {s.songsPlayed > 0 && (
        <View style={styles.distWrap}>
          <Text style={styles.sectionTitle}>Percentile Distribution</Text>

          {pctTotal > 0 ? (
            <View style={styles.distBar}>
              <DistSeg color="#27ae60" count={s.top1PercentCount} total={pctTotal} />
              <DistSeg color="#2ecc71" count={s.top5PercentCount} total={pctTotal} />
              <DistSeg color="#f1c40f" count={s.top10PercentCount} total={pctTotal} />
              <DistSeg color="#e67e22" count={s.top25PercentCount} total={pctTotal} />
              <DistSeg color="#e74c3c" count={s.top50PercentCount} total={pctTotal} />
              <DistSeg color="#7f8c8d" count={s.below50PercentCount} total={pctTotal} />
            </View>
          ) : (
            <Text style={styles.muted}>No percentile data yet.</Text>
          )}

          <View style={styles.legendGrid}>
            <LegendItem label="Top 1%" color="#27ae60" value={s.top1PercentCount} />
            <LegendItem label="Top 5%" color="#2ecc71" value={s.top5PercentCount} />
            <LegendItem label="Top 10%" color="#f1c40f" value={s.top10PercentCount} />
            <LegendItem label="Top 25%" color="#e67e22" value={s.top25PercentCount} />
            <LegendItem label="Top 50%" color="#e74c3c" value={s.top50PercentCount} />
            <LegendItem label="> 50%" color="#7f8c8d" value={s.below50PercentCount} />
          </View>
        </View>
      )}
    </FrostedSurface>
  );
});

const StatCell = React.memo(function StatCell(props: {label: string; value: string}) {
  return (
    <View style={styles.statCell}>
      <Text style={styles.statLabel}>{props.label}</Text>
      <Text style={styles.statValue}>{props.value}</Text>
    </View>
  );
});

const DistSeg = React.memo(function DistSeg(props: {color: string; count: number; total: number}) {
  if (props.count <= 0 || props.total <= 0) return null;
  return <View style={[styles.distSeg, {backgroundColor: props.color, flex: props.count}]} />;
});

const LegendItem = React.memo(function LegendItem(props: {label: string; color: string; value: number}) {
  return (
    <View style={styles.legendItem}>
      <View style={[styles.legendSwatch, {backgroundColor: props.color}]} />
      <Text style={styles.legendText}>
        {props.label}: {props.value}
      </Text>
    </View>
  );
});

const TopSongsCard = React.memo(function TopSongsCard(props: {
  cat: SuggestionCategory;
  songById: ReadonlyMap<string, Song>;
  onOpenSong?: (songId: string, title: string) => void;
}) {
  const {cat, songById, onOpenSong} = props;

  const useVirtualList = cat.songs.length > TOP_SONGS_VIRTUALIZE_THRESHOLD;

  const renderSong = useCallback(({item}: {item: any}) => {
    const song = songById.get(item.songId);
    const imageUri = song?.imagePath ?? song?.track?.au;
    return (
      <TopSongRow
        catKey={cat.key}
        item={item}
        imageUri={imageUri}
        onPress={() => onOpenSong?.(item.songId, item.title)}
      />
    );
  }, [cat.key, onOpenSong, songById]);

  return (
    <FrostedSurface style={styles.card} tint="dark" intensity={18}>
      <Text style={styles.cardTitle}>{cat.title}</Text>
      <Text style={styles.cardSubtitle}>{cat.description}</Text>

      <View style={styles.songList}>
        {useVirtualList ? (
          <FlatList
            data={cat.songs as any[]}
            keyExtractor={s => `${cat.key}:${s.songId}`}
            renderItem={renderSong}
            scrollEnabled={false}
            keyboardShouldPersistTaps="handled"
            removeClippedSubviews
            initialNumToRender={10}
            maxToRenderPerBatch={8}
            updateCellsBatchingPeriod={24}
            windowSize={5}
          />
        ) : (
          cat.songs.map(s => {
            const song = songById.get(s.songId);
            const imageUri = song?.imagePath ?? song?.track?.au;
            return (
              <TopSongRow
                key={`${cat.key}:${s.songId}`}
                catKey={cat.key}
                item={s as any}
                imageUri={imageUri}
                onPress={() => onOpenSong?.(s.songId, s.title)}
              />
            );
          })
        )}
      </View>
    </FrostedSurface>
  );
});

const TopSongRow = React.memo(function TopSongRow(props: {
  catKey: string;
  item: {songId: string; title: string; artist: string; percent?: number; stars?: number; fullCombo?: boolean};
  imageUri?: string;
  onPress: () => void;
}) {
  const {item} = props;
  const right = useMemo(() => formatRight(item), [item]);

  return (
    <Pressable
      onPress={props.onPress}
      style={({pressed}) => [styles.songRow, pressed && styles.songRowPressed]}
      accessibilityRole="button"
      accessibilityLabel={`Open ${item.title}`}
    >
      <View style={styles.songLeft}>
        <View style={styles.thumbWrap}>
          {props.imageUri ? (
            <Image source={{uri: props.imageUri}} style={styles.thumb} resizeMode="cover" />
          ) : (
            <View style={styles.thumbPlaceholder} />
          )}
        </View>

        <View style={styles.songRowText}>
          <Text numberOfLines={1} style={styles.songTitle}>
            {item.title || '(unknown)'}
          </Text>
          <Text numberOfLines={1} style={styles.songMeta}>
            {item.artist || '(unknown)'}
          </Text>
        </View>
      </View>

      {right ? (
        <View style={styles.songRight}>
          <Text numberOfLines={1} style={styles.songRightText}>
            {right}
          </Text>
        </View>
      ) : null}
    </Pressable>
  );
});

function formatRight(item: {percent?: number; stars?: number; fullCombo?: boolean}): string {
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
    gap: 10,
  },
  title: {
    color: '#FFFFFF',
    fontSize: 28,
    fontWeight: '800',
  },
  subtitle: {
    color: '#D7DEE8',
    fontSize: 13,
    opacity: 0.85,
    marginBottom: 8,
  },
  emptyState: {
    paddingVertical: 28,
    paddingHorizontal: 16,
    borderRadius: 14,
    gap: 8,
  },
  emptyTitle: {
    color: '#FFFFFF',
    fontSize: 20,
    fontWeight: '800',
    textAlign: 'center',
  },
  emptyBody: {
    color: '#D7DEE8',
    fontSize: 13,
    opacity: 0.85,
    textAlign: 'center',
    lineHeight: 18,
  },
  card: {
    borderRadius: 16,
    borderWidth: 2,
    borderColor: 'rgba(255,255,255,0.15)',
    padding: 14,
    gap: 10,
  },
  cardHeaderRow: {
    flexDirection: 'row',
    gap: 12,
    alignItems: 'center',
  },
  instrumentIcon: {
    width: 48,
    height: 48,
    resizeMode: 'contain',
  },
  cardHeaderText: {
    flex: 1,
    gap: 2,
  },
  cardTitle: {
    color: '#FFFFFF',
    fontSize: 20,
    fontWeight: '800',
  },
  cardSubtitle: {
    color: '#D7DEE8',
    fontSize: 13,
    opacity: 0.85,
  },
  statsGrid: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    columnGap: 16,
    rowGap: 10,
  },
  statCell: {
    width: '47%',
    gap: 2,
  },
  statLabel: {
    color: '#D7DEE8',
    fontSize: 12,
    opacity: 0.85,
  },
  statValue: {
    color: '#FFFFFF',
    fontSize: 14,
    fontWeight: '700',
  },
  distWrap: {
    gap: 8,
    marginTop: 8,
  },
  sectionTitle: {
    color: '#FFFFFF',
    fontSize: 14,
    fontWeight: '800',
  },
  muted: {
    color: '#D7DEE8',
    opacity: 0.8,
    fontSize: 13,
  },
  distBar: {
    flexDirection: 'row',
    height: 12,
    borderRadius: 6,
    overflow: 'hidden',
    backgroundColor: 'rgba(255,255,255,0.08)',
  },
  distSeg: {
    height: 12,
  },
  legendGrid: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    rowGap: 6,
    columnGap: 12,
  },
  legendItem: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 6,
  },
  legendSwatch: {
    width: 10,
    height: 10,
    borderRadius: 3,
  },
  legendText: {
    color: '#D7DEE8',
    fontSize: 12,
    opacity: 0.9,
  },
  songList: {
    gap: 8,
  },
  songRow: {
    paddingVertical: 8,
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    borderTopWidth: 1,
    borderTopColor: 'rgba(255,255,255,0.08)',
  },
  songRowPressed: {
    opacity: 0.85,
  },
  songLeft: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 10,
    flexGrow: 1,
    flexShrink: 1,
    minWidth: 0,
  },
  thumbWrap: {
    width: 40,
    height: 40,
    borderRadius: 8,
    overflow: 'hidden',
    backgroundColor: 'rgba(255,255,255,0.08)',
  },
  thumb: {
    width: 40,
    height: 40,
  },
  thumbPlaceholder: {
    width: 40,
    height: 40,
    backgroundColor: 'rgba(255,255,255,0.08)',
  },
  songRowText: {
    flexGrow: 1,
    flexShrink: 1,
    minWidth: 0,
    gap: 2,
  },
  songTitle: {
    color: '#FFFFFF',
    fontSize: 14,
    fontWeight: '700',
  },
  songMeta: {
    color: '#D7DEE8',
    fontSize: 12,
    opacity: 0.85,
  },
  songRight: {
    marginLeft: 12,
  },
  songRightText: {
    color: '#D7DEE8',
    fontSize: 12,
    opacity: 0.9,
  },
});
