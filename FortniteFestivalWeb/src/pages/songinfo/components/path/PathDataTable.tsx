/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { memo, useMemo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import {
  Colors, Font, Weight, Gap, Radius, Border,
  Display, TextAlign, Overflow,
  border, padding, frostedCard,
} from '@festival/theme';

// ── Types ────────────────────────────────────────────────────

export interface PathDataNote {
  beat: number;
  seconds?: number;
  isSpNote: boolean;
  frets: {
    green?: number;
    red?: number;
    yellow?: number;
    blue?: number;
    orange?: number;
    open?: number;
  };
}

export interface PathStartNote {
  beat: number;
  seconds?: number;
  cumulativeScore: number;
  noteValue: number;
  odPercent: number;
  isSpGranting: boolean;
}

export interface PathActivation {
  startBeat: number;
  endBeat: number;
  startSeconds?: number;
  endSeconds?: number;
  startNotes?: PathStartNote[];
}

export interface PathDataResponse {
  songName: string;
  artist: string;
  charter: string;
  difficulty: string;
  totalScore: number;
  pathSummary: string;
  activations: PathActivation[];
  notes: PathDataNote[];
  spPhrases: unknown[];
  measures: unknown[];
  bpms: unknown[];
  timeSignatures: unknown[];
}

// ── Helpers ──────────────────────────────────────────────────

interface TableRow {
  frets: PathDataNote['frets'];
  beat: number;
  seconds: number;
  odPercent: number;
  cumulativeScore: number;
}

const FRET_KEYS = ['green', 'red', 'yellow', 'blue', 'orange'] as const;
type FretKey = typeof FRET_KEYS[number];

const FRET_COLORS: Record<FretKey, string> = {
  green: '#2ECC71',
  red: '#E74C3C',
  yellow: '#F1C40F',
  blue: '#3498DB',
  orange: '#E67E22',
};
const FRET_INACTIVE = Colors.surfaceMuted;

function buildRows(data: PathDataResponse): TableRow[] {
  // Build sorted notes array for tolerance-based lookup
  const sortedNotes = [...data.notes].sort((a, b) => a.beat - b.beat);

  function findNote(beat: number): PathDataNote | undefined {
    const EPSILON = 0.02;
    // Binary search for closest note
    let lo = 0;
    let hi = sortedNotes.length - 1;
    while (lo <= hi) {
      const mid = (lo + hi) >>> 1;
      if (sortedNotes[mid].beat < beat - EPSILON) lo = mid + 1;
      else if (sortedNotes[mid].beat > beat + EPSILON) hi = mid - 1;
      else return sortedNotes[mid];
    }
    return undefined;
  }

  const rows: TableRow[] = [];
  for (const act of data.activations) {
    if (!act.startNotes) continue;
    for (const sn of act.startNotes) {
      const note = findNote(sn.beat);
      rows.push({
        frets: note?.frets ?? {},
        beat: sn.beat,
        seconds: sn.seconds ?? 0,
        odPercent: sn.odPercent * 100,
        cumulativeScore: sn.cumulativeScore,
      });
    }
  }
  return rows;
}

function formatTime(totalSeconds: number): string {
  const mins = Math.floor(totalSeconds / 60);
  const secs = Math.floor(totalSeconds % 60);
  const ms = Math.round((totalSeconds % 1) * 1000);
  return `${String(mins).padStart(2, '0')}:${String(secs).padStart(2, '0')}:${String(ms).padStart(3, '0')}`;
}

const scoreFormatter = new Intl.NumberFormat('en-US');

// ── Fret pill / open pill ─────────────────────────────────────

function FretPill({ fretKey, active }: { fretKey: FretKey; active: boolean }) {
  const s: CSSProperties = {
    display: Display.inlineBlock,
    width: 22,
    height: 22,
    borderRadius: Radius.xs,
    backgroundColor: active ? FRET_COLORS[fretKey] : FRET_INACTIVE,
    border: border(Border.thick, active ? 'transparent' : Colors.borderSubtle),
    flexShrink: 0,
  };
  return <span style={s} />;
}

function OpenPill({ active }: { active: boolean }) {
  const s: CSSProperties = {
    display: Display.inlineBlock,
    width: 22,
    height: 22,
    borderRadius: Radius.xs,
    backgroundColor: active ? Colors.textPrimary : FRET_INACTIVE,
    border: border(Border.thick, active ? 'transparent' : Colors.borderSubtle),
    flexShrink: 0,
  };
  return <span style={s} />;
}

// ── OD Progress Bar ──────────────────────────────────────────

function OdBar({ percent }: { percent: number }) {
  const clamped = Math.max(0, Math.min(100, Math.round(percent)));
  const s = useOdBarStyles(clamped);
  return (
    <div style={s.wrapper}>
      <div style={s.track}>
        <div style={s.fill} />
      </div>
      <span style={s.label}>{clamped}%</span>
    </div>
  );
}

function useOdBarStyles(percent: number) {
  return useMemo(() => ({
    wrapper: {
      display: Display.flex,
      alignItems: 'center',
      gap: Gap.sm,
      width: '100%',
    } as CSSProperties,
    track: {
      position: 'relative' as const,
      flex: 1,
      minWidth: 80,
      height: 8,
      backgroundColor: Colors.surfaceSubtle,
      borderRadius: Radius.full,
      overflow: Overflow.hidden,
    } as CSSProperties,
    fill: {
      position: 'absolute' as const,
      left: 0,
      top: 0,
      bottom: 0,
      width: `${percent}%`,
      backgroundColor: Colors.statusAmber,
      borderRadius: Radius.full,
    } as CSSProperties,
    label: {
      flexShrink: 0,
      fontSize: Font.sm,
      fontWeight: Weight.semibold,
      color: Colors.textSecondary,
      minWidth: 32,
      textAlign: TextAlign.right,
    } as CSSProperties,
  }), [percent]);
}

// ── Main table ───────────────────────────────────────────────

interface PathDataTableProps {
  data: PathDataResponse;
}

export function PathDataHeader() {
  const { t } = useTranslation();
  const s = useTableStyles();
  return (
    <div style={s.header}>
      <span style={s.hNote}>{t('paths.colNote')}</span>
      <span style={s.hCell}>{t('paths.colBeat')}</span>
      <span style={s.hCell}>{t('paths.colTime')}</span>
      <span style={s.hOd}>{t('paths.colOd')}</span>
      <span style={s.hScore}>{t('paths.colScore')}</span>
    </div>
  );
}

export default memo(function PathDataTable({ data }: PathDataTableProps) {
  const { t } = useTranslation();
  const rows = useMemo(() => buildRows(data), [data]);
  const s = useTableStyles();

  if (rows.length === 0) {
    return <p style={{ color: Colors.textMuted, fontSize: Font.md, textAlign: TextAlign.center }}>{t('paths.notAvailable')}</p>;
  }

  return (
    <div style={s.wrapper}>
      <div style={s.list}>
        {rows.map((row, i) => (
          <div key={i} style={s.row}>
            <div style={s.cellNote}>
              <div style={s.fretRow}>
                {FRET_KEYS.map(fk => (
                  <FretPill key={fk} fretKey={fk} active={row.frets[fk] !== undefined} />
                ))}
                <OpenPill active={row.frets.open !== undefined} />
              </div>
            </div>
            <span style={s.cell}>{row.beat.toFixed(2)}</span>
            <span style={s.cellMono}>{formatTime(row.seconds)}</span>
            <div style={s.cellOd}><OdBar percent={row.odPercent} /></div>
            <span style={s.cellScore}>{scoreFormatter.format(row.cumulativeScore)}</span>
          </div>
        ))}
      </div>
    </div>
  );
});

// ── Styles ───────────────────────────────────────────────────

function useTableStyles() {
  return useMemo(() => {
    const gridCols = '160px 80px 110px 1fr 100px';
    const cellBase: CSSProperties = {
      display: Display.flex,
      alignItems: 'center',
      fontSize: Font.sm,
      color: Colors.textSecondary,
      whiteSpace: 'nowrap' as const,
    };
    return {
      wrapper: {
        width: '100%',
        display: Display.flex,
        flexDirection: 'column' as const,
        gap: Gap.sm,
      } as CSSProperties,
      header: {
        display: 'grid' as const,
        gridTemplateColumns: gridCols,
        gap: Gap.lg,
        padding: padding(Gap.sm, Gap.section + Gap.xl),
        color: Colors.textMuted,
        fontWeight: Weight.semibold,
        fontSize: Font.xs,
        textTransform: 'uppercase' as const,
        letterSpacing: Font.letterSpacingWide,
      } as CSSProperties,
      hNote: { display: Display.flex, justifyContent: 'center' } as CSSProperties,
      hCell: { display: Display.flex } as CSSProperties,
      hOd: { display: Display.flex } as CSSProperties,
      hScore: { display: Display.flex, justifyContent: 'flex-end' } as CSSProperties,
      list: {
        display: Display.flex,
        flexDirection: 'column' as const,
        gap: Gap.sm,
      } as CSSProperties,
      row: {
        ...frostedCard,
        display: 'grid' as const,
        gridTemplateColumns: gridCols,
        gap: Gap.lg,
        padding: padding(Gap.md, Gap.xl),
        borderRadius: Radius.md,
        alignItems: 'center',
      } as CSSProperties,
      cellNote: { ...cellBase, justifyContent: 'center' } as CSSProperties,
      cell: { ...cellBase } as CSSProperties,
      cellMono: { ...cellBase } as CSSProperties,
      cellOd: { ...cellBase, minWidth: 0, width: '100%' } as CSSProperties,
      cellScore: { ...cellBase, justifyContent: 'flex-end', fontVariantNumeric: 'tabular-nums', fontWeight: Weight.semibold } as CSSProperties,
      fretRow: { display: Display.flex, gap: Gap.xs, alignItems: 'center', justifyContent: 'center' } as CSSProperties,
    };
  }, []);
}
