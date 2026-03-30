/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useEffect, useRef, useState, useMemo, type CSSProperties, type ReactNode, type RefObject } from 'react';
import { createPortal } from 'react-dom';
import { Link } from 'react-router-dom';
import { useScrollContainer } from '../../contexts/ScrollContainerContext';
import Paginator from '../common/Paginator';
import ArcSpinner from '../common/ArcSpinner';
import { staggerDelay, IS_PWA } from '@festival/ui-utils';
import { Gap, Layout, STAGGER_INTERVAL, FADE_DURATION, SPINNER_FADE_MS } from '@festival/theme';
import { useIsWideDesktop } from '../../hooks/ui/useIsMobile';
import { LoadPhase } from '@festival/core';
import { plbStyles as s, fixedFooterWide } from './paginatedLeaderboardStyles';

export interface PaginatedLeaderboardProps<T> {
  /** Page entries to render. */
  entries: T[];

  /** Current page number (1-indexed for display). */
  page: number;
  /** Total number of pages. */
  totalPages: number;
  /** Called with a 1-indexed page number. */
  onGoToPage: (page: number) => void;

  /** Stable key extractor for each entry. */
  entryKey: (entry: T) => string;
  /** Returns true if the entry belongs to the tracked player. */
  isPlayerEntry: (entry: T) => boolean;
  /** Render the inner content of a row (not the Link wrapper). */
  renderRow: (entry: T, index: number) => ReactNode;
  /** Link destination for each row. */
  entryLinkTo: (entry: T, isPlayer: boolean) => string;
  /** Optional state passed to every row Link. */
  linkState?: unknown;
  /** Ref attached to the tracked player's row Link (for auto-scroll). */
  playerRowRef?: RefObject<HTMLAnchorElement | null>;

  /** Whether to show a fixed player footer below pagination. */
  hasPlayerFooter: boolean;
  /**
   * Render the player footer element.
   * Receives the className and style that must be applied to the clickable
   * inner element (Link or div) so it looks correct in both FAB and desktop layouts.
   */
  renderPlayerFooter?: (props: { className: string; style: CSSProperties }) => ReactNode;

  /** True when data is being fetched (entries may be stale or empty). */
  loading: boolean;
  /** Skip all animations on initial mount (return visit with cached data). */
  cached?: boolean;

  /** True when viewport width is below mobile breakpoint. */
  isMobile: boolean;
  /** True when the FAB (mobile chrome / PWA) is present. */
  hasFab: boolean;

  /** Hide pagination when there is an error. */
  error?: boolean;
  /** Message shown when entries is empty. */
  emptyMessage?: string;

  /** Ref for the Page's stagger rush callback (resetRush on page change). */
  staggerRushRef?: RefObject<(() => void) | undefined>;
}

/**
 * Shared paginated leaderboard shell.
 *
 * Owns the full animation lifecycle:
 * - Spinner → SpinnerOut → ContentIn load-phase transitions
 * - Stagger animation mode (first / paginate / cached)
 * - maxVisibleRows measurement for stagger delay capping
 * - Animation retirement after stagger window completes
 *
 * Renders rows in a frosted-card list with stagger animations,
 * portals fixed-position pagination + player footer to document.body,
 * and manages scroll-container margins so content never hides behind the footer.
 */
