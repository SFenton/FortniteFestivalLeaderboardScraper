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
import { Layout, TRANSITION_MS } from '@festival/theme';
import { useIsMobile } from '../../../../hooks/ui/useIsMobile';
import { useDemoStyles } from '../../../songs/firstRun/demo/FilterDemo';

const MOBILE_FIT_BUFFER = Layout.filterToggleRowHeight;

type ToggleState = { label: string; desc: string; on: boolean };

function typeToggles(): ToggleState[] {
  return SUGGESTION_TYPES.map(st => ({ label: st.label, desc: st.description, on: true }));
}

export default function InstrumentFilterDemo() {
  const { t } = useTranslation();
  const { settings } = useSettings();
  const isMobile = useIsMobile();
  const s = useDemoStyles();
  const instruments = useMemo(() => visibleInstruments(settings), [settings]);
  const selectorItems = useMemo<InstrumentSelectorItem[]>(
    () => instruments.map(key => ({ key })),
    [instruments],
  );

  /* v8 ignore start -- instruments always has entries; null guard is defensive */
  const [instrument, setInstrument] = useState(instruments[0] ?? null);
  /* v8 ignore stop */

  const togglesRef = useRef<Record<string, ToggleState[]>>({});
  const getToggles = useCallback((key: string) => {
    if (!togglesRef.current[key]) { togglesRef.current[key] = typeToggles(); }
    return togglesRef.current[key]!;
  }, []);

  const [toggles, setToggles] = useState<ToggleState[]>(() =>
    /* v8 ignore next -- instrument always set from instruments[0] */
    instrument ? getToggles(instrument) : [],
  );

  const toggle = (i: number) => {
    setToggles(prev => {
      const next = prev.map((t, j) => j === i ? { ...t, on: !t.on } : t);
      /* v8 ignore start -- instrument always set when toggles are visible */
      if (instrument) { togglesRef.current[instrument] = next; }
      /* v8 ignore stop */
      return next;
    });
  };

  const handleSelectInstrument = useCallback((k: typeof instrument) => {
    /* v8 ignore start -- instrument always set when switching */
    if (instrument) { togglesRef.current[instrument] = toggles; }
    /* v8 ignore stop */
    setInstrument(k);
    if (k) { setToggles(getToggles(k)); }
  }, [instrument, toggles, getToggles]);

  const selectorStyleOverrides = useMemo(() => ({
    row: s.iconRow,
    button: s.iconButton,
    buttonActive: s.iconButtonActive,
    arrowButton: s.arrowButton,
  }), [s]);

  const h = useSlideHeight();
  const [maxToggles, setMaxToggles] = useState<number>(SUGGESTION_TYPES.length);
  const [showHeader, setShowHeader] = useState(true);

  useEffect(() => {
    if (!h) return;
    const availableHeight = Math.max(0, h - (isMobile ? MOBILE_FIT_BUFFER : 0));
    const afterInstr = availableHeight - Layout.filterInstrumentRowHeight;
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
  }, [h, instrument, isMobile, toggles.length]);

  return (
    <div style={s.wrapper}>
      <FadeIn delay={0} style={s.instrumentSection}>
        <InstrumentSelector
          instruments={selectorItems}
          selected={instrument}
          onSelect={handleSelectInstrument}
          styles={selectorStyleOverrides}
        />
      </FadeIn>

      {instrument && maxToggles > 0 && (
        <FadeIn delay={TRANSITION_MS}>
          {showHeader && (
            <div style={s.sectionHeader}>
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
