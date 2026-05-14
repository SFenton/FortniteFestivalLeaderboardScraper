/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useState, useMemo, useCallback, useEffect } from 'react';
import Modal from '../../../components/modals/Modal';
import { ModalSection } from '../../../components/modals/components/ModalSection';
import { ToggleRow } from '../../../components/common/ToggleRow';
import { Accordion } from '../../../components/common/Accordion';
import ConfirmAlert from '../../../components/modals/ConfirmAlert';
import { InstrumentSelector, type InstrumentSelectorItem } from '../../../components/common/InstrumentSelector';
import { InstrumentIcon } from '../../../components/display/InstrumentIcons';
import type { InstrumentKey } from '@festival/core/instruments';
import { InstrumentKeys } from '@festival/core/instruments';
import type { ServerInstrumentKey } from '@festival/core/api/serverTypes';
import { useModalDraft } from '../../../hooks/ui/useModalDraft';
import type { SelectedBandProfile } from '../../../hooks/data/useSelectedProfile';
import type { BandInstrumentFilterApplyPayload, BandInstrumentFilterAssignment } from '../../../types/bandFilter';
import {
  areBandInstrumentDraftsEqual,
  BandInstrumentFilterInvalidSelectionAlert,
  BandInstrumentFilterPicker,
  useBandInstrumentFilterController,
} from '../../band/modals/BandInstrumentFilterPicker';
import {
  SUGGESTION_TYPES,
  globalKeyFor,
  perInstrumentKeyFor,
} from '@festival/core/suggestions/suggestionFilterConfig';
import type { SuggestionTypeId } from '@festival/core/suggestions/suggestionFilterConfig';
import { Size } from '@festival/theme';
import { filterStyles } from '../../songs/modals/filterStyles';
import { useTranslation } from 'react-i18next';

// ---------------------------------------------------------------------------
// Filter draft type – mirrors the RN SuggestionsInstrumentFilters
// ---------------------------------------------------------------------------

export type SuggestionsFilterDraft = {
  suggestionsLeadFilter: boolean;
  suggestionsBassFilter: boolean;
  suggestionsDrumsFilter: boolean;
  suggestionsVocalsFilter: boolean;
  suggestionsProLeadFilter: boolean;
  suggestionsProBassFilter: boolean;
  suggestionsPeripheralVocalsFilter: boolean;
  suggestionsPeripheralCymbalsFilter: boolean;
  suggestionsPeripheralDrumsFilter: boolean;
  [key: string]: boolean;
};

export function defaultSuggestionsFilterDraft(): SuggestionsFilterDraft {
  const d: Record<string, boolean> = {
    suggestionsLeadFilter: true,
    suggestionsBassFilter: true,
    suggestionsDrumsFilter: true,
    suggestionsVocalsFilter: true,
    suggestionsProLeadFilter: true,
    suggestionsProBassFilter: true,
    suggestionsPeripheralVocalsFilter: true,
    suggestionsPeripheralCymbalsFilter: true,
    suggestionsPeripheralDrumsFilter: true,
  };
  for (const { id } of SUGGESTION_TYPES) {
    d[globalKeyFor(id)] = true;
    for (const inst of InstrumentKeys) {
      d[perInstrumentKeyFor(inst, id)] = true;
    }
  }
  return d as SuggestionsFilterDraft;
}

export function isSuggestionsFilterActive(draft: SuggestionsFilterDraft, mode: 'solo' | 'band' = 'solo'): boolean {
  const defaults = defaultSuggestionsFilterDraft();
  return Object.keys(defaults)
    .filter(k => mode === 'solo' || BAND_SUGGESTION_TYPE_IDS.some(typeId => k === globalKeyFor(typeId)))
    .some(k => (draft[k] ?? defaults[k]) !== defaults[k]);
}

// ---------------------------------------------------------------------------
// Instrument picker config
// ---------------------------------------------------------------------------

const INSTRUMENTS: { key: InstrumentKey; label: string; filterKey: keyof SuggestionsFilterDraft; showKey: string }[] = [
  { key: 'guitar',             label: 'Lead',                filterKey: 'suggestionsLeadFilter',              showKey: 'showLead' },
  { key: 'bass',               label: 'Bass',                filterKey: 'suggestionsBassFilter',              showKey: 'showBass' },
  { key: 'drums',              label: 'Drums',               filterKey: 'suggestionsDrumsFilter',             showKey: 'showDrums' },
  { key: 'vocals',             label: 'Tap Vocals',          filterKey: 'suggestionsVocalsFilter',            showKey: 'showVocals' },
  { key: 'pro_guitar',         label: 'Pro Lead',            filterKey: 'suggestionsProLeadFilter',           showKey: 'showProLead' },
  { key: 'pro_bass',           label: 'Pro Bass',            filterKey: 'suggestionsProBassFilter',           showKey: 'showProBass' },
  { key: 'peripheral_vocals',  label: 'Karaoke',            filterKey: 'suggestionsPeripheralVocalsFilter',  showKey: 'showPeripheralVocals' },
  { key: 'peripheral_cymbals', label: 'Pro Drums + Cymbals', filterKey: 'suggestionsPeripheralCymbalsFilter', showKey: 'showPeripheralCymbals' },
  { key: 'peripheral_drums',   label: 'Pro Drums',           filterKey: 'suggestionsPeripheralDrumsFilter',   showKey: 'showPeripheralDrums' },
];

