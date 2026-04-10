/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { memo, useMemo, useCallback, useRef, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import {
  DndContext,
  closestCenter,
  PointerSensor,
  KeyboardSensor,
  useSensor,
  useSensors,
  type DragEndEvent,
  type DragOverEvent,
  type DragStartEvent,
} from '@dnd-kit/core';
import {
  SortableContext,
  horizontalListSortingStrategy,
  useSortable,
  arrayMove,
} from '@dnd-kit/sortable';
import { CSS } from '@dnd-kit/utilities';
import {
  Colors, Font, Weight, Gap, Radius, Border,
  Display, TextAlign, Overflow, Cursor, CssValue,
  border, padding, frostedCard, STAGGER_ROW_MS,
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

  /** Find ALL notes at the same beat and merge their frets into one chord. */
  function findChord(beat: number): PathDataNote['frets'] {
    const EPSILON = 0.02;
    const merged: PathDataNote['frets'] = {};
    // Binary-search to any match, then scan left/right for all neighbours
    let lo = 0;
    let hi = sortedNotes.length - 1;
    let anchor = -1;
    while (lo <= hi) {
      const mid = (lo + hi) >>> 1;
      const midNote = sortedNotes[mid]!;
      if (midNote.beat < beat - EPSILON) lo = mid + 1;
      else if (midNote.beat > beat + EPSILON) hi = mid - 1;
      else { anchor = mid; break; }
    }
    if (anchor < 0) return merged;
    // Expand left
    let i = anchor;
    while (i >= 0 && Math.abs(sortedNotes[i]!.beat - beat) < EPSILON) {
      Object.assign(merged, sortedNotes[i]!.frets);
      i--;
    }
    // Expand right (skip anchor, already visited)
    i = anchor + 1;
    while (i < sortedNotes.length && Math.abs(sortedNotes[i]!.beat - beat) < EPSILON) {
      Object.assign(merged, sortedNotes[i]!.frets);
      i++;
    }
    return merged;
  }

  const MERGE_EPSILON = 0.02;
  const rows: TableRow[] = [];
  for (const act of data.activations) {
    if (!act.startNotes) continue;
    for (const sn of act.startNotes) {
      // Merge chord members at the same beat into one row
      const prev = rows.length > 0 ? rows[rows.length - 1] : undefined;
      if (prev && Math.abs(prev.beat - sn.beat) < MERGE_EPSILON) {
        // Take the higher cumulative score (it accumulates per sub-note)
        prev.cumulativeScore = Math.max(prev.cumulativeScore, sn.cumulativeScore);
        continue;
      }
      const frets = findChord(sn.beat);
      rows.push({
        frets,
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

// ── Column ordering ──────────────────────────────────────────

export type ColumnKey = 'note' | 'beat' | 'time' | 'od' | 'score';
export const DEFAULT_COLUMN_ORDER: ColumnKey[] = ['note', 'beat', 'time', 'od', 'score'];

const COLUMN_WIDTHS: Record<ColumnKey, string> = {
  note: '160px',
  beat: '80px',
  time: '110px',
  od: '1fr',
  score: '100px',
};

function buildGridCols(order: ColumnKey[]): string {
  return order.map(k => COLUMN_WIDTHS[k]).join(' ');
}

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
      fontSize: Font.md,
      fontWeight: Weight.semibold,
      color: Colors.textPrimary,
      minWidth: 32,
      textAlign: TextAlign.right,
      paddingBottom: 2,
    } as CSSProperties,
  }), [percent]);
}

// ── Main table ───────────────────────────────────────────────

interface PathDataTableProps {
  data: PathDataResponse;
}

const COL_LABEL_KEYS: Record<ColumnKey, string> = {
  note: 'paths.colNote',
  beat: 'paths.colBeat',
  time: 'paths.colTime',
  od: 'paths.colOd',
  score: 'paths.colScore',
};

const DRAG_ICON = (
  <svg width="8" height="12" viewBox="0 0 12 18" fill="currentColor" aria-hidden="true" style={{ flexShrink: 0 }}>
    <circle cx="3" cy="3" r="1.5" /><circle cx="9" cy="3" r="1.5" />
    <circle cx="3" cy="9" r="1.5" /><circle cx="9" cy="9" r="1.5" />
    <circle cx="3" cy="15" r="1.5" /><circle cx="9" cy="15" r="1.5" />
  </svg>
);

function SortableHeaderCell({ colKey }: { colKey: ColumnKey }) {
  const { t } = useTranslation();
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({ id: colKey });
  const style: CSSProperties = {
    display: Display.flex,
    alignItems: 'center',
    justifyContent: 'center',
    gap: Gap.xs,
    cursor: isDragging ? 'grabbing' as CSSProperties['cursor'] : Cursor.grab,
    transform: CSS.Translate.toString(transform),
    transition,
    zIndex: isDragging ? 10 : undefined,
    opacity: isDragging ? 0.7 : 1,
    userSelect: CssValue.none as CSSProperties['userSelect'],
  };
  return (
    <span ref={setNodeRef} style={style} {...attributes} {...listeners}>
      {DRAG_ICON}
      {t(COL_LABEL_KEYS[colKey])}
    </span>
  );
}

interface PathDataHeaderProps {
  isMobile: boolean;
  columnOrder?: ColumnKey[];
  onColumnOrderChange?: (order: ColumnKey[]) => void;
}

export function PathDataHeader({ isMobile, columnOrder = DEFAULT_COLUMN_ORDER, onColumnOrderChange }: PathDataHeaderProps) {
  const s = useTableStyles(isMobile, columnOrder);
  const preDragOrder = useRef<ColumnKey[]>(columnOrder);

  const sensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 5 } }),
    useSensor(KeyboardSensor),
  );

  /* v8 ignore start — DnD handlers */
  const handleDragStart = useCallback((_event: DragStartEvent) => {
    preDragOrder.current = columnOrder;
  }, [columnOrder]);

  const handleDragOver = useCallback((event: DragOverEvent) => {
    const { active, over } = event;
    if (!over || active.id === over.id || !onColumnOrderChange) return;
    const oldIndex = columnOrder.indexOf(active.id as ColumnKey);
    const newIndex = columnOrder.indexOf(over.id as ColumnKey);
    if (oldIndex === -1 || newIndex === -1) return;
    onColumnOrderChange(arrayMove(columnOrder, oldIndex, newIndex));
  }, [columnOrder, onColumnOrderChange]);

  const handleDragEnd = useCallback((_event: DragEndEvent) => {
    // Order is already updated by onDragOver — nothing to do
  }, []);

  const handleDragCancel = useCallback(() => {
    if (onColumnOrderChange) onColumnOrderChange(preDragOrder.current);
  }, [onColumnOrderChange]);
  /* v8 ignore stop */

  if (isMobile) return null;
  return (
    <DndContext
      sensors={sensors}
      collisionDetection={closestCenter}
      onDragStart={handleDragStart}
      onDragOver={handleDragOver}
      onDragEnd={handleDragEnd}
      onDragCancel={handleDragCancel}
    >
      <SortableContext items={columnOrder} strategy={horizontalListSortingStrategy}>
        <div style={s.header}>
          {columnOrder.map(col => (
            <SortableHeaderCell key={col} colKey={col} />
          ))}
        </div>
      </SortableContext>
    </DndContext>
  );
}

