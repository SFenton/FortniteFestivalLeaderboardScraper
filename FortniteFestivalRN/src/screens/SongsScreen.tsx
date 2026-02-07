import React, {useCallback, useEffect, useMemo, useRef, useState} from 'react';
import {FlatList, Image, Platform, Pressable, StyleSheet, Text, TextInput, useWindowDimensions, View} from 'react-native';
import Ionicons from 'react-native-vector-icons/Ionicons';
import {useOptionalBottomTabBarHeight} from '../navigation/useOptionalBottomTabBarHeight';

import { Screen } from '../ui/Screen';
import {usePageInstrumentation} from '../app/instrumentation/usePageInstrumentation';
import {useFestival} from '../app/festival/FestivalContext';
import type {LeaderboardData, Song} from '../core/models';
import {buildSongDisplayRow, defaultAdvancedMissingFilters, defaultPrimaryInstrumentOrder, filterAndSortSongs, normalizeInstrumentOrder, type InstrumentQuerySettings} from '../app/songs/songFiltering';
import {getInstrumentIconSource, getInstrumentStatusVisual} from '../ui/instruments/instrumentVisuals';
import {PlatformModal} from '../ui/PlatformModal';
import {FrostedSurface} from '../ui/FrostedSurface';
import {CenteredEmptyStateCard} from '../ui/CenteredEmptyStateCard';
import {PageHeader} from '../ui/PageHeader';
import type {AdvancedMissingFilters, SongSortMode} from '../core/songListConfig';
import type {InstrumentKey} from '../core/instruments';

const SongRow = React.memo(function SongRow(props: {
  song: Song;
  leaderboardData?: LeaderboardData;
  settings: InstrumentQuerySettings;
  useCompactLayout: boolean;
  hideInstrumentIcons: boolean;
  onOpen: (songId: string, title: string) => void;
}) {
  const {song, leaderboardData, settings, onOpen} = props;

  const id = song.track.su;
  const title = song.track.tt ?? song._title ?? id;
  const artist = song.track.an ?? '';
  const year = song.track.ry;
  const imageUri = song.imagePath ?? song.track.au;

  const showInstrumentIcons = !props.hideInstrumentIcons;

  const row = useMemo(() => {
    if (!showInstrumentIcons) return null;
    return buildSongDisplayRow({song, leaderboardData, settings});
  }, [leaderboardData, settings, showInstrumentIcons, song]);

  return (
    <Pressable
      onPress={() => onOpen(id, title)}
      style={styles.rowPressable}
      accessibilityRole="button"
      accessibilityLabel={`Open ${title}`}
    >
      {({pressed}) => (
        <FrostedSurface style={[styles.rowSurface, pressed && styles.rowSurfacePressed]} tint="dark" intensity={12}>
          {props.useCompactLayout ? (
            <View style={styles.rowInnerCompact}>
              <View style={styles.compactTopRow}>
                <View style={styles.thumbWrap}>
                  {imageUri ? (
                    <Image source={{uri: imageUri}} style={styles.thumb} resizeMode="cover" />
                  ) : (
                    <View style={styles.thumbPlaceholder} />
                  )}
                </View>

                <View style={styles.rowText}>
                  <Text numberOfLines={1} style={styles.songTitle}>
                    {title}
                  </Text>
                  <Text numberOfLines={1} style={styles.songMeta}>
                    {artist}
                    {artist && year ? ' • ' : ''}
                    {year ?? ''}
                  </Text>
                </View>
              </View>

              {showInstrumentIcons && row ? (
                <View style={styles.instrumentRowCompact}>
                  {row.instrumentStatuses.map(s => {
                    const {fill, stroke} = getInstrumentStatusVisual({hasScore: s.hasScore, isFullCombo: s.isFullCombo});
                    const opacity = s.isEnabled ? 1 : 0.35;
                    return (
                      <View
                        key={s.instrumentKey}
                        style={[styles.instrumentChipCompact, {backgroundColor: fill, borderColor: stroke, opacity}]}
                      >
                        <Image source={getInstrumentIconSource(s.instrumentKey)} style={styles.instrumentIconCompact} resizeMode="contain" />
                      </View>
                    );
                  })}
                </View>
              ) : null}
            </View>
          ) : (
            <View style={styles.rowInner}>
              <View style={styles.left}>
                <View style={styles.thumbWrap}>
                  {imageUri ? (
                    <Image source={{uri: imageUri}} style={styles.thumb} resizeMode="cover" />
                  ) : (
                    <View style={styles.thumbPlaceholder} />
                  )}
                </View>

                <View style={styles.rowText}>
                  <Text numberOfLines={1} style={styles.songTitle}>
                    {title}
                  </Text>
                  <Text numberOfLines={1} style={styles.songMeta}>
                    {artist}
                    {artist && year ? ' • ' : ''}
                    {year ?? ''}
                  </Text>
                </View>
              </View>

              {!props.hideInstrumentIcons && row ? (
                <View style={styles.instrumentRow}>
                  {row.instrumentStatuses.map(s => {
                    const {fill, stroke} = getInstrumentStatusVisual({hasScore: s.hasScore, isFullCombo: s.isFullCombo});
                    const opacity = s.isEnabled ? 1 : 0.35;
                    return (
                      <View
                        key={s.instrumentKey}
                        style={[styles.instrumentChip, {backgroundColor: fill, borderColor: stroke, opacity}]}
                      >
                        <Image source={getInstrumentIconSource(s.instrumentKey)} style={styles.instrumentIcon} resizeMode="contain" />
                      </View>
                    );
                  })}
                </View>
              ) : null}
            </View>
          )}
        </FrostedSurface>
      )}
    </Pressable>
  );
}, (prev, next) => (
  prev.song === next.song &&
  prev.leaderboardData === next.leaderboardData &&
  prev.settings === next.settings &&
  prev.useCompactLayout === next.useCompactLayout &&
  prev.hideInstrumentIcons === next.hideInstrumentIcons &&
  prev.onOpen === next.onOpen
));

