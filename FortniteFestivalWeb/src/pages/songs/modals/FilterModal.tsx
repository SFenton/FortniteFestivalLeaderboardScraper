/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useMemo, useCallback, useState } from 'react';
import Modal from '../../../components/modals/Modal';
import { ModalSection } from '../../../components/modals/components/ModalSection';
import { ToggleRow } from '../../../components/common/ToggleRow';
import { Accordion } from '../../../components/common/Accordion';
import { BulkActions } from '../../../components/modals/components/BulkActions';
import ConfirmAlert from '../../../components/modals/ConfirmAlert';
import { InstrumentIcon } from '../../../components/display/InstrumentIcons';
import type { ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import { useModalDraft } from '../../../hooks/ui/useModalDraft';
import { INSTRUMENT_KEYS, INSTRUMENT_LABELS } from '@festival/core/api/serverTypes';
import type { SongFilters } from '../../../utils/songSettings';
import { useSettings, isInstrumentVisible } from '../../../contexts/SettingsContext';
import DifficultyBars from '../../../components/songs/metadata/DifficultyBars';
import { Size } from '@festival/theme';
import { filterStyles } from './filterStyles';
import { useTranslation } from 'react-i18next';

export type FilterDraft = SongFilters & {
  instrumentFilter: InstrumentKey | null;
};

type FilterModalProps = {
  visible: boolean;
  draft: FilterDraft;
  savedDraft?: FilterDraft;
  availableSeasons: number[];
  onChange: (d: FilterDraft) => void;
  onCancel: () => void;
  onReset: () => void;
  onApply: () => void;
};

const PERCENTILE_THRESHOLDS = [1, 2, 3, 4, 5, 10, 15, 20, 25, 30, 40, 50, 60, 70, 80, 90, 100] as const;

export default function FilterModal({ visible, draft, savedDraft, availableSeasons, onChange, onCancel, onReset, onApply }: FilterModalProps) {
  const { t } = useTranslation();
  const { settings: appSettings } = useSettings();
  const visibleKeys = INSTRUMENT_KEYS.filter(k => isInstrumentVisible(appSettings, k));

  const toggleMissingScores = (key: InstrumentKey) => {
    onChange({ ...draft, missingScores: { ...draft.missingScores, [key]: !(draft.missingScores[key] ?? false) } });
  };
  const toggleMissingFCs = (key: InstrumentKey) => {
    onChange({ ...draft, missingFCs: { ...draft.missingFCs, [key]: !(draft.missingFCs[key] ?? false) } });
  };
  const toggleHasScores = (key: InstrumentKey) => {
    onChange({ ...draft, hasScores: { ...draft.hasScores, [key]: !(draft.hasScores[key] ?? false) } });
  };
  const toggleHasFCs = (key: InstrumentKey) => {
    onChange({ ...draft, hasFCs: { ...draft.hasFCs, [key]: !(draft.hasFCs[key] ?? false) } });
  };

  // Global toggles: set/unset all visible instruments at once
  const allOn = (map: Record<string, boolean>) => visibleKeys.every(k => map[k] === true);
  const toggleGlobal = (field: 'missingScores' | 'missingFCs' | 'hasScores' | 'hasFCs') => {
    const current = allOn(draft[field]);
    const updated: Record<string, boolean> = { ...draft[field] };
    for (const k of visibleKeys) updated[k] = !current;
    onChange({ ...draft, [field]: updated });
  };

  const hasInstrument = draft.instrumentFilter != null;

  const { hasChanges, confirmOpen, setConfirmOpen, handleClose, confirmDiscard } = useModalDraft(draft, savedDraft, onCancel);

  return (
    <>
    <Modal visible={visible} title="Filter Songs" onClose={handleClose} onApply={onApply} onReset={onReset} resetLabel="Reset Filter Settings" resetHint="Restore all filter options to their defaults." applyLabel="Apply Filter Changes" applyDisabled={!hasChanges}>
      {/* Global filters */}
      <ModalSection>
        <Accordion title="Global Score & FC Toggles" hint="Toggles that impact all instruments globally. Turning these on or off will enable or disable them across all instruments.">
          <ToggleRow
            label="Missing Scores"
            description="Songs missing scores on all visible instruments."
            checked={allOn(draft.missingScores)}
            onToggle={() => toggleGlobal('missingScores')}
          />
          <ToggleRow
            label="Has Scores"
            description="Songs with scores on all visible instruments."
            checked={allOn(draft.hasScores)}
            onToggle={() => toggleGlobal('hasScores')}
          />
          <ToggleRow
            label="Missing FCs"
            description="Songs missing FCs on all visible instruments."
            checked={allOn(draft.missingFCs)}
            onToggle={() => toggleGlobal('missingFCs')}
          />
          <ToggleRow
            label="Has FCs"
            description="Songs with FCs on all visible instruments."
            checked={allOn(draft.hasFCs)}
            onToggle={() => toggleGlobal('hasFCs')}
          />
        </Accordion>
      </ModalSection>

      {/* Missing filters nstrument accordions */}
      <ModalSection title="Individual Score & FC Toggles" hint="Toggles that impact individual instruments. Turning these on or off will not impact other individual instruments. These filters are computed per-instrument and then OR'd with other instruments. (Example: Lead &ldquo;has scores&rdquo; and Drums &ldquo;missing scores&rdquo; will yield all songs where Lead has a score OR drums do not have a score.)">
        {visibleKeys.map(key => (
          <Accordion key={key} title={INSTRUMENT_LABELS[key]} icon={<InstrumentIcon instrument={key} size={28} />}>
            <ToggleRow
              label={`Missing ${INSTRUMENT_LABELS[key]} Scores`}
              description={`Songs missing scores on ${INSTRUMENT_LABELS[key]}.`}
              checked={draft.missingScores[key] ?? false}
              onToggle={() => toggleMissingScores(key)}
            />
            <ToggleRow
              label={`Has ${INSTRUMENT_LABELS[key]} Scores`}
              description={`Songs with scores on ${INSTRUMENT_LABELS[key]}.`}
              checked={draft.hasScores[key] ?? false}
              onToggle={() => toggleHasScores(key)}
            />
            <ToggleRow
              label={`Missing ${INSTRUMENT_LABELS[key]} FCs`}
              description={`Songs missing FCs on ${INSTRUMENT_LABELS[key]}.`}
              checked={draft.missingFCs[key] ?? false}
              onToggle={() => toggleMissingFCs(key)}
            />
            <ToggleRow
              label={`Has ${INSTRUMENT_LABELS[key]} FCs`}
              description={`Songs with FCs on ${INSTRUMENT_LABELS[key]}.`}
              checked={draft.hasFCs[key] ?? false}
              onToggle={() => toggleHasFCs(key)}
            />
          </Accordion>
        ))}
      </ModalSection>

      {/* Instrument selector */}
      <ModalSection title="Selected Instrument Filters" hint="Select an instrument to only show its metadata on each song row. When none is selected, all instruments are shown.">
        <div style={filterStyles.instrumentRow}>
          {visibleKeys.map(key => {
            const selected = draft.instrumentFilter === key;
            return (
              <button
                key={key}
                style={filterStyles.instrumentBtn}
                onClick={() => onChange({ ...draft, instrumentFilter: selected ? null : key })}
                title={INSTRUMENT_LABELS[key]}
              >
                <div style={selected ? filterStyles.instrumentCircleActive : filterStyles.instrumentCircle} />
                <div style={filterStyles.instrumentIconWrap}>
                  <InstrumentIcon instrument={key} size={Size.iconInstrument} />
                </div>
              </button>
            );
          })}
        </div>
      </ModalSection>

      {/* Instrument-specific filters (animated in/out) */}
      <div style={{ ...filterStyles.instrumentFiltersWrap, gridTemplateRows: hasInstrument ? '1fr' : '0fr' }}>
        <div style={filterStyles.instrumentFiltersInner}>
          <ModalSection>
            <Accordion title="Season" hint="Filter by the season in which the score was achieved.">
              <SeasonToggles
                availableSeasons={availableSeasons}
                seasonFilter={draft.seasonFilter}
                onChange={seasonFilter => onChange({ ...draft, seasonFilter })}
              />
            </Accordion>
          </ModalSection>

          <ModalSection>
            <Accordion title="Percentile" hint="Show or hide songs based on their leaderboard ranking bracket.">
              <PercentileToggles
                percentileFilter={draft.percentileFilter}
                onChange={percentileFilter => onChange({ ...draft, percentileFilter })}
              />
            </Accordion>
          </ModalSection>

          <ModalSection>
            <Accordion title="Stars" hint="Filter songs by the number of stars on your high score.">
              <StarsToggles
                starsFilter={draft.starsFilter}
                onChange={starsFilter => onChange({ ...draft, starsFilter })}
              />
            </Accordion>
          </ModalSection>

          <ModalSection>
            <Accordion title="Song Intensity" hint="Filter by the song's difficulty rating for the selected instrument.">
              <DifficultyToggles
                difficultyFilter={draft.difficultyFilter}
                onChange={difficultyFilter => onChange({ ...draft, difficultyFilter })}
              />
            </Accordion>
          </ModalSection>
        </div>
      </div>
    </Modal>

      {confirmOpen && (
        <ConfirmAlert
          title={t('filter.cancelTitle')}
          message={t('filter.cancelMessage')}
          onNo={() => setConfirmOpen(false)}
          onYes={confirmDiscard}
        />
      )}
    </>
  );
}

/* -- Toggle components for composite filters -- */

function SeasonToggles({ availableSeasons, seasonFilter, onChange }: { availableSeasons: number[]; seasonFilter: Record<number, boolean>; onChange: (f: Record<number, boolean>) => void }) {
  const seasons = [...availableSeasons, 0];

  const isOn = (s: number) => seasonFilter[s] !== false;
  const toggleSeason = (s: number) => onChange({ ...seasonFilter, [s]: !isOn(s) });
  const selectAll = () => {
    const next: Record<number, boolean> = {};
    for (const s of seasons) next[s] = true;
    onChange(next);
  };
  const clearAll = () => {
    const next: Record<number, boolean> = {};
    for (const s of seasons) next[s] = false;
    onChange(next);
  };

  return (
    <>
      <BulkActions onSelectAll={selectAll} onClearAll={clearAll} />
      {seasons.map(s => (
        <ToggleRow key={s} label={s === 0 ? 'No Score' : `Season ${s}`} checked={isOn(s)} onToggle={() => toggleSeason(s)} />
      ))}
    </>
  );
}

/* v8 ignore start -- V8 misses inline callbacks inside un-exported sub-components; covered via ModalCallbacks tests */
function PercentileToggles({ percentileFilter, onChange }: { percentileFilter: Record<number, boolean>; onChange: (f: Record<number, boolean>) => void }) {
  const allKeys = [0, ...PERCENTILE_THRESHOLDS];
  const isOn = (p: number) => percentileFilter[p] !== false;
  const toggleP = (p: number) => onChange({ ...percentileFilter, [p]: !isOn(p) });
  const selectAll = () => {
    const next: Record<number, boolean> = {};
    for (const p of allKeys) next[p] = true;
    onChange(next);
  };
  const clearAll = () => {
    const next: Record<number, boolean> = {};
    for (const p of allKeys) next[p] = false;
    onChange(next);
  };

  return (
    <>
      <BulkActions onSelectAll={selectAll} onClearAll={clearAll} />
      {allKeys.map(p => (
        <ToggleRow key={p} label={p === 0 ? 'No Score' : `Top ${p}%`} checked={isOn(p)} onToggle={() => toggleP(p)} />
      ))}
    </>
  );
}
function StarsToggles({ starsFilter, onChange }: { starsFilter: Record<number, boolean>; onChange: (f: Record<number, boolean>) => void }) {
  const allKeys = [6, 5, 4, 3, 2, 1, 0];

  const starLabel = (k: number) => {
    if (k === 0) return 'No Score';
    const isGold = k === 6;
    const count = isGold ? 5 : k;
    const src = isGold ? `${import.meta.env.BASE_URL}star_gold.png` : `${import.meta.env.BASE_URL}star_white.png`;
    return (
      <span style={{ display: 'inline-flex', alignItems: 'center', gap: 2 }}>
        {Array.from({ length: count }, (_, i) => (
          <img key={i} src={src} alt="" width={14} height={14} />
        ))}
      </span>
    );
  };

  const isOn = (s: number) => starsFilter[s] !== false;
  const toggleS = (s: number) => onChange({ ...starsFilter, [s]: !isOn(s) });
  const selectAll = () => {
    const next: Record<number, boolean> = {};
    for (const s of allKeys) next[s] = true;
    onChange(next);
  };
  const clearAll = () => {
    const next: Record<number, boolean> = {};
    for (const s of allKeys) next[s] = false;
    onChange(next);
  };

  return (
    <>
      <BulkActions onSelectAll={selectAll} onClearAll={clearAll} />
      {allKeys.map(s => (
        <ToggleRow key={s} label={starLabel(s)} checked={isOn(s)} onToggle={() => toggleS(s)} />
      ))}
    </>
  );
}

/* v8 ignore stop */

function DifficultyToggles({ difficultyFilter, onChange }: { difficultyFilter: Record<number, boolean>; onChange: (f: Record<number, boolean>) => void }) {
  const allKeys = [1, 2, 3, 4, 5, 6, 7, 0];
  const diffLabel = (k: number): React.ReactNode =>
    k === 0 ? 'No Score' : <DifficultyBars level={k} />;

  const isOn = (d: number) => difficultyFilter[d] !== false;
  const toggleD = (d: number) => onChange({ ...difficultyFilter, [d]: !isOn(d) });
  const selectAll = () => {
    const next: Record<number, boolean> = {};
    for (const d of allKeys) next[d] = true;
    onChange(next);
  };
  const clearAll = () => {
    const next: Record<number, boolean> = {};
    for (const d of allKeys) next[d] = false;
    onChange(next);
  };

  return (
    <>
      <BulkActions onSelectAll={selectAll} onClearAll={clearAll} />
      {allKeys.map(d => (
        <ToggleRow key={d} label={diffLabel(d)} checked={isOn(d)} onToggle={() => toggleD(d)} />
      ))}
    </>
  );
}
