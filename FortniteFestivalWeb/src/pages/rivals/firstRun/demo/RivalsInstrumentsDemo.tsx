/* eslint-disable react/forbid-dom-props -- useStyles pattern */
/**
 * First-run demo: Per-instrument rival sections with periodic row swaps.
 * Swap count scales with visible rows (1 if ≤3, 2 if ≤6, 3 otherwise).
 */
import { useState, useEffect, useCallback, useRef, useMemo, type CSSProperties } from 'react';
import type { RivalSummary } from '@festival/core/api/serverTypes';
import RivalRow from '../../components/RivalRow';
import InstrumentHeader from '../../../../components/display/InstrumentHeader';
import FadeIn from '../../../../components/page/FadeIn';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';
import { DEMO_INSTRUMENT_RIVALS } from '../../../../firstRun/demoData';
import { InstrumentHeaderSize } from '@festival/core';
import type { ServerInstrumentKey } from '@festival/core/api/serverTypes';
import {
  Gap, Opacity, CssValue, PointerEvents, flexColumn,
  FADE_DURATION, DEMO_SWAP_INTERVAL_MS,
} from '@festival/theme';

const INSTRUMENTS: ServerInstrumentKey[] = ['Solo_Guitar', 'Solo_Drums', 'Solo_Vocals'];
const RIVAL_ROW_HEIGHT = 100;
const SECTION_HEADER_HEIGHT = 36;
const NOOP = () => {};

type InstrumentRow = { instrument: ServerInstrumentKey; above: RivalSummary; below: RivalSummary };

/** Flat index for all rival rows across all instrument sections. */
type FlatRef = { instIdx: number; dir: 'above' | 'below' };

export default function RivalsInstrumentsDemo() {
  const h = useSlideHeight();
  const s = useStyles();

  const budget = h || 320;
  const sectionHeight = SECTION_HEADER_HEIGHT + 2 * (RIVAL_ROW_HEIGHT + 2) + Gap.md;
  const maxSections = Math.max(1, Math.floor(budget / sectionHeight));
  const visibleInstruments = INSTRUMENTS.slice(0, Math.min(maxSections, INSTRUMENTS.length));

  const [rows, setRows] = useState<InstrumentRow[]>(() =>
    visibleInstruments.map(inst => {
      const pool = DEMO_INSTRUMENT_RIVALS[inst]!;
      return { instrument: inst, above: pool.above[0]!, below: pool.below[0]! };
    }),
  );
  const [fadingKeys, setFadingKeys] = useState<ReadonlySet<string>>(new Set());
  const poolIdxRef = useRef<Record<string, { above: number; below: number }>>(
    Object.fromEntries(visibleInstruments.map(inst => [inst, { above: 1, below: 1 }])),
  );

  // Resize
  useEffect(() => {
    setRows(prev => {
      if (prev.length === visibleInstruments.length) return prev;
      return visibleInstruments.map(inst => {
        const existing = prev.find(r => r.instrument === inst);
        if (existing) return existing;
        const pool = DEMO_INSTRUMENT_RIVALS[inst]!;
        return { instrument: inst, above: pool.above[0]!, below: pool.below[0]! };
      });
    });
  }, [visibleInstruments.length]);

  const rotate = useCallback(() => {
    if (rows.length === 0) return;

    // Total rival rows across all visible instrument sections
    const totalRows = rows.length * 2;
    const swapCount = totalRows <= 3 ? 1 : totalRows <= 6 ? 2 : 3;

    // Build flat index of all rows, pick N unique
    const allRefs: FlatRef[] = [];
    for (let i = 0; i < rows.length; i++) {
      allRefs.push({ instIdx: i, dir: 'above' });
      allRefs.push({ instIdx: i, dir: 'below' });
    }
    // Shuffle and take swapCount
    for (let i = allRefs.length - 1; i > 0; i--) {
      const j = Math.floor(Math.random() * (i + 1));
      [allRefs[i], allRefs[j]] = [allRefs[j]!, allRefs[i]!];
    }
    const targets = allRefs.slice(0, swapCount);

    // Fade the targeted rows
    const keys = new Set<string>();
    for (const t of targets) {
      const row = rows[t.instIdx]!;
      keys.add(t.dir === 'above' ? row.above.accountId : row.below.accountId);
    }
    setFadingKeys(keys);

    setTimeout(() => {
      setRows(prev => {
        const next = [...prev];
        for (const t of targets) {
          const inst = next[t.instIdx]!.instrument;
          const pool = DEMO_INSTRUMENT_RIVALS[inst]![t.dir];
          const ref = poolIdxRef.current[inst] ?? { above: 1, below: 1 };
          const nextPoolIdx = ref[t.dir] % pool.length;
          ref[t.dir] = nextPoolIdx + 1;
          next[t.instIdx] = { ...next[t.instIdx]!, [t.dir]: pool[nextPoolIdx]! };
        }
        return next;
      });
      setFadingKeys(new Set());
    }, FADE_DURATION);
  }, [rows]);

  useEffect(() => {
    const timer = setInterval(rotate, DEMO_SWAP_INTERVAL_MS);
    return () => clearInterval(timer);
  }, [rotate]);

  let idx = 0;

  return (
    <div style={s.wrapper}>
      {rows.map(row => (
        <div key={row.instrument} style={s.section}>
          <FadeIn delay={(idx++) * 80} style={s.header}>
            <InstrumentHeader instrument={row.instrument} size={InstrumentHeaderSize.SM} />
          </FadeIn>
          <div style={s.list}>
            <FadeIn delay={(idx++) * 80}>
              <div style={fadingKeys.has(row.above.accountId) ? s.fading : s.visible}>
                <RivalRow rival={row.above} direction="above" onClick={NOOP} />
              </div>
            </FadeIn>
            <FadeIn delay={(idx++) * 80}>
              <div style={fadingKeys.has(row.below.accountId) ? s.fading : s.visible}>
                <RivalRow rival={row.below} direction="below" onClick={NOOP} />
              </div>
            </FadeIn>
          </div>
        </div>
      ))}
    </div>
  );
}

function useStyles() {
  return useMemo(() => {
    const trans = `opacity ${FADE_DURATION}ms ease, transform ${FADE_DURATION}ms ease`;
    return {
      wrapper: {
        ...flexColumn,
        gap: Gap.lg,
        width: CssValue.full,
        pointerEvents: PointerEvents.none,
      } as CSSProperties,
      section: {
        ...flexColumn,
        gap: Gap.sm,
      } as CSSProperties,
      header: {} as CSSProperties,
      list: {
        ...flexColumn,
        gap: 2,
      } as CSSProperties,
      visible: {
        transition: trans,
        opacity: 1,
        transform: 'translateY(0)',
      } as CSSProperties,
      fading: {
        transition: trans,
        opacity: Opacity.none,
        transform: 'translateY(4px)',
      } as CSSProperties,
    };
  }, []);
}
