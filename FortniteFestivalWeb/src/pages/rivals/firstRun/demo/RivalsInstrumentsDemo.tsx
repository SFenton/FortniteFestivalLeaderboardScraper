/* eslint-disable react/forbid-dom-props -- useStyles pattern */
/**
 * First-run demo: Per-instrument rival sections with periodic row swaps.
 * Falls back to a single visible rival card per instrument when space is tight.
 */
import { useState, useEffect, useCallback, useRef, useMemo, type CSSProperties } from 'react';
import type { RivalSummary, ServerInstrumentKey } from '@festival/core/api/serverTypes';
import { InstrumentHeaderSize } from '@festival/core';
import {
  Gap, Opacity, CssValue, PointerEvents, flexColumn,
  FADE_DURATION, DEMO_SWAP_INTERVAL_MS,
} from '@festival/theme';
import RivalRow from '../../components/RivalRow';
import InstrumentHeader from '../../../../components/display/InstrumentHeader';
import FadeIn from '../../../../components/page/FadeIn';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';
import { DEMO_INSTRUMENT_RIVALS } from '../../../../firstRun/demoData';
import { useRivalsSharedStyles } from '../../useRivalsSharedStyles';
import { useContainerWidth } from '../../../../hooks/ui/useContainerWidth';

const INSTRUMENTS: ServerInstrumentKey[] = ['Solo_Guitar', 'Solo_Drums', 'Solo_Vocals'];
const COMPACT_RIVAL_ROW_HEIGHT = 100;
const DEFAULT_RIVAL_ROW_HEIGHT = 76;
const WIDE_RIVAL_ROW_HEIGHT = 60;
const SECTION_HEADER_HEIGHT = 36;
const DOUBLE_CARD_SECTION_BUFFER = 80;
const NARROW_ROW_BREAKPOINT = 380;
const WIDE_ROW_BREAKPOINT = 620;
const NOOP = () => {};

type RivalDirection = 'above' | 'below';
type InstrumentRow = { instrument: ServerInstrumentKey; above: RivalSummary; below: RivalSummary };
type FlatRef = { instIdx: number; dir: RivalDirection };

function estimateRivalRowHeight(containerWidth: number | undefined) {
  if (!containerWidth || containerWidth <= NARROW_ROW_BREAKPOINT) {
    return COMPACT_RIVAL_ROW_HEIGHT;
  }

  if (containerWidth >= WIDE_ROW_BREAKPOINT) {
    return WIDE_RIVAL_ROW_HEIGHT;
  }

  return DEFAULT_RIVAL_ROW_HEIGHT;
}

function countVisibleSections(budget: number, rowHeight: number, cardCount: number) {
  const sectionHeight = SECTION_HEADER_HEIGHT + cardCount * (rowHeight + 2) + Gap.sm;
  return Math.max(1, Math.floor((budget + Gap.lg) / (sectionHeight + Gap.lg)));
}

