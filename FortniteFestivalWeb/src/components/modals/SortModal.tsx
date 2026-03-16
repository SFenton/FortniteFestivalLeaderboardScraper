import { useMemo, useCallback, useState } from 'react';
import Modal, { ModalSection, RadioRow, ReorderList, Accordion } from './Modal';
import ConfirmAlert from './ConfirmAlert';
import type { InstrumentKey } from '../../models';
import { INSTRUMENT_LABELS } from '../../models';
import type { SongSortMode } from '../songSettings';
import { INSTRUMENT_SORT_MODES, METADATA_SORT_DISPLAY } from '../songSettings';
import { Colors, Font, Gap } from '@festival/theme';
import { IoArrowUp, IoArrowDown } from 'react-icons/io5';

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
  stars: boolean;
};

type Props = {
  visible: boolean;
  draft: SortDraft;
  savedDraft?: SortDraft;
  instrumentFilter: InstrumentKey | null;
  hasPlayer?: boolean;
  metadataVisibility?: MetadataVisibility;
  onChange: (d: SortDraft) => void;
  onCancel: () => void;
  onReset: () => void;
  onApply: () => void;
};

export default function SortModal({ visible, draft, savedDraft, instrumentFilter, hasPlayer, metadataVisibility: mv, onChange, onCancel, onReset, onApply }: Props) {
  const setMode = (sortMode: SongSortMode) => onChange({ ...draft, sortMode });

  const hasChanges = useMemo(() => {
    if (!savedDraft) return true;
    return draft.sortMode !== savedDraft.sortMode
      || draft.sortAscending !== savedDraft.sortAscending
      || JSON.stringify(draft.metadataOrder) !== JSON.stringify(savedDraft.metadataOrder)
      || JSON.stringify(draft.instrumentOrder) !== JSON.stringify(savedDraft.instrumentOrder);
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

  const visibleInstrumentSortModes = mv
    ? INSTRUMENT_SORT_MODES.filter(({ mode }) => {
        const visMap: Record<string, boolean> = {
          score: mv.score, percentage: mv.percentage, percentile: mv.percentile,
          stars: mv.stars, seasonachieved: mv.seasonachieved, intensity: mv.intensity,
        };
        return visMap[mode] !== false;
      })
    : INSTRUMENT_SORT_MODES;

  const visibleMetadataOrder = mv
    ? draft.metadataOrder.filter(k => {
        const visMap: Record<string, boolean> = {
          score: mv.score, percentage: mv.percentage, percentile: mv.percentile,
          stars: mv.stars, seasonachieved: mv.seasonachieved, intensity: mv.intensity,
        };
        return visMap[k] !== false;
      })
    : draft.metadataOrder;

  const anyMetadataVisible = !mv || (mv.score || mv.percentage || mv.percentile || mv.stars || mv.seasonachieved || mv.intensity);

  return (
    <>
    <Modal visible={visible} title="Sort Songs" onClose={handleClose} onApply={onApply} onReset={onReset} resetLabel="Reset Sort Settings" resetHint="Restore sort mode, direction, and metadata priority to their defaults." applyLabel="Apply Sort Changes" applyDisabled={!hasChanges}>
      {/* Primary sort mode */}
      {hasPlayer ? (
        <ModalSection>
          <Accordion title="Mode" hint="Choose which property to sort the song list by." defaultOpen={!instrumentFilter}>
            <RadioRow label="Title" selected={draft.sortMode === 'title'} onSelect={() => setMode('title')} />
            <RadioRow label="Artist" selected={draft.sortMode === 'artist'} onSelect={() => setMode('artist')} />
            <RadioRow label="Year" selected={draft.sortMode === 'year'} onSelect={() => setMode('year')} />
            <RadioRow label="Has FC" selected={draft.sortMode === 'hasfc'} onSelect={() => setMode('hasfc')} />
          </Accordion>
        </ModalSection>
      ) : (
        <ModalSection title="Mode" hint="Choose which property to sort the song list by.">
          <RadioRow label="Title" selected={draft.sortMode === 'title'} onSelect={() => setMode('title')} />
          <RadioRow label="Artist" selected={draft.sortMode === 'artist'} onSelect={() => setMode('artist')} />
          <RadioRow label="Year" selected={draft.sortMode === 'year'} onSelect={() => setMode('year')} />
          <RadioRow label="Has FC" selected={draft.sortMode === 'hasfc'} onSelect={() => setMode('hasfc')} />
        </ModalSection>
      )}

      {/* Instrument-specific sort modes (only when an instrument is selected) */}
      {instrumentFilter != null && visibleInstrumentSortModes.length > 0 && (
        <ModalSection>
          <Accordion title="Filtered Instrument Sort Mode" hint="Filtering to a single instrument enables more sort options.">
            {visibleInstrumentSortModes.map(({ mode, label }) => (
              <RadioRow key={mode} label={label} selected={draft.sortMode === mode} onSelect={() => setMode(mode)} />
            ))}
          </Accordion>
        </ModalSection>
      )}

      {/* Direction */}
      <ModalSection>
        <div style={directionStyles.inner}>
          <div style={directionStyles.textCol}>
            <div style={directionStyles.title}>Sort Direction</div>
            <div style={directionStyles.hint}>
              {draft.sortAscending ? 'Ascending (A–Z, low–high)' : 'Descending (Z–A, high–low)'}
            </div>
          </div>
          <div style={directionStyles.icons}>
            <button
              style={directionStyles.iconBtn}
              onClick={() => onChange({ ...draft, sortAscending: true })}
              aria-label="Ascending"
            >
              <div style={{ ...directionStyles.iconCircle, ...(draft.sortAscending ? directionStyles.iconCircleActive : {}) }} />
              <IoArrowUp size={20} style={{ position: 'relative' as const, zIndex: 1, color: draft.sortAscending ? Colors.textPrimary : Colors.textMuted, transition: 'color 200ms ease' }} />
            </button>
            <button
              style={directionStyles.iconBtn}
              onClick={() => onChange({ ...draft, sortAscending: false })}
              aria-label="Descending"
            >
              <div style={{ ...directionStyles.iconCircle, ...(!draft.sortAscending ? directionStyles.iconCircleActive : {}) }} />
              <IoArrowDown size={20} style={{ position: 'relative' as const, zIndex: 1, color: !draft.sortAscending ? Colors.textPrimary : Colors.textMuted, transition: 'color 200ms ease' }} />
            </button>
          </div>
        </div>
      </ModalSection>

      {/* Metadata sort priority (only when an instrument is selected) */}
      {instrumentFilter != null && anyMetadataVisible && (
        <ModalSection title="Metadata Sort Priority" hint="When two songs have the same value for the selected sort mode, songs are sorted by comparing these properties in order from top to bottom.">
          <ReorderList
            items={visibleMetadataOrder.map(k => ({ key: k, label: METADATA_SORT_DISPLAY[k] ?? k }))}
            onReorder={(items) => onChange({ ...draft, metadataOrder: items.map(i => i.key) })}
          />
        </ModalSection>
      )}

      {/* Primary Instrument Order (only when NO instrument is selected and sorting by Has FC) */}
      {instrumentFilter == null && draft.sortMode === 'hasfc' && (
        <ModalSection>
          <Accordion title="Primary Instrument Order" hint="Instruments are checked in this order when sorting by Has FC." defaultOpen>
            <ReorderList
              items={draft.instrumentOrder.map(k => ({ key: k, label: INSTRUMENT_LABELS[k] }))}
              onReorder={(items) => onChange({ ...draft, instrumentOrder: items.map(i => i.key) as InstrumentKey[] })}
            />
          </Accordion>
        </ModalSection>
      )}
    </Modal>

      {confirmOpen && (
        <ConfirmAlert
          title="Cancel Song Sort Changes"
          message="Are you sure you want to discard your song sort changes?"
          onNo={() => setConfirmOpen(false)}
          onYes={confirmDiscard}
        />
      )}
    </>
  );
}

const directionStyles: Record<string, React.CSSProperties> = {
  inner: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.xl,
    paddingBottom: Gap.md,
  },
  textCol: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column' as const,
    gap: Gap.xs,
  },
  title: {
    fontSize: Font.lg,
    fontWeight: 700,
    color: Colors.textPrimary,
  },
  hint: {
    fontSize: Font.sm,
    color: Colors.textSecondary,
  },
  icons: {
    display: 'flex',
    gap: Gap.md,
    flexShrink: 0,
    marginRight: -12,
  },
  iconBtn: {
    width: 40,
    height: 40,
    borderRadius: '50%',
    border: 'none',
    backgroundColor: 'transparent',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    cursor: 'pointer',
    position: 'relative' as const,
    overflow: 'hidden' as const,
  },
  iconCircle: {
    position: 'absolute' as const,
    inset: 0,
    borderRadius: '50%',
    backgroundColor: Colors.accentPurple,
    transform: 'scale(0)',
    transition: 'transform 250ms ease',
  },
  iconCircleActive: {
    transform: 'scale(1)',
  },
};

