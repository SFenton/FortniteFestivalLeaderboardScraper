import React, {useCallback, useMemo} from 'react';
import {FlatList, Image, Pressable, StyleSheet, Text, View} from 'react-native';
import MaskedView from '@react-native-masked-view/masked-view';
import LinearGradient from 'react-native-linear-gradient';

import type {InstrumentKey} from '../core/instruments';
import type {Song} from '../core/models';
import {useFestival} from '../app/festival/FestivalContext';
import {usePageInstrumentation} from '../app/instrumentation/usePageInstrumentation';
import {buildInstrumentStats, buildTopSongCategories, type InstrumentDetailedStats} from '../app/statistics/statistics';
import type {SuggestionCategory} from '../core/suggestions/types';
import {getInstrumentIconSource} from '../ui/instruments/instrumentVisuals';
import {Screen} from '../ui/Screen';
import {FrostedSurface} from '../ui/FrostedSurface';
import {CenteredEmptyStateCard} from '../ui/CenteredEmptyStateCard';
import {PageHeader} from '../ui/PageHeader';
import {useTabBarLayout} from '../navigation/useOptionalBottomTabBarHeight';

const TOP_SONGS_VIRTUALIZE_THRESHOLD = 12;

const shouldShowCategory = (
  categoryKey: string,
  settings: {showLead: boolean; showDrums: boolean; showVocals: boolean; showBass: boolean; showProLead: boolean; showProBass: boolean},
): boolean => {
  const key = categoryKey.toLowerCase();
  if (key.includes('pro_guitar') || key.includes('prolead') || key.includes('pro_lead')) return settings.showProLead;
  if (key.includes('pro_bass') || key.includes('probass')) return settings.showProBass;
  if (key.includes('guitar') || key.includes('lead')) return settings.showLead;
  if (key.includes('bass')) return settings.showBass;
  if (key.includes('drums')) return settings.showDrums;
  if (key.includes('vocals') || key.includes('vocal')) return settings.showVocals;
  return true;
};

const isInstrumentEnabled = (
  instrument: InstrumentKey,
  settings: {showLead: boolean; showDrums: boolean; showVocals: boolean; showBass: boolean; showProLead: boolean; showProBass: boolean},
): boolean => {
  switch (instrument) {
    case 'guitar':
      return settings.showLead;
    case 'bass':
      return settings.showBass;
    case 'drums':
      return settings.showDrums;
    case 'vocals':
      return settings.showVocals;
    case 'pro_guitar':
      return settings.showProLead;
    case 'pro_bass':
      return settings.showProBass;
    default:
      return true;
  }
};

type StatsListItem =
  | {type: 'instrument'; key: string; stats: InstrumentDetailedStats}
  | {type: 'top'; key: string; cat: SuggestionCategory};

