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
  /** Optional instruments to hide from the rendered selector without changing the source list. */
  hiddenInstruments?: readonly K[];
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
  /** Lead instrument signature ("Guitar" or "Keyboard") for icon variant. */
  sig?: string;
  children?: React.ReactNode;
}

export function InstrumentSelector<K extends AnyInstrumentKey = ServerInstrumentKey>({
  instruments, selected, onSelect, hiddenInstruments, required, compact,
  compactLabels, deferSelection, classNames, styles: sty, sig, children,
}: InstrumentSelectorProps<K>) {
  const hiddenInstrumentSet = hiddenInstruments ? new Set(hiddenInstruments) : null;
  const availableItems = hiddenInstrumentSet
    ? instruments.filter(inst => !hiddenInstrumentSet.has(inst.key))
    : instruments;
  const effectiveSelected = selected != null && availableItems.some(inst => inst.key === selected)
    ? selected
    : null;
  const hasSelection = effectiveSelected != null;

  // Local preview index for compact mode when deferSelection is on and nothing is selected.
  const [previewIdx, setPreviewIdx] = useState(0);
  // Reset preview when instruments list changes (e.g. settings toggle)
  useEffect(() => { setPreviewIdx(0); }, [availableItems.length]);
  const previewKey = availableItems[previewIdx]?.key ?? availableItems[0]?.key;

  // Auto-compact: when `compact` is undefined, measure the row to decide.
  const rowRef = useRef<HTMLDivElement>(null);
  const [autoCompact, setAutoCompact] = useState(false);
  const isCompact = (compact ?? autoCompact) && availableItems.length > 0;

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
      const needed = availableItems.length > 0
        ? availableItems.length * btnWidth + (availableItems.length - 1) * gap
        : 0;
      setAutoCompact(width > 0 && availableItems.length > 0 && width < needed);
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, [compact, availableItems.length, sty?.button?.width, sty?.row?.gap, sty?.row, classNames?.row]);

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
  const compactPreviewKey = effectiveSelected ?? previewKey;

  const cycle = useCallback((dir: 1 | -1) => {
    if (availableItems.length === 0) {
      return;
    }
    if (!effectiveSelected) {
      if (deferSelection) {
        setPreviewIdx(prev => (prev + dir + availableItems.length) % availableItems.length);
      } else {
        onSelect(availableItems[dir === 1 ? 0 : availableItems.length - 1]!.key);
      }
      return;
    }
    const idx = availableItems.findIndex(i => i.key === effectiveSelected);
    const next = (idx + dir + availableItems.length) % availableItems.length;
    onSelect(availableItems[next]!.key);
  }, [availableItems, effectiveSelected, onSelect, deferSelection]);

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
              onClick={() => {
                if (effectiveSelected) {
                  onSelect(required ? effectiveSelected : null);
                  return;
                }
                if (compactPreviewKey) {
                  onSelect(compactPreviewKey);
                }
              }}
            >
              {useStyOverride ? (
                compactPreviewKey ? <InstrumentIcon instrument={compactPreviewKey} sig={sig} size={Size.iconInstrument} /> : null
              ) : (
                <>
                  <div style={effectiveSelected ? filterStyles.instrumentCircleActive : filterStyles.instrumentCircle} />
                  <div style={filterStyles.instrumentIconWrap}>
                    {compactPreviewKey ? <InstrumentIcon instrument={compactPreviewKey} sig={sig} size={Size.iconInstrument} /> : null}
                  </div>
                </>
              )}
            </button>
            <button onClick={() => cycle(1)} className={arrowClass || undefined} style={arrowStyle} aria-label={compactLabels?.next}>
              <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><path d="M6 3L11 8L6 13" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/></svg>
            </button>
          </>
        ) : (
          availableItems.map(inst => {
            const isSelected = effectiveSelected === inst.key;
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
                  <InstrumentIcon instrument={inst.key} sig={sig} size={Size.iconInstrument} />
                ) : (
                  <>
                    <div style={isSelected ? filterStyles.instrumentCircleActive : filterStyles.instrumentCircle} />
                    <div style={filterStyles.instrumentIconWrap}>
                      <InstrumentIcon instrument={inst.key} sig={sig} size={Size.iconInstrument} />
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
