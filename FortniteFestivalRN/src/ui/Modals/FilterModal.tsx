import React from 'react';
import {Alert, Image, Platform, Pressable, ScrollView, StyleSheet, Switch, Text, useWindowDimensions, View} from 'react-native';
import {useSafeAreaInsets} from 'react-native-safe-area-context';

import {PlatformModal} from './PlatformModal';
import {FrostedSurface} from '../FrostedSurface';
import type {AdvancedMissingFilters} from '../../core/songListConfig';
import type {InstrumentShowSettings} from '../../app/songs/songFiltering';
import {modalStyles as styles} from './modalStyles';
import type {InstrumentKey} from '../../core/instruments';
import {getInstrumentIconSource} from '../instruments/instrumentVisuals';

export function FilterModal(props: {
  visible: boolean;
  draft: AdvancedMissingFilters;
  onChange: (d: AdvancedMissingFilters) => void;
  onCancel: () => void;
  onReset: () => void;
  onApply: () => void;
  hideProFilters?: boolean;
  showInstruments: InstrumentShowSettings;
  onShowInstrumentToggle: (key: keyof InstrumentShowSettings) => void;
  selectedInstrumentFilter: InstrumentKey | null;
  onSelectedInstrumentFilterChange: (key: InstrumentKey | null) => void;
}) {
  const t = (k: keyof AdvancedMissingFilters) =>
    props.onChange({...props.draft, [k]: !props.draft[k]});

  const instrumentPickerOrder: {key: InstrumentKey; label: string; showKey: keyof InstrumentShowSettings}[] = [
    {key: 'guitar', label: 'Lead', showKey: 'showLead'},
    {key: 'bass', label: 'Bass', showKey: 'showBass'},
    {key: 'vocals', label: 'Vocals', showKey: 'showVocals'},
    {key: 'drums', label: 'Drums', showKey: 'showDrums'},
    {key: 'pro_guitar', label: 'Pro Lead', showKey: 'showProLead'},
    {key: 'pro_bass', label: 'Pro Bass', showKey: 'showProBass'},
  ];

  const visibleInstruments = instrumentPickerOrder.filter(i => props.showInstruments[i.showKey]);

  // Clear selection if the instrument was hidden in settings.
  const effectiveSelected = props.selectedInstrumentFilter && visibleInstruments.some(i => i.key === props.selectedInstrumentFilter)
    ? props.selectedInstrumentFilter
    : null;

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
              <ToggleRow label="Pad Scores" description="Toggle this on to filter to songs that are missing scores on Lead, Bass, Drums, or Vocals." checked={props.draft.missingPadScores} onToggle={() => t('missingPadScores')} first />
              <ToggleRow label="Pad FCs" description="Toggle this on to filter to songs that are missing FCs on Lead, Bass, Drums, or Vocals." checked={props.draft.missingPadFCs} onToggle={() => t('missingPadFCs')} last={!!props.hideProFilters} />
              {!props.hideProFilters && <ToggleRow label="Pro Scores" description="Toggle this on to filter to songs that are missing scores on Pro Lead or Pro Bass." checked={props.draft.missingProScores} onToggle={() => t('missingProScores')} />}
              {!props.hideProFilters && <ToggleRow label="Pro FCs" description="Toggle this on to filter to songs that are missing FCs on Pro Lead or Pro Bass." checked={props.draft.missingProFCs} onToggle={() => t('missingProFCs')} last />}
          </View>

          <View style={styles.modalSection}>
            <Text style={styles.modalSectionTitle}>Instrument</Text>
            <Text style={styles.modalHint}>Select an instrument to only show its metadata on each song row. When none is selected, all instruments are shown.</Text>
            <View style={localStyles.instrumentRow}>
              {visibleInstruments.map(inst => {
                const isSelected = effectiveSelected === inst.key;
                return (
                  <Pressable
                    key={inst.key}
                    onPress={() => props.onSelectedInstrumentFilterChange(isSelected ? null : inst.key)}
                    style={({pressed}) => [
                      localStyles.instrumentBtn,
                      isSelected && localStyles.instrumentBtnSelected,
                      pressed && styles.smallBtnPressed,
                    ]}
                    accessibilityRole="button"
                    accessibilityLabel={inst.label}
                  >
                    <Image source={getInstrumentIconSource(inst.key)} style={localStyles.instrumentIcon} resizeMode="contain" />
                  </Pressable>
                );
              })}
            </View>
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

function ToggleRow(props: {label: string; description?: string; checked: boolean; onToggle: () => void; first?: boolean; last?: boolean}) {
  return (
    <Pressable
      onPress={props.onToggle}
      style={({pressed}) => [
        styles.orderRow,
        props.first && {marginTop: 6},
        pressed && styles.rowBtnPressed,
      ]}
      accessibilityRole="switch"
    >
      <View style={{flex: 1, marginRight: 12}}>
        <Text style={styles.orderName}>{props.label}</Text>
        {props.description ? <Text style={styles.modalHint}>{props.description}</Text> : null}
      </View>
      <Switch
        value={props.checked}
        onValueChange={props.onToggle}
        trackColor={{false: '#263244', true: 'rgba(45,130,230,1)'}}
        thumbColor={props.checked ? '#FFFFFF' : '#8899AA'}
      />
    </Pressable>
  );
}

const localStyles = StyleSheet.create({
  instrumentRow: {
    flexDirection: 'row',
    justifyContent: 'space-between',
    marginTop: 8,
    gap: 8,
  },
  instrumentBtn: {
    width: 40,
    height: 40,
    borderRadius: 20,
    borderWidth: 2,
    borderColor: 'transparent',
    backgroundColor: 'transparent',
    alignItems: 'center',
    justifyContent: 'center',
  },
  instrumentBtnSelected: {
    borderColor: '#1A5FB4',
    backgroundColor: '#2D82E6',
  },
  instrumentIcon: {
    width: 32,
    height: 32,
  },
});
