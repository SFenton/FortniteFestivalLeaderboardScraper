import React, {useCallback, useMemo} from 'react';
import {Alert, Image, Platform, Pressable, ScrollView, StyleSheet, Text, useWindowDimensions, View} from 'react-native';
import Ionicons from 'react-native-vector-icons/Ionicons';
import DraggableFlatList, {type RenderItemParams} from 'react-native-draggable-flatlist';
import {useSafeAreaInsets} from 'react-native-safe-area-context';

import {PlatformModal} from './PlatformModal';
import {FrostedSurface} from '../FrostedSurface';
import {normalizeInstrumentOrder, normalizeMetadataSortPriority} from '../../app/songs/songFiltering';
import type {InstrumentOrderItem, InstrumentShowSettings, MetadataSortItem, MetadataSortKey, SongSortMode} from '../../core/songListConfig';
import {isInstrumentVisible} from '../../core/songListConfig';
import type {InstrumentKey} from '../../core/instruments';
import {modalStyles as styles} from './modalStyles';
import {getInstrumentIconSource} from '../instruments/instrumentVisuals';

/** Controls which instrument-specific sort modes are visible in the FISM section. */
export type MetadataVisibility = {
  score: boolean;
  percentage: boolean;
  percentile: boolean;
  seasonachieved: boolean;
  isfc: boolean;
  stars: boolean;
};

