/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { memo, useMemo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import {
  Colors, Font, Weight, Gap, Radius, Border,
  Display, TextAlign, Overflow,
  border, padding,
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
  // Build beat → note lookup
  const noteByBeat = new Map<number, PathDataNote>();
  for (const n of data.notes) {
    noteByBeat.set(n.beat, n);
  }

  const rows: TableRow[] = [];
  for (const act of data.activations) {
    if (!act.startNotes) continue;
    for (const sn of act.startNotes) {
      const note = noteByBeat.get(sn.beat);
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

// ── Fret pill / open triangle ────────────────────────────────

const PILL_SIZE = 18;

function FretPill({ fretKey, active }: { fretKey: FretKey; active: boolean }) {
  const s: CSSProperties = {
    display: Display.inlineBlock,
    width: PILL_SIZE,
    height: PILL_SIZE,
    borderRadius: Radius.full,
    backgroundColor: active ? FRET_COLORS[fretKey] : FRET_INACTIVE,
    border: active ? 'none' : border(Border.thin, Colors.borderSubtle),
    flexShrink: 0,
  };
  return <span style={s} />;
}

function OpenTriangle({ active }: { active: boolean }) {
  const color = active ? Colors.textPrimary : FRET_INACTIVE;
  return (
    <svg width={PILL_SIZE} height={PILL_SIZE} viewBox="0 0 18 18" style={{ flexShrink: 0, display: 'block' }}>
      <polygon
        points="9,2 16,16 2,16"
        fill={active ? color : 'none'}
        stroke={color}
        strokeWidth={active ? 0 : 1.5}
      />
    </svg>
  );
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
      fontSize: Font.xs,
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

export default memo(function PathDataTable({ data }: PathDataTableProps) {
  const { t } = useTranslation();
  const rows = useMemo(() => buildRows(data), [data]);
  const s = useTableStyles();

  if (rows.length === 0) {
    return <p style={{ color: Colors.textMuted, fontSize: Font.md, textAlign: TextAlign.center }}>{t('paths.notAvailable')}</p>;
  }

  return (
    <div style={s.wrapper}>
      <table style={s.table}>
        <thead>
          <tr>
            <th style={s.thNote}>{t('paths.colNote')}</th>
            <th style={s.th}>{t('paths.colBeat')}</th>
            <th style={s.th}>{t('paths.colTime')}</th>
            <th style={s.thOd}>{t('paths.colOd')}</th>
            <th style={s.thScore}>{t('paths.colScore')}</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((row, i) => (
            <tr key={i} style={i % 2 === 0 ? s.rowEven : s.rowOdd}>
              <td style={s.tdNote}>
                <div style={s.fretRow}>
                  {FRET_KEYS.map(fk => (
                    <FretPill key={fk} fretKey={fk} active={row.frets[fk] !== undefined} />
                  ))}
                  <OpenTriangle active={row.frets.open !== undefined} />
                </div>
              </td>
              <td style={s.td}>{row.beat.toFixed(2)}</td>
              <td style={s.tdMono}>{formatTime(row.seconds)}</td>
              <td style={s.tdOd}><OdBar percent={row.odPercent} /></td>
              <td style={s.tdScore}>{scoreFormatter.format(row.cumulativeScore)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
});

// ── Styles ───────────────────────────────────────────────────

function useTableStyles() {
  return useMemo(() => {
    const cellBase: CSSProperties = {
      padding: padding(Gap.md, Gap.lg),
      fontSize: Font.sm,
      color: Colors.textSecondary,
      whiteSpace: 'nowrap' as const,
    };
    const headerBase: CSSProperties = {
      ...cellBase,
      position: 'sticky' as const,
      top: 0,
      zIndex: 2,
      backgroundColor: Colors.surfaceElevated,
      color: Colors.textPrimary,
      fontWeight: Weight.semibold,
      fontSize: Font.xs,
      textTransform: 'uppercase' as const,
      letterSpacing: Font.letterSpacingWide,
      borderBottom: border(Border.thin, Colors.borderPrimary),
    };
    return {
      wrapper: {
        width: '100%',
        overflow: 'auto' as const,
      } as CSSProperties,
      table: {
        width: '100%',
        borderCollapse: 'collapse' as const,
        tableLayout: 'auto' as const,
      } as CSSProperties,
      th: { ...headerBase, textAlign: TextAlign.left } as CSSProperties,
      thNote: { ...headerBase, textAlign: TextAlign.center } as CSSProperties,
      thOd: { ...headerBase, textAlign: TextAlign.left, minWidth: 120 } as CSSProperties,
      thScore: { ...headerBase, textAlign: 'right' as const } as CSSProperties,
      rowEven: { backgroundColor: 'transparent' } as CSSProperties,
      rowOdd: { backgroundColor: Colors.surfaceSubtle } as CSSProperties,
      td: { ...cellBase, textAlign: TextAlign.left } as CSSProperties,
      tdNote: { ...cellBase, textAlign: TextAlign.center } as CSSProperties,
      tdMono: { ...cellBase, textAlign: TextAlign.left, fontFamily: 'monospace' } as CSSProperties,
      tdOd: { ...cellBase, textAlign: TextAlign.left, minWidth: 120 } as CSSProperties,
      tdScore: { ...cellBase, textAlign: 'right' as const, fontVariantNumeric: 'tabular-nums', fontWeight: Weight.semibold } as CSSProperties,
      fretRow: { display: Display.flex, gap: Gap.xs, alignItems: 'center', justifyContent: 'center' } as CSSProperties,
    };
  }, []);
}
