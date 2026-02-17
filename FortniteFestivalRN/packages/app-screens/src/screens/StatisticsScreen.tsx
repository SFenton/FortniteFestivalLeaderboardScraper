import React, {useCallback, useMemo} from 'react';
import {FlatList, Platform, ScrollView, StyleSheet, Text, View} from 'react-native';


import type {InstrumentKey, Song, SuggestionCategory} from '@festival/core';
import {buildInstrumentStats, buildTopSongCategories, isInstrumentVisible, shouldShowCategory, type InstrumentDetailedStats} from '@festival/core';
import {useFestival, usePageInstrumentation} from '@festival/contexts';
import {StatisticsInstrumentCard, Screen, CenteredEmptyStateCard, PageHeader, HamburgerButton, useCardGrid, WIN_SCROLLBAR_INSET, FadeScrollView, TopSongRow, CategoryCard, gridStyles, Layout, Gap} from '@festival/ui';
import {useTabBarLayout} from '../navigation/useOptionalBottomTabBarHeight';
import {useWindowsFlyoutUi} from '../navigation/windowsFlyoutUi';

const TOP_SONGS_VIRTUALIZE_THRESHOLD = 12;

type StatsListItem =
  | {type: 'instrument'; key: string; stats: InstrumentDetailedStats}
  | {type: 'top'; key: string; cat: SuggestionCategory};

export function StatisticsScreen(props: {onOpenSong?: (songId: string, title: string) => void}) {
  usePageInstrumentation('Statistics');

  const {openFlyout} = useWindowsFlyoutUi();
  const hamburger = Platform.OS === 'windows' ? <HamburgerButton onPress={openFlyout} /> : undefined;

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
    return stats.filter(s => isInstrumentVisible(s.instrumentKey, instrumentQuerySettings));
  }, [boards, instrumentQuerySettings, songs.length]);

  const topCategories = useMemo(() => {
    const cats = buildTopSongCategories({boards});
    return cats.filter(c => shouldShowCategory(c.key, instrumentQuerySettings));
  }, [boards, instrumentQuerySettings]);

  const hasAnyScores = boards.length > 0;

  const header = useMemo(() => <PageHeader title="Statistics" left={hamburger} />, [hamburger]);

  const listStyle = useMemo(() => ({flex: 1, marginBottom: tabBarMargin}), [tabBarMargin]);
  const listContentStyle = useMemo(() => ({paddingTop: 32, paddingBottom: tabBarHeight + 16, paddingRight: WIN_SCROLLBAR_INSET}), [tabBarHeight]);
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
          <PageHeader title="Statistics" left={hamburger} />
          <CenteredEmptyStateCard title="No Statistics Available" body={emptyBody} />
        </View>
      </Screen>
    );
  }

  return (
    <Screen>
      <View style={styles.content}>
        {header}

        <FadeScrollView>
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
                  <View style={gridStyles.sectionHeader}>
                    <Text style={gridStyles.sectionHeaderTitle}>Instrument Statistics</Text>
                    <Text style={gridStyles.sectionHeaderDescription}>A quick look at your overall Festival statistics per instrument.</Text>
                  </View>
                  <View style={gridStyles.cardGrid}>
                    <View style={gridStyles.cardGridColumnLeft}>
                      {instrumentStats.filter((_, i) => i % 2 === 0).map(s => (
                        <StatisticsInstrumentCard key={`inst:${s.instrumentKey}`} data={s} />
                      ))}
                    </View>
                    <View style={gridStyles.cardGridColumnRight}>
                      {instrumentStats.filter((_, i) => i % 2 !== 0).map(s => (
                        <StatisticsInstrumentCard key={`inst:${s.instrumentKey}`} data={s} />
                      ))}
                    </View>
                  </View>
                </>
              )}
              {topCategories.length > 0 && (
                <>
                  <View style={[gridStyles.sectionHeader, instrumentStats.length > 0 && styles.cardGridSectionGap]}>
                    <Text style={gridStyles.sectionHeaderTitle}>Top Songs Per Instrument</Text>
                    <Text style={gridStyles.sectionHeaderDescription}>A selection of the top five songs you've played per instrument.</Text>
                  </View>
                  <View style={gridStyles.cardGrid}>
                    <View style={gridStyles.cardGridColumnLeft}>
                      {topCategories.filter((_, i) => i % 2 === 0).map(c => (
                        <TopSongsCard key={`top:${c.key}`} cat={c} songById={songById} onOpenSong={onOpenSong} />
                      ))}
                    </View>
                    <View style={gridStyles.cardGridColumnRight}>
                      {topCategories.filter((_, i) => i % 2 !== 0).map(c => (
                        <TopSongsCard key={`top:${c.key}`} cat={c} songById={songById} onOpenSong={onOpenSong} />
                      ))}
                    </View>
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
        </FadeScrollView>
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
        item={item}
        imageUri={imageUri}
        onPress={() => onOpenSong?.(item.songId, item.title)}
      />
    );
  }, [onOpenSong, songById]);

  return (
    <CategoryCard
      title={cat.title}
      description={cat.description}
      descriptionNumberOfLines={2}
      instrumentKey={catInstrumentKey}
      style={props.style}
      headerRowStyle={styles.topSongsHeaderRowExtra}>
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
              item={s as any}
              imageUri={imageUri}
              onPress={() => onOpenSong?.(s.songId, s.title)}
            />
          );
        })
      )}
    </CategoryCard>
  );
});

const styles = StyleSheet.create({
  content: {
    flex: 1,
    paddingHorizontal: Layout.paddingHorizontal,
    paddingTop: Layout.paddingTop,
    paddingBottom: Layout.paddingBottom,
    gap: Gap.lg,
  },
  cardGridSectionGap: {
    marginTop: 42, // 10 base + 32 to match fade gradient top gap
  },
  listSeparator: {
    height: Gap.lg,
  },
  /** Extra header-row overrides specific to TopSongsCard (flex-start + minHeight). */
  topSongsHeaderRowExtra: {
    alignItems: 'flex-start',
    minHeight: 62, // title (22) + gap (4) + 2 subtitle lines (18×2) = consistent height
  },
});
