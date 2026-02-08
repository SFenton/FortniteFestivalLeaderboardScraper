import React from 'react';
import {Alert, Platform, Pressable, ScrollView, Switch, Text, useWindowDimensions, View} from 'react-native';
import {useSafeAreaInsets} from 'react-native-safe-area-context';

import {PlatformModal} from './PlatformModal';
import {FrostedSurface} from '../FrostedSurface';
import type {AdvancedMissingFilters} from '../../core/songListConfig';
import {modalStyles as styles} from './modalStyles';

export function FilterModal(props: {
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
  const {height: screenHeight} = useWindowDimensions();
  const {bottom: safeBottom} = useSafeAreaInsets();
  const isMobile = Platform.OS !== 'windows';

  return (
    <PlatformModal visible={props.visible} onRequestClose={props.onCancel} variant={variant} fullWidth={isMobile}>
      <FrostedSurface style={[styles.modalCard, isMobile && styles.modalCardMobile, isMobile && {height: screenHeight * 0.8}]} tint="dark" intensity={18}>
          {/* Pinned header */}
          <View style={[styles.modalHeader, isMobile && styles.modalHeaderPinned]}>
            <Text style={styles.modalTitle}>Filter Songs</Text>
            <Pressable onPress={props.onCancel} style={({pressed}) => [pressed && styles.smallBtnPressed]}>
              <FrostedSurface style={styles.modalClose} tint="dark" intensity={12}>
                <Text style={styles.modalCloseText}>Cancel</Text>
              </FrostedSurface>
            </Pressable>
          </View>

          {/* Scrollable content */}
          <ScrollView style={isMobile ? styles.modalScrollContent : undefined} contentContainerStyle={isMobile ? styles.modalScrollInner : undefined} showsVerticalScrollIndicator={false}>
          <View style={styles.modalSection}>
            <Text style={styles.modalSectionTitle}>Missing</Text>
            <Text style={styles.modalHint}>Only show songs where you are missing scores or full combos on pad or pro instruments.</Text>
            <FrostedSurface style={styles.orderList} tint="dark" intensity={12}>
              <ToggleRow label="Pad Scores" checked={props.draft.missingPadScores} onToggle={() => t('missingPadScores')} first />
              <ToggleRow label="Pad FCs" checked={props.draft.missingPadFCs} onToggle={() => t('missingPadFCs')} />
              <ToggleRow label="Pro Scores" checked={props.draft.missingProScores} onToggle={() => t('missingProScores')} />
              <ToggleRow label="Pro FCs" checked={props.draft.missingProFCs} onToggle={() => t('missingProFCs')} last />
            </FrostedSurface>
          </View>

          <View style={styles.modalSection}>
            <Text style={styles.modalSectionTitle}>Include Instruments</Text>
            <Text style={styles.modalHint}>Choose which instruments to show in the app. Impacts all pages.</Text>
            <FrostedSurface style={styles.orderList} tint="dark" intensity={12}>
              <ToggleRow label="Lead" checked={props.draft.includeLead} onToggle={() => t('includeLead')} first />
              <ToggleRow label="Bass" checked={props.draft.includeBass} onToggle={() => t('includeBass')} />
              <ToggleRow label="Drums" checked={props.draft.includeDrums} onToggle={() => t('includeDrums')} />
              <ToggleRow label="Vocals" checked={props.draft.includeVocals} onToggle={() => t('includeVocals')} />
              <ToggleRow label="Pro Lead" checked={props.draft.includeProGuitar} onToggle={() => t('includeProGuitar')} />
              <ToggleRow label="Pro Bass" checked={props.draft.includeProBass} onToggle={() => t('includeProBass')} last />
            </FrostedSurface>
          </View>
          </ScrollView>

          {/* Pinned footer */}
          <View style={[styles.modalFooter, isMobile && styles.modalFooterPinned, isMobile && {paddingBottom: 14 + safeBottom}]}>
            <Pressable onPress={() => Alert.alert('Reset Filters', 'Are you sure you want to reset all filters to their defaults?', [{text: 'Cancel', style: 'cancel'}, {text: 'Reset', style: 'destructive', onPress: props.onReset}])} style={({pressed}) => [styles.modalDangerBtn, pressed && styles.smallBtnPressed]}>
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

function ToggleRow(props: {label: string; checked: boolean; onToggle: () => void; first?: boolean; last?: boolean}) {
  return (
    <Pressable
      onPress={props.onToggle}
      style={({pressed}) => [
        styles.orderRow,
        props.first && styles.orderRowFirst,
        props.last && styles.orderRowLast,
        !props.first && styles.orderRowSeparator,
        pressed && styles.rowBtnPressed,
      ]}
      accessibilityRole="switch"
    >
      <Text style={styles.orderName}>{props.label}</Text>
      <Switch
        value={props.checked}
        onValueChange={props.onToggle}
        trackColor={{false: '#263244', true: 'rgba(45,130,230,1)'}}
        thumbColor={props.checked ? '#FFFFFF' : '#8899AA'}
      />
    </Pressable>
  );
}
