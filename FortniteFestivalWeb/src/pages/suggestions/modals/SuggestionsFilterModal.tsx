/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useState, useMemo, useCallback } from 'react';
import Modal from '../../../components/modals/Modal';
import { ModalSection } from '../../../components/modals/components/ModalSection';
import { ToggleRow } from '../../../components/common/ToggleRow';
import { Accordion } from '../../../components/common/Accordion';
import ConfirmAlert from '../../../components/modals/ConfirmAlert';
import { InstrumentIcon } from '../../../components/display/InstrumentIcons';
import type { InstrumentKey } from '@festival/core/instruments';
import { InstrumentKeys } from '@festival/core/instruments';
import {
  SUGGESTION_TYPES,
  globalKeyFor,
  perInstrumentKeyFor,
} from '@festival/core/suggestions/suggestionFilterConfig';
import type { SuggestionTypeId } from '@festival/core/suggestions/suggestionFilterConfig';
import { Size } from '@festival/theme';
import filterCss from '../../songs/modals/FilterModal.module.css';

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
  const [selectedInstrument, setSelectedInstrument] = useState<InstrumentKey | null>(null);

  const visibleInstruments = INSTRUMENTS.filter(i => instrumentVisibility[i.showKey as keyof InstrumentVisibility]);
  const effectiveSelectedInstrument = selectedInstrument && visibleInstruments.some(i => i.key === selectedInstrument) ? selectedInstrument : null;

  const toggle = (key: string) => onChange({ ...draft, [key]: !draft[key] });

  const hasChanges = useMemo(() => {
    if (!savedDraft) return true;
    return JSON.stringify(draft) !== JSON.stringify(savedDraft);
  }, [draft, savedDraft]);

  const [confirmOpen, setConfirmOpen] = useState(false);
  const handleClose = useCallback(() => {
    if (hasChanges) {
      setConfirmOpen(true);
    } else {
      onCancel();
    }
  }, [hasChanges, onCancel]);
  const confirmDiscard = useCallback(() => {
    setConfirmOpen(false);
    onCancel();
  }, [onCancel]);

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
    <>
    <Modal visible={visible} title="Filter Suggestions" onClose={handleClose} onApply={onApply} onReset={onReset} resetLabel="Reset Suggestion Filters" resetHint="Restore all suggestion filter options to their defaults." applyLabel="Apply Filter Changes" applyDisabled={!hasChanges}>
      {/* Instruments */}
      <ModalSection>
        <Accordion title="Instruments" hint="Choose which instruments appear in your suggestions.">
          {visibleInstruments.map(inst => (
            <ToggleRow
              key={inst.key}
              label={
                <span className={filterCss.instrumentLabel}>
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
        <Accordion title="General" hint="Toggle broad suggestion types on or off.">
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
      <ModalSection title="Instrument-Specific" hint="Select an instrument to filter its suggestion types individually.">
        <div className={filterCss.instrumentRow}>
          {visibleInstruments.map(inst => {
            const isSelected = effectiveSelectedInstrument === inst.key;
            return (
              <button
                key={inst.key}
                className={filterCss.instrumentBtn}
                onClick={() => setSelectedInstrument(cur => cur === inst.key ? null : inst.key)}
                title={inst.label}
              >
                <div className={isSelected ? filterCss.instrumentCircleActive : filterCss.instrumentCircle} />
                <div className={filterCss.instrumentIconWrap}>
                  <InstrumentIcon instrument={inst.key} size={Size.iconInstrument} />
                </div>
              </button>
            );
          })}
        </div>

        <div className={filterCss.instrumentFiltersWrap} style={{ gridTemplateRows: effectiveSelectedInstrument ? '1fr' : '0fr' }}>
          <div className={filterCss.instrumentFiltersInner}>
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
          </div>
        </div>
      </ModalSection>
    </Modal>

      {confirmOpen && (
        <ConfirmAlert
          title="Cancel Suggestion Filter Changes"
          message="Are you sure you want to discard your suggestion filter changes?"
          onNo={() => setConfirmOpen(false)}
          onYes={confirmDiscard}
        />
      )}
    </>
  );
}

