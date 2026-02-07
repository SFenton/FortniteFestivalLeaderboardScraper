import React, {useMemo} from 'react';
import {Alert, Platform, Pressable, ScrollView, Text, useWindowDimensions, View} from 'react-native';
import Ionicons from 'react-native-vector-icons/Ionicons';
import DraggableFlatList, {type RenderItemParams} from 'react-native-draggable-flatlist';
import {useSafeAreaInsets} from 'react-native-safe-area-context';

import {PlatformModal} from './PlatformModal';
import {FrostedSurface} from '../FrostedSurface';
import {normalizeInstrumentOrder} from '../../app/songs/songFiltering';
import type {InstrumentOrderItem, SongSortMode} from '../../core/songListConfig';
import type {InstrumentKey} from '../../core/instruments';
import {modalStyles as styles} from './modalStyles';

export function SortModal(props: {
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
                label="Has FC"
                selected={props.draft.sortMode === 'hasfc'}
                onPress={() => props.onChange({...props.draft, sortMode: 'hasfc'})}
              />
            </View>
          </View>

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
              {orderItems.map((it, idx) => (
                <View key={it.key} style={[styles.orderRow, idx === 0 && styles.orderRowFirst, idx === orderItems.length - 1 && styles.orderRowLast, idx > 0 && styles.orderRowSeparator]}>
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
              </FrostedSurface>
            ) : (
              <FrostedSurface style={styles.orderList} tint="dark" intensity={12}>
                <DraggableFlatList<InstrumentOrderItem>
                  data={orderItems}
                  keyExtractor={(item) => item.key}
                  scrollEnabled={false}
                  onDragEnd={({data}) => {
                    props.onChange({...props.draft, order: data.map(i => i.key)});
                  }}
                  renderItem={({item, drag, isActive, getIndex}: RenderItemParams<InstrumentOrderItem>) => {
                    const idx = getIndex() ?? 0;
                    return (
                      <Pressable
                        onLongPress={drag}
                        delayLongPress={100}
                        disabled={isActive}
                        style={[styles.orderRow, idx === 0 && styles.orderRowFirst, idx === orderItems.length - 1 && styles.orderRowLast, idx > 0 && styles.orderRowSeparator, isActive && styles.orderRowActive]}
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
