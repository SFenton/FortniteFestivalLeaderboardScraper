import { useState } from 'react';
import Modal, { ModalSection, ToggleRow } from './Modal';
import { InstrumentIcon } from './InstrumentIcons';
import type { InstrumentKey } from '@festival/core/instruments';
import { InstrumentKeys } from '@festival/core/instruments';
import {
  SUGGESTION_TYPES,
  globalKeyFor,
  perInstrumentKeyFor,
} from '@festival/core/suggestions/suggestionFilterConfig';
import type { SuggestionTypeId } from '@festival/core/suggestions/suggestionFilterConfig';
import { Colors, Gap, Radius } from '../theme';

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

const INSTRUMENTS: { key: InstrumentKey; label: string; filterKey: keyof SuggestionsFilterDraft }[] = [
  { key: 'guitar',     label: 'Lead',     filterKey: 'suggestionsLeadFilter' },
  { key: 'bass',       label: 'Bass',     filterKey: 'suggestionsBassFilter' },
  { key: 'drums',      label: 'Drums',    filterKey: 'suggestionsDrumsFilter' },
  { key: 'vocals',     label: 'Vocals',   filterKey: 'suggestionsVocalsFilter' },
  { key: 'pro_guitar', label: 'Pro Lead', filterKey: 'suggestionsProLeadFilter' },
  { key: 'pro_bass',   label: 'Pro Bass', filterKey: 'suggestionsProBassFilter' },
];

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

type Props = {
  visible: boolean;
  draft: SuggestionsFilterDraft;
  onChange: (d: SuggestionsFilterDraft) => void;
  onCancel: () => void;
  onReset: () => void;
  onApply: () => void;
};

export default function SuggestionsFilterModal({ visible, draft, onChange, onCancel, onReset, onApply }: Props) {
  const [selectedInstrument, setSelectedInstrument] = useState<InstrumentKey | null>(null);

  const toggle = (key: string) => onChange({ ...draft, [key]: !draft[key] });

  const toggleGlobal = (typeId: SuggestionTypeId) => {
    const gk = globalKeyFor(typeId);
    const turningOff = draft[gk];
    const updates: Record<string, boolean> = { [gk]: !turningOff };
    for (const inst of INSTRUMENTS) {
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
      const allOff = INSTRUMENTS.every(inst => {
        const pk = perInstrumentKeyFor(inst.key, typeId);
        return pk === key ? true : !draft[pk];
      });
      if (allOff) updates[gk] = false;
    }
    onChange({ ...draft, ...updates });
  };

  return (
    <Modal visible={visible} title="Filter Suggestions" onClose={onCancel} onApply={onApply} onReset={onReset}>
      {/* Instruments */}
      <ModalSection title="Instruments" hint="Choose which instruments appear in your suggestions.">
        {INSTRUMENTS.map(inst => (
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
      </ModalSection>

      {/* General type toggles */}
      <ModalSection title="General" hint="Toggle broad suggestion types on or off.">
        {SUGGESTION_TYPES.map(st => (
          <ToggleRow
            key={st.id}
            label={st.label}
            description={st.description}
            checked={!!draft[globalKeyFor(st.id)]}
            onToggle={() => toggleGlobal(st.id)}
          />
        ))}
      </ModalSection>

      {/* Instrument-specific type toggles */}
      <ModalSection title="Instrument-Specific" hint="These filters will filter out suggestions on a per-instrument basis, rather than global.">
        <div style={localStyles.instrumentRow}>
          {INSTRUMENTS.map(inst => {
            const isSelected = selectedInstrument === inst.key;
            return (
              <button
                key={inst.key}
                style={{
                  ...localStyles.instrumentBtn,
                  ...(isSelected ? localStyles.instrumentBtnSelected : {}),
                }}
                onClick={() => setSelectedInstrument(cur => cur === inst.key ? null : inst.key)}
                title={inst.label}
              >
                <InstrumentIcon instrument={inst.key} size={24} />
              </button>
            );
          })}
        </div>

        {selectedInstrument && (
          <div style={{ marginTop: Gap.lg }}>
            {SUGGESTION_TYPES.map(st => {
              const key = perInstrumentKeyFor(selectedInstrument, st.id);
              return (
                <ToggleRow
                  key={st.id}
                  label={st.label}
                  description={st.description}
                  checked={!!draft[key]}
                  onToggle={() => togglePerInstrument(selectedInstrument, st.id)}
                />
              );
            })}
          </div>
        )}
      </ModalSection>
    </Modal>
  );
}

// ---------------------------------------------------------------------------
// Local styles
// ---------------------------------------------------------------------------

const localStyles: Record<string, React.CSSProperties> = {
  instrumentRow: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: Gap.md,
    marginBottom: Gap.md,
  },
  instrumentBtn: {
    width: 44,
    height: 44,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    borderRadius: Radius.sm,
    backgroundColor: Colors.surfaceSubtle,
    border: `1px solid ${Colors.borderSubtle}`,
    cursor: 'pointer',
    padding: 0,
  },
  instrumentBtnSelected: {
    backgroundColor: Colors.chipSelectedBgSubtle,
    borderColor: Colors.accentBlue,
  },
  instrumentLabel: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: Gap.md,
  },
};
