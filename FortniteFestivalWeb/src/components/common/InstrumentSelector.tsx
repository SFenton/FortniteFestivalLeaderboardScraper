/**
 * Reusable instrument selector with collapsible content section.
 * Shows a row of instrument circle buttons; clicking one expands children below it.
 * Used in FilterModal, SuggestionsFilterModal, PathsModal, and ScoreHistoryChart.
 */
import { useCallback } from 'react';
import { Size } from '@festival/theme';
import { SERVER_INSTRUMENT_LABELS, type ServerInstrumentKey } from '@festival/core/api/serverTypes';
import { InstrumentIcon } from '../display/InstrumentIcons';
import filterCss from '../../pages/songs/modals/FilterModal.module.css';

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
  /** Override CSS class names for custom styling. */
  classNames?: InstrumentSelectorClassNames;
  children?: React.ReactNode;
}

export function InstrumentSelector({
  instruments, selected, onSelect, required, compact,
  compactLabels, classNames, children,
}: InstrumentSelectorProps) {
  const hasSelection = selected != null;

  const rowClass = classNames?.row ?? filterCss.instrumentRow;
  const btnClass = classNames?.button ?? filterCss.instrumentBtn;
  const btnActiveClass = classNames?.buttonActive ?? btnClass;
  const arrowClass = classNames?.arrowButton ?? '';

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
      <div className={rowClass}>
        {compact ? (
          <>
            <button onClick={() => cycle(-1)} className={arrowClass} aria-label={compactLabels?.previous}>
              <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><path d="M10 3L5 8L10 13" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/></svg>
            </button>
            <button className={btnActiveClass}>
              {selected && <InstrumentIcon instrument={selected} size={Size.iconInstrument} />}
            </button>
            <button onClick={() => cycle(1)} className={arrowClass} aria-label={compactLabels?.next}>
              <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><path d="M6 3L11 8L6 13" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/></svg>
            </button>
          </>
        ) : (
          instruments.map(inst => {
            const isSelected = selected === inst.key;
            return (
              <button
                key={inst.key}
                className={isSelected ? btnActiveClass : btnClass}
                onClick={() => onSelect(isSelected && !required ? null : inst.key)}
                title={inst.label ?? SERVER_INSTRUMENT_LABELS[inst.key]}
              >
                <div className={isSelected ? filterCss.instrumentCircleActive : filterCss.instrumentCircle} />
                <div className={filterCss.instrumentIconWrap}>
                  <InstrumentIcon instrument={inst.key} size={Size.iconInstrument} />
                </div>
              </button>
            );
          })
        )}
      </div>

      {children && (
        <div className={filterCss.instrumentFiltersWrap} style={{ gridTemplateRows: hasSelection ? '1fr' : '0fr' }}>
          <div className={filterCss.instrumentFiltersInner}>
            {children}
          </div>
        </div>
      )}
    </>
  );
}