export default function RivalsInstrumentsDemo() {
  const h = useSlideHeight();
  const s = useStyles();
  const shared = useRivalsSharedStyles();
  const wrapperRef = useRef<HTMLDivElement>(null);
  const measuredWidth = useContainerWidth(wrapperRef);
  const containerWidth = measuredWidth > 0
    ? measuredWidth
    : (typeof window !== 'undefined' ? window.innerWidth : undefined);
  const rowHeightEstimate = estimateRivalRowHeight(containerWidth);

  const budget = h || 320;
  const doubleSectionHeight = SECTION_HEADER_HEIGHT + 2 * (rowHeightEstimate + 2) + Gap.sm + DOUBLE_CARD_SECTION_BUFFER;
  const useSingleCardMode = budget <= doubleSectionHeight;
  const visibleCardCount = useSingleCardMode ? 1 : 2;
  const maxSections = useSingleCardMode ? 1 : countVisibleSections(budget, rowHeightEstimate, visibleCardCount);
  const visibleInstruments = INSTRUMENTS.slice(0, Math.min(maxSections, INSTRUMENTS.length));

  const [rows, setRows] = useState<InstrumentRow[]>(() =>
    visibleInstruments.map(inst => {
      const pool = DEMO_INSTRUMENT_RIVALS[inst]!;
      return { instrument: inst, above: pool.above[0]!, below: pool.below[0]! };
    }),
  );
  const [singleCardDirections, setSingleCardDirections] = useState<Partial<Record<ServerInstrumentKey, RivalDirection>>>(
    () => Object.fromEntries(visibleInstruments.map(inst => [inst, 'above'])) as Partial<Record<ServerInstrumentKey, RivalDirection>>,
  );
  const [fadingKeys, setFadingKeys] = useState<ReadonlySet<string>>(new Set());
  const poolIdxRef = useRef<Record<string, { above: number; below: number }>>(
    Object.fromEntries(INSTRUMENTS.map(inst => [inst, { above: 1, below: 1 }])),
  );

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

  useEffect(() => {
    setSingleCardDirections(prev => {
      const next: Partial<Record<ServerInstrumentKey, RivalDirection>> = {};
      for (const inst of visibleInstruments) {
        next[inst] = prev[inst] ?? 'above';
      }
      return next;
    });
  }, [visibleInstruments]);

  const rotate = useCallback(() => {
    if (rows.length === 0) return;

    if (useSingleCardMode) {
      const allRefs: FlatRef[] = rows.map((row, instIdx) => ({
        instIdx,
        dir: singleCardDirections[row.instrument] ?? 'above',
      }));
      const swapCount = allRefs.length <= 3 ? 1 : allRefs.length <= 6 ? 2 : 3;

      for (let i = allRefs.length - 1; i > 0; i--) {
        const j = Math.floor(Math.random() * (i + 1));
        [allRefs[i], allRefs[j]] = [allRefs[j]!, allRefs[i]!];
      }
      const targets = allRefs.slice(0, swapCount);

      const keys = new Set<string>();
      for (const t of targets) {
        const row = rows[t.instIdx]!;
        keys.add(row[t.dir].accountId);
      }
      setFadingKeys(keys);

      const currentDirections = singleCardDirections;
      setTimeout(() => {
        const nextDirections = { ...currentDirections };

        setRows(prev => {
          const next = [...prev];
          for (const t of targets) {
            const inst = next[t.instIdx]!.instrument;
            const nextDir: RivalDirection = (currentDirections[inst] ?? 'above') === 'above' ? 'below' : 'above';
            const pool = DEMO_INSTRUMENT_RIVALS[inst]![nextDir];
            const ref = poolIdxRef.current[inst] ?? { above: 1, below: 1 };
            const nextPoolIdx = ref[nextDir] % pool.length;
            ref[nextDir] = nextPoolIdx + 1;
            poolIdxRef.current[inst] = ref;
            next[t.instIdx] = { ...next[t.instIdx]!, [nextDir]: pool[nextPoolIdx]! };
            nextDirections[inst] = nextDir;
          }
          return next;
        });

        setSingleCardDirections(nextDirections);
        setFadingKeys(new Set());
      }, FADE_DURATION);

      return;
    }

    const totalRows = rows.length * 2;
    const swapCount = totalRows <= 3 ? 1 : totalRows <= 6 ? 2 : 3;

    const allRefs: FlatRef[] = [];
    for (let i = 0; i < rows.length; i++) {
      allRefs.push({ instIdx: i, dir: 'above' });
      allRefs.push({ instIdx: i, dir: 'below' });
    }
    for (let i = allRefs.length - 1; i > 0; i--) {
      const j = Math.floor(Math.random() * (i + 1));
      [allRefs[i], allRefs[j]] = [allRefs[j]!, allRefs[i]!];
    }
    const targets = allRefs.slice(0, swapCount);

    const keys = new Set<string>();
    for (const t of targets) {
      const row = rows[t.instIdx]!;
      keys.add(row[t.dir].accountId);
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
          poolIdxRef.current[inst] = ref;
          next[t.instIdx] = { ...next[t.instIdx]!, [t.dir]: pool[nextPoolIdx]! };
        }
        return next;
      });
      setFadingKeys(new Set());
    }, FADE_DURATION);
  }, [rows, singleCardDirections, useSingleCardMode]);

  useEffect(() => {
    const timer = setInterval(rotate, DEMO_SWAP_INTERVAL_MS);
    return () => clearInterval(timer);
  }, [rotate]);

  let idx = 0;

  return (
    <div ref={wrapperRef} style={s.wrapper} data-testid="rivals-fre-instruments-wrapper" data-card-mode={useSingleCardMode ? 'single' : 'double'}>
      {rows.map(row => {
        const visibleDirection = singleCardDirections[row.instrument] ?? 'above';
        const visibleRival = row[visibleDirection];

        return (
          <div
            key={row.instrument}
            style={s.section}
            data-testid={`rivals-fre-instrument-section-${row.instrument}`}
            data-visible-cards={useSingleCardMode ? 1 : 2}
            data-visible-direction={useSingleCardMode ? visibleDirection : 'both'}
          >
            <FadeIn delay={(idx++) * 80} style={s.header}>
              <InstrumentHeader instrument={row.instrument} size={InstrumentHeaderSize.SM} />
            </FadeIn>
            <div style={{ ...shared.rivalList, ...s.list }}>
              {useSingleCardMode ? (
                <FadeIn delay={(idx++) * 80}>
                  <div style={fadingKeys.has(visibleRival.accountId) ? s.fading : s.visible}>
                    <RivalRow rival={visibleRival} direction={visibleDirection} onClick={NOOP} />
                  </div>
                </FadeIn>
              ) : (
                <>
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
                </>
              )}
            </div>
          </div>
        );
      })}
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
