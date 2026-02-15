import React, {useCallback, useMemo} from 'react';
import {Alert, Image, Platform, Pressable, ScrollView, StyleSheet, Switch, Text, useWindowDimensions, View} from 'react-native';
import {useSafeAreaInsets} from 'react-native-safe-area-context';
import Ionicons from 'react-native-vector-icons/Ionicons';

import {PlatformModal} from './PlatformModal';
import {FrostedSurface} from '../FrostedSurface';
import {Accordion} from '../Accordion';
import {PERCENTILE_THRESHOLDS} from '../../core/songListConfig';
import type {AdvancedMissingFilters} from '../../core/songListConfig';
import type {InstrumentShowSettings} from '../../app/songs/songFiltering';
import {modalStyles as styles} from './modalStyles';
import type {InstrumentKey} from '../../core/instruments';
import {getInstrumentIconSource} from '../instruments/instrumentVisuals';
import {DifficultyBars} from '../instruments/InstrumentCard';

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
  /** Whether the Season Achieved metadata column is enabled in settings. */
  seasonVisible?: boolean;
  /** Sorted list of distinct season numbers present in the local DB. */
  availableSeasons?: number[];
  /** Sorted list of distinct percentile buckets present in the local DB. */
  availablePercentiles?: number[];
  /** Sorted list of distinct star counts present in the local DB (1–6). */
  availableStars?: number[];
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

          {props.selectedInstrumentFilter != null && props.seasonVisible && (
          <View style={styles.modalSection}>
            <Accordion
              title="Season"
              hint="Filter songs by the season in which the score was achieved on the selected instrument."
            >
              <SeasonsToggles
                availableSeasons={props.availableSeasons ?? []}
                seasonFilter={props.draft.seasonFilter ?? {}}
                onSeasonFilterChange={(next) => props.onChange({...props.draft, seasonFilter: next})}
              />
            </Accordion>
          </View>
          )}

          {props.selectedInstrumentFilter != null && (
          <View style={styles.modalSection}>
            <Accordion
              title="Percentile"
              hint="Show or hide songs based on their leaderboard ranking bracket for the selected instrument."
            >
              <PercentileToggles
                availablePercentiles={props.availablePercentiles ?? []}
                percentileFilter={props.draft.percentileFilter ?? {}}
                onPercentileFilterChange={(next) => props.onChange({...props.draft, percentileFilter: next})}
              />
            </Accordion>
          </View>
          )}

          {props.selectedInstrumentFilter != null && (
          <View style={styles.modalSection}>
            <Accordion
              title="Stars"
              hint="Filter songs by the number of stars you have on your high score."
            >
              <StarsToggles
                availableStars={props.availableStars ?? []}
                starsFilter={props.draft.starsFilter ?? {}}
                onStarsFilterChange={(next) => props.onChange({...props.draft, starsFilter: next})}
              />
            </Accordion>
          </View>
          )}

          {props.selectedInstrumentFilter != null && (
          <View style={styles.modalSection}>
            <Accordion
              title="Song Intensity"
              hint="Filter by song intensity for the selected instrument."
            >
              <DifficultyToggles
                difficultyFilter={props.draft.difficultyFilter ?? {}}
                onDifficultyFilterChange={(next) => props.onChange({...props.draft, difficultyFilter: next})}
              />
            </Accordion>
          </View>
          )}
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

function SeasonsToggles(props: {
  availableSeasons: number[];
  seasonFilter: Record<number, boolean>;
  onSeasonFilterChange: (next: Record<number, boolean>) => void;
}) {
  // One toggle per season + "No Score" (0) at the bottom.
  const allKeys = useMemo(() => [...props.availableSeasons, 0], [props.availableSeasons]);
  // Empty record = all enabled; explicit false = disabled
  const isEnabled = (s: number) => props.seasonFilter[s] !== false;
  const toggle = (s: number) =>
    props.onSeasonFilterChange({...props.seasonFilter, [s]: !isEnabled(s)});
  const clearAll = () =>
    props.onSeasonFilterChange(Object.fromEntries(allKeys.map(k => [k, false])));
  const selectAll = () =>
    // Reset to empty object (= all enabled)
    props.onSeasonFilterChange({});
  return (
    <>
      <View style={localStyles.bulkBtnRow}>
        <Pressable onPress={clearAll} style={({pressed}) => [localStyles.bulkBtn, localStyles.bulkBtnDanger, pressed && styles.smallBtnPressed]}>
          <Ionicons name="close" size={14} color="#FFFFFF" />
          <Text style={localStyles.bulkBtnText}>Clear All</Text>
        </Pressable>
        <Pressable onPress={selectAll} style={({pressed}) => [localStyles.bulkBtn, localStyles.bulkBtnSuccess, pressed && styles.smallBtnPressed]}>
          <Ionicons name="checkmark" size={14} color="#FFFFFF" />
          <Text style={localStyles.bulkBtnText}>Select All</Text>
        </Pressable>
      </View>
      {allKeys.map((s, idx) => (
        <ToggleRow
          key={s}
          label={s === 0 ? 'No Score' : `Season ${s}`}
          checked={isEnabled(s)}
          onToggle={() => toggle(s)}
          first={idx === 0}
          last={idx === allKeys.length - 1}
        />
      ))}
    </>
  );
}

