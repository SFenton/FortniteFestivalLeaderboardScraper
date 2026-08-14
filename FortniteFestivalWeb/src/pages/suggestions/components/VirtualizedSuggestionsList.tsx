import {
  useCallback,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  type CSSProperties,
  type FocusEvent,
  type RefObject,
} from 'react';
import { defaultRangeExtractor, useVirtualizer } from '@tanstack/react-virtual';
import type { BandType } from '@festival/core/api';
import { LoadPhase } from '@festival/core/runtime';
import type { LeaderboardData, SuggestionCategory } from '@festival/core/types';
import { Gap } from '@festival/theme';
import FadeIn from '../../../components/page/FadeIn';
import { useIsNarrow } from '../../../hooks/ui/useIsMobile';
import { useScrollFade } from '../../../hooks/ui/useScrollFade';
import { useVirtualListScrollMargin } from '../../../hooks/ui/useVirtualListScrollMargin';
import { getCardDelay } from '../suggestionsHelpers';
import { CategoryCard } from './CategoryCard';

const CATEGORY_HEADER_ESTIMATE = 92;
const CATEGORY_ROW_ESTIMATE = 72;
const CATEGORY_ROW_NARROW_ESTIMATE = 104;
const MIN_CATEGORY_ESTIMATE = 180;
const VIRTUAL_OVERSCAN = 4;

export type VisibleSuggestionRow = {
  id: string;
  sourceIndex: number;
  category: SuggestionCategory;
};

type VirtualizedSuggestionsListProps = {
  rows: VisibleSuggestionRow[];
  phase: LoadPhase;
  skipAnimation: boolean;
  revealedCount: number;
  identity: string | null;
  measurementKey: string;
  categoryLimit: number;
  generatedCategoryCount: number;
  loadTriggerCount: number;
  scrollContainerRef: RefObject<HTMLElement | null>;
  albumArtMap: Map<string, string>;
  scoresIndex: Record<string, LeaderboardData>;
  bandType?: BandType;
};

