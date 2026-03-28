/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useEffect, type CSSProperties, type ReactNode, type RefObject } from 'react';
import { createPortal } from 'react-dom';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { useScrollContainer } from '../../contexts/ScrollContainerContext';
import { PaginationButton } from '../common/PaginationButton';
import { staggerDelay } from '@festival/ui-utils';
import { Gap, Layout, STAGGER_INTERVAL, FADE_DURATION } from '@festival/theme';
import { plbStyles as s } from './paginatedLeaderboardStyles';

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

  /** Current animation mode. */
  animMode: 'first' | 'paginate' | 'cached';
  /** Number of visible rows (used to cap stagger delays). */
  maxVisibleRows: number;

  /** True when viewport width is below mobile breakpoint. */
  isMobile: boolean;
  /** True when the FAB (mobile chrome / PWA) is present. */
  hasFab: boolean;

  /** Only show pagination after data has loaded at least once. */
  hasLoaded?: boolean;
  /** Hide pagination when there is an error. */
  error?: boolean;
  /** Message shown when entries is empty. */
  emptyMessage?: string;
}

/**
 * Shared paginated leaderboard shell.
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
  animMode,
  maxVisibleRows,
  isMobile,
  hasFab,
  hasLoaded = true,
  error = false,
  emptyMessage,
}: PaginatedLeaderboardProps<T>) {
  const { t } = useTranslation();
  const scrollContainerRef = useScrollContainer();

  const hasPagination = totalPages > 1;

  // ── Scroll margin management ──
  // Sets marginBottom on the app shell scroll container so the last row
  // never hides behind fixed-position pagination / player footer.
  // Pages using PaginatedLeaderboard pass fabSpacer="none" to Page so
  // that Page does not compete for the same marginBottom.
  // Layout.fabPaddingBottom is the calibrated clearance for the FAB region
  // (accounts for bottom nav overlap with the scroll container).
  // The player footer sits at the same vertical level as the FAB, so
  // fabPaddingBottom already covers it. Pagination stacks above both.
  useEffect(() => {
    const el = scrollContainerRef.current;
    if (!el) return;
    let margin: number;
    if (hasFab) {
      // Mobile: fabPaddingBottom (96px) shrinks the scroll container to clear
      // the FAB/player-footer region. When pagination is also present we need
      // additional margin so the gap between the last row and the pagination
      // equals the gap between pagination and the player footer (Gap.sm).
      //
      // Derive extra margin from the pagination's fixed position:
      //   paginationTop (from VP bottom) = mobilePagination.cssBottom + paginationHeight
      //   scrollBottomOffset = fabPaddingBottom + bottomNavHeight
      //   extra = paginationTop − scrollBottomOffset + Gap.sm
      //
      // bottomNavHeight isn't a Layout constant, but we can calculate it at
      // runtime from the scroll container's natural height.
      margin = Layout.fabPaddingBottom;
      if (hasPagination) {
        const paginationCssBottom = Layout.fabBottom
          + (Layout.fabSize - Layout.entryRowHeight) / 2
          + Layout.entryRowHeight + Gap.sm;
        const paginationTop = paginationCssBottom + Layout.paginationHeight;
        // Natural (no-margin) scroll container extends from rectTop to rectTop + (clientH + margin).
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

  return (
    <>
      {/* ── Row list ── */}
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

      {/* v8 ignore start — fixed pagination + player footer portaled to body */}
      {createPortal(
        <>
          {hasLoaded && !error && hasPagination && (
            <div style={hasFab ? s.mobilePagination : s.desktopPagination}>
              <div
                className={hasFab ? 'fab-player-footer' : ''}
                style={isMobile ? s.paginationMobile : s.pagination}
              >
                <PaginationButton disabled={page <= 1} onClick={() => onGoToPage(1)}>
                  {t('leaderboard.first')}
                </PaginationButton>
                <PaginationButton disabled={page <= 1} onClick={() => onGoToPage(page - 1)}>
                  {t('leaderboard.prev')}
                </PaginationButton>
                <span style={s.pageInfo}>
                  <span style={s.pageInfoBadge}>
                    {page.toLocaleString()} / {totalPages.toLocaleString()}
                  </span>
                </span>
                <PaginationButton disabled={page >= totalPages} onClick={() => onGoToPage(page + 1)}>
                  {t('leaderboard.next')}
                </PaginationButton>
                <PaginationButton disabled={page >= totalPages} onClick={() => onGoToPage(totalPages)}>
                  {t('leaderboard.last')}
                </PaginationButton>
              </div>
            </div>
          )}
          {hasPlayerFooter && renderPlayerFooter && (
            <div style={hasFab ? s.playerFooterFab : s.desktopPlayerFooter}>
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
