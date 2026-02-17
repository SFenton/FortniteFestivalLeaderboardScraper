import React, {useState} from 'react';
import {Alert, Image, Platform, Pressable, ScrollView, StyleSheet, Text, useWindowDimensions, View} from 'react-native';
import {useSafeAreaInsets} from 'react-native-safe-area-context';

import {PlatformModal} from './PlatformModal';
import {FrostedSurface} from '../FrostedSurface';
import {ToggleRow} from '../controls/ToggleRow';
import {modalStyles as styles} from './modalStyles';
import type {InstrumentKey, InstrumentShowSettings} from '@festival/core';
import {getInstrumentIconSource} from '../instruments/instrumentVisuals';
import type {SuggestionTypeSettings, SuggestionTypeSettingsKey} from '@festival/core';
import {SUGGESTION_TYPES, defaultSuggestionTypeSettings, globalKeyFor, perInstrumentKeyFor} from '@festival/core';

export type SuggestionsInstrumentFilters = {
  suggestionsLeadFilter: boolean;
  suggestionsBassFilter: boolean;
  suggestionsDrumsFilter: boolean;
  suggestionsVocalsFilter: boolean;
  suggestionsProLeadFilter: boolean;
  suggestionsProBassFilter: boolean;
} & SuggestionTypeSettings;

export const defaultSuggestionsInstrumentFilters = (): SuggestionsInstrumentFilters => ({
  suggestionsLeadFilter: true,
  suggestionsBassFilter: true,
  suggestionsDrumsFilter: true,
  suggestionsVocalsFilter: true,
  suggestionsProLeadFilter: true,
  suggestionsProBassFilter: true,
  ...defaultSuggestionTypeSettings(),
});

