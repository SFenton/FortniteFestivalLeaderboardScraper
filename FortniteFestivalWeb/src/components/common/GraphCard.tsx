/* eslint-disable react/forbid-dom-props -- useStyles pattern */
/**
 * GraphCard — reusable chart shell with instrument selector, pagination,
 * animated detail card, animated list, and "view all" button.
 *
 * Generic over data-point type T. All chart-type-specific rendering is
 * delegated to render-prop slots.
 */
import { memo, useMemo, useRef, useState, useCallback, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { CardPhase } from '@festival/core';
import type { ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import { InstrumentSelector, type InstrumentSelectorItem } from './InstrumentSelector';
import { useChartDimensions } from '../../hooks/chart/useChartDimensions';
import { useIsMobile } from '../../hooks/ui/useIsMobile';
import { useChartPagination } from '../../hooks/chart/useChartPagination';
import { useCardAnimation } from '../../hooks/chart/useCardAnimation';
import { useListAnimation } from '../../hooks/chart/useListAnimation';

import {
  Colors, Font, Gap, Size, Layout, Radius, Weight,
  CHART_ANIM_SETTLE,
  frostedCard, padding, border, flexCenter, transition,
  Cursor, CssValue, Display, Align, Justify, Overflow, Position, Opacity,
} from '@festival/theme';

/* ── Props ── */

export type GraphCardProps<T> = {
  /** Full chart data array (all points, not just visible). */
  data: T[];
  /** Whether data is still loading. */
  loading: boolean;
  /** Instrument selector config. */
  instruments: InstrumentSelectorItem<InstrumentKey>[];
  /** Currently selected instrument key. */
  selected: InstrumentKey;
  /** Called when the user picks a different instrument. */
  onInstrumentSelect: (key: InstrumentKey) => void;
  /** Header title text. */
  title: string;
  /** Header subtitle text. */
  subtitle: string;
  /** Text shown while loading. */
  loadingMessage: string;
  /** Text shown when data is empty. */
  emptyMessage: string;
  /** Identity function to match two data points (for pagination point tracking). */
  identity: (a: T, b: T) => boolean;

  /**
   * Render the Recharts chart content.
   * Receives the visible (paginated) data, whether animate is active,
   * the current selected point, and a setter.
   */
  renderChart: (ctx: {
    visibleData: T[];
    animating: boolean;
    selectedPoint: T | null;
    setSelectedPoint: (p: T | null | ((prev: T | null) => T | null)) => void;
  }) => ReactNode;

  /** Render the detail card content for a selected point. */
  renderDetailCard?: (point: T) => ReactNode;

  /** Items for the animated list beneath the chart. */
  listData?: T[];
  /** Render a single list item. */
  renderListItem?: (point: T, index: number, phase: 'idle' | 'in' | 'out') => ReactNode;

  /** "View all" button label. If omitted, no button is shown. */
  viewAllLabel?: string;
  /** Called when "View all" is clicked. */
  onViewAll?: () => void;

  /** Skip animations (useful for tests). */
  skipAnimation?: boolean;
};

/* ── Component ── */

function GraphCardInner<T>({
  data,
  loading,
  instruments,
  selected,
  onInstrumentSelect,
  title,
  subtitle,
  loadingMessage,
  emptyMessage,
  identity,
  renderChart,
  renderDetailCard,
  listData,
  renderListItem,
  viewAllLabel,
  onViewAll,
  skipAnimation,
}: GraphCardProps<T>) {
  const { t } = useTranslation();
  const isMobile = useIsMobile();
  const st = useGraphCardStyles();

  // Chart container sizing → bar count
  const chartContainerRef = useRef<HTMLDivElement>(null);
  const { maxBars } = useChartDimensions(chartContainerRef);

  // Pagination + point selection
  const {
    setChartOffset, selectedPoint, setSelectedPoint,
    selectedIndex, visibleChartData, needsPagination,
    navigatePoint, backDisabled, forwardDisabled, maxOffset,
    pageStart, pageEnd,
  } = useChartPagination(data, maxBars, selected, identity);

  // Recharts animation on paginate
  const [animatingPage, setAnimatingPage] = useState(false);
  const pageAnimTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  /* v8 ignore start */
  const paginateChart = useCallback((action: () => void, willPageChange: boolean) => {
    if (!willPageChange) { action(); return; }
    if (pageAnimTimer.current) clearTimeout(pageAnimTimer.current);
    setAnimatingPage(true);
    action();
    pageAnimTimer.current = setTimeout(() => setAnimatingPage(false), CHART_ANIM_SETTLE);
  }, []);

  const isOnCurrentPage = useCallback((idx: number) => idx >= pageStart && idx < pageEnd, [pageStart, pageEnd]);
  /* v8 ignore stop */

  // Card animation (selected point detail)
  const { displayedPoint, cardPhase, cardHeight, cardContentRef } = useCardAnimation(selectedPoint);

  // List animation
  const { displayedCards, listPhase, listHeight } = useListAnimation(listData ?? [], skipAnimation);

  const compactLabels = useMemo(() => ({
    previous: t('aria.previousInstrument'),
    next: t('aria.nextInstrument'),
  }), [t]);

  const handleInstrumentSelect = useCallback((key: InstrumentKey | null) => {
    if (key) onInstrumentSelect(key);
  }, [onInstrumentSelect]);

  return (
    <div>
      <div style={st.chartContainer} ref={chartContainerRef}>
        {/* Instrument selector */}
        {instruments.length > 1 && (
          <div style={st.iconRowWrap}>
            <InstrumentSelector
              instruments={instruments}
              selected={selected}
              onSelect={handleInstrumentSelect}
              required
              compactLabels={compactLabels}
              styles={st.selectorStyles}
            />
          </div>
        )}

        {/* Header */}
        <div style={st.chartHeader}>
          <div style={st.chartTitle}>{title}</div>
          <div style={st.chartSubtitle}>{subtitle}</div>
        </div>

        {/* Loading / Empty / Chart */}
        {/* v8 ignore start */}
        {loading && <div style={st.placeholder}>{loadingMessage}</div>}
        {!loading && data.length === 0 && <div style={st.placeholder}>{emptyMessage}</div>}
        {!loading && data.length > 0 && renderChart({
          visibleData: visibleChartData,
          animating: animatingPage,
          selectedPoint,
          setSelectedPoint,
        })}
        {/* v8 ignore stop */}

        {/* Detail card (animated) */}
        {displayedPoint && renderDetailCard && (
          <div style={{
            overflow: 'hidden',
            maxHeight: (cardPhase === CardPhase.Growing || cardPhase === CardPhase.Open || cardPhase === CardPhase.Fading || cardPhase === CardPhase.SwapOut || cardPhase === CardPhase.SwapIn) ? cardHeight : 0,
            transition: `max-height 0.25s ${cardPhase === CardPhase.Shrinking ? 'ease-in' : 'ease-out'}`,
            marginTop: Gap.xl,
            alignSelf: 'stretch',
            ...(!isMobile ? { width: '50%', marginLeft: 'auto', marginRight: 'auto' } : {}),
          }}>
            <div style={{
              ...st.detailCard,
              ...(!isMobile ? st.detailCardDesktop : {}),
              opacity: (cardPhase === CardPhase.Open || cardPhase === CardPhase.SwapOut || cardPhase === CardPhase.SwapIn) ? 1 : 0,
              transform: (cardPhase === CardPhase.Open || cardPhase === CardPhase.SwapOut || cardPhase === CardPhase.SwapIn) ? 'translateY(0)' : 'translateY(-8px)',
              transition: 'opacity 0.15s ease, transform 0.15s ease',
            }} ref={cardContentRef}>
              <div style={{
                display: 'flex', alignItems: 'center', gap: Gap.xl, width: '100%',
                opacity: (cardPhase === CardPhase.Open || cardPhase === CardPhase.SwapIn) ? 1 : (cardPhase === CardPhase.SwapOut ? 0 : undefined),
                transform: (cardPhase === CardPhase.Open || cardPhase === CardPhase.SwapIn) ? 'translateY(0)' : (cardPhase === CardPhase.SwapOut ? 'translateY(-6px)' : undefined),
                transition: (cardPhase === CardPhase.SwapOut || cardPhase === CardPhase.SwapIn) ? 'opacity 0.12s ease, transform 0.12s ease' : 'none',
              }}>
                {renderDetailCard(displayedPoint)}
              </div>
            </div>
          </div>
        )}

        {/* Pagination controls */}
        {/* v8 ignore start */}
        {!loading && needsPagination && (
          <div style={st.chartPagination}>
            <button
              style={backDisabled ? st.pageButtonDisabled : st.pageButton}
              disabled={backDisabled}
              onClick={() => {
                const target = selectedIndex - maxBars;
                const willChange = selectedPoint ? !isOnCurrentPage(Math.max(0, target)) : true;
                paginateChart(() => {
                  if (selectedPoint) { navigatePoint(target); }
                  else { setChartOffset(o => Math.min(o + maxBars, maxOffset)); }
                }, willChange);
              }}
              aria-label={t('aria.backOnePage')}
            >
              <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><path d="M9 3L4 8L9 13" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/><path d="M14 3L9 8L14 13" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/></svg>
            </button>
            <button
              style={backDisabled ? st.pageButtonDisabled : st.pageButton}
              disabled={backDisabled}
              onClick={() => {
                const target = selectedIndex - 1;
                const willChange = selectedPoint ? !isOnCurrentPage(target) : true;
                paginateChart(() => {
                  if (selectedPoint) { navigatePoint(target); }
                  else { setChartOffset(o => Math.min(o + 1, maxOffset)); }
                }, willChange);
              }}
              aria-label={t('aria.backOneEntry')}
            >
              <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><path d="M10 3L5 8L10 13" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/></svg>
            </button>
            <button
              style={forwardDisabled ? st.pageButtonDisabled : st.pageButton}
              disabled={forwardDisabled}
              onClick={() => {
                const target = selectedIndex + 1;
                const willChange = selectedPoint ? !isOnCurrentPage(target) : true;
                paginateChart(() => {
                  if (selectedPoint) { navigatePoint(target); }
                  else { setChartOffset(o => Math.max(o - 1, 0)); }
                }, willChange);
              }}
              aria-label={t('aria.forwardOneEntry')}
            >
              <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><path d="M6 3L11 8L6 13" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/></svg>
            </button>
            <button
              style={forwardDisabled ? st.pageButtonDisabled : st.pageButton}
              disabled={forwardDisabled}
              onClick={() => {
                const target = selectedIndex + maxBars;
                const willChange = selectedPoint ? !isOnCurrentPage(Math.min(target, data.length - 1)) : true;
                paginateChart(() => {
                  if (selectedPoint) { navigatePoint(target); }
                  else { setChartOffset(o => Math.max(o - maxBars, 0)); }
                }, willChange);
              }}
              aria-label={t('aria.forwardOnePage')}
            >
              <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><path d="M7 3L12 8L7 13" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/><path d="M2 3L7 8L2 13" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/></svg>
            </button>
          </div>
        )}
        {/* v8 ignore stop */}
      </div>

      {/* Animated card list */}
      {renderListItem && (displayedCards.length > 0 || listHeight > 0) && (
        <div style={{
          overflow: 'clip',
          overflowClipMargin: 24,
          transition: 'height 0.3s ease',
          height: listHeight,
          marginTop: Gap.xl,
        }}>
          <div style={{ display: 'flex', flexDirection: 'column', gap: Gap.sm }}>
            {displayedCards.map((point, i) => renderListItem(point, i, listPhase))}
          </div>
        </div>
      )}

      {/* View all button */}
      {viewAllLabel && onViewAll && data.length > 0 && (
        <button style={st.viewAllButton} onClick={onViewAll}>
          {viewAllLabel}
        </button>
      )}
    </div>
  );
}

// memo wrapper preserving generic type — cast is safe because memo doesn't
// narrow the generic, and the inner component handles all props correctly.
const GraphCard = memo(GraphCardInner) as typeof GraphCardInner;
export default GraphCard;

/* ── Styles ── */

const FAST_TRANSITION = 'all 0.15s ease';

function useGraphCardStyles() {
  return useMemo(() => {
    const circleBtn: React.CSSProperties = {
      background: CssValue.none, border: border(1, Colors.borderPrimary), borderRadius: CssValue.circle,
      width: Size.iconLg, height: Size.iconLg, padding: 0, cursor: Cursor.pointer,
      ...flexCenter, color: Colors.textSecondary, transition: FAST_TRANSITION,
    };
    const selectorIconBtn: React.CSSProperties = {
      background: CssValue.none, border: CssValue.none, borderRadius: CssValue.circle,
      width: Layout.demoInstrumentBtn, height: Layout.demoInstrumentBtn,
      padding: 0, cursor: Cursor.pointer, transition: FAST_TRANSITION,
      ...flexCenter, opacity: Opacity.disabled,
      position: Position.relative, overflow: Overflow.hidden,
    };
    return {
      iconRowWrap: { width: '100%' } as React.CSSProperties,
      chartContainer: {
        ...frostedCard, borderRadius: Radius.lg, padding: padding(Gap.xl, Gap.xl, Gap.xl),
        display: 'flex', flexDirection: 'column' as const, alignItems: 'center',
      } as React.CSSProperties,
      placeholder: {
        color: Colors.textMuted, fontSize: Font.md, fontStyle: 'italic',
        textAlign: 'center' as const, padding: padding(Gap.section, 0), width: '100%',
      } as React.CSSProperties,
      chartHeader: { textAlign: 'center' as const, marginBottom: Gap.md } as React.CSSProperties,
      chartTitle: { color: Colors.textPrimary, fontSize: Font.title, fontWeight: Weight.bold } as React.CSSProperties,
      chartSubtitle: { color: Colors.textMuted, fontSize: Font.lg, marginTop: Gap.xs } as React.CSSProperties,
      detailCard: {
        display: 'flex', alignItems: 'center', gap: Gap.xl,
        minHeight: Size.iconXl, fontSize: Font.md, color: 'inherit',
        width: '100%', boxSizing: 'border-box' as const,
      } as React.CSSProperties,
      detailCardDesktop: {
        ...frostedCard, display: 'flex', alignItems: 'center', gap: Gap.xl,
        padding: padding(Gap.md, Gap.xl), minHeight: Size.iconXl, borderRadius: Radius.md,
        fontSize: Font.md, color: 'inherit', transition: transition('border-color', 150),
      } as React.CSSProperties,
      chartPagination: {
        display: 'flex', justifyContent: 'center', alignItems: 'center',
        gap: Gap.md, paddingTop: Gap.xl, paddingBottom: Gap.md,
      } as React.CSSProperties,
      pageButton: { ...circleBtn } as React.CSSProperties,
      pageButtonDisabled: { ...circleBtn, opacity: 0.3, cursor: 'default' } as React.CSSProperties,
      viewAllButton: {
        ...frostedCard, display: 'flex', alignItems: 'center', justifyContent: 'center',
        width: '100%', height: Size.iconXl, marginTop: Gap.sm, borderRadius: Radius.md,
        color: Colors.textPrimary, fontSize: Font.md, fontWeight: Weight.semibold,
        cursor: 'pointer', transition: transition('background-color', 150),
      } as React.CSSProperties,
      selectorStyles: {
        row: { display: Display.flex, justifyContent: Justify.center, alignItems: Align.center, gap: Gap.lg, width: CssValue.full } as React.CSSProperties,
        button: selectorIconBtn,
        buttonActive: { ...selectorIconBtn, backgroundColor: Colors.statusGreen, opacity: 1 } as React.CSSProperties,
        arrowButton: { ...circleBtn } as React.CSSProperties,
      },
    };
  }, []);
}
