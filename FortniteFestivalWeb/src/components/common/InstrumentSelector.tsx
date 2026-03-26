/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
/**
 * Reusable instrument selector with collapsible content section.
 * Shows a row of instrument circle buttons; clicking one expands children below it.
 * Used in FilterModal, SuggestionsFilterModal, PathsModal, and ScoreHistoryChart.
 */
import { useCallback, type CSSProperties } from 'react';
import { Size } from '@festival/theme';
import { SERVER_INSTRUMENT_LABELS, type ServerInstrumentKey } from '@festival/core/api/serverTypes';
import { InstrumentIcon } from '../display/InstrumentIcons';
import { filterStyles } from '../../pages/songs/modals/filterStyles';

export interface InstrumentSelectorItem {
  key: ServerInstrumentKey;
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

export interface InstrumentSelectorProps {
  instruments: InstrumentSelectorItem[];
  selected: ServerInstrumentKey | null;
  onSelect: (key: ServerInstrumentKey | null) => void;
  /** When true, clicking the selected instrument does not deselect it. */
  required?: boolean;
  /** When true, shows arrow buttons with a single active icon instead of the full row. */
  compact?: boolean;
  /** Accessible labels for compact arrow buttons. */
  compactLabels?: { previous?: string; next?: string };
  /** @deprecated Prefer `styles` for new code. Override CSS class names for custom styling. */
  classNames?: InstrumentSelectorClassNames;
  /** Inline style overrides. Takes precedence over classNames. */
  styles?: InstrumentSelectorStyleOverrides;
  children?: React.ReactNode;
}

export function InstrumentSelector({
  instruments, selected, onSelect, required, compact,
  compactLabels, classNames, styles: sty, children,
}: InstrumentSelectorProps) {
  const hasSelection = selected != null;

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
    /* v8 ignore start */
    if (!selected) return;
    /* v8 ignore stop */
    const idx = instruments.findIndex(i => i.key === selected);
    const next = (idx + dir + instruments.length) % instruments.length;
    onSelect(instruments[next]!.key);
  }, [instruments, selected, onSelect]);

  return (
    <>
      <div className={rowClass} style={rowStyle}>
        {compact ? (
          <>
            <button onClick={() => cycle(-1)} className={arrowClass || undefined} style={arrowStyle} aria-label={compactLabels?.previous}>
              <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><path d="M10 3L5 8L10 13" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/></svg>
            </button>
            <button className={btnActiveClass || undefined} style={btnActiveStyle ?? (btnActiveClass ? undefined : filterStyles.instrumentBtn)}>
              {selected && <InstrumentIcon instrument={selected} size={Size.iconInstrument} />}
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
                title={inst.label ?? SERVER_INSTRUMENT_LABELS[inst.key]}
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