export function SongsScreen(props: {onOpenSong?: (songId: string, title: string) => void}) {
  usePageInstrumentation('Songs');

  const {width} = useWindowDimensions();
  const useCompactLayout = width < 900;

  const {onOpenSong} = props;

  // `Screen` intentionally does not apply bottom safe-area padding (to avoid a
  // persistent dead band above the navbar). Lists need explicit padding so the
  // final rows aren’t hidden behind the tab bar.
  const tabBarHeight = useOptionalBottomTabBarHeight();

  // Fixed-height rows let FlatList skip measurement work.
  // Keep this in sync with styles: rowInner padding + thumb/chip sizes + row margin.
  const ROW_HEIGHT = 72;

  const listStyle = useMemo(() => ({flex: 1, marginBottom: -tabBarHeight}), [tabBarHeight]);
  const listContentStyle = useMemo(() => [styles.list, {paddingBottom: tabBarHeight + 16}], [tabBarHeight]);
  const scrollInsets = useMemo(() => ({bottom: tabBarHeight}), [tabBarHeight]);

  const {
    state: {songs, scoresIndex, settings},
    actions: {logUi, setSettings},
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

  const [query, setQuery] = useState('');
  const [showSort, setShowSort] = useState(false);
  const [showFilter, setShowFilter] = useState(false);

  const [sortDraft, setSortDraft] = useState<{sortMode: SongSortMode; sortAscending: boolean; order: InstrumentKey[]}>({
    sortMode: settings.songsSortMode,
    sortAscending: settings.songsSortAscending,
    order: normalizeInstrumentOrder(settings.songsPrimaryInstrumentOrder).map(i => i.key),
  });

  const [filterDraft, setFilterDraft] = useState<AdvancedMissingFilters>(settings.songsAdvancedMissingFilters);

  useEffect(() => {
    // Keep drafts in sync if settings change externally (e.g., after storage load)
    setSortDraft({
      sortMode: settings.songsSortMode,
      sortAscending: settings.songsSortAscending,
      order: normalizeInstrumentOrder(settings.songsPrimaryInstrumentOrder).map(i => i.key),
    });
    setFilterDraft(settings.songsAdvancedMissingFilters);
  }, [
    settings.songsAdvancedMissingFilters,
    settings.songsPrimaryInstrumentOrder,
    settings.songsSortAscending,
    settings.songsSortMode,
  ]);

  const queryNorm = query.trim().toLowerCase();
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
    });
  }, [queryNorm, scoresIndex, settings.songsAdvancedMissingFilters, settings.songsPrimaryInstrumentOrder, settings.songsSortAscending, settings.songsSortMode, songs]);

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
        hideInstrumentIcons={settings.songsHideInstrumentIcons}
        onOpen={onOpen}
      />
    );
  }, [instrumentQuerySettings, onOpen, scoresIndex, settings.songsHideInstrumentIcons, useCompactLayout]);

  const sortLabel = useMemo(() => {
    switch (settings.songsSortMode) {
      case 'title':
        return 'Title';
      case 'artist':
        return 'Artist';
      case 'hasfc':
        return 'Has FC';
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
      !f.includeProBass
    );
  }, [settings.songsAdvancedMissingFilters]);

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
    if (!f.includeProGuitar) instruments.push('pro guitar');
    if (!f.includeProBass) instruments.push('pro bass');

    if (parts.length === 0 && instruments.length === 0) return 'No filters applied';
    if (instruments.length > 0) parts.push(`excluding ${instruments.join(', ')}`);
    return parts.join('; ');
  }, [settings.songsAdvancedMissingFilters]);

  const isSortActive = settings.songsSortMode !== 'title' || settings.songsSortAscending !== true;
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
              returnKeyType="search"
            />
          </FrostedSurface>

          <Pressable
            onPress={() => {
              setSortDraft({
                sortMode: settings.songsSortMode,
                sortAscending: settings.songsSortAscending,
                order: normalizeInstrumentOrder(settings.songsPrimaryInstrumentOrder).map(i => i.key),
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
              setShowFilter(true);
            }}
            style={({pressed}) => [styles.iconBtnBare, pressed && styles.smallBtnPressed]}
            accessibilityRole="button"
            accessibilityLabel={`Open filter options. ${filterLabel}`}
          >
            <Ionicons name="funnel" size={18} color={filterIconColor} />
          </Pressable>
        </View>

        <FlatList
          data={filtered}
          keyExtractor={s => s.track.su}
          renderItem={renderItem}
          keyboardShouldPersistTaps="handled"
          style={listStyle}
          contentContainerStyle={[listContentStyle, filtered.length === 0 && styles.listEmptyGrow]}
          scrollIndicatorInsets={scrollInsets}
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

        <SortModal
          visible={showSort}
          draft={sortDraft}
          onChange={setSortDraft}
          onCancel={() => setShowSort(false)}
          onReset={() => {
            setSortDraft({
              sortMode: 'title',
              sortAscending: true,
              order: defaultPrimaryInstrumentOrder().map(i => i.key),
            });
          }}
          onApply={() => {
            setShowSort(false);
            logUi(`[SONGS] apply sort mode=${sortDraft.sortMode} asc=${sortDraft.sortAscending} order=${sortDraft.order.join(',')}`);
            const next = {
              ...settings,
              songsSortMode: sortDraft.sortMode,
              songsSortAscending: sortDraft.sortAscending,
              songsPrimaryInstrumentOrder: sortDraft.order,
            };
            setSettings(next);
          }}
        />

        <FilterModal
          visible={showFilter}
          draft={filterDraft}
          onChange={setFilterDraft}
          onCancel={() => setShowFilter(false)}
          onReset={() => setFilterDraft(defaultAdvancedMissingFilters())}
          onApply={() => {
            setShowFilter(false);
            logUi(`[SONGS] apply advanced filters`);
            setSettings({...settings, songsAdvancedMissingFilters: filterDraft});
          }}
        />
      </View>
    </Screen>
  );
}