function PercentileToggles(props: {
  availablePercentiles: number[];
  percentileFilter: Record<number, boolean>;
  onPercentileFilterChange: (next: Record<number, boolean>) => void;
}) {
  // Show every supported bucket with "No Score" (0) at the bottom.
  const allKeys = useMemo(() => [...PERCENTILE_THRESHOLDS, 0], []);
  const isEnabled = (k: number) => props.percentileFilter[k] !== false;
  const toggle = (k: number) =>
    props.onPercentileFilterChange({...props.percentileFilter, [k]: !isEnabled(k)});
  const clearAll = () =>
    props.onPercentileFilterChange(Object.fromEntries(allKeys.map(k => [k, false])));
  const selectAll = () =>
    props.onPercentileFilterChange({});
  return (
    <>
      <View style={localStyles.bulkBtnRow}>
        <Pressable onPress={clearAll} style={({pressed}) => [localStyles.bulkBtn, localStyles.bulkBtnDanger, pressed && styles.smallBtnPressed]}>
          <Ionicons name="close" size={14} color="#FFFFFF" />
          <Text style={localStyles.bulkBtnText}>Clear All</Text>
        </Pressable>
        <Pressable onPress={selectAll} style={({pressed}) => [localStyles.bulkBtn, localStyles.bulkBtnSuccess, pressed && styles.smallBtnPressed]}>
          <Ionicons name="checkmark" size={14} color="#FFFFFF" />
          <Text style={localStyles.bulkBtnText}>Select All</Text>
        </Pressable>
      </View>
      {allKeys.map((k, idx) => (
        <ToggleRow
          key={k}
          label={k === 0 ? 'No Score' : `Top ${k}%`}
          checked={isEnabled(k)}
          onToggle={() => toggle(k)}
          first={idx === 0}
          last={idx === allKeys.length - 1}
        />
      ))}
    </>
  );
}

const STAR_WHITE_ICON = require('../../assets/icons/star_white.png');
const STAR_GOLD_ICON = require('../../assets/icons/star_gold.png');
const DIFFICULTY_FILTER_ORDER = [1, 2, 3, 4, 5, 6, 7, 0] as const;

/** Ordered star filter keys: 6 = Gold Stars, then 5 down to 1, then 0 = No Score. */
const STAR_FILTER_ORDER = [6, 5, 4, 3, 2, 1, 0] as const;

function StarIcons({count, gold}: {count: number; gold?: boolean}) {
  const icons = [];
  for (let i = 0; i < count; i++) {
    icons.push(
      <Image
        key={i}
        source={gold ? STAR_GOLD_ICON : STAR_WHITE_ICON}
        style={localStyles.starIcon}
        resizeMode="contain"
      />,
    );
  }
  return <View style={localStyles.starIconRow}>{icons}</View>;
}

function StarToggleRow(props: {starCount: number; checked: boolean; onToggle: () => void; first?: boolean; last?: boolean}) {
  const {starCount, checked, onToggle, first, last} = props;
  const label = starCount === 0 ? 'No Score' : starCount === 6 ? 'Gold Stars' : `${starCount} Stars`;
  return (
    <Pressable
      onPress={onToggle}
      style={({pressed}) => [
        styles.orderRow,
        first && {marginTop: 6},
        pressed && styles.rowBtnPressed,
      ]}
      accessibilityRole="switch"
      accessibilityLabel={label}
    >
      <View style={{flex: 1, marginRight: 12}}>
        {starCount === 0 ? (
          <Text style={styles.orderName}>No Score</Text>
        ) : starCount === 6 ? (
          <StarIcons count={5} gold />
        ) : (
          <StarIcons count={starCount} />
        )}
      </View>
      <Switch
        value={checked}
        onValueChange={onToggle}
        trackColor={{false: '#263244', true: 'rgba(45,130,230,1)'}}
        thumbColor={checked ? '#FFFFFF' : '#8899AA'}
      />
    </Pressable>
  );
}

