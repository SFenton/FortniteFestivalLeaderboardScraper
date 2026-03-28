/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useState, useEffect, useRef, useMemo, useCallback, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import type { ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import { INSTRUMENT_LABELS } from '@festival/core/api/serverTypes';
import { InstrumentSelector, type InstrumentSelectorItem } from '../../../../components/common/InstrumentSelector';
import { ToggleRow } from '../../../../components/common/ToggleRow';
import FadeIn from '../../../../components/page/FadeIn';
import { useSettings, visibleInstruments } from '../../../../contexts/SettingsContext';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';
import {
  Colors, Font, Weight, Gap, Layout, Opacity, LineHeight, Border,
  Display, Align, Justify, Position, Cursor, Overflow, CssValue, CssProp,
  flexCenter, border, transition, IconSize, TRANSITION_MS, NAV_TRANSITION_MS,
} from '@festival/theme';


/** Shared demo styles used by FilterDemo, SortDemo, InstrumentFilterDemo, SortControlsDemo. */
export function useDemoStyles() {
  return useMemo(() => {
    const iconButton: CSSProperties = {
      background: CssValue.none,
      border: CssValue.none,
      borderRadius: CssValue.circle,
      width: Layout.demoInstrumentBtn,
      height: Layout.demoInstrumentBtn,
      padding: Gap.none,
      cursor: Cursor.pointer,
      transition: transition(CssProp.all, NAV_TRANSITION_MS),
      ...flexCenter,
      opacity: Opacity.disabled,
      position: Position.relative,
      overflow: Overflow.hidden,
    };
    return {
      iconRow: {
        display: Display.flex,
        justifyContent: Justify.center,
        alignItems: Align.center,
        gap: Gap.lg,
        width: CssValue.full,
        overflow: Overflow.hidden,
      } as CSSProperties,
      iconButton,
      iconButtonActive: {
        ...iconButton,
        backgroundColor: Colors.statusGreen,
        opacity: 1,
      } as CSSProperties,
      arrowButton: {
        background: CssValue.none,
        border: border(Border.thin, Colors.borderPrimary),
        borderRadius: CssValue.circle,
        width: IconSize.lg,
        height: IconSize.lg,
        padding: Gap.none,
        cursor: Cursor.pointer,
        ...flexCenter,
        color: Colors.textSecondary,
        lineHeight: LineHeight.none,
        transition: transition(CssProp.all, NAV_TRANSITION_MS),
      } as CSSProperties,
      wrapper: { width: CssValue.full } as CSSProperties,
      instrumentSection: { marginBottom: Gap.md } as CSSProperties,
      modeSection: { marginBottom: Gap.lg } as CSSProperties,
      modeSectionCompact: { marginBottom: Gap.none } as CSSProperties,
      sectionHeader: {
        fontSize: Font.lg,
        fontWeight: Weight.bold,
        color: Colors.textPrimary,
        marginBottom: Gap.sm,
      } as CSSProperties,
      sectionHint: {
        fontSize: Font.sm,
        color: Colors.textSecondary,
        marginBottom: Gap.md,
        lineHeight: LineHeight.snug,
      } as CSSProperties,
    };
  }, []);
}

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

  const s = useDemoStyles();

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

  const selectorStyleOverrides = useMemo(() => ({
    row: s.iconRow,
    button: s.iconButton,
    buttonActive: s.iconButtonActive,
    arrowButton: s.arrowButton,
  }), [s]);

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
