import Modal, { ModalSection, ToggleRow, Accordion, BulkActions } from './Modal';
import { InstrumentIcon } from './InstrumentIcons';
import type { InstrumentKey } from '../models';
import { INSTRUMENT_KEYS, INSTRUMENT_LABELS } from '../models';
import type { SongFilters } from './songSettings';
import { useSettings, isInstrumentVisible } from '../contexts/SettingsContext';
import { Colors, Gap, Radius } from '../theme';

export type FilterDraft = SongFilters & {
  instrumentFilter: InstrumentKey | null;
};

type Props = {
  visible: boolean;
  draft: FilterDraft;
  availableSeasons: number[];
  onChange: (d: FilterDraft) => void;
  onCancel: () => void;
  onReset: () => void;
  onApply: () => void;
};

const PERCENTILE_THRESHOLDS = [1, 2, 3, 4, 5, 10, 15, 20, 25, 30, 40, 50, 60, 70, 80, 90, 100] as const;

export default function FilterModal({ visible, draft, availableSeasons, onChange, onCancel, onReset, onApply }: Props) {
  const { settings: appSettings } = useSettings();
  const visibleKeys = INSTRUMENT_KEYS.filter(k => isInstrumentVisible(appSettings, k));

  const toggle = (key: keyof SongFilters) => {
    const val = draft[key];
    if (typeof val === 'boolean') {
      onChange({ ...draft, [key]: !val });
    }
  };

  const hasInstrument = draft.instrumentFilter != null;

  return (
    <Modal visible={visible} title="Filter Songs" onClose={onCancel} onApply={onApply} onReset={onReset}>
      {/* Missing filters */}
      <ModalSection title="Missing" hint="Only show songs where you are missing scores or full combos on pad or pro instruments.">
        <ToggleRow label="Pad Scores" description="Songs missing scores on Lead, Bass, Drums, or Vocals." checked={draft.missingPadScores} onToggle={() => toggle('missingPadScores')} />
        <ToggleRow label="Pad FCs" description="Songs missing FCs on Lead, Bass, Drums, or Vocals." checked={draft.missingPadFCs} onToggle={() => toggle('missingPadFCs')} />
        <ToggleRow label="Pro Scores" description="Songs missing scores on Pro Lead or Pro Bass." checked={draft.missingProScores} onToggle={() => toggle('missingProScores')} />
        <ToggleRow label="Pro FCs" description="Songs missing FCs on Pro Lead or Pro Bass." checked={draft.missingProFCs} onToggle={() => toggle('missingProFCs')} />
      </ModalSection>

      {/* Instrument selector */}
      <ModalSection title="Instrument" hint="Select an instrument to only show its metadata on each song row. When none is selected, all instruments are shown.">
        <div style={localStyles.instrumentRow}>
          {visibleKeys.map(key => {
            const selected = draft.instrumentFilter === key;
            return (
              <button
                key={key}
                style={{
                  ...localStyles.instrumentBtn,
                  ...(selected ? localStyles.instrumentBtnSelected : {}),
                }}
                onClick={() => onChange({ ...draft, instrumentFilter: selected ? null : key })}
                title={INSTRUMENT_LABELS[key]}
              >
                <InstrumentIcon instrument={key} size={24} />
              </button>
            );
          })}
        </div>
      </ModalSection>

      {/* Season filter (instrument-specific) */}
      {hasInstrument && (
        <Accordion title="Season" hint="Filter by the season in which the score was achieved.">
          <SeasonToggles
            availableSeasons={availableSeasons}
            seasonFilter={draft.seasonFilter}
            onChange={seasonFilter => onChange({ ...draft, seasonFilter })}
          />
        </Accordion>
      )}

      {/* Percentile filter (instrument-specific) */}
      {hasInstrument && (
        <Accordion title="Percentile" hint="Show or hide songs based on their leaderboard ranking bracket.">
          <PercentileToggles
            percentileFilter={draft.percentileFilter}
            onChange={percentileFilter => onChange({ ...draft, percentileFilter })}
          />
        </Accordion>
      )}

      {/* Stars filter (instrument-specific) */}
      {hasInstrument && (
        <Accordion title="Stars" hint="Filter songs by the number of stars on your high score.">
          <StarsToggles
            starsFilter={draft.starsFilter}
            onChange={starsFilter => onChange({ ...draft, starsFilter })}
          />
        </Accordion>
      )}

      {/* Difficulty filter (instrument-specific) */}
      {hasInstrument && (
        <Accordion title="Song Intensity" hint="Filter by the song's difficulty rating for the selected instrument.">
          <DifficultyToggles
            difficultyFilter={draft.difficultyFilter}
            onChange={difficultyFilter => onChange({ ...draft, difficultyFilter })}
          />
        </Accordion>
      )}
    </Modal>
  );
}

/* ── Sub-components for composite filters ── */

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

function DifficultyBars({ level }: { level: number }) {
  const barW = 10;
  const barH = 20;
  const offset = Math.round(barW * 0.26);
  const gap = 2;
  const totalW = 7 * barW + 6 * gap;

  return (
    <svg width={totalW} height={barH} style={{ display: 'block' }}>
      {Array.from({ length: 7 }, (_, i) => {
        const filled = i + 1 <= level;
        const x = i * (barW + gap);
        return (
          <polygon
            key={i}
            points={`${x + offset},0 ${x + barW},0 ${x + barW - offset},${barH} ${x},${barH}`}
            fill={filled ? '#FFFFFF' : '#666666'}
          />
        );
      })}
    </svg>
  );
}

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

const localStyles: Record<string, React.CSSProperties> = {
  instrumentRow: {
    display: 'flex',
    gap: Gap.md,
    flexWrap: 'wrap',
    justifyContent: 'center',
  },
  instrumentBtn: {
    width: 44,
    height: 44,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    borderRadius: Radius.xs,
    border: `1px solid ${Colors.borderPrimary}`,
    backgroundColor: Colors.transparent,
    cursor: 'pointer',
    opacity: 0.6,
    transition: 'opacity 0.15s, border-color 0.15s',
  },
  instrumentBtnSelected: {
    opacity: 1,
    borderColor: Colors.accentBlue,
    backgroundColor: Colors.chipSelectedBgSubtle,
  },
  chipGrid: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: Gap.sm,
  },
};