export default memo(function PathDataTable({ data, isMobile, columnOrder = DEFAULT_COLUMN_ORDER, stagger }: PathDataTableProps & { isMobile: boolean; columnOrder?: ColumnKey[]; stagger?: boolean }) {
  const { t } = useTranslation();
  const rows = useMemo(() => buildRows(data), [data]);
  const s = useTableStyles(isMobile, columnOrder);

  if (rows.length === 0) {
    return <p style={{ color: Colors.textMuted, fontSize: Font.md, textAlign: TextAlign.center }}>{t('paths.notAvailable')}</p>;
  }

  function renderDesktopCell(row: TableRow, col: ColumnKey) {
    switch (col) {
      case 'note':
        return (
          <div key={col} style={s.cellNote}>
            <div style={s.fretRow}>
              {FRET_KEYS.map(fk => (
                <FretPill key={fk} fretKey={fk} active={row.frets[fk] !== undefined} />
              ))}
            </div>
          </div>
        );
      case 'beat':
        return <span key={col} style={s.cell}>{row.beat.toFixed(2)}</span>;
      case 'time':
        return <span key={col} style={s.cellMono}>{formatTime(row.seconds)}</span>;
      case 'od':
        return <div key={col} style={s.cellOd}><OdBar percent={row.odPercent} /></div>;
      case 'score':
        return <span key={col} style={s.cellScore}>{scoreFormatter.format(row.cumulativeScore)}</span>;
    }
  }

  return (
    <div style={s.wrapper}>
      <div style={s.list}>
        {rows.map((row, i) => (
          <div key={i} style={{
            ...s.row,
            ...(stagger ? {
              opacity: 0,
              animation: `fadeInUp 400ms ease-out ${i * STAGGER_ROW_MS}ms forwards`,
            } : {}),
          }}>
            {isMobile ? (
              <>
                <div>
                  <div style={s.mobileLabelNote}>{t('paths.colNote')}</div>
                  <div style={s.fretRow}>
                    {FRET_KEYS.map(fk => (
                      <FretPill key={fk} fretKey={fk} active={row.frets[fk] !== undefined} />
                    ))}
                  </div>
                </div>
                <div style={s.mobileDataRow}>
                  <div style={{ flex: 1 }}>
                    <div style={s.mobileLabel}>{t('paths.colBeat')}</div>
                    <span style={s.cell}>{row.beat.toFixed(2)}</span>
                  </div>
                  <div style={{ flex: 1 }}>
                    <div style={s.mobileLabel}>{t('paths.colTime')}</div>
                    <span style={s.cellMono}>{formatTime(row.seconds)}</span>
                  </div>
                  <div style={{ flex: 1 }}>
                    <div style={s.mobileLabel}>{t('paths.colScore')}</div>
                    <span style={s.cellScore}>{scoreFormatter.format(row.cumulativeScore)}</span>
                  </div>
                </div>
                <div>
                  <div style={s.mobileLabelOd}>{t('paths.colOd')}</div>
                  <div style={s.cellOd}><OdBar percent={row.odPercent} /></div>
                </div>
              </>
            ) : (
              <>{columnOrder.map(col => renderDesktopCell(row, col))}</>
            )}
          </div>
        ))}
      </div>
    </div>
  );
});

