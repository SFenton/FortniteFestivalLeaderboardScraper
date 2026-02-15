import React, {useCallback, useMemo} from 'react';
import {FlatList, Image, Pressable, ScrollView, StyleSheet, Text, View} from 'react-native';
import MaskedView from '@react-native-masked-view/masked-view';
import LinearGradient from 'react-native-linear-gradient';

import type {InstrumentKey, Song, SuggestionCategory} from '@festival/core';
import {buildInstrumentStats, buildTopSongCategories, type InstrumentDetailedStats} from '@festival/core';
import {useFestival} from '@festival/contexts';
import {usePageInstrumentation} from '@festival/contexts';
import {getInstrumentIconSource} from '@festival/ui/instruments/instrumentVisuals';
import {StatisticsInstrumentCard} from '@festival/ui/instruments/StatisticsInstrumentCard';
import {Screen} from '@festival/ui/Screen';
import {FrostedSurface} from '@festival/ui/FrostedSurface';
import {CenteredEmptyStateCard} from '@festival/ui/CenteredEmptyStateCard';
import {PageHeader} from '@festival/ui/PageHeader';
import {useCardGrid} from '@festival/ui/useCardGrid';
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

  const isCardGrid = useCardGrid();

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
    if (item.type === 'instrument') return <StatisticsInstrumentCard data={item.stats} />;
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
          {isCardGrid ? (
            <ScrollView
              style={listStyle}
              contentContainerStyle={listContentStyle}
              scrollIndicatorInsets={scrollInsets}
              showsVerticalScrollIndicator={false}
              showsHorizontalScrollIndicator={false}
              keyboardShouldPersistTaps="handled"
            >
              {instrumentStats.length > 0 && (
                <>
                  <View style={styles.sectionHeader}>
                    <Text style={styles.sectionHeaderTitle}>Instrument Statistics</Text>
                    <Text style={styles.sectionHeaderDescription}>A quick look at your overall Festival statistics per instrument.</Text>
                  </View>
                  <View style={styles.cardGrid}>
                    {instrumentStats.map(s => (
                      <View key={`inst:${s.instrumentKey}`} style={styles.cardGridCell}>
                      <StatisticsInstrumentCard data={s} style={styles.cardGridChildFill} />
                      </View>
                    ))}
                    {instrumentStats.length % 2 !== 0 && <View style={styles.cardGridCell} />}
                  </View>
                </>
              )}
              {topCategories.length > 0 && (
                <>
                  <View style={[styles.sectionHeader, instrumentStats.length > 0 && styles.cardGridSectionGap]}>
                    <Text style={styles.sectionHeaderTitle}>Top Songs Per Instrument</Text>
                    <Text style={styles.sectionHeaderDescription}>A selection of the top five songs you've played per instrument.</Text>
                  </View>
                  <View style={styles.cardGrid}>
                    {topCategories.map(c => (
                      <View key={`top:${c.key}`} style={styles.cardGridCell}>
                      <TopSongsCard cat={c} songById={songById} onOpenSong={onOpenSong} style={styles.cardGridChildFill} />
                      </View>
                    ))}
                    {topCategories.length % 2 !== 0 && <View style={styles.cardGridCell} />}
                  </View>
                </>
              )}
            </ScrollView>
          ) : (
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
          )}
        </MaskedView>
      </View>
    </Screen>
  );
}

const TopSongsCard = React.memo(function TopSongsCard(props: {
  cat: SuggestionCategory;
  songById: ReadonlyMap<string, Song>;
  onOpenSong?: (songId: string, title: string) => void;
  style?: import('react-native').StyleProp<import('react-native').ViewStyle>;
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
    <FrostedSurface style={[styles.topSongsCard, props.style]} tint="dark" intensity={18}>
      <View style={styles.topSongsHeaderRow}>
        <View style={styles.topSongsHeaderLeft}>
          <Text style={styles.topSongsTitle} numberOfLines={1}>{cat.title}</Text>
          <Text style={styles.topSongsSubtitle} numberOfLines={2}>{cat.description}</Text>
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
  const rightText = useMemo(() => formatRightText(item), [item]);

  const percentilePill = useMemo(() => {
    if (typeof item.percent !== 'number' || !Number.isFinite(item.percent) || item.percent <= 0) return undefined;
    const display = `Top ${item.percent.toFixed(2)}%`;
    const isTop5 = item.percent <= 5;
    return {display, isTop5};
  }, [item.percent]);

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

          {(percentilePill || rightText) ? (
            <View style={styles.topSongRight}>
              {percentilePill ? (
                <View style={[styles.percentilePill, percentilePill.isTop5 && styles.percentilePillGold]}>
                  <Text style={[styles.percentilePillText, percentilePill.isTop5 && styles.percentilePillTextGold]} numberOfLines={1}>
                    {percentilePill.display}
                  </Text>
                </View>
              ) : null}
              {rightText ? (
                <Text numberOfLines={1} style={styles.topSongRightText}>
                  {rightText}
                </Text>
              ) : null}
            </View>
          ) : null}
        </View>
      )}
    </Pressable>
  );
});

/** Format non-percentile right-side text (stars, FC). */
function formatRightText(item: {stars?: number; fullCombo?: boolean}): string {
  const parts: string[] = [];

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
  sectionHeader: {
    gap: 4,
    marginBottom: 10,
  },
  sectionHeaderTitle: {
    color: '#FFFFFF',
    fontSize: 20,
    fontWeight: '800',
  },
  sectionHeaderDescription: {
    color: '#D7DEE8',
    fontSize: 14,
    lineHeight: 20,
  },
  cardGrid: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    columnGap: 10,
    rowGap: 10,
  },
  cardGridCell: {
    flexBasis: '47%',
    flexGrow: 1,
    flexShrink: 0,
  },
  cardGridChildFill: {
    flex: 1,
  },
  cardGridSectionGap: {
    marginTop: 42, // 10 base + 32 to match fade gradient top gap
  },
  listSeparator: {
    height: 10,
  },
  topSongsCard: {
    borderRadius: 12,
    padding: 12,
    gap: 8,
  },
  topSongsHeaderRow: {
    flexDirection: 'row',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    minHeight: 62, // title (22) + gap (4) + 2 subtitle lines (18×2) = consistent height
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
  percentilePill: {
    backgroundColor: '#1D3A71',
    borderRadius: 10,
    paddingHorizontal: 8,
    paddingVertical: 3,
    borderWidth: 2,
    borderColor: 'transparent',
    alignItems: 'center',
    justifyContent: 'center',
  },
  percentilePillGold: {
    backgroundColor: '#332915',
    borderColor: '#FFD700',
  },
  percentilePillText: {
    color: '#FFFFFF',
    fontWeight: '800',
    fontSize: 12,
  },
  percentilePillTextGold: {
    color: '#FFD700',
  },
});
