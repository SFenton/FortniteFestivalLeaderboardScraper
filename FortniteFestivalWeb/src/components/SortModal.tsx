import Modal, { ModalSection, RadioRow, ChoicePill, ReorderList, Accordion } from './Modal';
import type { InstrumentKey } from '../models';
import { INSTRUMENT_LABELS } from '../models';
import type { SongSortMode } from './songSettings';
import { INSTRUMENT_SORT_MODES, METADATA_SORT_DISPLAY } from './songSettings';

export type SortDraft = {
  sortMode: SongSortMode;
  sortAscending: boolean;
  metadataOrder: string[];
  instrumentOrder: InstrumentKey[];
};

export type MetadataVisibility = {
  score: boolean;
  percentage: boolean;
  percentile: boolean;
  seasonachieved: boolean;
  intensity: boolean;
  isfc: boolean;
  stars: boolean;
};

type Props = {
  visible: boolean;
  draft: SortDraft;
  instrumentFilter: InstrumentKey | null;
  metadataVisibility?: MetadataVisibility;
  onChange: (d: SortDraft) => void;
  onCancel: () => void;
  onReset: () => void;
  onApply: () => void;
};

export default function SortModal({ visible, draft, instrumentFilter, metadataVisibility: mv, onChange, onCancel, onReset, onApply }: Props) {
  const setMode = (sortMode: SongSortMode) => onChange({ ...draft, sortMode });

  const visibleInstrumentSortModes = mv
    ? INSTRUMENT_SORT_MODES.filter(({ mode }) => {
        const visMap: Record<string, boolean> = {
          score: mv.score, percentage: mv.percentage, percentile: mv.percentile,
          isfc: mv.isfc, stars: mv.stars, seasonachieved: mv.seasonachieved, intensity: mv.intensity,
        };
        return visMap[mode] !== false;
      })
    : INSTRUMENT_SORT_MODES;

  const visibleMetadataOrder = mv
    ? draft.metadataOrder.filter(k => {
        const visMap: Record<string, boolean> = {
          score: mv.score, percentage: mv.percentage, percentile: mv.percentile,
          isfc: mv.isfc, stars: mv.stars, seasonachieved: mv.seasonachieved, intensity: mv.intensity,
        };
        return visMap[k] !== false;
      })
    : draft.metadataOrder;

  const anyMetadataVisible = !mv || (mv.score || mv.percentage || mv.percentile || mv.isfc || mv.stars || mv.seasonachieved || mv.intensity);

  return (
    <Modal visible={visible} title="Sort Songs" onClose={onCancel} onApply={onApply} onReset={onReset}>
      {/* Primary sort mode */}
      <ModalSection title="Mode" hint="Choose which property to sort the song list by.">
        <RadioRow label="Title" selected={draft.sortMode === 'title'} onSelect={() => setMode('title')} />
        <RadioRow label="Artist" selected={draft.sortMode === 'artist'} onSelect={() => setMode('artist')} />
        <RadioRow label="Year" selected={draft.sortMode === 'year'} onSelect={() => setMode('year')} />
        <RadioRow label="Has FC" selected={draft.sortMode === 'hasfc'} onSelect={() => setMode('hasfc')} />
      </ModalSection>

      {/* Instrument-specific sort modes (only when an instrument is selected) */}
      {instrumentFilter != null && visibleInstrumentSortModes.length > 0 && (
        <ModalSection title="Filtered Instrument Sort Mode" hint="Filtering to a single instrument enables more sort options.">
          {visibleInstrumentSortModes.map(({ mode, label }) => (
            <RadioRow key={mode} label={label} selected={draft.sortMode === mode} onSelect={() => setMode(mode)} />
          ))}
        </ModalSection>
      )}

      {/* Direction */}
      <ModalSection title="Direction" hint="Choose whether to sort ascending (A–Z, low–high) or descending (Z–A, high–low).">
        <div style={{ display: 'flex', gap: 8 }}>
          <ChoicePill label="Ascending" selected={draft.sortAscending} onSelect={() => onChange({ ...draft, sortAscending: true })} />
          <ChoicePill label="Descending" selected={!draft.sortAscending} onSelect={() => onChange({ ...draft, sortAscending: false })} />
        </div>
      </ModalSection>

      {/* Metadata sort priority (only when an instrument is selected) */}
      {instrumentFilter != null && anyMetadataVisible && (
        <ModalSection title="Metadata Sort Priority" hint="When scores are tied, metadata is compared in this order to break the tie.">
          <ReorderList
            items={visibleMetadataOrder.map(k => ({ key: k, label: METADATA_SORT_DISPLAY[k] ?? k }))}
            onReorder={(items) => onChange({ ...draft, metadataOrder: items.map(i => i.key) })}
          />
        </ModalSection>
      )}

      {/* Primary Instrument Order (only when NO instrument is selected and sorting by Has FC) */}
      {instrumentFilter == null && draft.sortMode === 'hasfc' && (
        <Accordion title="Primary Instrument Order" hint="Instruments are checked in this order when sorting by Has FC." defaultOpen>
          <ReorderList
            items={draft.instrumentOrder.map(k => ({ key: k, label: INSTRUMENT_LABELS[k] }))}
            onReorder={(items) => onChange({ ...draft, instrumentOrder: items.map(i => i.key) as InstrumentKey[] })}
          />
        </Accordion>
      )}
    </Modal>
  );
}