function SortModal(props: {
  visible: boolean;
  draft: {sortMode: SongSortMode; sortAscending: boolean; order: InstrumentKey[]};
  onChange: (d: {sortMode: SongSortMode; sortAscending: boolean; order: InstrumentKey[]}) => void;
  onCancel: () => void;
  onReset: () => void;
  onApply: () => void;
}) {
  const orderItems = useMemo(() => normalizeInstrumentOrder(props.draft.order), [props.draft.order]);
  const normalizedKeys = useMemo(() => orderItems.map(i => i.key), [orderItems]);
  const variant = Platform.OS === 'windows' ? 'center' : 'bottom';

  return (
    <PlatformModal visible={props.visible} onRequestClose={props.onCancel} variant={variant}>
      <FrostedSurface style={styles.modalCard} tint="dark" intensity={18}>
          <View style={styles.modalHeader}>
            <Text style={styles.modalTitle}>Sort Songs</Text>
            <Pressable onPress={props.onCancel} style={({pressed}) => [styles.modalClose, pressed && styles.smallBtnPressed]}>
              <Text style={styles.modalCloseText}>Cancel</Text>
            </Pressable>
          </View>

          <View style={styles.modalSection}>
            <Text style={styles.modalSectionTitle}>Mode</Text>
            <View style={styles.choiceRow}>
              <Choice
                label="Title"
                selected={props.draft.sortMode === 'title'}
                onPress={() => props.onChange({...props.draft, sortMode: 'title'})}
              />
              <Choice
                label="Artist"
                selected={props.draft.sortMode === 'artist'}
                onPress={() => props.onChange({...props.draft, sortMode: 'artist'})}
              />
              <Choice
                label="Has FC"
                selected={props.draft.sortMode === 'hasfc'}
                onPress={() => props.onChange({...props.draft, sortMode: 'hasfc'})}
              />
            </View>
          </View>

          <View style={styles.modalSection}>
            <Text style={styles.modalSectionTitle}>Direction</Text>
            <Pressable
              onPress={() => props.onChange({...props.draft, sortAscending: !props.draft.sortAscending})}
              style={({pressed}) => [styles.rowBtn, pressed && styles.rowBtnPressed]}
            >
              <Text style={styles.rowBtnText}>{props.draft.sortAscending ? 'Ascending ▲' : 'Descending ▼'}</Text>
            </Pressable>
          </View>

          <View style={styles.modalSection}>
            <Text style={styles.modalSectionTitle}>Primary Instrument Order</Text>
            <Text style={styles.modalHint}>
              Sorting by Has FC uses contiguous FCs from the top of this list.
            </Text>

            {orderItems.map((it, idx) => (
              <View key={it.key} style={styles.orderRow}>
                <Text style={styles.orderName}>{it.displayName}</Text>
                <View style={styles.orderBtns}>
                  <Pressable
                    onPress={() => {
                      if (idx <= 0) return;
                      const next = [...normalizedKeys];
                      const tmp = next[idx - 1];
                      next[idx - 1] = next[idx];
                      next[idx] = tmp;
                      props.onChange({...props.draft, order: next});
                    }}
                    style={({pressed}) => [styles.orderBtn, pressed && styles.smallBtnPressed]}
                  >
                    <Text style={styles.orderBtnText}>↑</Text>
                  </Pressable>
                  <Pressable
                    onPress={() => {
                      if (idx >= normalizedKeys.length - 1) return;
                      const next = [...normalizedKeys];
                      const tmp = next[idx + 1];
                      next[idx + 1] = next[idx];
                      next[idx] = tmp;
                      props.onChange({...props.draft, order: next});
                    }}
                    style={({pressed}) => [styles.orderBtn, pressed && styles.smallBtnPressed]}
                  >
                    <Text style={styles.orderBtnText}>↓</Text>
                  </Pressable>
                </View>
              </View>
            ))}
          </View>

          <View style={styles.modalFooter}>
            <Pressable onPress={props.onReset} style={({pressed}) => [styles.modalDangerBtn, pressed && styles.smallBtnPressed]}>
              <Text style={styles.modalBtnText}>Reset</Text>
            </Pressable>
            <Pressable onPress={props.onApply} style={({pressed}) => [styles.modalPrimaryBtn, pressed && styles.smallBtnPressed]}>
              <Text style={styles.modalBtnText}>Apply</Text>
            </Pressable>
          </View>
      </FrostedSurface>
    </PlatformModal>
  );
}