export function VirtualizedSuggestionsList({
  rows,
  phase,
  skipAnimation,
  revealedCount,
  identity,
  measurementKey,
  categoryLimit,
  generatedCategoryCount,
  loadTriggerCount,
  scrollContainerRef,
  albumArtMap,
  scoresIndex,
  bandType,
}: VirtualizedSuggestionsListProps) {
  const listRef = useRef<HTMLDivElement>(null);
  const isNarrow = useIsNarrow();
  const [focusedRowId, setFocusedRowId] = useState<string | null>(null);
  const focusedRowIndex = useMemo(
    () => focusedRowId ? rows.findIndex(row => row.id === focusedRowId) : -1,
    [focusedRowId, rows],
  );

  const scrollMargin = useVirtualListScrollMargin({
    scrollContainerRef,
    listRef,
    enabled: phase === LoadPhase.ContentIn && rows.length > 0,
    revision: `${identity ?? ''}:${rows.length}`,
  });

  const rangeExtractor = useCallback((range: Parameters<typeof defaultRangeExtractor>[0]) => {
    const indexes = defaultRangeExtractor(range);
    if (focusedRowIndex >= 0 && !indexes.includes(focusedRowIndex)) {
      indexes.push(focusedRowIndex);
      indexes.sort((left, right) => left - right);
    }
    return indexes;
  }, [focusedRowIndex]);

  const virtualizer = useVirtualizer({
    count: phase === LoadPhase.ContentIn ? rows.length : 0,
    getScrollElement: () => scrollContainerRef.current,
    getItemKey: index => rows[index]?.id ?? index,
    estimateSize: index => estimateCategoryHeight(rows[index]?.category, isNarrow),
    gap: Gap.section,
    paddingEnd: Gap.section,
    overscan: VIRTUAL_OVERSCAN,
    rangeExtractor,
    scrollMargin,
  });
  const previousMeasurementKeyRef = useRef(measurementKey);

  useEffect(() => {
    virtualizer.measure();
  }, [identity, isNarrow, virtualizer]);

  useLayoutEffect(() => {
    if (previousMeasurementKeyRef.current === measurementKey) return;
    previousMeasurementKeyRef.current = measurementKey;
    const scrollElement = scrollContainerRef.current;
    if (!scrollElement) return;

    virtualizer.shouldAdjustScrollPositionOnItemSizeChange = () => false;
    virtualizer.measure();
    scrollElement.scrollTo(0, 0);
    let secondFrame = 0;
    const firstFrame = requestAnimationFrame(() => {
      scrollElement.scrollTo(0, 0);
      secondFrame = requestAnimationFrame(() => {
        scrollElement.scrollTo(0, 0);
        virtualizer.shouldAdjustScrollPositionOnItemSizeChange = undefined;
      });
    });

    return () => {
      cancelAnimationFrame(firstFrame);
      cancelAnimationFrame(secondFrame);
      virtualizer.shouldAdjustScrollPositionOnItemSizeChange = undefined;
    };
  }, [measurementKey, scrollContainerRef, virtualizer]);

  useScrollFade(
    scrollContainerRef,
    listRef,
    [identity, phase, rows.length > 0],
    { dynamicChildren: true },
  );

  const handleFocusCapture = useCallback((event: FocusEvent<HTMLDivElement>) => {
    const row = event.target instanceof Element
      ? event.target.closest<HTMLElement>('[data-suggestion-row-id]')
      : null;
    setFocusedRowId(row?.dataset.suggestionRowId ?? null);
  }, []);

  const handleBlurCapture = useCallback((event: FocusEvent<HTMLDivElement>) => {
    const nextRow = event.relatedTarget instanceof Element
      ? event.relatedTarget.closest<HTMLElement>('[data-suggestion-row-id]')
      : null;
    setFocusedRowId(nextRow?.dataset.suggestionRowId ?? null);
  }, []);

  return (
    <div
      ref={listRef}
      data-testid="suggestions-list"
      data-category-limit={categoryLimit}
      data-generated-category-count={generatedCategoryCount}
      data-visible-category-count={rows.length}
      data-load-trigger-count={loadTriggerCount}
      data-suggestions-cache-key={identity ?? undefined}
      onFocusCapture={handleFocusCapture}
      onBlurCapture={handleBlurCapture}
      style={{
        height: virtualizer.getTotalSize(),
        position: 'relative',
        width: '100%',
      }}
    >
      {virtualizer.getVirtualItems().map((virtualRow) => {
        const row = rows[virtualRow.index]!;
        const delay = getCardDelay(
          virtualRow.index,
          skipAnimation,
          phase,
          revealedCount,
        );
        return (
          <div
            key={row.id}
            ref={virtualizer.measureElement}
            data-index={virtualRow.index}
            data-source-index={row.sourceIndex}
            data-suggestion-row-id={row.id}
            style={{
              position: 'absolute',
              top: 0,
              left: 0,
              width: '100%',
              transform: `translateY(${virtualRow.start - virtualizer.options.scrollMargin}px)`,
            }}
          >
            <FadeIn
              delay={delay === -1 ? undefined : (delay ?? 0)}
              hidden={delay === null}
            >
              <CategoryCard
                category={row.category}
                albumArtMap={albumArtMap}
                scoresIndex={scoresIndex}
                bandType={bandType}
                style={cardStyle}
              />
            </FadeIn>
          </div>
        );
      })}
    </div>
  );
}

function estimateCategoryHeight(
  category: SuggestionCategory | undefined,
  isNarrow: boolean,
): number {
  if (!category) return MIN_CATEGORY_ESTIMATE;
  const rowEstimate = isNarrow ? CATEGORY_ROW_NARROW_ESTIMATE : CATEGORY_ROW_ESTIMATE;
  return Math.max(
    MIN_CATEGORY_ESTIMATE,
    CATEGORY_HEADER_ESTIMATE + category.songs.length * rowEstimate,
  );
}

const cardStyle: CSSProperties = {
  marginBottom: 0,
};
