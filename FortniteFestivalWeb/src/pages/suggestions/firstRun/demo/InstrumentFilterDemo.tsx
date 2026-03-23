/**
 * First-run demo: Per-instrument suggestion type toggles.
 * Instrument selector row + per-instrument type toggles, matching SuggestionsFilterModal.
 */
import { useState, useEffect, useRef, useMemo, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { InstrumentSelector, type InstrumentSelectorItem } from '../../../../components/common/InstrumentSelector';
import { ToggleRow } from '../../../../components/common/ToggleRow';
import FadeIn from '../../../../components/page/FadeIn';
import { useSettings, visibleInstruments } from '../../../../contexts/SettingsContext';
import { SUGGESTION_TYPES } from '@festival/core/suggestions/suggestionFilterConfig';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';
import { Gap, Layout, TRANSITION_MS } from '@festival/theme';
import s from '../../../songs/firstRun/demo/FilterDemo.module.css';

type ToggleState = { label: string; desc: string; on: boolean };

function typeToggles(): ToggleState[] {
  return SUGGESTION_TYPES.map(st => ({ label: st.label, desc: st.description, on: true }));
}

export default function InstrumentFilterDemo() {
  const { t } = useTranslation();
  const { settings } = useSettings();
  const instruments = useMemo(() => visibleInstruments(settings), [settings]);
  const selectorItems = useMemo<InstrumentSelectorItem[]>(
    () => instruments.map(key => ({ key })),
    [instruments],
  );

  const [instrument, setInstrument] = useState(instruments[0] ?? null);

  const togglesRef = useRef<Record<string, ToggleState[]>>({});
  const getToggles = useCallback((key: string) => {
    if (!togglesRef.current[key]) { togglesRef.current[key] = typeToggles(); }
    return togglesRef.current[key]!;
  }, []);

  const [toggles, setToggles] = useState<ToggleState[]>(() =>
    instrument ? getToggles(instrument) : [],
  );

  const toggle = (i: number) => {
    setToggles(prev => {
      const next = prev.map((t, j) => j === i ? { ...t, on: !t.on } : t);
      if (instrument) { togglesRef.current[instrument] = next; }
      return next;
    });
  };

  const handleSelectInstrument = useCallback((k: string | null) => {
    if (instrument) { togglesRef.current[instrument] = toggles; }
    setInstrument(k);
    if (k) { setToggles(getToggles(k)); }
  }, [instrument, toggles, getToggles]);

  const rowRef = useRef<HTMLDivElement>(null);
  const [compact, setCompact] = useState(false);
  useEffect(() => {
    const el = rowRef.current;
    if (!el) return;
    const ro = new ResizeObserver(entries => {
      const width = entries[0]?.contentRect.width ?? 0;
      const needed = instruments.length * Layout.demoInstrumentBtn + (instruments.length - 1) * Gap.lg;
      setCompact(width < needed);
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, [instruments.length]);

  const selectorClassNames = useMemo(() => ({
    row: s.iconRow,
    button: s.iconButton,
    buttonActive: s.iconButtonActive,
    arrowButton: s.arrowButton,
  }), []);

  const h = useSlideHeight();
  const [maxToggles, setMaxToggles] = useState(SUGGESTION_TYPES.length);
  const [showHeader, setShowHeader] = useState(true);

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
              {t('firstRun.suggestions.demo.perInstrumentHeader')}
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