const BAND_SUGGESTION_TYPE_IDS: SuggestionTypeId[] = ['NearFC', 'StarProgress', 'Unplayed', 'PercentilePush', 'PctImprove', 'Stale'];

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

type InstrumentVisibility = {
  showLead: boolean;
  showBass: boolean;
  showDrums: boolean;
  showVocals: boolean;
  showProLead: boolean;
  showProBass: boolean;
  showPeripheralVocals: boolean;
  showPeripheralCymbals: boolean;
  showPeripheralDrums: boolean;
};

type BandComboFilterProps = {
  selectedBand: SelectedBandProfile;
  appliedAssignments: readonly BandInstrumentFilterAssignment[];
  onApply: (payload: BandInstrumentFilterApplyPayload) => void;
  onReset: () => void;
};

type SuggestionsModalDraftState = {
  suggestions: SuggestionsFilterDraft;
  bandCombo: readonly (ServerInstrumentKey | null)[];
};

type SuggestionsFilterModalProps = {
  visible: boolean;
  draft: SuggestionsFilterDraft;
  savedDraft?: SuggestionsFilterDraft;
  mode?: 'solo' | 'band';
  instrumentVisibility: InstrumentVisibility;
  bandComboFilter?: BandComboFilterProps;
  onChange: (d: SuggestionsFilterDraft) => void;
  onCancel: () => void;
  onReset: () => void;
  onApply: () => void;
};

const noopBandApply = () => {};
const noopBandReset = () => {};

