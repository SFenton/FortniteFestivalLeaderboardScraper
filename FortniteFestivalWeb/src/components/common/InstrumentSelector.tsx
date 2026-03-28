/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
/**
 * Reusable instrument selector with collapsible content section.
 * Shows a row of instrument circle buttons; clicking one expands children below it.
 * When the row is too narrow to fit all buttons, automatically switches to compact
 * arrow-cycling mode (unless overridden via the explicit `compact` prop).
 * Used in FilterModal, SuggestionsFilterModal, PathsModal, and ScoreHistoryChart.
 */
import { useCallback, useEffect, useRef, useState, type CSSProperties } from 'react';
import { Size, Layout, Gap } from '@festival/theme';
import { SERVER_INSTRUMENT_LABELS, type ServerInstrumentKey } from '@festival/core/api/serverTypes';
import type { InstrumentKey } from '@festival/core/instruments';
import { InstrumentIcon } from '../display/InstrumentIcons';
import { filterStyles } from '../../pages/songs/modals/filterStyles';

/** Any instrument key type accepted by the selector. */
export type AnyInstrumentKey = ServerInstrumentKey | InstrumentKey;

export interface InstrumentSelectorItem<K extends AnyInstrumentKey = ServerInstrumentKey> {
  key: K;
  label?: string;
}

export interface InstrumentSelectorClassNames {
  row?: string;
  button?: string;
  buttonActive?: string;
  arrowButton?: string;
}

/** Inline style overrides — preferred over classNames for new code. */
export interface InstrumentSelectorStyleOverrides {
  row?: CSSProperties;
  button?: CSSProperties;
  buttonActive?: CSSProperties;
  arrowButton?: CSSProperties;
}

export interface InstrumentSelectorProps<K extends AnyInstrumentKey = ServerInstrumentKey> {
  instruments: InstrumentSelectorItem<K>[];
  selected: K | null;
  onSelect: (key: K | null) => void;
  /** When true, clicking the selected instrument does not deselect it. */
  required?: boolean;
  /**
   * Controls compact (arrow-cycling) mode.
   * - `true` — always compact.
   * - `false` — always full row.
   * - `undefined` (default) — auto-detect via ResizeObserver; switches to compact
   *   when the container is too narrow to fit all instrument buttons in a single row.
   */
  compact?: boolean;
  /** Accessible labels for compact arrow buttons. */
  compactLabels?: { previous?: string; next?: string };
  /**
   * When true, compact-mode arrows cycle a local preview instead of calling
   * `onSelect` when nothing is selected.  Tapping the center icon commits
   * the previewed instrument.  Once an instrument is selected, arrows cycle
   * the selection directly via `onSelect` as usual.
   */
  deferSelection?: boolean;
  /** @deprecated Prefer `styles` for new code. Override CSS class names for custom styling. */
  classNames?: InstrumentSelectorClassNames;
  /** Inline style overrides. Takes precedence over classNames. */
  styles?: InstrumentSelectorStyleOverrides;
  children?: React.ReactNode;
}