export function StatisticsScreen(props: {onOpenSong?: (songId: string, title: string) => void}) {
  usePageInstrumentation('Statistics');

  const {height: tabBarHeight, marginBottom: tabBarMargin} = useTabBarLayout();

  const {onOpenSong} = props;

  const {
    state: {songs, scoresIndex, settings},
  } = useFestival();

  const instrumentQuerySettings = useMemo(() => ({
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

  const hasAnyScores = boards.length > 0;

  const header = useMemo(() => <PageHeader title="Statistics" />, []);

  const listStyle = useMemo(() => ({flex: 1, marginBottom: tabBarMargin}), [tabBarMargin]);
  const listContentStyle = useMemo(() => ({paddingTop: 32, paddingBottom: tabBarHeight + 16}), [tabBarHeight]);
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

  const itemSeparator = useCallback(() => <View style={styles.listSeparator} />, []);

  if (!hasAnyScores) {
    const emptyBody = settings.hasEverSyncedScores
      ? 'No scores found yet. If you just synced, give it a moment or try syncing again.'
      : 'Sync your scores to see your Fortnite Festival stats.';

    return (
      <Screen>
        <View style={styles.content}>
          <PageHeader title="Statistics" />
          <CenteredEmptyStateCard title="No Statistics Available" body={emptyBody} />
        </View>
      </Screen>
    );
  }

  return (
    <Screen>
      <View style={styles.content}>
        {header}

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
            style={listStyle}
            contentContainerStyle={listContentStyle}
            scrollIndicatorInsets={scrollInsets}
            showsVerticalScrollIndicator={false}
            showsHorizontalScrollIndicator={false}
            keyboardShouldPersistTaps="handled"
            data={data}
            keyExtractor={i => i.key}
            renderItem={renderItem}
            ItemSeparatorComponent={itemSeparator}
            removeClippedSubviews
            initialNumToRender={8}
            maxToRenderPerBatch={6}
            updateCellsBatchingPeriod={24}
            windowSize={7}
          />
        </MaskedView>
      </View>
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
        <StatCell label="Average Accuracy" value={`${s.averageAccuracy.toFixed(2)}%`} />
        <StatCell label="Best Accuracy" value={`${s.bestAccuracy.toFixed(2)}%`} />
        <StatCell label="Average Stars" value={`${s.averageStars.toFixed(2)}`} />
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

  const catInstrumentKey = useMemo(() => {
    // Keys are `stats_top_five_weighted_{instrument}` or `stats_top_five_{instrument}`.
    const known: InstrumentKey[] = ['pro_guitar', 'pro_bass', 'guitar', 'bass', 'drums', 'vocals'];
    for (const k of known) {
      if (cat.key.endsWith(`_${k}`)) return k;
    }
    return undefined;
  }, [cat.key]);

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
    <FrostedSurface style={styles.topSongsCard} tint="dark" intensity={18}>
      <View style={styles.topSongsHeaderRow}>
        <View style={styles.topSongsHeaderLeft}>
          <Text style={styles.topSongsTitle} numberOfLines={1}>{cat.title}</Text>
          <Text style={styles.topSongsSubtitle}>{cat.description}</Text>
        </View>

        {catInstrumentKey ? (
          <View style={styles.topSongsHeaderRight}>
            <Image source={getInstrumentIconSource(catInstrumentKey)} style={styles.topSongsHeaderIcon} resizeMode="contain" />
          </View>
        ) : null}
      </View>

      <View style={styles.topSongsList}>
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
      style={styles.topSongRowPressable}
      accessibilityRole="button"
      accessibilityLabel={`Open ${item.title}`}
    >
      {({pressed}) => (
        <View style={[styles.topSongRowInner, pressed && styles.topSongRowPressed]}>
          <View style={styles.topSongLeft}>
            <View style={styles.topSongThumbWrap}>
              {props.imageUri ? (
                <Image source={{uri: props.imageUri}} style={styles.topSongThumb} resizeMode="cover" />
              ) : (
                <View style={styles.topSongThumbPlaceholder} />
              )}
            </View>

            <View style={styles.topSongText}>
              <Text numberOfLines={1} style={styles.topSongTitle}>
                {item.title || '(unknown)'}
              </Text>
              <Text numberOfLines={1} style={styles.topSongMeta}>
                {item.artist || '(unknown)'}
              </Text>
            </View>
          </View>

          {right ? (
            <View style={styles.topSongRight}>
              <Text numberOfLines={1} style={styles.topSongRightText}>
                {right}
              </Text>
            </View>
          ) : null}
        </View>
      )}
    </Pressable>
  );
});

function formatRight(item: {percent?: number; stars?: number; fullCombo?: boolean}): string {
  const parts: string[] = [];

  if (typeof item.percent === 'number' && Number.isFinite(item.percent) && item.percent > 0) {
    parts.push(`Top ${item.percent.toFixed(2)}%`);
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
    gap: 10,
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
  listSeparator: {
    height: 10,
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
    marginTop: 24,
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
    marginTop: 24,
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
  topSongsCard: {
    borderRadius: 12,
    padding: 12,
    gap: 8,
  },
  topSongsHeaderRow: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
  },
  topSongsHeaderLeft: {
    flex: 1,
    minWidth: 0,
    gap: 4,
  },
  topSongsHeaderRight: {
    flexShrink: 0,
    paddingLeft: 12,
    justifyContent: 'center',
    alignItems: 'center',
  },
  topSongsTitle: {
    color: '#FFFFFF',
    fontSize: 16,
    fontWeight: '700',
  },
  topSongsSubtitle: {
    color: '#D7DEE8',
    fontSize: 13,
    lineHeight: 18,
  },
  topSongsHeaderIcon: {
    width: 28,
    height: 28,
    opacity: 0.92,
  },
  topSongsList: {
    gap: 8,
    marginTop: 4,
  },
  topSongRowPressable: {},
  topSongRowInner: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: 10,
    paddingVertical: 10,
    paddingHorizontal: 10,
  },
  topSongRowPressed: {
    opacity: 0.85,
  },
  topSongLeft: {
    flexGrow: 1,
    flexShrink: 1,
    minWidth: 0,
    flexDirection: 'row',
    alignItems: 'center',
    gap: 10,
  },
  topSongThumbWrap: {
    width: 44,
    height: 44,
    borderRadius: 10,
    overflow: 'hidden',
    backgroundColor: '#0F172A',
  },
  topSongThumb: {
    width: '100%',
    height: '100%',
  },
  topSongThumbPlaceholder: {
    width: '100%',
    height: '100%',
    backgroundColor: '#111827',
  },
  topSongText: {
    flexGrow: 1,
    flexShrink: 1,
    minWidth: 0,
    gap: 2,
  },
  topSongTitle: {
    color: '#FFFFFF',
    fontWeight: '700',
    fontSize: 14,
  },
  topSongMeta: {
    color: '#9AA6B2',
    fontSize: 12,
  },
  topSongRight: {
    flexShrink: 0,
    alignItems: 'flex-end',
    justifyContent: 'center',
    gap: 4,
  },
  topSongRightText: {
    color: '#D7DEE8',
    fontSize: 12,
    fontWeight: '700',
  },
});
