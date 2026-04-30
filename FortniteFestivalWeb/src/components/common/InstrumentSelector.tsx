/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
/**
 * Reusable instrument selector with collapsible content section.
 * Shows a row of instrument circle buttons; clicking one expands children below it.
 * When the row is too narrow to fit all buttons, automatically switches to compact
 * arrow-cycling mode (unless overridden via the explicit `compact` prop).
 * Used in FilterModal, SuggestionsFilterModal, PathsModal, and ScoreHistoryChart.
 */
import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties } from 'react';
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
  /** Optional instruments to render but prevent from being selected. */
  disabledInstruments?: readonly K[];
  /** Optional instruments to render as conflicting while still allowing selection. */
  mutedInstruments?: readonly K[];
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
  instruments, selected, onSelect, hiddenInstruments, disabledInstruments, mutedInstruments, required, compact,
  compactLabels, deferSelection, classNames, styles: sty, sig, children,
}: InstrumentSelectorProps<K>) {
  const hiddenInstrumentSet = useMemo(() => hiddenInstruments ? new Set(hiddenInstruments) : null, [hiddenInstruments]);
  const disabledInstrumentSet = useMemo(() => disabledInstruments ? new Set(disabledInstruments) : null, [disabledInstruments]);
  const mutedInstrumentSet = useMemo(() => mutedInstruments ? new Set(mutedInstruments) : null, [mutedInstruments]);
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

  const findNextSelectableIndex = useCallback((startIndex: number, dir: 1 | -1) => {
    for (let offset = 1; offset <= availableItems.length; offset += 1) {
      const next = (startIndex + (offset * dir) + availableItems.length) % availableItems.length;
      const nextItem = availableItems[next];
      if (nextItem && !disabledInstrumentSet?.has(nextItem.key)) return next;
    }
    return -1;
  }, [availableItems, disabledInstrumentSet]);

  const cycle = useCallback((dir: 1 | -1) => {
    if (availableItems.length === 0) {
      return;
    }
    if (!effectiveSelected) {
      if (deferSelection) {
        setPreviewIdx(prev => (prev + dir + availableItems.length) % availableItems.length);
      } else {
        const edgeIndex = dir === 1 ? -1 : 0;
        const nextIndex = findNextSelectableIndex(edgeIndex, dir);
        if (nextIndex >= 0) onSelect(availableItems[nextIndex]!.key);
      }
      return;
    }
    const idx = availableItems.findIndex(i => i.key === effectiveSelected);
    const next = findNextSelectableIndex(idx, dir);
    if (next < 0) return;
    onSelect(availableItems[next]!.key);
  }, [availableItems, effectiveSelected, onSelect, deferSelection, findNextSelectableIndex]);

  const compactPreviewDisabled = compactPreviewKey != null && disabledInstrumentSet?.has(compactPreviewKey);
  const compactPreviewMuted = !compactPreviewDisabled && compactPreviewKey != null && mutedInstrumentSet?.has(compactPreviewKey);
  const compactButtonStyle = mergeInstrumentButtonStyle(
    btnActiveStyle ?? (btnActiveClass ? undefined : filterStyles.instrumentBtn),
    compactPreviewDisabled ? instrumentDisabledStyle : compactPreviewMuted ? instrumentMutedStyle : undefined,
  );

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
              style={compactButtonStyle}
              disabled={compactPreviewDisabled}
              data-conflict={compactPreviewMuted ? 'true' : undefined}
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
            const isDisabled = disabledInstrumentSet?.has(inst.key) ?? false;
            const isMuted = !isSelected && !isDisabled && (mutedInstrumentSet?.has(inst.key) ?? false);
            const baseButtonStyle = useStyOverride
              ? (isSelected ? btnActiveStyle : btnStyle)
              : ((isSelected ? btnActiveClass : btnClass) ? undefined : filterStyles.instrumentBtn);
            return (
              <button
                key={inst.key}
                className={(isSelected ? btnActiveClass : btnClass) || undefined}
                style={mergeInstrumentButtonStyle(baseButtonStyle, isDisabled ? instrumentDisabledStyle : isMuted ? instrumentMutedStyle : undefined)}
                disabled={isDisabled}
                data-conflict={isMuted ? 'true' : undefined}
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

const instrumentMutedStyle: CSSProperties = {
  cursor: 'pointer',
  filter: 'grayscale(1)',
  opacity: 0.42,
};

const instrumentDisabledStyle: CSSProperties = {
  cursor: 'not-allowed',
  filter: 'grayscale(1)',
  opacity: 0.28,
};

function mergeInstrumentButtonStyle(base: CSSProperties | undefined, state: CSSProperties | undefined) {
  if (!state) return base;
  return { ...(base ?? {}), ...state };
}