function StarsToggles(props: {
  availableStars: number[];
  starsFilter: Record<number, boolean>;
  onStarsFilterChange: (next: Record<number, boolean>) => void;
}) {
  // Stars are a small fixed set (0–6), so always show every option.
  const allKeys = STAR_FILTER_ORDER;
  const isEnabled = useCallback((k: number) => props.starsFilter[k] !== false, [props.starsFilter]);
  const toggle = useCallback(
    (k: number) => props.onStarsFilterChange({...props.starsFilter, [k]: !isEnabled(k)}),
    [props.starsFilter, isEnabled, props.onStarsFilterChange],
  );
  const clearAll = useCallback(
    () => props.onStarsFilterChange(Object.fromEntries(allKeys.map(k => [k, false]))),
    [allKeys, props.onStarsFilterChange],
  );
  const selectAll = useCallback(
    () => props.onStarsFilterChange({}),
    [props.onStarsFilterChange],
  );

  return (
    <>
      <View style={localStyles.bulkBtnRow}>
        <Pressable onPress={clearAll} style={({pressed}) => [localStyles.bulkBtn, localStyles.bulkBtnDanger, pressed && styles.smallBtnPressed]}>
          <Ionicons name="close" size={14} color="#FFFFFF" />
          <Text style={localStyles.bulkBtnText}>Clear All</Text>
        </Pressable>
        <Pressable onPress={selectAll} style={({pressed}) => [localStyles.bulkBtn, localStyles.bulkBtnSuccess, pressed && styles.smallBtnPressed]}>
          <Ionicons name="checkmark" size={14} color="#FFFFFF" />
          <Text style={localStyles.bulkBtnText}>Select All</Text>
        </Pressable>
      </View>
      {allKeys.map((k, idx) => (
        <StarToggleRow
          key={k}
          starCount={k}
          checked={isEnabled(k)}
          onToggle={() => toggle(k)}
          first={idx === 0}
          last={idx === allKeys.length - 1}
        />
      ))}
    </>
  );
}

function DifficultyToggles(props: {
  difficultyFilter: Record<number, boolean>;
  onDifficultyFilterChange: (next: Record<number, boolean>) => void;
}) {
  const allKeys = DIFFICULTY_FILTER_ORDER;
  const isEnabled = useCallback((k: number) => props.difficultyFilter[k] !== false, [props.difficultyFilter]);
  const toggle = useCallback(
    (k: number) => props.onDifficultyFilterChange({...props.difficultyFilter, [k]: !isEnabled(k)}),
    [props.difficultyFilter, isEnabled, props.onDifficultyFilterChange],
  );
  const clearAll = useCallback(
    () => props.onDifficultyFilterChange(Object.fromEntries(allKeys.map(k => [k, false]))),
    [allKeys, props.onDifficultyFilterChange],
  );
  const selectAll = useCallback(
    () => props.onDifficultyFilterChange({}),
    [props.onDifficultyFilterChange],
  );

  return (
    <>
      <View style={localStyles.bulkBtnRow}>
        <Pressable onPress={clearAll} style={({pressed}) => [localStyles.bulkBtn, localStyles.bulkBtnDanger, pressed && styles.smallBtnPressed]}>
          <Ionicons name="close" size={14} color="#FFFFFF" />
          <Text style={localStyles.bulkBtnText}>Clear All</Text>
        </Pressable>
        <Pressable onPress={selectAll} style={({pressed}) => [localStyles.bulkBtn, localStyles.bulkBtnSuccess, pressed && styles.smallBtnPressed]}>
          <Ionicons name="checkmark" size={14} color="#FFFFFF" />
          <Text style={localStyles.bulkBtnText}>Select All</Text>
        </Pressable>
      </View>
      {allKeys.map((k, idx) => (
        <DifficultyToggleRow
          key={k}
          difficulty={k}
          checked={isEnabled(k)}
          onToggle={() => toggle(k)}
          first={idx === 0}
          last={idx === allKeys.length - 1}
        />
      ))}
    </>
  );
}

function DifficultyToggleRow(props: {difficulty: number; checked: boolean; onToggle: () => void; first?: boolean; last?: boolean}) {
  const {difficulty, checked, onToggle, first} = props;
  return (
    <Pressable
      onPress={onToggle}
      style={({pressed}) => [
        styles.orderRow,
        first && {marginTop: 6},
        pressed && styles.rowBtnPressed,
      ]}
      accessibilityRole="switch"
      accessibilityLabel={difficulty === 0 ? 'No Score' : `Difficulty ${difficulty} of 7`}
    >
      <View style={{flex: 1, marginRight: 12}}>
        {difficulty === 0 ? (
          <Text style={styles.orderName}>No Score</Text>
        ) : (
          <DifficultyBars rawDifficulty={difficulty - 1} compact barWidth={14} barHeight={28} gap={2} />
        )}
      </View>
      <Switch
        value={checked}
        onValueChange={onToggle}
        trackColor={{false: '#263244', true: 'rgba(45,130,230,1)'}}
        thumbColor={checked ? '#FFFFFF' : '#8899AA'}
      />
    </Pressable>
  );
}

const localStyles = StyleSheet.create({
  starIcon: {
    width: 32,
    height: 32,
  },
  starIconRow: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 2,
  },
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
  bulkBtnRow: {
    flexDirection: 'row',
    gap: 8,
    marginBottom: 4,
  },
  bulkBtn: {
    flex: 1,
    flexDirection: 'row',
    paddingVertical: 8,
    borderRadius: 10,
    borderWidth: 1,
    alignItems: 'center',
    justifyContent: 'center',
    gap: 4,
  },
  bulkBtnDanger: {
    borderColor: 'rgba(198,40,40,0.4)',
    backgroundColor: 'rgba(198,40,40,0.4)',
  },
  bulkBtnSuccess: {
    borderColor: 'rgba(40,167,69,0.4)',
    backgroundColor: 'rgba(40,167,69,0.4)',
  },
  bulkBtnText: {
    color: '#FFFFFF',
    fontSize: 12,
    fontWeight: '700',
  },
});