export default function SuggestionsFilterModal({ visible, draft, savedDraft, mode = 'solo', instrumentVisibility, bandComboFilter, onChange, onCancel, onReset, onApply }: SuggestionsFilterModalProps) {
  const { t } = useTranslation();
  const [selectedInstrument, setSelectedInstrument] = useState<InstrumentKey | null>(null);
  useEffect(() => { if (visible) setSelectedInstrument(null); }, [visible]);

  const showBandComboSection = mode === 'band' && !!bandComboFilter;
  const bandComboController = useBandInstrumentFilterController({
    visible: visible && showBandComboSection,
    selectedBand: bandComboFilter?.selectedBand ?? null,
    appliedAssignments: bandComboFilter?.appliedAssignments ?? [],
    onApply: bandComboFilter?.onApply ?? noopBandApply,
    onReset: bandComboFilter?.onReset ?? noopBandReset,
  });
  const showInstrumentControls = mode === 'solo';
  const visibleInstruments = INSTRUMENTS.filter(i => instrumentVisibility[i.showKey as keyof InstrumentVisibility]);
  const visibleSuggestionTypes = mode === 'band'
    ? SUGGESTION_TYPES.filter(type => BAND_SUGGESTION_TYPE_IDS.includes(type.id))
    : SUGGESTION_TYPES;
  const effectiveSelectedInstrument = selectedInstrument && visibleInstruments.some(i => i.key === selectedInstrument) ? selectedInstrument : null;

  const selectorItems = useMemo<InstrumentSelectorItem<InstrumentKey>[]>(
    () => visibleInstruments.map(i => ({ key: i.key, label: i.label })),
    [visibleInstruments],
  );

  const handleInstrumentSelect = useCallback((key: InstrumentKey | null) => {
    setSelectedInstrument(key);
  }, []);

  const toggle = (key: string) => onChange({ ...draft, [key]: !draft[key] });

  const modalDraft = useMemo<SuggestionsModalDraftState>(() => ({
    suggestions: draft,
    bandCombo: showBandComboSection ? bandComboController.draft : [],
  }), [bandComboController.draft, draft, showBandComboSection]);
  const modalSavedDraft = useMemo<SuggestionsModalDraftState | undefined>(() => (
    savedDraft ? {
      suggestions: savedDraft,
      bandCombo: showBandComboSection ? bandComboController.savedDraft : [],
    } : undefined
  ), [bandComboController.savedDraft, savedDraft, showBandComboSection]);

  const { hasChanges, confirmOpen, setConfirmOpen, handleClose } = useModalDraft(modalDraft, modalSavedDraft, onCancel, areSuggestionsModalDraftsEqual);
  const bandComboBlocksApply = showBandComboSection && bandComboController.hasChanges && bandComboController.applyDisabled;

  const handleReset = useCallback(() => {
    onReset();
    if (showBandComboSection) bandComboController.resetDraft();
  }, [bandComboController, onReset, showBandComboSection]);

  const handleApply = useCallback(() => {
    if (showBandComboSection && bandComboController.hasChanges && !bandComboController.apply()) return;
    onApply();
  }, [bandComboController, onApply, showBandComboSection]);

  const toggleGlobal = (typeId: SuggestionTypeId) => {
    const gk = globalKeyFor(typeId);
    const turningOff = draft[gk];
    const updates: Record<string, boolean> = { [gk]: !turningOff };
    for (const inst of visibleInstruments) {
      updates[perInstrumentKeyFor(inst.key, typeId)] = !turningOff;
    }
    onChange({ ...draft, ...updates });
  };

  const togglePerInstrument = (instrument: InstrumentKey, typeId: SuggestionTypeId) => {
    const key = perInstrumentKeyFor(instrument, typeId);
    const gk = globalKeyFor(typeId);
    const turningOn = !draft[key];
    const updates: Record<string, boolean> = { [key]: turningOn };
    if (turningOn && !draft[gk]) {
      updates[gk] = true;
    } else if (!turningOn) {
      const allOff = visibleInstruments.every(inst => {
        const pk = perInstrumentKeyFor(inst.key, typeId);
        return pk === key ? true : !draft[pk];
      });
      if (allOff) updates[gk] = false;
    }
    onChange({ ...draft, ...updates });
  };

  return (
    <Modal visible={visible} title={t('common.filterSuggestions')} onClose={handleClose} onApply={handleApply} onReset={handleReset} resetLabel={t('suggestionsFilter.resetLabel')} resetHint={t('suggestionsFilter.resetHint')} applyLabel={t('filter.applyLabel')} applyDisabled={!hasChanges || bandComboBlocksApply} afterPanel={showBandComboSection && bandComboController.pendingInvalidSelection ? (
        <BandInstrumentFilterInvalidSelectionAlert controller={bandComboController} />
      ) : confirmOpen ? (
        <ConfirmAlert
          title={t('suggestionsFilter.cancelTitle')}
          message={t('suggestionsFilter.cancelMessage')}
          onNo={() => setConfirmOpen(false)}
          onYes={onCancel}
          onExitComplete={() => setConfirmOpen(false)}
        />
      ) : null}>
      {showBandComboSection ? (
        <BandInstrumentFilterPicker controller={bandComboController} compact />
      ) : null}

      {/* Instruments */}
      {showInstrumentControls && <ModalSection>
        <Accordion title={t('suggestionsFilter.instruments')} hint={t('suggestionsFilter.instrumentsHint')}>
          {visibleInstruments.map(inst => (
            <ToggleRow
              key={inst.key}
              label={
                <span style={filterStyles.instrumentLabel}>
                  <InstrumentIcon instrument={inst.key} size={Size.iconTab} />
                  {inst.label}
                </span>
              }
              checked={!!draft[inst.filterKey]}
              onToggle={() => toggle(inst.filterKey as string)}
            />
          ))}
        </Accordion>
      </ModalSection>}

      {/* General type toggles */}
      <ModalSection>
        <Accordion title={t('suggestionsFilter.general')} hint={t('suggestionsFilter.generalHint')}>
          {visibleSuggestionTypes.map(st => (
            <ToggleRow
              key={st.id}
              label={t(`suggestionFilterType.${st.id}`)}
              description={t(`suggestionFilterType.${st.id}Desc`)}
              checked={!!draft[globalKeyFor(st.id)]}
              onToggle={() => toggleGlobal(st.id)}
            />
          ))}
        </Accordion>
      </ModalSection>

      {/* Instrument-specific type toggles */}
      {showInstrumentControls && <ModalSection title={t('suggestionsFilter.instrumentSpecific')} hint={t('suggestionsFilter.instrumentSpecificHint')}>
        <InstrumentSelector<InstrumentKey>
          instruments={selectorItems}
          selected={effectiveSelectedInstrument}
          onSelect={handleInstrumentSelect}
          deferSelection
        >
          {visibleSuggestionTypes.map(st => {
            const inst = effectiveSelectedInstrument ?? visibleInstruments[0]?.key ?? 'guitar';
            const key = perInstrumentKeyFor(inst, st.id);
            return (
              <ToggleRow
                key={st.id}
                label={t(`suggestionFilterType.${st.id}`)}
                description={t(`suggestionFilterType.${st.id}Desc`)}
                checked={!!draft[key]}
                onToggle={() => { if (effectiveSelectedInstrument) togglePerInstrument(effectiveSelectedInstrument, st.id); }}
              />
            );
          })}
        </InstrumentSelector>
      </ModalSection>}
    </Modal>
  );
}

function areSuggestionsModalDraftsEqual(a: SuggestionsModalDraftState, b: SuggestionsModalDraftState) {
  return JSON.stringify(a.suggestions) === JSON.stringify(b.suggestions)
    && areBandInstrumentDraftsEqual(a.bandCombo, b.bandCombo);
}

