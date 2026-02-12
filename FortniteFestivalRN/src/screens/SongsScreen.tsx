import React, {useCallback, useEffect, useMemo, useRef, useState} from 'react';
import {FlatList, Platform, Pressable, StyleSheet, TextInput, useWindowDimensions, View} from 'react-native';
import Ionicons from 'react-native-vector-icons/Ionicons';
import MaskedView from '@react-native-masked-view/masked-view';
import LinearGradient from 'react-native-linear-gradient';
import {useTabBarLayout} from '../navigation/useOptionalBottomTabBarHeight';
import {useCardGrid} from '../ui/useCardGrid';

import { Screen } from '../ui/Screen';
import {usePageInstrumentation} from '../app/instrumentation/usePageInstrumentation';
import {useFestival} from '../app/festival/FestivalContext';
import type {LeaderboardData, Song} from '../core/models';
import {buildSongDisplayRow, defaultAdvancedMissingFilters, defaultMetadataSortPriority, defaultPrimaryInstrumentOrder, filterAndSortSongs, normalizeInstrumentOrder, normalizeMetadataSortPriority, type InstrumentShowSettings} from '../app/songs/songFiltering';
import {getInstrumentStatusVisual} from '../ui/instruments/instrumentVisuals';
import {SortModal} from '../ui/Modals/SortModal';
import {FilterModal} from '../ui/Modals/FilterModal';
import {FrostedSurface} from '../ui/FrostedSurface';
import {CenteredEmptyStateCard} from '../ui/CenteredEmptyStateCard';
import {PageHeader} from '../ui/PageHeader';
import {SongRow as SongRowView, type InstrumentChipVisual, type InstrumentDetailData, type SongRowDisplayData} from '../ui/songs/SongRow';
import type {AdvancedMissingFilters, MetadataSortKey, SongSortMode} from '../core/songListConfig';
import type {InstrumentKey} from '../core/instruments';
import {formatIntegerWithCommas} from '../app/format/formatters';
import {formatSeason} from '../app/songInfo/songInfo';

type SongRowWrapperProps = {
  song: Song;
  leaderboardData?: LeaderboardData;
  settings: InstrumentShowSettings;
  useCompactLayout: boolean;
  inlineInstruments: boolean;
  hideInstrumentIcons: boolean;
  selectedInstrumentFilter: InstrumentKey | null;
  metadataDisplayOrder: MetadataSortKey[];
  onOpen: (songId: string, title: string) => void;
};

const SongRow = React.memo<SongRowWrapperProps>(function SongRow(props) {
  const {song, leaderboardData, settings, onOpen} = props;

  const id = song.track.su;
  const title = song.track.tt ?? song._title ?? id;
  const artist = song.track.an ?? '';
  const year = song.track.ry;
  const imageUri = song.imagePath ?? song.track.au;

  const noInstrumentsEnabled = !settings.showLead && !settings.showBass && !settings.showDrums && !settings.showVocals && !settings.showProLead && !settings.showProBass;
  const showInstrumentIcons = !props.hideInstrumentIcons && !noInstrumentsEnabled;

  const row = useMemo(() => {
    if (!showInstrumentIcons) return null;
    return buildSongDisplayRow({song, leaderboardData, settings});
  }, [leaderboardData, settings, showInstrumentIcons, song]);

  const instruments = useMemo<InstrumentChipVisual[] | undefined>(() => {
    if (!showInstrumentIcons || !row) return undefined;
    const all = row.instrumentStatuses.filter(s => s.isEnabled).map(s => {
      const {fill, stroke} = getInstrumentStatusVisual({hasScore: s.hasScore, isFullCombo: s.isFullCombo});
      return {instrumentKey: s.instrumentKey, fill, stroke};
    });
    if (props.selectedInstrumentFilter) {
      return all.filter(s => s.instrumentKey === props.selectedInstrumentFilter);
    }
    return all;
  }, [row, showInstrumentIcons, props.selectedInstrumentFilter]);

  const instrumentDetail = useMemo<InstrumentDetailData | undefined>(() => {
    if (!props.selectedInstrumentFilter) return undefined;
    if (!leaderboardData) {
      return {scoreDisplay: '', starsCount: 0, hasScore: false, isFullCombo: false, seasonDisplay: ''};
    }
    const tracker = (leaderboardData as any)[props.selectedInstrumentFilter];
    if (!tracker || !tracker.initialized) {
      return {scoreDisplay: '', starsCount: 0, hasScore: false, isFullCombo: false, seasonDisplay: ''};
    }
    return {
      scoreDisplay: formatIntegerWithCommas(tracker.maxScore),
      starsCount: tracker.numStars,
      hasScore: true,
      isFullCombo: tracker.isFullCombo,
      seasonDisplay: formatSeason(tracker.seasonAchieved),
      percentHitDisplay: tracker.percentHit > 0 ? `${Math.floor(tracker.percentHit / 10000)}%` : undefined,
      percentileDisplay: tracker.leaderboardPercentileFormatted || (tracker.rank > 0 ? `#${formatIntegerWithCommas(tracker.rank)}` : undefined),
      isTop5Percentile: tracker.rawPercentile > 0 && tracker.rawPercentile <= 0.05,
    };
  }, [leaderboardData, props.selectedInstrumentFilter]);

  const data = useMemo<SongRowDisplayData>(() => ({title, artist, year, imageUri, instruments, instrumentDetail, metadataDisplayOrder: props.metadataDisplayOrder}), [title, artist, year, imageUri, instruments, instrumentDetail, props.metadataDisplayOrder]);
  const handlePress = useCallback(() => onOpen(id, title), [id, title, onOpen]);

  return (
    <SongRowView data={data} compact={props.useCompactLayout} inlineInstruments={props.inlineInstruments} onPress={handlePress} />
  );
}, (prev, next) => (
  prev.song === next.song &&
  prev.leaderboardData === next.leaderboardData &&
  prev.settings === next.settings &&
  prev.useCompactLayout === next.useCompactLayout &&
  prev.inlineInstruments === next.inlineInstruments &&
  prev.hideInstrumentIcons === next.hideInstrumentIcons &&
  prev.selectedInstrumentFilter === next.selectedInstrumentFilter &&
  prev.metadataDisplayOrder === next.metadataDisplayOrder &&
  prev.onOpen === next.onOpen
));

