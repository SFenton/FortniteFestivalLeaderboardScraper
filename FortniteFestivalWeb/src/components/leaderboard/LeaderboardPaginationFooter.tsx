/* eslint-disable react/forbid-dom-props -- fixed footer uses shared inline shell styles */
import { useEffect, type CSSProperties } from 'react';
import { createPortal } from 'react-dom';
import { IS_PWA } from '@festival/ui-utils';
import { Gap, Layout } from '@festival/theme';
import { useScrollContainer } from '../../contexts/ScrollContainerContext';
import { useIsWideDesktop } from '../../hooks/ui/useIsMobile';
import Paginator from '../common/Paginator';
import { fixedFooterWide, plbStyles as s } from './paginatedLeaderboardStyles';

type LeaderboardFooterScrollMarginOptions = {
  hasFab: boolean;
  hasPagination: boolean;
  hasPlayerFooter?: boolean;
  rowHeight?: number;
};

type FixedLeaderboardPaginationProps = {
  page: number;
  totalPages: number;
  onGoToPage: (page: number) => void;
  isMobile: boolean;
  hasFab: boolean;
  hasPlayerFooter?: boolean;
  rowHeight?: number;
};

export function getLeaderboardPwaOffset(): number {
  return IS_PWA ? Gap.section - Gap.md : 0;
}

export function useLeaderboardFooterScrollMargin({
  hasFab,
  hasPagination,
  hasPlayerFooter = false,
  rowHeight = Layout.entryRowHeight,
}: LeaderboardFooterScrollMarginOptions) {
  const scrollContainerRef = useScrollContainer();
  const pwaOffset = getLeaderboardPwaOffset();

  useEffect(() => {
    const el = scrollContainerRef.current;
    if (!el) return;

    let margin: number;
    if (hasFab) {
      margin = Layout.fabPaddingBottom;
      if (hasPagination) {
        const paginationCssBottom = hasPlayerFooter
          ? Layout.fabBottom + pwaOffset + (Layout.fabSize - rowHeight) / 2 + rowHeight + Gap.sm
          : Layout.fabBottom + pwaOffset + Layout.fabSize + Gap.sm;
        const paginationTop = paginationCssBottom + Layout.paginationHeight;
        const naturalHeight = el.clientHeight + (parseFloat(el.style.marginBottom) || 0);
        const headerHeight = el.getBoundingClientRect().top;
        const viewportHeight = window.innerHeight;
        const bottomNavHeight = viewportHeight - headerHeight - naturalHeight;
        const scrollBottomOffset = margin + bottomNavHeight;
        margin += Math.max(0, paginationTop - scrollBottomOffset) + Gap.sm;
      }
    } else {
      margin = Layout.fabPaddingBottom;
      if (hasPlayerFooter) margin += rowHeight + Gap.xl;
      if (hasPagination) margin += Layout.paginationHeight + Gap.xl;
    }

    el.style.marginBottom = `${margin}px`;
    return () => { el.style.marginBottom = ''; };
  }, [hasFab, hasPagination, hasPlayerFooter, pwaOffset, rowHeight, scrollContainerRef]);
}

export function FixedLeaderboardPagination({
  page,
  totalPages,
  onGoToPage,
  isMobile,
  hasFab,
  hasPlayerFooter = false,
  rowHeight = Layout.entryRowHeight,
}: FixedLeaderboardPaginationProps) {
  const isWideDesktop = useIsWideDesktop();
  const pwaOffset = getLeaderboardPwaOffset();
  const wideOverride = isWideDesktop ? fixedFooterWide : undefined;

  return createPortal(
    <div data-testid="leaderboard-fixed-pagination" style={{ ...getFixedPaginationStyle(hasFab, hasPlayerFooter, rowHeight, pwaOffset), ...wideOverride }}>
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
        <span data-testid="leaderboard-page-info" style={s.pageInfoBadge}>
          {page.toLocaleString()} / {totalPages.toLocaleString()}
        </span>
      </Paginator>
    </div>,
    document.body,
  );
}

export function getFixedPaginationStyle(hasFab: boolean, hasPlayerFooter: boolean, rowHeight: number, pwaOffset: number): CSSProperties {
  if (hasFab) {
    if (hasPlayerFooter) {
      return {
        ...s.mobilePagination,
        bottom: Layout.fabBottom + pwaOffset + (Layout.fabSize - rowHeight) / 2 + rowHeight + Gap.sm,
      };
    }
    return s.mobilePaginationNoPlayer;
  }

  if (hasPlayerFooter) {
    return {
      ...s.desktopPagination,
      bottom: Layout.fabPaddingBottom + rowHeight + Gap.xl,
    };
  }

  return s.desktopPaginationNoPlayer;
}

export function getFixedPlayerFooterStyle(hasFab: boolean, rowHeight: number, pwaOffset: number): CSSProperties {
  if (hasFab) {
    return {
      ...s.playerFooterFab,
      bottom: Layout.fabBottom + pwaOffset + (Layout.fabSize - rowHeight) / 2,
    };
  }

  return s.desktopPlayerFooter;
}