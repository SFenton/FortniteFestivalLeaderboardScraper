import React from 'react';
import {Platform, Pressable, Text, View} from 'react-native';

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

  return (
    <PlatformModal visible={props.visible} onRequestClose={props.onCancel} variant={variant}>
      <FrostedSurface style={styles.modalCard} tint="dark" intensity={18}>
          <View style={styles.modalHeader}>
            <Text style={styles.modalTitle}>Filter Songs</Text>
            <Pressable onPress={props.onCancel} style={({pressed}) => [pressed && styles.smallBtnPressed]}>
              <FrostedSurface style={styles.modalClose} tint="dark" intensity={12}>
                <Text style={styles.modalCloseText}>Cancel</Text>
              </FrostedSurface>
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

function ToggleRow(props: {label: string; checked: boolean; onPress: () => void}) {
  return (
    <Pressable onPress={props.onPress} style={({pressed}) => [styles.toggleRow, pressed && styles.rowBtnPressed]} accessibilityRole="button">
      <Text style={styles.toggleLabel}>{props.label}</Text>
      <Text style={[styles.toggleValue, props.checked && styles.toggleValueOn]}>{props.checked ? 'On' : 'Off'}</Text>
    </Pressable>
  );
}
