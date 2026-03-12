import { useState, useMemo, useCallback } from 'react';
import Modal, { ModalSection, ToggleRow, Accordion } from './Modal';
import ConfirmAlert from './ConfirmAlert';
import { InstrumentIcon } from './InstrumentIcons';
import type { InstrumentKey } from '@festival/core/instruments';
import { InstrumentKeys } from '@festival/core/instruments';
import {
  SUGGESTION_TYPES,
  globalKeyFor,
  perInstrumentKeyFor,
} from '@festival/core/suggestions/suggestionFilterConfig';
import type { SuggestionTypeId } from '@festival/core/suggestions/suggestionFilterConfig';
import { Colors, Font, Gap } from '../theme';

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

type Props = {
  visible: boolean;
  draft: SuggestionsFilterDraft;
  savedDraft?: SuggestionsFilterDraft;
  instrumentVisibility: InstrumentVisibility;
  onChange: (d: SuggestionsFilterDraft) => void;
  onCancel: () => void;
  onReset: () => void;
  onApply: () => void;
};

export default function SuggestionsFilterModal({ visible, draft, savedDraft, instrumentVisibility, onChange, onCancel, onReset, onApply }: Props) {
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
                <span style={localStyles.instrumentLabel}>
                  <InstrumentIcon instrument={inst.key} size={20} />
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
        <Accordion title="General" hint="Toggle broad suggestion types on or off." defaultOpen>
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
        <div style={localStyles.instrumentRow}>
          {visibleInstruments.map(inst => {
            const isSelected = effectiveSelectedInstrument === inst.key;
            return (
              <button
                key={inst.key}
                style={localStyles.instrumentBtn}
                onClick={() => setSelectedInstrument(cur => cur === inst.key ? null : inst.key)}
                title={inst.label}
              >
                <div style={{ ...localStyles.instrumentCircle, ...(isSelected ? localStyles.instrumentCircleActive : {}) }} />
                <div style={{ position: 'relative' as const, zIndex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
                  <InstrumentIcon instrument={inst.key} size={48} />
                </div>
              </button>
            );
          })}
        </div>

        <div style={{
          display: 'grid',
          gridTemplateRows: effectiveSelectedInstrument ? '1fr' : '0fr',
          transition: 'grid-template-rows 400ms ease',
        }}>
          <div style={{ overflow: 'hidden', minHeight: 0 }}>
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

// ---------------------------------------------------------------------------
// Local styles
// ---------------------------------------------------------------------------

const localStyles: Record<string, React.CSSProperties> = {
  instrumentRow: {
    display: 'flex',
    flexWrap: 'wrap',
    justifyContent: 'center',
    gap: Gap.md,
    marginBottom: Gap.md,
  },
  instrumentBtn: {
    width: 64,
    height: 64,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    borderRadius: '50%',
    border: 'none',
    backgroundColor: 'transparent',
    cursor: 'pointer',
    padding: 0,
    position: 'relative' as const,
    overflow: 'hidden' as const,
  },
  instrumentCircle: {
    position: 'absolute' as const,
    inset: 0,
    borderRadius: '50%',
    backgroundColor: '#2ECC71',
    transform: 'scale(0)',
    transition: 'transform 250ms ease',
  },
  instrumentCircleActive: {
    transform: 'scale(1)',
  },
  instrumentLabel: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: Gap.md,
  },
};