function FilterModal(props: {
  visible: boolean;
  draft: AdvancedMissingFilters;
  onChange: (d: AdvancedMissingFilters) => void;
  onCancel: () => void;
  onReset: () => void;
  onApply: () => void;
}) {
  const t = (k: keyof AdvancedMissingFilters) =>
    props.onChange({...props.draft, [k]: !props.draft[k]});

  const variant = Platform.OS === 'windows' ? 'center' : 'bottom';

  return (
    <PlatformModal visible={props.visible} onRequestClose={props.onCancel} variant={variant}>
      <FrostedSurface style={styles.modalCard} tint="dark" intensity={18}>
          <View style={styles.modalHeader}>
            <Text style={styles.modalTitle}>Filter Songs</Text>
            <Pressable onPress={props.onCancel} style={({pressed}) => [styles.modalClose, pressed && styles.smallBtnPressed]}>
              <Text style={styles.modalCloseText}>Cancel</Text>
            </Pressable>
          </View>

          <View style={styles.modalSection}>
            <Text style={styles.modalSectionTitle}>Missing</Text>
            <ToggleRow label="Pad Scores" checked={props.draft.missingPadScores} onPress={() => t('missingPadScores')} />
            <ToggleRow label="Pad FCs" checked={props.draft.missingPadFCs} onPress={() => t('missingPadFCs')} />
            <ToggleRow label="Pro Scores" checked={props.draft.missingProScores} onPress={() => t('missingProScores')} />
            <ToggleRow label="Pro FCs" checked={props.draft.missingProFCs} onPress={() => t('missingProFCs')} />
          </View>

          <View style={styles.modalSection}>
            <Text style={styles.modalSectionTitle}>Include Instruments</Text>
            <ToggleRow label="Lead" checked={props.draft.includeLead} onPress={() => t('includeLead')} />
            <ToggleRow label="Bass" checked={props.draft.includeBass} onPress={() => t('includeBass')} />
            <ToggleRow label="Drums" checked={props.draft.includeDrums} onPress={() => t('includeDrums')} />
            <ToggleRow label="Vocals" checked={props.draft.includeVocals} onPress={() => t('includeVocals')} />
            <ToggleRow label="Pro Guitar" checked={props.draft.includeProGuitar} onPress={() => t('includeProGuitar')} />
            <ToggleRow label="Pro Bass" checked={props.draft.includeProBass} onPress={() => t('includeProBass')} />
          </View>

          <View style={styles.modalFooter}>
            <Pressable onPress={props.onReset} style={({pressed}) => [styles.modalDangerBtn, pressed && styles.smallBtnPressed]}>
              <Text style={styles.modalBtnText}>Reset</Text>
            </Pressable>
            <Pressable onPress={props.onApply} style={({pressed}) => [styles.modalPrimaryBtn, pressed && styles.smallBtnPressed]}>
              <Text style={styles.modalBtnText}>Apply</Text>
            </Pressable>
          </View>
      </FrostedSurface>
    </PlatformModal>
  );
}