export function SuggestionsFilterModal(props: {
  visible: boolean;
  draft: SuggestionsInstrumentFilters;
  instrumentVisibility: InstrumentShowSettings;
  onChange: (d: SuggestionsInstrumentFilters) => void;
  onCancel: () => void;
  onReset: () => void;
  onApply: () => void;
}) {
  const toggle = (k: keyof SuggestionsInstrumentFilters) =>
    props.onChange({...props.draft, [k]: !props.draft[k]});

  const [selectedInstrument, setSelectedInstrument] = useState<InstrumentKey | null>(null);

  /** Toggle a global type and sync all per-instrument toggles for that type. */
  const toggleGlobal = (typeId: (typeof SUGGESTION_TYPES)[number]['id']) => {
    const gk = globalKeyFor(typeId);
    const turningOff = props.draft[gk];
    const updates: Partial<SuggestionsInstrumentFilters> = {[gk]: !turningOff};
    for (const inst of visibleInstruments) {
      updates[perInstrumentKeyFor(inst.key, typeId)] = turningOff ? false : true;
    }
    props.onChange({...props.draft, ...updates} as SuggestionsInstrumentFilters);
  };

  /** Toggle a per-instrument type. Auto-enables global if turning on; auto-disables global if last one turned off. */
  const togglePerInstrument = (instrument: InstrumentKey, typeId: (typeof SUGGESTION_TYPES)[number]['id']) => {
    const key = perInstrumentKeyFor(instrument, typeId);
    const gk = globalKeyFor(typeId);
    const turningOn = !props.draft[key];
    const updates: Partial<SuggestionsInstrumentFilters> = {[key]: turningOn};
    if (turningOn && !props.draft[gk]) {
      updates[gk] = true;
    } else if (!turningOn) {
      const allOff = visibleInstruments.every(inst => {
        const pk = perInstrumentKeyFor(inst.key, typeId);
        return pk === key ? true : !props.draft[pk];
      });
      if (allOff) updates[gk] = false;
    }
    props.onChange({...props.draft, ...updates} as SuggestionsInstrumentFilters);
  };

  const instrumentPickerOrder: {key: InstrumentKey; label: string; filterKey: keyof SuggestionsInstrumentFilters; showKey: keyof InstrumentShowSettings}[] = [
    {key: 'guitar', label: 'Lead', filterKey: 'suggestionsLeadFilter', showKey: 'showLead'},
    {key: 'bass', label: 'Bass', filterKey: 'suggestionsBassFilter', showKey: 'showBass'},
    {key: 'vocals', label: 'Vocals', filterKey: 'suggestionsVocalsFilter', showKey: 'showVocals'},
    {key: 'drums', label: 'Drums', filterKey: 'suggestionsDrumsFilter', showKey: 'showDrums'},
    {key: 'pro_guitar', label: 'Pro Lead', filterKey: 'suggestionsProLeadFilter', showKey: 'showProLead'},
    {key: 'pro_bass', label: 'Pro Bass', filterKey: 'suggestionsProBassFilter', showKey: 'showProBass'},
  ];

  const visibleInstruments = instrumentPickerOrder.filter(i => props.instrumentVisibility[i.showKey]);

  // Clear instrument-specific picker selection if the instrument was hidden in app settings.
  const effectiveSelectedInstrument = selectedInstrument && visibleInstruments.some(i => i.key === selectedInstrument) ? selectedInstrument : null;

  const variant = Platform.OS === 'windows' ? 'center' : 'bottom';
  const {height: screenHeight} = useWindowDimensions();
  const {bottom: safeBottom} = useSafeAreaInsets();
  const isMobile = Platform.OS !== 'windows';

  return (
    <PlatformModal visible={props.visible} onRequestClose={props.onCancel} variant={variant} fullWidth={isMobile}>
      <FrostedSurface style={[styles.modalCard, isMobile && styles.modalCardMobile, !isMobile && styles.modalCardWindows, isMobile && {height: screenHeight * 0.8}]} tint="dark" intensity={18}>
        {/* Pinned header */}
        <View style={[styles.modalHeader, isMobile && styles.modalHeaderPinned]}>
          <Text style={styles.modalTitle}>Filter Suggestions</Text>
          <Pressable onPress={props.onCancel} style={({pressed}) => [pressed && styles.smallBtnPressed]}>
            <FrostedSurface style={styles.modalClose} tint="dark" intensity={12}>
              <Text style={styles.modalCloseText}>Cancel</Text>
            </FrostedSurface>
          </Pressable>
        </View>

        {/* Scrollable content */}
        <ScrollView style={styles.modalScrollContent} contentContainerStyle={styles.modalScrollInner} showsVerticalScrollIndicator={false}>
          <View style={styles.modalSection}>
            <Text style={styles.modalSectionTitle}>Instruments</Text>
            <Text style={styles.modalHint}>Choose which instruments appear in your suggestions.</Text>
            {visibleInstruments.map((inst, i) => (
              <ToggleRow
                key={inst.key}
                label={inst.label}
                icon={getInstrumentIconSource(inst.key)}
                checked={props.draft[inst.filterKey]}
                onToggle={() => toggle(inst.filterKey)}
                first={i === 0}
                last={i === visibleInstruments.length - 1}
              />
            ))}
          </View>

          <View style={styles.modalSection}>
            <Text style={styles.modalSectionTitle}>General</Text>
            <Text style={styles.modalHint}>Toggle broad suggestion types on or off.</Text>
            {SUGGESTION_TYPES.map((st, i) => (
              <ToggleRow
                key={st.id}
                label={st.label}
                description={st.description}
                checked={props.draft[globalKeyFor(st.id)]}
                onToggle={() => toggleGlobal(st.id)}
                first={i === 0}
                last={i === SUGGESTION_TYPES.length - 1}
              />
            ))}
          </View>

          <View style={styles.modalSection}>
            <Text style={styles.modalSectionTitle}>Instrument-Specific</Text>
            <Text style={styles.modalHint}>These filters will filter out suggestions on a per-instrument basis, rather than global.</Text>
            <View style={localStyles.instrumentRow}>
              {visibleInstruments.map(inst => {
                const isSelected = effectiveSelectedInstrument === inst.key;
                return (
                  <Pressable
                    key={inst.key}
                    onPress={() => setSelectedInstrument(cur => cur === inst.key ? null : inst.key)}
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

            {effectiveSelectedInstrument && (
              <View style={{marginTop: 12, gap: 2}}>
                {SUGGESTION_TYPES.map((st, i) => {
                  const key = perInstrumentKeyFor(effectiveSelectedInstrument, st.id);
                  return (
                    <ToggleRow
                      key={st.id}
                      label={st.label}
                      description={st.description}
                      checked={props.draft[key]}
                      onToggle={() => togglePerInstrument(effectiveSelectedInstrument, st.id)}
                      first={i === 0}
                      last={i === SUGGESTION_TYPES.length - 1}
                    />
                  );
                })}
              </View>
            )}
          </View>
        </ScrollView>

        {/* Pinned footer */}
        <View style={[styles.modalFooter, isMobile && styles.modalFooterPinned, isMobile && {paddingBottom: 14 + safeBottom}]}>
          <Pressable onPress={() => Alert.alert('Reset Filters', 'Are you sure you want to reset all suggestion filters to their defaults?', [{text: 'Cancel', style: 'cancel'}, {text: 'Reset', style: 'destructive', onPress: props.onReset}])} style={({pressed}) => [styles.modalDangerBtn, pressed && styles.smallBtnPressed]}>
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