export function SortModal(props: {
  visible: boolean;
  draft: {sortMode: SongSortMode; sortAscending: boolean; order: InstrumentKey[]; metadataOrder: MetadataSortKey[]};
  showInstruments: InstrumentShowSettings;
  instrumentFilter: InstrumentKey | null;
  metadataVisibility?: MetadataVisibility;
  onChange: (d: {sortMode: SongSortMode; sortAscending: boolean; order: InstrumentKey[]; metadataOrder: MetadataSortKey[]}) => void;
  onCancel: () => void;
  onReset: () => void;
  onApply: () => void;
}) {
  const orderItems = useMemo(() => normalizeInstrumentOrder(props.draft.order), [props.draft.order]);
  const metadataItems = useMemo(() => normalizeMetadataSortPriority(props.draft.metadataOrder), [props.draft.metadataOrder]);
  const metadataKeys = useMemo(() => metadataItems.map(i => i.key), [metadataItems]);

  // Filter metadata items to only those enabled in metadata visibility settings.
  const mv = props.metadataVisibility;
  const visibleMetadataItems = useMemo(() => {
    if (!mv) return metadataItems;
    const visMap: Record<string, boolean> = {score: mv.score, percentage: mv.percentage, percentile: mv.percentile, seasonachieved: mv.seasonachieved, isfc: mv.isfc, stars: mv.stars};
    return metadataItems.filter(i => visMap[i.key] !== false);
  }, [metadataItems, mv]);
  const visibleMetadataKeys = useMemo(() => visibleMetadataItems.map(i => i.key), [visibleMetadataItems]);

  /** Set an instrument-specific sort mode (metadata priority order is user-controlled). */
  const selectInstrumentSortMode = useCallback((mode: MetadataSortKey) => {
    props.onChange({...props.draft, sortMode: mode});
  }, [props]);

  // Split into visible and hidden based on show-instrument settings
  const visibleItems = useMemo(() => orderItems.filter(i => isInstrumentVisible(i.key, props.showInstruments)), [orderItems, props.showInstruments]);
  const hiddenKeys = useMemo(() => orderItems.filter(i => !isInstrumentVisible(i.key, props.showInstruments)).map(i => i.key), [orderItems, props.showInstruments]);
  const visibleKeys = useMemo(() => visibleItems.map(i => i.key), [visibleItems]);
  const variant = Platform.OS === 'windows' ? 'center' : 'bottom';
  const {height: screenHeight} = useWindowDimensions();
  const {bottom: safeBottom} = useSafeAreaInsets();
  const isMobile = Platform.OS !== 'windows';

  return (
    <PlatformModal visible={props.visible} onRequestClose={props.onCancel} variant={variant} fullWidth={isMobile}>
      <FrostedSurface style={[styles.modalCard, isMobile && styles.modalCardMobile, isMobile && {height: screenHeight * 0.8}]} tint="dark" intensity={18}>
          {/* Pinned header */}
          <View style={[styles.modalHeader, isMobile && styles.modalHeaderPinned]}>
            <Text style={styles.modalTitle}>Sort Songs</Text>
            <Pressable onPress={props.onCancel} style={({pressed}) => [pressed && styles.smallBtnPressed]}>
              <FrostedSurface style={styles.modalClose} tint="dark" intensity={12}>
                <Text style={styles.modalCloseText}>Cancel</Text>
              </FrostedSurface>
            </Pressable>
          </View>

          {/* Scrollable content */}
          <ScrollView style={isMobile ? styles.modalScrollContent : undefined} contentContainerStyle={isMobile ? styles.modalScrollInner : undefined} showsVerticalScrollIndicator={false}>
          <View style={styles.modalSection}>
            <Text style={styles.modalSectionTitle}>Mode</Text>
            <Text style={styles.modalHint}>Choose which property to sort the song list by.</Text>
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
                label="Year"
                selected={props.draft.sortMode === 'year'}
                onPress={() => props.onChange({...props.draft, sortMode: 'year'})}
              />
              <Choice
                label="Has FC"
                selected={props.draft.sortMode === 'hasfc'}
                onPress={() => props.onChange({...props.draft, sortMode: 'hasfc'})}
              />
            </View>
          </View>

          {props.instrumentFilter != null && (() => {
            const mv = props.metadataVisibility;
            const fismChoices: {label: string; mode: MetadataSortKey}[] = [
              ...(mv?.score !== false ? [{label: 'Score', mode: 'score' as MetadataSortKey}] : []),
              ...(mv?.percentage !== false ? [{label: 'Percentage', mode: 'percentage' as MetadataSortKey}] : []),
              ...(mv?.percentile !== false ? [{label: 'Percentile', mode: 'percentile' as MetadataSortKey}] : []),
              ...(mv?.isfc !== false ? [{label: 'Is FC', mode: 'isfc' as MetadataSortKey}] : []),
              ...(mv?.stars !== false ? [{label: 'Stars', mode: 'stars' as MetadataSortKey}] : []),
              ...(mv?.seasonachieved !== false ? [{label: 'Season', mode: 'seasonachieved' as MetadataSortKey}] : []),
            ];
            if (fismChoices.length === 0) return null;
            // Chunk into rows of 3, padding incomplete rows with invisible spacers
            const rows: ({label: string; mode: MetadataSortKey} | null)[][] = [];
            for (let i = 0; i < fismChoices.length; i += 3) {
              const row = fismChoices.slice(i, i + 3);
              while (row.length < 3) row.push(null as any);
              rows.push(row);
            }
            return (
              <View style={styles.modalSection}>
                <Text style={styles.modalSectionTitle}>Filtered Instrument Sort Mode</Text>
                <Text style={styles.modalHint}>Filtering to a single instrument enables more sort options. You can select an option here, or still use the options above.</Text>
                {rows.map((row, ri) => (
                  <View key={ri} style={[styles.choiceRow, ri > 0 && {marginTop: 8}]}>
                    {row.map((c, ci) => c ? (
                      <Choice
                        key={c.mode}
                        label={c.label}
                        selected={props.draft.sortMode === c.mode}
                        onPress={() => selectInstrumentSortMode(c.mode)}
                      />
                    ) : (
                      <View key={`spacer-${ci}`} style={{flex: 1}} />
                    ))}
                  </View>
                ))}
              </View>
            );
          })()}

          <View style={styles.modalSection}>
            <Text style={styles.modalSectionTitle}>Direction</Text>
            <Text style={styles.modalHint}>Choose whether to sort ascending (A–Z, low–high) or descending (Z–A, high–low).</Text>
            <View style={styles.choiceRow}>
              <Choice
                label="Ascending"
                selected={props.draft.sortAscending}
                onPress={() => props.onChange({...props.draft, sortAscending: true})}
              />
              <Choice
                label="Descending"
                selected={!props.draft.sortAscending}
                onPress={() => props.onChange({...props.draft, sortAscending: false})}
              />
            </View>
          </View>

          {props.instrumentFilter != null && (() => {
            const mv = props.metadataVisibility;
            const anyMetadataVisible = mv ? (mv.score || mv.percentage || mv.percentile || mv.isfc || mv.stars || mv.seasonachieved) : true;
            if (!anyMetadataVisible) return null;
            return (
          <View style={styles.modalSection}>
            <Text style={styles.modalSectionTitle}>Metadata Sort Priority</Text>
            <Text style={styles.modalHint}>
              {Platform.OS === 'windows'
                ? 'When two songs tie on the selected sort mode, ties are broken by these properties in order from top to bottom (skipping the active sort).'
                : 'Drag to reorder. When two songs tie on the selected sort mode, ties are broken by these properties in order from top to bottom (skipping the active sort).'}
            </Text>

            {Platform.OS === 'windows' ? (
              <FrostedSurface style={styles.orderList} tint="dark" intensity={12}>
              {visibleMetadataItems.map((it, idx) => (
                <View key={it.key} style={[styles.orderRow, idx === 0 && styles.orderRowFirst, idx === visibleMetadataItems.length - 1 && styles.orderRowLast, idx > 0 && styles.orderRowSeparator]}>
                  <Text style={styles.orderName}>{it.displayName}</Text>
                  <View style={styles.orderBtns}>
                    <Pressable
                      onPress={() => {
                        if (idx <= 0) return;
                        const next = [...visibleMetadataKeys];
                        const tmp = next[idx - 1];
                        next[idx - 1] = next[idx];
                        next[idx] = tmp;
                        // Merge reordered visible keys with hidden keys preserving hidden positions
                        const hiddenMeta = metadataKeys.filter(k => !new Set(next).has(k));
                        props.onChange({...props.draft, metadataOrder: [...next, ...hiddenMeta]});
                      }}
                      style={({pressed}) => [styles.orderBtn, pressed && styles.smallBtnPressed]}
                    >
                      <Text style={styles.orderBtnText}>↑</Text>
                    </Pressable>
                    <Pressable
                      onPress={() => {
                        if (idx >= visibleMetadataKeys.length - 1) return;
                        const next = [...visibleMetadataKeys];
                        const tmp = next[idx + 1];
                        next[idx + 1] = next[idx];
                        next[idx] = tmp;
                        const hiddenMeta = metadataKeys.filter(k => !new Set(next).has(k));
                        props.onChange({...props.draft, metadataOrder: [...next, ...hiddenMeta]});
                      }}
                      style={({pressed}) => [styles.orderBtn, pressed && styles.smallBtnPressed]}
                    >
                      <Text style={styles.orderBtnText}>↓</Text>
                    </Pressable>
                  </View>
                </View>
              ))}
              </FrostedSurface>
            ) : (
              <FrostedSurface style={styles.orderList} tint="dark" intensity={12}>
                <DraggableFlatList<MetadataSortItem>
                  data={visibleMetadataItems}
                  keyExtractor={(item) => item.key}
                  scrollEnabled={false}
                  onDragEnd={({data}) => {
                    const reorderedVisible = data.map(i => i.key);
                    const hiddenMeta = metadataKeys.filter(k => !new Set(reorderedVisible).has(k));
                    props.onChange({...props.draft, metadataOrder: [...reorderedVisible, ...hiddenMeta]});
                  }}
                  renderItem={({item, drag, isActive, getIndex}: RenderItemParams<MetadataSortItem>) => {
                    const idx = getIndex() ?? 0;
                    return (
                      <Pressable
                        onLongPress={drag}
                        delayLongPress={100}
                        disabled={isActive}
                        style={[styles.orderRow, idx === 0 && styles.orderRowFirst, idx === visibleMetadataItems.length - 1 && styles.orderRowLast, idx > 0 && styles.orderRowSeparator, isActive && styles.orderRowActive]}
                      >
                        <Text style={styles.orderName}>{item.displayName}</Text>
                        <Ionicons name="menu" size={20} color="#8899AA" />
                      </Pressable>
                    );
                  }}
                />
              </FrostedSurface>
            )}
          </View>
          );})()}

          {props.instrumentFilter == null && (
          <View style={styles.modalSection}>
            <Text style={styles.modalSectionTitle}>Primary Instrument Order</Text>
            <Text style={styles.modalHint}>
              {Platform.OS === 'windows'
                ? 'When sorting by Has FC, songs are ranked by how many consecutive instruments have a full combo, starting from the top of this list.'
                : 'Drag to reorder. When sorting by Has FC, songs are ranked by how many consecutive instruments have a full combo, starting from the top.'}
            </Text>

            {Platform.OS === 'windows' ? (
              // Windows: keep up/down buttons (no gesture handler support)
              <FrostedSurface style={styles.orderList} tint="dark" intensity={12}>
              {visibleItems.map((it, idx) => (
                <View key={it.key} style={[styles.orderRow, idx === 0 && styles.orderRowFirst, idx === visibleItems.length - 1 && styles.orderRowLast, idx > 0 && styles.orderRowSeparator]}>
                  <View style={localStyles.instrumentLabelRow}>
                    <Image source={getInstrumentIconSource(it.key)} style={localStyles.instrumentIcon} resizeMode="contain" />
                    <Text style={styles.orderName}>{it.displayName}</Text>
                  </View>
                  <View style={styles.orderBtns}>
                    <Pressable
                      onPress={() => {
                        if (idx <= 0) return;
                        const next = [...visibleKeys];
                        const tmp = next[idx - 1];
                        next[idx - 1] = next[idx];
                        next[idx] = tmp;
                        props.onChange({...props.draft, order: [...next, ...hiddenKeys]});
                      }}
                      style={({pressed}) => [styles.orderBtn, pressed && styles.smallBtnPressed]}
                    >
                      <Text style={styles.orderBtnText}>↑</Text>
                    </Pressable>
                    <Pressable
                      onPress={() => {
                        if (idx >= visibleKeys.length - 1) return;
                        const next = [...visibleKeys];
                        const tmp = next[idx + 1];
                        next[idx + 1] = next[idx];
                        next[idx] = tmp;
                        props.onChange({...props.draft, order: [...next, ...hiddenKeys]});
                      }}
                      style={({pressed}) => [styles.orderBtn, pressed && styles.smallBtnPressed]}
                    >
                      <Text style={styles.orderBtnText}>↓</Text>
                    </Pressable>
                  </View>
                </View>
              ))}
              </FrostedSurface>
            ) : (
              <FrostedSurface style={styles.orderList} tint="dark" intensity={12}>
                <DraggableFlatList<InstrumentOrderItem>
                  data={visibleItems}
                  keyExtractor={(item) => item.key}
                  scrollEnabled={false}
                  onDragEnd={({data}) => {
                    props.onChange({...props.draft, order: [...data.map(i => i.key), ...hiddenKeys]});
                  }}
                  renderItem={({item, drag, isActive, getIndex}: RenderItemParams<InstrumentOrderItem>) => {
                    const idx = getIndex() ?? 0;
                    return (
                      <Pressable
                        onLongPress={drag}
                        delayLongPress={100}
                        disabled={isActive}
                        style={[styles.orderRow, idx === 0 && styles.orderRowFirst, idx === visibleItems.length - 1 && styles.orderRowLast, idx > 0 && styles.orderRowSeparator, isActive && styles.orderRowActive]}
                      >
                        <View style={localStyles.instrumentLabelRow}>
                          <Image source={getInstrumentIconSource(item.key)} style={localStyles.instrumentIcon} resizeMode="contain" />
                          <Text style={styles.orderName}>{item.displayName}</Text>
                        </View>
                        <Ionicons name="menu" size={20} color="#8899AA" />
                      </Pressable>
                    );
                  }}
                />
              </FrostedSurface>
            )}
          </View>
          )}
          </ScrollView>

          {/* Pinned footer */}
          <View style={[styles.modalFooter, isMobile && styles.modalFooterPinned, isMobile && {paddingBottom: 14 + safeBottom}]}>
            <Pressable onPress={() => Alert.alert('Reset Sort', 'Are you sure you want to reset sort settings to their defaults?', [{text: 'Cancel', style: 'cancel'}, {text: 'Reset', style: 'destructive', onPress: props.onReset}])} style={({pressed}) => [styles.modalDangerBtn, pressed && styles.smallBtnPressed]}>
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
    <Pressable onPress={props.onPress} style={({pressed}) => [{flex: 1}, pressed && styles.rowBtnPressed]}>
      <FrostedSurface style={[styles.choice, props.selected && styles.choiceSelected]} tint="dark" intensity={12}>
        <Text style={[styles.choiceText, props.selected && styles.choiceTextSelected]}>{props.label}</Text>
      </FrostedSurface>
    </Pressable>
  );
}

const localStyles = StyleSheet.create({
  instrumentLabelRow: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 8,
  },
  instrumentIcon: {
    width: 36,
    height: 36,
    marginRight: 8,
  },
});