function Choice(props: {label: string; selected: boolean; onPress: () => void}) {
  return (
    <Pressable onPress={props.onPress} style={({pressed}) => [styles.choice, props.selected && styles.choiceSelected, pressed && styles.rowBtnPressed]}>
      <Text style={[styles.choiceText, props.selected && styles.choiceTextSelected]}>{props.label}</Text>
    </Pressable>
  );
}

function ToggleRow(props: {label: string; checked: boolean; onPress: () => void}) {
  return (
    <Pressable onPress={props.onPress} style={({pressed}) => [styles.toggleRow, pressed && styles.rowBtnPressed]} accessibilityRole="button">
      <Text style={styles.toggleLabel}>{props.label}</Text>
      <Text style={[styles.toggleValue, props.checked && styles.toggleValueOn]}>{props.checked ? 'On' : 'Off'}</Text>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  content: {
    flex: 1,
    paddingHorizontal: 20,
    paddingVertical: 16,
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
  list: {
    paddingVertical: 4,
  },
  listEmptyGrow: {
    flexGrow: 1,
  },
  modalCard: {
    padding: 14,
    gap: 12,
    width: '100%',
    maxWidth: 640,
    alignSelf: 'center',
  },
  modalHeader: {
    flexDirection: 'row',
    justifyContent: 'space-between',
    alignItems: 'center',
  },
  modalTitle: {
    color: '#FFFFFF',
    fontSize: 18,
    fontWeight: '800',
  },
  modalClose: {
    paddingHorizontal: 10,
    paddingVertical: 8,
    borderRadius: 10,
    borderWidth: 1,
    borderColor: '#2B3B55',
  },
  modalCloseText: {
    color: '#D7DEE8',
    fontSize: 12,
    fontWeight: '700',
  },
  modalSection: {
    gap: 8,
  },
  modalSectionTitle: {
    color: '#FFFFFF',
    fontSize: 14,
    fontWeight: '800',
  },
  modalHint: {
    color: '#D7DEE8',
    opacity: 0.85,
    fontSize: 12,
    lineHeight: 16,
  },
  choiceRow: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: 8,
  },
  choice: {
    paddingHorizontal: 12,
    paddingVertical: 8,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: '#2B3B55',
    backgroundColor: '#0B1220',
  },
  choiceSelected: {
    borderColor: '#2D82E6',
    backgroundColor: 'rgba(45,130,230,0.18)',
  },
  choiceText: {
    color: '#D7DEE8',
    fontSize: 12,
    fontWeight: '700',
  },
  choiceTextSelected: {
    color: '#FFFFFF',
  },
  rowBtn: {
    paddingHorizontal: 12,
    paddingVertical: 10,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: '#2B3B55',
    backgroundColor: '#0B1220',
  },
  rowBtnPressed: {
    opacity: 0.85,
  },
  rowBtnText: {
    color: '#FFFFFF',
    fontSize: 13,
    fontWeight: '700',
  },
  toggleRow: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    paddingVertical: 10,
    paddingHorizontal: 12,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: '#263244',
    backgroundColor: '#0B1220',
  },
  toggleLabel: {
    color: '#FFFFFF',
    fontSize: 13,
    fontWeight: '700',
  },
  toggleValue: {
    color: '#D7DEE8',
    fontSize: 12,
    opacity: 0.85,
    fontWeight: '700',
  },
  toggleValueOn: {
    color: '#2ecc71',
    opacity: 1,
  },
  orderRow: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    paddingVertical: 8,
    paddingHorizontal: 12,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: '#263244',
    backgroundColor: '#0B1220',
  },
  orderName: {
    color: '#FFFFFF',
    fontSize: 13,
    fontWeight: '700',
  },
  orderBtns: {
    flexDirection: 'row',
    gap: 8,
  },
  orderBtn: {
    width: 34,
    height: 34,
    borderRadius: 10,
    borderWidth: 1,
    borderColor: '#2B3B55',
    alignItems: 'center',
    justifyContent: 'center',
  },
  orderBtnText: {
    color: '#FFFFFF',
    fontSize: 16,
    fontWeight: '900',
  },
  modalFooter: {
    flexDirection: 'row',
    gap: 12,
  },
  modalDangerBtn: {
    flex: 1,
    borderRadius: 12,
    paddingVertical: 12,
    backgroundColor: '#C62828',
    alignItems: 'center',
  },
  modalPrimaryBtn: {
    flex: 1,
    borderRadius: 12,
    paddingVertical: 12,
    backgroundColor: '#2D82E6',
    alignItems: 'center',
  },
  modalBtnText: {
    color: '#FFFFFF',
    fontWeight: '800',
  },
  rowPressable: {
    marginBottom: 8,
  },
  rowSurface: {
    borderRadius: 12,
  },
  rowSurfacePressed: {
    opacity: 0.92,
  },
  rowInner: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    paddingHorizontal: 12,
    paddingVertical: 10,
  },
  rowInnerCompact: {
    paddingHorizontal: 12,
    paddingVertical: 10,
    gap: 10,
  },
  compactTopRow: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 10,
    minWidth: 0,
  },
  left: {
    flex: 1,
    flexDirection: 'row',
    alignItems: 'center',
    gap: 10,
    minWidth: 0,
  },
  rowText: {
    flex: 1,
    minWidth: 0,
  },
  instrumentRow: {
    flexDirection: 'row',
    gap: 6,
    alignItems: 'center',
    flexShrink: 0,
    marginLeft: 10,
  },
  instrumentRowCompact: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: 8,
    alignItems: 'center',
    justifyContent: 'center',
    alignSelf: 'center',
  },
  instrumentChip: {
    width: 40,
    height: 40,
    borderRadius: 20,
    borderWidth: 2,
    alignItems: 'center',
    justifyContent: 'center',
  },
  instrumentChipCompact: {
    width: 34,
    height: 34,
    borderRadius: 17,
    borderWidth: 2,
    alignItems: 'center',
    justifyContent: 'center',
  },
  instrumentIcon: {
    width: 32,
    height: 32,
  },
  instrumentIconCompact: {
    width: 24,
    height: 24,
  },
  thumbWrap: {
    width: 44,
    height: 44,
    borderRadius: 10,
    overflow: 'hidden',
    backgroundColor: '#0B1220',
    borderWidth: 1,
    borderColor: '#263244',
  },
  thumb: {
    width: '100%',
    height: '100%',
  },
  thumbPlaceholder: {
    flex: 1,
    backgroundColor: '#0B1220',
  },
  songTitle: {
    color: '#FFFFFF',
    fontSize: 14,
    fontWeight: '700',
  },
  songMeta: {
    color: '#B8C0CC',
    fontSize: 12,
    marginTop: 2,
  },
});
