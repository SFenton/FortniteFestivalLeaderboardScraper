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
import { useModalDraft } from '../../../hooks/ui/useModalDraft';
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
// Filter draft type â€“ mirrors the RN SuggestionsInstrumentFilters
// ---------------------------------------------------------------------------

export type SuggestionsFilterDraft = {
  suggestionsLeadFilter: boolean;
  suggestionsBassFilter: boolean;
  suggestionsDrumsFilter: boolean;
  suggestionsVocalsFilter: boolean;
  suggestionsProLeadFilter: boolean;
  suggestionsProBassFilter: boolean;
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
  };
  for (const { id } of SUGGESTION_TYPES) {
    d[globalKeyFor(id)] = true;
    for (const inst of InstrumentKeys) {
      d[perInstrumentKeyFor(inst, id)] = true;
    }
  }
  return d as SuggestionsFilterDraft;
}

export function isSuggestionsFilterActive(draft: SuggestionsFilterDraft): boolean {
  const defaults = defaultSuggestionsFilterDraft();
  return Object.keys(defaults).some(k => (draft[k] ?? defaults[k]) !== defaults[k]);
}

// ---------------------------------------------------------------------------
// Instrument picker config
// ---------------------------------------------------------------------------

const INSTRUMENTS: { key: InstrumentKey; label: string; filterKey: keyof SuggestionsFilterDraft; showKey: string }[] = [
  { key: 'guitar',     label: 'Lead',     filterKey: 'suggestionsLeadFilter',    showKey: 'showLead' },
  { key: 'bass',       label: 'Bass',     filterKey: 'suggestionsBassFilter',    showKey: 'showBass' },
  { key: 'drums',      label: 'Drums',    filterKey: 'suggestionsDrumsFilter',   showKey: 'showDrums' },
  { key: 'vocals',     label: 'Vocals',   filterKey: 'suggestionsVocalsFilter',  showKey: 'showVocals' },
  { key: 'pro_guitar', label: 'Pro Lead', filterKey: 'suggestionsProLeadFilter', showKey: 'showProLead' },
  { key: 'pro_bass',   label: 'Pro Bass', filterKey: 'suggestionsProBassFilter', showKey: 'showProBass' },
];

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
};

type SuggestionsFilterModalProps = {
  visible: boolean;
  draft: SuggestionsFilterDraft;
  savedDraft?: SuggestionsFilterDraft;
  instrumentVisibility: InstrumentVisibility;
  onChange: (d: SuggestionsFilterDraft) => void;
  onCancel: () => void;
  onReset: () => void;
  onApply: () => void;
};

export default function SuggestionsFilterModal({ visible, draft, savedDraft, instrumentVisibility, onChange, onCancel, onReset, onApply }: SuggestionsFilterModalProps) {
  const { t } = useTranslation();
  const [selectedInstrument, setSelectedInstrument] = useState<InstrumentKey | null>(null);
  useEffect(() => { if (visible) setSelectedInstrument(null); }, [visible]);

  const visibleInstruments = INSTRUMENTS.filter(i => instrumentVisibility[i.showKey as keyof InstrumentVisibility]);
  const effectiveSelectedInstrument = selectedInstrument && visibleInstruments.some(i => i.key === selectedInstrument) ? selectedInstrument : null;

  const selectorItems = useMemo<InstrumentSelectorItem<InstrumentKey>[]>(
    () => visibleInstruments.map(i => ({ key: i.key, label: i.label })),
    [visibleInstruments],
  );

  const handleInstrumentSelect = useCallback((key: InstrumentKey | null) => {
    setSelectedInstrument(key);
  }, []);

  const toggle = (key: string) => onChange({ ...draft, [key]: !draft[key] });

  const { hasChanges, confirmOpen, setConfirmOpen, handleClose, confirmDiscard } = useModalDraft(draft, savedDraft, onCancel);

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
    <Modal visible={visible} title={t('common.filterSuggestions')} onClose={handleClose} onApply={onApply} onReset={onReset} resetLabel={t('suggestionsFilter.resetLabel')} resetHint={t('suggestionsFilter.resetHint')} applyLabel={t('filter.applyLabel')} applyDisabled={!hasChanges} afterPanel={confirmOpen ? (
        <ConfirmAlert
          title={t('suggestionsFilter.cancelTitle')}
          message={t('suggestionsFilter.cancelMessage')}
          onNo={() => setConfirmOpen(false)}
          onYes={onCancel}
          onExitComplete={() => setConfirmOpen(false)}
        />
      ) : null}>
      {/* Instruments */}
      <ModalSection>
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
      </ModalSection>

      {/* General type toggles */}
      <ModalSection>
        <Accordion title={t('suggestionsFilter.general')} hint={t('suggestionsFilter.generalHint')}>
          {SUGGESTION_TYPES.map(st => (
            <ToggleRow
              key={st.id}
              label={st.label}
              description={st.description}
              checked={!!draft[globalKeyFor(st.id)]}
              onToggle={() => toggleGlobal(st.id)}
            />
          ))}
        </Accordion>
      </ModalSection>

      {/* Instrument-specific type toggles */}
      <ModalSection title={t('suggestionsFilter.instrumentSpecific')} hint={t('suggestionsFilter.instrumentSpecificHint')}>
        <InstrumentSelector<InstrumentKey>
          instruments={selectorItems}
          selected={effectiveSelectedInstrument}
          onSelect={handleInstrumentSelect}
          deferSelection
        >
          {effectiveSelectedInstrument && (
            SUGGESTION_TYPES.map(st => {
              const key = perInstrumentKeyFor(effectiveSelectedInstrument, st.id);
              return (
                <ToggleRow
                  key={st.id}
                  label={st.label}
                  description={st.description}
                  checked={!!draft[key]}
                  onToggle={() => togglePerInstrument(effectiveSelectedInstrument, st.id)}
                />
              );
            })
          )}
        </InstrumentSelector>
      </ModalSection>
    </Modal>
  );
}