// ── Styles ───────────────────────────────────────────────────

function useTableStyles(isMobile: boolean, columnOrder: ColumnKey[] = DEFAULT_COLUMN_ORDER) {
  return useMemo(() => {
    const gridCols = isMobile ? '1fr auto' : buildGridCols(columnOrder);
    const cellBase: CSSProperties = {
      display: Display.flex,
      alignItems: 'center',
      fontSize: Font.md,
      color: Colors.textPrimary,
      fontWeight: Weight.semibold,
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
      list: {
        display: Display.flex,
        flexDirection: 'column' as const,
        gap: Gap.sm,
      } as CSSProperties,
      row: isMobile ? {
        ...frostedCard,
        display: Display.flex,
        flexDirection: 'column' as const,
        gap: Gap.xl,
        padding: padding(Gap.md, Gap.xl),
        borderRadius: Radius.md,
      } as CSSProperties : {
        ...frostedCard,
        display: 'grid' as const,
        gridTemplateColumns: gridCols,
        gap: Gap.lg,
        padding: padding(Gap.md, Gap.xl),
        borderRadius: Radius.md,
        alignItems: 'center',
      } as CSSProperties,
      mobileDataRow: {
        display: Display.flex,
        gap: Gap.md,
        alignItems: 'flex-start',
      } as CSSProperties,
      mobileLabel: {
        fontSize: Font.xs,
        color: Colors.textMuted,
        fontWeight: Weight.semibold,
        textTransform: 'uppercase' as const,
        letterSpacing: Font.letterSpacingWide,
        marginBottom: Gap.xs,
      } as CSSProperties,
      mobileLabelNote: {
        fontSize: Font.xs,
        color: Colors.textMuted,
        fontWeight: Weight.semibold,
        textTransform: 'uppercase' as const,
        letterSpacing: Font.letterSpacingWide,
        marginBottom: Gap.md,
      } as CSSProperties,
      mobileLabelOd: {
        fontSize: Font.xs,
        color: Colors.textMuted,
        fontWeight: Weight.semibold,
        textTransform: 'uppercase' as const,
        letterSpacing: Font.letterSpacingWide,
        marginBottom: Gap.sm,
      } as CSSProperties,
      cellNote: { ...cellBase, justifyContent: isMobile ? 'flex-start' : 'center' } as CSSProperties,
      cell: { ...cellBase, justifyContent: isMobile ? 'flex-start' : 'center' } as CSSProperties,
      cellMono: { ...cellBase, justifyContent: isMobile ? 'flex-start' : 'center' } as CSSProperties,
      cellOd: { ...cellBase, justifyContent: isMobile ? 'flex-start' : 'center', minWidth: 0, width: '100%' } as CSSProperties,
      cellScore: { ...cellBase, justifyContent: isMobile ? 'flex-start' : 'center', fontVariantNumeric: 'tabular-nums', fontWeight: Weight.semibold } as CSSProperties,
      fretRow: { display: Display.flex, gap: Gap.xs, alignItems: 'center', justifyContent: isMobile ? 'flex-start' : 'center' } as CSSProperties,
    };
  }, [isMobile, columnOrder]);
}