export function SongsScreen(props: {onOpenSong?: (songId: string, title: string) => void}) {
  usePageInstrumentation('Songs');

  const {width} = useWindowDimensions();
  const useCompactLayout = width < 900;
  const isTabletOrFoldable = useCardGrid();

  const {onOpenSong} = props;

  // `Screen` intentionally does not apply bottom safe-area padding (to avoid a
  // persistent dead band above the navbar). Lists need explicit padding so the
  // final rows aren’t hidden behind the tab bar.
  const {height: tabBarHeight, marginBottom: tabBarMargin} = useTabBarLayout();

  // Fixed-height rows let FlatList skip measurement work.
  // Keep this in sync with styles: rowInner padding + thumb/chip sizes + row margin.
  const ROW_HEIGHT = 72;

  const listStyle = useMemo(() => ({flex: 1, marginBottom: tabBarMargin}), [tabBarMargin]);
  const listContentStyle = useMemo(() => [styles.list, {paddingBottom: tabBarHeight}], [tabBarHeight]);
  const scrollInsets = useMemo(() => ({bottom: tabBarHeight}), [tabBarHeight]);

  const {
    state: {songs, scoresIndex, settings},
    actions: {logUi, setSettings},
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

  const [query, setQuery] = useState('');
  const [showSort, setShowSort] = useState(false);
  const [showFilter, setShowFilter] = useState(false);

  const [sortDraft, setSortDraft] = useState<{sortMode: SongSortMode; sortAscending: boolean; order: InstrumentKey[]; metadataOrder: MetadataSortKey[]}>({
    sortMode: settings.songsSortMode,
    sortAscending: settings.songsSortAscending,
    order: normalizeInstrumentOrder(settings.songsPrimaryInstrumentOrder).map(i => i.key),
    metadataOrder: settings.songMetadataSortPriority,
  });

  const [filterDraft, setFilterDraft] = useState<AdvancedMissingFilters>(settings.songsAdvancedMissingFilters);
  const [instrumentFilterDraft, setInstrumentFilterDraft] = useState<InstrumentKey | null>(settings.songsSelectedInstrumentFilter);

  useEffect(() => {
    // Keep drafts in sync if settings change externally (e.g., after storage load)
    setSortDraft({
      sortMode: settings.songsSortMode,
      sortAscending: settings.songsSortAscending,
      order: normalizeInstrumentOrder(settings.songsPrimaryInstrumentOrder).map(i => i.key),
      metadataOrder: settings.songMetadataSortPriority,
    });
    setFilterDraft(settings.songsAdvancedMissingFilters);
    setInstrumentFilterDraft(settings.songsSelectedInstrumentFilter);
  }, [
    settings.songsAdvancedMissingFilters,
    settings.songsPrimaryInstrumentOrder,
    settings.songsSortAscending,
    settings.songsSortMode,
    settings.songsSelectedInstrumentFilter,
    settings.songMetadataSortPriority,
  ]);

  const queryNorm = query.trim().toLowerCase();
  const normalizedMetadataKeys = useMemo(() => normalizeMetadataSortPriority(settings.songMetadataSortPriority).map(i => i.key), [settings.songMetadataSortPriority]);
  const filtered = useMemo(() => {
    const orderItems = normalizeInstrumentOrder(settings.songsPrimaryInstrumentOrder);
    return filterAndSortSongs({
      songs,
      scoresIndex,
      filterText: queryNorm,
      advanced: settings.songsAdvancedMissingFilters,
      sortMode: settings.songsSortMode,
      sortAscending: settings.songsSortAscending,
      instrumentOrder: orderItems,
      instrumentFilter: settings.songsSelectedInstrumentFilter,
      metadataSortPriority: normalizedMetadataKeys,
    });
  }, [queryNorm, scoresIndex, settings.songsAdvancedMissingFilters, settings.songsPrimaryInstrumentOrder, settings.songsSortAscending, settings.songsSortMode, settings.songsSelectedInstrumentFilter, normalizedMetadataKeys, songs]);

  // Log song catalog once when it becomes available.
  const loggedCountRef = useRef<number | null>(null);
  useEffect(() => {
    if (songs.length === 0) return;
    if (loggedCountRef.current === songs.length) return;
    loggedCountRef.current = songs.length;
    logUi(`[SONGS] loaded ${songs.length} songs`);
  }, [logUi, songs.length]);

  // Debounce query logging so we don't spam.
  const queryTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  useEffect(() => {
    if (queryTimerRef.current) clearTimeout(queryTimerRef.current);
    queryTimerRef.current = setTimeout(() => {
      logUi(`[SONGS] search '${queryNorm || '(empty)'}' -> ${filtered.length} results`);
    }, 350);

    return () => {
      if (queryTimerRef.current) clearTimeout(queryTimerRef.current);
      queryTimerRef.current = null;
    };
  }, [filtered.length, logUi, queryNorm]);

  const onOpen = useCallback((songId: string, title: string) => {
    logUi(`[SONGS] open ${songId} '${title}'`);
    onOpenSong?.(songId, title);
  }, [logUi, onOpenSong]);

  const renderItem = useCallback(({item}: {item: Song}) => {
    const id = item.track.su;
    const leaderboardData = scoresIndex[id];
    return (
      <SongRow
        song={item}
        leaderboardData={leaderboardData}
        settings={instrumentQuerySettings}
        useCompactLayout={useCompactLayout}
        inlineInstruments={isTabletOrFoldable}
        hideInstrumentIcons={settings.songsHideInstrumentIcons}
        selectedInstrumentFilter={settings.songsSelectedInstrumentFilter}
        metadataDisplayOrder={normalizedMetadataKeys}
        onOpen={onOpen}
      />
    );
  }, [instrumentQuerySettings, isTabletOrFoldable, normalizedMetadataKeys, onOpen, scoresIndex, settings.songsHideInstrumentIcons, settings.songsSelectedInstrumentFilter, useCompactLayout]);

  const sortLabel = useMemo(() => {
    switch (settings.songsSortMode) {
      case 'title':
        return 'Title';
      case 'artist':
        return 'Artist';
      case 'hasfc':
        return 'Has FC';
      case 'isfc':
        return 'Is FC';
      case 'score':
        return 'Score';
      case 'percentage':
        return 'Percentage';
      case 'stars':
        return 'Stars';
      case 'seasonachieved':
        return 'Season';
      case 'percentile':
        return 'Percentile';
      default:
        return String(settings.songsSortMode);
    }
  }, [settings.songsSortMode]);

  const dirLabel = settings.songsSortAscending ? 'Ascending' : 'Descending';

  const isFilterActive = useMemo(() => {
    const f = settings.songsAdvancedMissingFilters;
    return (
      f.missingPadFCs ||
      f.missingProFCs ||
      f.missingPadScores ||
      f.missingProScores ||
      !f.includeLead ||
      !f.includeBass ||
      !f.includeDrums ||
      !f.includeVocals ||
      !f.includeProGuitar ||
      !f.includeProBass ||
      settings.songsSelectedInstrumentFilter != null
    );
  }, [settings.songsAdvancedMissingFilters, settings.songsSelectedInstrumentFilter]);

  const filterLabel = useMemo(() => {
    const f = settings.songsAdvancedMissingFilters;
    const parts: string[] = [];
    if (f.missingPadFCs) parts.push('missing pad FCs');
    if (f.missingProFCs) parts.push('missing pro FCs');
    if (f.missingPadScores) parts.push('missing pad scores');
    if (f.missingProScores) parts.push('missing pro scores');

    const instruments: string[] = [];
    if (!f.includeLead) instruments.push('lead');
    if (!f.includeBass) instruments.push('bass');
    if (!f.includeDrums) instruments.push('drums');
    if (!f.includeVocals) instruments.push('vocals');
    if (!f.includeProGuitar) instruments.push('pro lead');
    if (!f.includeProBass) instruments.push('pro bass');

    if (parts.length === 0 && instruments.length === 0) return 'No filters applied';
    if (instruments.length > 0) parts.push(`excluding ${instruments.join(', ')}`);
    if (settings.songsSelectedInstrumentFilter) parts.push(`instrument: ${settings.songsSelectedInstrumentFilter}`);
    return parts.join('; ');
  }, [settings.songsAdvancedMissingFilters, settings.songsSelectedInstrumentFilter]);

  const defaultOrder = defaultPrimaryInstrumentOrder().map(i => i.key);
  const currentOrder = normalizeInstrumentOrder(settings.songsPrimaryInstrumentOrder).map(i => i.key);
  const isOrderChanged = defaultOrder.length !== currentOrder.length || defaultOrder.some((k, i) => k !== currentOrder[i]);
  const defaultMeta = defaultMetadataSortPriority().map(i => i.key);
  const isMetaOrderChanged = defaultMeta.length !== normalizedMetadataKeys.length || defaultMeta.some((k, i) => k !== normalizedMetadataKeys[i]);
  const isSortActive = settings.songsSortMode !== 'title' || settings.songsSortAscending !== true || isOrderChanged || isMetaOrderChanged;
  const sortIconColor = isSortActive ? '#2D82E6' : '#D7DEE8';
  const filterIconColor = isFilterActive ? '#2D82E6' : '#D7DEE8';

  return (
    <Screen>
      <View style={styles.content}>
        <PageHeader title="Songs" />

        <View style={styles.controls}>
          <FrostedSurface style={styles.searchSurface} tint="dark" intensity={18}>
            <TextInput
              value={query}
              onChangeText={setQuery}
              placeholder="Search title / artist"
              placeholderTextColor="#FFFFFF"
              autoCapitalize="none"
              autoCorrect={false}
              style={styles.searchInput}
              returnKeyType="done"
            />
          </FrostedSurface>

          <Pressable
            onPress={() => {
              setSortDraft({
                sortMode: settings.songsSortMode,
                sortAscending: settings.songsSortAscending,
                order: normalizeInstrumentOrder(settings.songsPrimaryInstrumentOrder).map(i => i.key),
                metadataOrder: settings.songMetadataSortPriority,
              });
              setShowSort(true);
            }}
            style={({pressed}) => [styles.iconBtnBare, pressed && styles.smallBtnPressed]}
            accessibilityRole="button"
            accessibilityLabel={`Open sort options. Current: ${sortLabel} ${dirLabel}`}
          >
            <Ionicons name="swap-vertical-sharp" size={20} color={sortIconColor} />
          </Pressable>

          <Pressable
            onPress={() => {
              setFilterDraft(settings.songsAdvancedMissingFilters);
              setInstrumentFilterDraft(settings.songsSelectedInstrumentFilter);
              setShowFilter(true);
            }}
            style={({pressed}) => [styles.iconBtnBare, pressed && styles.smallBtnPressed]}
            accessibilityRole="button"
            accessibilityLabel={`Open filter options. ${filterLabel}`}
          >
            <Ionicons name="funnel" size={18} color={filterIconColor} />
          </Pressable>
        </View>

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
            data={filtered}
            keyExtractor={s => s.track.su}
            renderItem={renderItem}
            keyboardShouldPersistTaps="handled"
            style={listStyle}
            contentContainerStyle={[listContentStyle, filtered.length === 0 && styles.listEmptyGrow]}
            scrollIndicatorInsets={scrollInsets}
            showsVerticalScrollIndicator={false}
            showsHorizontalScrollIndicator={false}
            removeClippedSubviews={Platform.OS === 'android'}
            initialNumToRender={12}
            maxToRenderPerBatch={8}
            updateCellsBatchingPeriod={24}
            windowSize={7}
            getItemLayout={useCompactLayout ? undefined : (_data, index) => ({length: ROW_HEIGHT, offset: ROW_HEIGHT * index, index})}
            ListEmptyComponent={
              <CenteredEmptyStateCard
                title={songs.length === 0 ? 'No songs yet' : 'No results'}
                body={songs.length === 0 ? 'Songs not loaded yet. Check Settings.' : 'No songs match your search.'}
              />
            }
          />
        </MaskedView>

        <SortModal
          visible={showSort}
          draft={sortDraft}
          showInstruments={instrumentQuerySettings}
          instrumentFilter={settings.songsSelectedInstrumentFilter}
          onChange={setSortDraft}
          onCancel={() => {
            setSortDraft({
              sortMode: settings.songsSortMode,
              sortAscending: settings.songsSortAscending,
              order: normalizeInstrumentOrder(settings.songsPrimaryInstrumentOrder).map(i => i.key),
              metadataOrder: settings.songMetadataSortPriority,
            });
            setShowSort(false);
          }}
          onReset={() => {
            const defaults = {
              sortMode: 'title' as SongSortMode,
              sortAscending: true,
              order: defaultPrimaryInstrumentOrder().map(i => i.key),
              metadataOrder: defaultMetadataSortPriority().map(i => i.key),
            };
            setSortDraft(defaults);
            setShowSort(false);
            logUi('[SONGS] reset sort to defaults');
            setSettings({
              ...settings,
              songsSortMode: defaults.sortMode,
              songsSortAscending: defaults.sortAscending,
              songsPrimaryInstrumentOrder: defaults.order,
              songMetadataSortPriority: defaults.metadataOrder,
            });
          }}
          onApply={() => {
            setShowSort(false);
            logUi(`[SONGS] apply sort mode=${sortDraft.sortMode} asc=${sortDraft.sortAscending} order=${sortDraft.order.join(',')} meta=${sortDraft.metadataOrder.join(',')}`);
            const next = {
              ...settings,
              songsSortMode: sortDraft.sortMode,
              songsSortAscending: sortDraft.sortAscending,
              songsPrimaryInstrumentOrder: sortDraft.order,
              songMetadataSortPriority: sortDraft.metadataOrder,
            };
            setSettings(next);
          }}
        />

        <FilterModal
          visible={showFilter}
          draft={filterDraft}
          onChange={setFilterDraft}
          hideProFilters={!settings.showProLead && !settings.showProBass}
          showInstruments={instrumentQuerySettings}
          onShowInstrumentToggle={(key) => {
            setSettings({...settings, [key]: !settings[key]});
          }}
          onCancel={() => setShowFilter(false)}
          onReset={() => {
            const defaults = defaultAdvancedMissingFilters();
            setFilterDraft(defaults);
            setInstrumentFilterDraft(null);
            setShowFilter(false);
            logUi('[SONGS] reset filters to defaults');
            setSettings({...settings, songsAdvancedMissingFilters: defaults, songsSelectedInstrumentFilter: null});
          }}
          onApply={() => {
            setShowFilter(false);
            logUi(`[SONGS] apply advanced filters`);
            setSettings({...settings, songsAdvancedMissingFilters: filterDraft, songsSelectedInstrumentFilter: instrumentFilterDraft});
          }}
          selectedInstrumentFilter={instrumentFilterDraft}
          onSelectedInstrumentFilterChange={setInstrumentFilterDraft}
        />
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
    gap: 10,
    position: 'relative',
  },
  body: {
    color: '#D7DEE8',
    fontSize: 14,
    lineHeight: 20,
  },
  controls: {
    flexDirection: 'row',
    gap: 10,
    alignItems: 'center',
  },
  smallBtn: {
    borderWidth: 1,
    borderColor: '#2B3B55',
    backgroundColor: '#0B1220',
    borderRadius: 10,
    paddingHorizontal: 10,
    paddingVertical: Platform.OS === 'ios' ? 10 : 8,
  },
  smallBtnPressed: {
    opacity: 0.85,
  },
  smallBtnText: {
    color: '#FFFFFF',
    fontSize: 12,
    fontWeight: '700',
  },
  iconBtnBare: {
    paddingHorizontal: 8,
    paddingVertical: Platform.OS === 'ios' ? 10 : 8,
    alignItems: 'center',
    justifyContent: 'center',
  },
  searchSurface: {
    flex: 1,
    borderRadius: 10,
    borderColor: '#2B3B55',
  },
  searchInput: {
    flex: 1,
    paddingHorizontal: 12,
    paddingVertical: Platform.OS === 'ios' ? 10 : 8,
    color: '#FFFFFF',
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
  list: {
    paddingTop: 32,
    paddingBottom: 4,
  },
  listEmptyGrow: {
    flexGrow: 1,
  },
});