export function InstrumentSelector<K extends AnyInstrumentKey = ServerInstrumentKey>({
  instruments, selected, onSelect, required, compact,
  compactLabels, deferSelection, classNames, styles: sty, children,
}: InstrumentSelectorProps<K>) {
  const hasSelection = selected != null;

  // Local preview index for compact mode when deferSelection is on and nothing is selected.
  const [previewIdx, setPreviewIdx] = useState(0);
  // Reset preview when instruments list changes (e.g. settings toggle)
  useEffect(() => { setPreviewIdx(0); }, [instruments.length]);
  const previewKey = instruments[previewIdx]?.key ?? instruments[0]?.key;

  // Auto-compact: when `compact` is undefined, measure the row to decide.
  const rowRef = useRef<HTMLDivElement>(null);
  const [autoCompact, setAutoCompact] = useState(false);
  const isCompact = compact ?? autoCompact;

  useEffect(() => {
    // Skip measurement when compact is explicitly controlled by the caller.
    if (compact != null) return;
    const el = rowRef.current;
    if (!el) return;
    const ro = new ResizeObserver((entries) => {
      const width = entries[0]?.contentRect.width ?? 0;
      // Derive button width and gap from the effective styles.
      const btnWidth = (sty?.button?.width as number | undefined) ?? Layout.demoInstrumentBtn;
      const gap = (sty?.row?.gap as number | undefined) ?? ((classNames?.row || sty?.row) ? 0 : Gap.md);
      const needed = instruments.length * btnWidth + (instruments.length - 1) * gap;
      setAutoCompact(width > 0 && width < needed);
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, [compact, instruments.length, sty?.button?.width, sty?.row?.gap, sty?.row, classNames?.row]);

  // Style overrides (sty) take precedence over classNames, which take precedence over defaults.
  const rowClass = sty ? undefined : classNames?.row;
  const btnClass = sty ? undefined : classNames?.button;
  const btnActiveClass = sty ? undefined : (classNames?.buttonActive ?? classNames?.button);
  const arrowClass = sty ? undefined : classNames?.arrowButton;

  const rowStyle = sty?.row ?? (rowClass ? undefined : filterStyles.instrumentRow);
  const btnStyle = sty?.button;
  const btnActiveStyle = sty?.buttonActive ?? sty?.button;
  const arrowStyle = sty?.arrowButton;
  const useStyOverride = !!sty;

  const cycle = useCallback((dir: 1 | -1) => {
    if (!selected) {
      if (deferSelection) {
        setPreviewIdx(prev => (prev + dir + instruments.length) % instruments.length);
      } else {
        onSelect(instruments[dir === 1 ? 0 : instruments.length - 1]!.key);
      }
      return;
    }
    const idx = instruments.findIndex(i => i.key === selected);
    const next = (idx + dir + instruments.length) % instruments.length;
    onSelect(instruments[next]!.key);
  }, [instruments, selected, onSelect, deferSelection]);

  return (
    <>
      <div ref={rowRef} className={rowClass} style={rowStyle}>
        {isCompact ? (
          <>
            <button onClick={() => cycle(-1)} className={arrowClass || undefined} style={arrowStyle} aria-label={compactLabels?.previous}>
              <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><path d="M10 3L5 8L10 13" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/></svg>
            </button>
            <button
              className={btnActiveClass || undefined}
              style={btnActiveStyle ?? (btnActiveClass ? undefined : filterStyles.instrumentBtn)}
              onClick={() => onSelect(selected ? (required ? selected : null) : (previewKey ?? instruments[0]!.key))}
            >
              {useStyOverride ? (
                <InstrumentIcon instrument={selected ?? previewKey ?? instruments[0]!.key} size={Size.iconInstrument} />
              ) : (
                <>
                  <div style={selected ? filterStyles.instrumentCircleActive : filterStyles.instrumentCircle} />
                  <div style={filterStyles.instrumentIconWrap}>
                    <InstrumentIcon instrument={selected ?? previewKey ?? instruments[0]!.key} size={Size.iconInstrument} />
                  </div>
                </>
              )}
            </button>
            <button onClick={() => cycle(1)} className={arrowClass || undefined} style={arrowStyle} aria-label={compactLabels?.next}>
              <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><path d="M6 3L11 8L6 13" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/></svg>
            </button>
          </>
        ) : (
          instruments.map(inst => {
            const isSelected = selected === inst.key;
            return (
              <button
                key={inst.key}
                className={(isSelected ? btnActiveClass : btnClass) || undefined}
                style={
                  useStyOverride
                    ? (isSelected ? btnActiveStyle : btnStyle)
                    : ((isSelected ? btnActiveClass : btnClass) ? undefined : filterStyles.instrumentBtn)
                }
                onClick={() => onSelect(isSelected && !required ? null : inst.key)}
                title={inst.label ?? (SERVER_INSTRUMENT_LABELS as Record<string, string>)[inst.key] ?? inst.key}
              >
                {useStyOverride ? (
                  <InstrumentIcon instrument={inst.key} size={Size.iconInstrument} />
                ) : (
                  <>
                    <div style={isSelected ? filterStyles.instrumentCircleActive : filterStyles.instrumentCircle} />
                    <div style={filterStyles.instrumentIconWrap}>
                      <InstrumentIcon instrument={inst.key} size={Size.iconInstrument} />
                    </div>
                  </>
                )}
              </button>
            );
          })
        )}
      </div>

      {children && (
        <div style={{ ...filterStyles.instrumentFiltersWrap, gridTemplateRows: hasSelection ? '1fr' : '0fr' }}>
          <div style={filterStyles.instrumentFiltersInner}>
            {children}
          </div>
        </div>
      )}
    </>
  );
}
