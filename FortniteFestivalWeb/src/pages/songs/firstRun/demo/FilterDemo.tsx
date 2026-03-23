import { useState, useEffect, useRef, useMemo, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import type { ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import { INSTRUMENT_LABELS } from '@festival/core/api/serverTypes';
import { InstrumentSelector, type InstrumentSelectorItem } from '../../../../components/common/InstrumentSelector';
import { ToggleRow } from '../../../../components/common/ToggleRow';
import FadeIn from '../../../../components/page/FadeIn';
import { useSettings, visibleInstruments } from '../../../../contexts/SettingsContext';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';
import { Gap, Layout, TRANSITION_MS } from '@festival/theme';
import s from './FilterDemo.module.css';

type ToggleState = { label: string; desc: string; on: boolean };

/** Toggles matching the real FilterModal's instrument-specific filters. */
function instrumentToggles(_key: InstrumentKey, t: (key: string) => string): ToggleState[] {
  return [
    { label: t('filter.seasonTitle'), desc: t('filter.seasonHint'), on: false },
    { label: t('filter.percentileTitle'), desc: t('filter.percentileHint'), on: false },
    { label: t('filter.starsTitle'), desc: t('filter.starsHint'), on: true },
    { label: t('filter.intensityTitle'), desc: t('filter.intensityHint'), on: false },
  ];
}

export default function FilterDemo() {
  const { t } = useTranslation();
  const { settings } = useSettings();
  const instruments = useMemo(() => visibleInstruments(settings), [settings]);
  const selectorItems = useMemo<InstrumentSelectorItem[]>(
    () => instruments.map(key => ({ key })),
    [instruments],
  );

  /* v8 ignore start -- instruments always has entries; instrument/null guards are defensive */
  const [instrument, setInstrument] = useState<InstrumentKey | null>(() => instruments[0] ?? null);

  // Per-instrument toggle state — persists across instrument switches.
  const togglesRef = useRef<Record<string, ToggleState[]>>({});
  const getToggles = useCallback((key: InstrumentKey) => {
    if (!togglesRef.current[key]) togglesRef.current[key] = instrumentToggles(key, t);
    return togglesRef.current[key]!;
  }, [t]);

  const [toggles, setToggles] = useState<ToggleState[]>(() =>
    instrument ? getToggles(instrument) : [],
  );

  const toggle = (i: number) => {
    setToggles(prev => {
      const next = prev.map((t, j) => j === i ? { ...t, on: !t.on } : t);
      if (instrument) togglesRef.current[instrument] = next;
      return next;
    });
  };

  const handleSelectInstrument = useCallback((k: InstrumentKey | null) => {
    if (instrument) togglesRef.current[instrument] = toggles;
    setInstrument(k);
    if (k) setToggles(getToggles(k));
  }, [instrument, toggles, getToggles]);
  /* v8 ignore stop */

  // Compact mode: measure container width to decide.
  const rowRef = useRef<HTMLDivElement>(null);
  const [compact, setCompact] = useState(false);
  useEffect(() => {
    const el = rowRef.current;
    /* v8 ignore start -- ref is always set after render */
    if (!el) return;
    /* v8 ignore stop */
    /* v8 ignore start -- ResizeObserver callback depends on real DOM measurements */
    const ro = new ResizeObserver(entries => {
      const width = entries[0]?.contentRect.width ?? 0;
      const needed = instruments.length * Layout.demoInstrumentBtn + (instruments.length - 1) * Gap.lg;
      setCompact(width < needed);
    });
    /* v8 ignore stop */
    ro.observe(el);
    return () => ro.disconnect();
  }, [instruments.length]);

  const selectorClassNames = useMemo(() => ({
    row: s.iconRow,
    button: s.iconButton,
    buttonActive: s.iconButtonActive,
    arrowButton: s.arrowButton,
  }), []);

  const [maxToggles, setMaxToggles] = useState(4);
  const [showHeader, setShowHeader] = useState(true);
  const h = useSlideHeight();

  useEffect(() => {
    if (!h) return;
    const afterInstr = h - Layout.filterInstrumentRowHeight;
    if (!instrument || afterInstr < Layout.filterToggleRowHeight) {
      setMaxToggles(0);
      setShowHeader(false);
      return;
    }
    if (afterInstr >= Layout.filterHeaderHeight + Layout.filterToggleRowHeight) {
      setShowHeader(true);
      const count = Math.floor((afterInstr - Layout.filterHeaderHeight) / Layout.filterToggleRowHeight);
      setMaxToggles(Math.min(count, toggles.length));
    } else {
      setShowHeader(false);
      const count = Math.floor(afterInstr / Layout.filterToggleRowHeight);
      setMaxToggles(Math.min(count, toggles.length));
    }
  }, [h, instrument, toggles.length]);

  return (
    <div className={s.wrapper}>
      <FadeIn delay={0} className={s.instrumentSection}>
        <div ref={rowRef}>
          <InstrumentSelector
            instruments={selectorItems}
            selected={instrument}
            onSelect={handleSelectInstrument}
            compact={compact}
            classNames={selectorClassNames}
          />
        </div>
      </FadeIn>

      {instrument && maxToggles > 0 && (
        <FadeIn delay={TRANSITION_MS}>
          {showHeader && (
            <div className={s.sectionHeader}>
              {t('filter.instrumentHeader', { instrument: INSTRUMENT_LABELS[instrument] })}
            </div>
          )}
          {toggles.slice(0, maxToggles).map((t, i) => (
            <ToggleRow key={t.label} label={t.label} description={t.desc} checked={t.on} onToggle={() => toggle(i)} />
          ))}
        </FadeIn>
      )}
    </div>
  );
}