export function PaginatedLeaderboard<T>({
  entries,
  page,
  totalPages,
  onGoToPage,
  entryKey,
  isPlayerEntry,
  renderRow,
  entryLinkTo,
  linkState,
  playerRowRef,
  hasPlayerFooter,
  renderPlayerFooter,
  loading,
  cached: skipAllAnim = false,
  isMobile,
  hasFab,
  error = false,
  emptyMessage,
  staggerRushRef,
}: PaginatedLeaderboardProps<T>) {
  const isWideDesktop = useIsWideDesktop();
  const wideOverride = isWideDesktop ? fixedFooterWide : undefined;
  const scrollContainerRef = useScrollContainer();

  const hasPagination = totalPages > 1;
  const hasLoadedOnce = useRef(!loading);

  // ── Animation lifecycle ──
  const [animMode, setAnimMode] = useState<'first' | 'paginate' | 'cached'>(
    skipAllAnim ? 'cached' : 'first',
  );
  const [loadPhase, setLoadPhase] = useState<LoadPhase>(
    !loading ? LoadPhase.ContentIn : LoadPhase.Loading,
  );
  const loadPhaseRef = useRef(loadPhase);
  loadPhaseRef.current = loadPhase;

  // Compute maxVisibleRows from the scroll container's viewport height.
  const ROW_SLOT = Layout.entryRowHeight + Gap.sm;
  const scrollViewHeight = scrollContainerRef.current?.clientHeight
    ?? Math.max(0, window.innerHeight - (isMobile ? 120 : 200));
  const maxVisibleRows = useMemo(
    () => Math.min(entries.length, Math.max(1, Math.ceil(scrollViewHeight / ROW_SLOT))),
    [entries.length, scrollViewHeight, ROW_SLOT],
  );

  // Spinner → SpinnerOut → ContentIn transition + animation retirement.
  useEffect(() => {
    if (loading || error) {
      setLoadPhase(LoadPhase.Loading);
      return;
    }
    // Data arrived.
    hasLoadedOnce.current = true;
    // If already showing content (e.g. return visit), stay there.
    if (loadPhaseRef.current === LoadPhase.ContentIn) return;

    // Fade out spinner, then show content.
    setLoadPhase(LoadPhase.SpinnerOut);
    let retireId: ReturnType<typeof setTimeout>;
    const id = setTimeout(() => {
      staggerRushRef?.current?.();
      setAnimMode(prev => prev === 'cached' ? 'paginate' : prev);
      footerShownRef.current = false;
      setLoadPhase(LoadPhase.ContentIn);
      // Retire stagger animations after they've had time to finish.
      const staggerWindow = maxVisibleRows * STAGGER_INTERVAL + FADE_DURATION + 100;
      retireId = setTimeout(() => setAnimMode('cached'), staggerWindow);
    }, SPINNER_FADE_MS);
    return () => { clearTimeout(id); clearTimeout(retireId); };
  // eslint-disable-next-line react-hooks/exhaustive-deps -- animation sequence
  }, [loading, error]);

  // Reset animMode to 'paginate' when `page` changes (after initial mount).
  const prevPageRef = useRef(page);
  useEffect(() => {
    if (page !== prevPageRef.current) {
      prevPageRef.current = page;
      setAnimMode('paginate');
    }
  }, [page]);

  // ── Scroll margin management ──
  useEffect(() => {
    const el = scrollContainerRef.current;
    if (!el) return;
    let margin: number;
    if (hasFab) {
      margin = Layout.fabPaddingBottom;
      if (hasPagination) {
        const pwaOffset = IS_PWA ? Gap.section - Gap.md : 0;
        const paginationCssBottom = hasPlayerFooter
          ? Layout.fabBottom + pwaOffset + (Layout.fabSize - Layout.entryRowHeight) / 2 + Layout.entryRowHeight + Gap.sm
          : Layout.fabBottom + pwaOffset + Layout.fabSize + Gap.sm;
        const paginationTop = paginationCssBottom + Layout.paginationHeight;
        const naturalHeight = el.clientHeight + (parseFloat(el.style.marginBottom) || 0);
        const headerHeight = el.getBoundingClientRect().top;
        const vpHeight = window.innerHeight;
        const bottomNavHeight = vpHeight - headerHeight - naturalHeight;
        const scrollBottomOffset = margin + bottomNavHeight;
        margin += Math.max(0, paginationTop - scrollBottomOffset) + Gap.sm;
      }
    } else {
      margin = 0;
      if (hasPlayerFooter) margin += Layout.entryRowHeight + Gap.xl;
      if (hasPagination) margin += Layout.paginationHeight + Gap.xl;
    }
    el.style.marginBottom = `${margin}px`;
    return () => { el.style.marginBottom = ''; };
  }, [hasFab, hasPagination, hasPlayerFooter, scrollContainerRef]);

  const contentVisible = loadPhase === LoadPhase.ContentIn;

  // Player footer staggers in once after the last visible row on first load.
  // While waiting, it stays hidden (opacity: 0). Once contentVisible + stagger
  // delay fires, it fades in. footerShownRef is only flipped in onAnimationEnd
  // so that mid-animation re-renders don't kill the in-flight animation.
  // Note: we intentionally DON'T check animMode here — the row-level retirement
  // (animMode→cached) fires before the footer animation completes. footerShownRef
  // is the sole guard against re-animation.
  const footerShownRef = useRef(skipAllAnim);
  const footerStaggerStyle: CSSProperties | undefined = footerShownRef.current
    ? undefined
    : contentVisible
      ? { opacity: 0, animation: `fadeInUp ${FADE_DURATION}ms ease-out ${maxVisibleRows * STAGGER_INTERVAL}ms forwards` }
      : { opacity: 0 };

  return (
    <>
      {/* ── Spinner ── */}
      {!contentVisible && (
        <div
          style={{
            display: 'flex', alignItems: 'center', justifyContent: 'center',
            minHeight: 'calc(100vh - 350px)',
            ...(loadPhase === LoadPhase.SpinnerOut
              ? { animation: `fadeOut ${SPINNER_FADE_MS}ms ease-out forwards` }
              : {}),
          }}
        >
          <ArcSpinner />
        </div>
      )}

      {/* ── Row list (only mounted when content is visible) ── */}
      {contentVisible && (
        <div style={s.list}>
          {entries.map((entry, i) => {
            const isPlayer = isPlayerEntry(entry);
            const rowStyle = isPlayer ? s.playerEntryRow : s.entryRow;
            const delay = animMode === 'cached' ? null : (staggerDelay(i, STAGGER_INTERVAL, maxVisibleRows) ?? 0);
            const staggerStyle: CSSProperties | undefined = delay != null
              ? { opacity: 0, animation: `fadeInUp ${FADE_DURATION}ms ease-out ${delay}ms forwards` }
              : undefined;
            return (
              <Link
                key={entryKey(entry)}
                ref={isPlayer && playerRowRef ? playerRowRef : undefined}
                to={entryLinkTo(entry, isPlayer)}
                state={linkState}
                style={{ ...rowStyle, ...staggerStyle }}
                onAnimationEnd={(ev) => {
                  /* v8 ignore start — animation cleanup */
                  const el = ev.currentTarget;
                  el.style.opacity = '';
                  el.style.animation = '';
                  /* v8 ignore stop */
                }}
              >
                {renderRow(entry, i)}
              </Link>
            );
          })}
          {entries.length === 0 && emptyMessage && (
            <div style={s.emptyRow}>{emptyMessage}</div>
          )}
        </div>
      )}

      {/* v8 ignore start — fixed pagination + player footer portaled to body */}
      {createPortal(
        <>
          {hasLoadedOnce.current && !error && hasPagination && (
            <div style={{ ...(hasFab
              ? (hasPlayerFooter ? s.mobilePagination : s.mobilePaginationNoPlayer)
              : (hasPlayerFooter ? s.desktopPagination : s.desktopPaginationNoPlayer)), ...wideOverride }}>
              <Paginator
                className={hasFab ? 'fab-player-footer' : ''}
                style={isMobile ? s.paginationMobile : s.pagination}
                onSkipPrev={() => onGoToPage(1)}
                onPrev={() => onGoToPage(page - 1)}
                onNext={() => onGoToPage(page + 1)}
                onSkipNext={() => onGoToPage(totalPages)}
                prevDisabled={page <= 1}
                nextDisabled={page >= totalPages}
              >
                <span style={s.pageInfoBadge}>
                  {page.toLocaleString()} / {totalPages.toLocaleString()}
                </span>
              </Paginator>
            </div>
          )}
          {hasPlayerFooter && renderPlayerFooter && (
            <div
              style={{ ...(hasFab ? s.playerFooterFab : s.desktopPlayerFooter), ...wideOverride, ...footerStaggerStyle }}
              onAnimationEnd={(ev) => {
                footerShownRef.current = true;
                const el = ev.currentTarget;
                el.style.opacity = '';
                el.style.animation = '';
              }}
            >
              {renderPlayerFooter({
                className: hasFab ? 'fab-player-footer' : '',
                style: s.playerFooterRow,
              })}
            </div>
          )}
        </>,
        document.body,
      )}
      {/* v8 ignore stop */}
    </>
  );
}
