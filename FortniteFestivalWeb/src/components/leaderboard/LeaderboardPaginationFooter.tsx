/* eslint-disable react/forbid-dom-props -- fixed footer uses shared inline shell styles */
import { useEffect, type CSSProperties, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { Gap, Layout } from '@festival/theme';
import { readSafeAreaBottomPx, safeAreaBottomOffset } from '../../utils/safeAreaStyles';
import { useScrollContainer } from '../../contexts/ScrollContainerContext';
import { useIsWideDesktop } from '../../hooks/ui/useIsMobile';
import Paginator from '../common/Paginator';
import { fixedFooterWide, plbStyles as s } from './paginatedLeaderboardStyles';

type LeaderboardFooterScrollMarginOptions = {
  hasFab: boolean;
  hasPagination: boolean;
  hasPlayerFooter?: boolean;
  footerPlacement?: FixedLeaderboardFooterPlacement;
  reserveBottomSpace?: boolean;
  rowHeight?: number;
};

type FixedLeaderboardPaginationProps = {
  page: number;
  totalPages: number;
  onGoToPage: (page: number) => void;
  isMobile: boolean;
  hasFab: boolean;
  reserveFabSpace?: boolean;
  hasPlayerFooter?: boolean;
  footerPlacement?: FixedLeaderboardFooterPlacement;
  rowHeight?: number;
};

type FixedLeaderboardPlayerFooterProps = {
  hasFab: boolean;
  reserveFabSpace?: boolean;
  footerPlacement?: FixedLeaderboardFooterPlacement;
  rowHeight?: number;
  children: (props: { className: string; style: CSSProperties }) => ReactNode;
};

export type FixedLeaderboardFooterPlacement = 'default' | 'aboveFab';

function getPlayerFooterBottomOffset(hasFab: boolean, rowHeight: number, footerPlacement: FixedLeaderboardFooterPlacement): number {
  if (hasFab && footerPlacement === 'aboveFab') return Layout.fabBottom + Layout.fabSize + Gap.xl;
  if (hasFab) return Layout.fabBottom + (Layout.fabSize - rowHeight) / 2;
  return Layout.fabPaddingBottom;
}

function getPaginationBottomOffset(hasFab: boolean, hasPlayerFooter: boolean, rowHeight: number, safeAreaBottom: number, footerPlacement: FixedLeaderboardFooterPlacement): number {
  if (hasFab) {
    return hasPlayerFooter
      ? getPlayerFooterBottomOffset(hasFab, rowHeight, footerPlacement) + safeAreaBottom + rowHeight + Gap.xl
      : Layout.fabBottom + safeAreaBottom + Layout.fabSize + Gap.sm;
  }

  return hasPlayerFooter
    ? Layout.fabPaddingBottom + rowHeight + Gap.xl
    : Layout.fabPaddingBottom;
}

function addMarginForFixedControl(el: HTMLElement, margin: number, controlTopOffset: number, gap: number): number {
  const naturalHeight = el.clientHeight + (parseFloat(el.style.marginBottom) || 0);
  const headerHeight = el.getBoundingClientRect().top;
  const viewportHeight = window.innerHeight;
  const bottomChromeHeight = viewportHeight - headerHeight - naturalHeight;
  const scrollBottomOffset = margin + bottomChromeHeight;
  return margin + Math.max(0, controlTopOffset - scrollBottomOffset) + gap;
}

export function useLeaderboardFooterScrollMargin({
  hasFab,
  hasPagination,
  hasPlayerFooter = false,
  footerPlacement = 'default',
  reserveBottomSpace = true,
  rowHeight = Layout.entryRowHeight,
}: LeaderboardFooterScrollMarginOptions) {
  const scrollContainerRef = useScrollContainer();

  useEffect(() => {
    const el = scrollContainerRef.current;
    if (!el) return;
    const safeAreaBottom = readSafeAreaBottomPx();
    if (!reserveBottomSpace) {
      const margin = hasPagination
        ? addMarginForFixedControl(
          el,
          0,
          getPaginationBottomOffset(hasFab, hasPlayerFooter, rowHeight, safeAreaBottom, footerPlacement) + Layout.paginationHeight,
          Gap.sm,
        )
        : 0;
      el.style.marginBottom = margin > 0 ? `${margin}px` : '';
      return () => { el.style.marginBottom = ''; };
    }

    let margin: number;
    if (hasFab) {
      margin = Layout.fabPaddingBottom;
      if (hasPagination) {
        const paginationCssBottom = getPaginationBottomOffset(hasFab, hasPlayerFooter, rowHeight, safeAreaBottom, footerPlacement);
        const paginationTop = paginationCssBottom + Layout.paginationHeight;
        margin = addMarginForFixedControl(el, margin, paginationTop, Gap.sm);
      } else if (hasPlayerFooter && footerPlacement === 'aboveFab') {
        const playerFooterTop = getPlayerFooterBottomOffset(hasFab, rowHeight, footerPlacement) + safeAreaBottom + rowHeight;
        margin = addMarginForFixedControl(el, margin, playerFooterTop, Gap.sm);
      }
    } else {
      margin = Layout.fabPaddingBottom;
      if (hasPlayerFooter) margin += rowHeight + Gap.xl;
      if (hasPagination) margin += Layout.paginationHeight + Gap.xl;
    }

    el.style.marginBottom = `${margin}px`;
    return () => { el.style.marginBottom = ''; };
  }, [footerPlacement, hasFab, hasPagination, hasPlayerFooter, reserveBottomSpace, rowHeight, scrollContainerRef]);
}

export function FixedLeaderboardPagination({
  page,
  totalPages,
  onGoToPage,
  isMobile,
  hasFab,
  reserveFabSpace = hasFab,
  hasPlayerFooter = false,
  footerPlacement = 'default',
  rowHeight = Layout.entryRowHeight,
}: FixedLeaderboardPaginationProps) {
  const isWideDesktop = useIsWideDesktop();
  const wideOverride = isWideDesktop ? fixedFooterWide : undefined;

  return createPortal(
    <div data-testid="leaderboard-fixed-pagination" style={{ ...getFixedPaginationStyle(hasFab, hasPlayerFooter, rowHeight, footerPlacement), ...wideOverride }}>
      <Paginator
        className={reserveFabSpace ? 'fab-player-footer' : ''}
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

export function FixedLeaderboardPlayerFooter({
  hasFab,
  reserveFabSpace = hasFab,
  footerPlacement = 'default',
  rowHeight = Layout.entryRowHeight,
  children,
}: FixedLeaderboardPlayerFooterProps) {
  const isWideDesktop = useIsWideDesktop();
  const wideOverride = isWideDesktop ? fixedFooterWide : undefined;
  const rowHeightStyle: CSSProperties | undefined = rowHeight !== Layout.entryRowHeight
    ? { height: rowHeight, boxSizing: 'border-box' }
    : undefined;

  return createPortal(
    <div data-testid="leaderboard-fixed-player-footer" style={{ ...getFixedPlayerFooterStyle(hasFab, rowHeight, footerPlacement), ...wideOverride }}>
      {children({
        className: reserveFabSpace ? 'fab-player-footer' : '',
        style: { ...s.playerFooterRow, ...rowHeightStyle },
      })}
    </div>,
    document.body,
  );
}

export function getFixedPaginationStyle(hasFab: boolean, hasPlayerFooter: boolean, rowHeight: number, footerPlacement: FixedLeaderboardFooterPlacement = 'default'): CSSProperties {
  if (hasFab) {
    if (hasPlayerFooter) {
      return {
        ...s.mobilePagination,
        bottom: safeAreaBottomOffset(getPlayerFooterBottomOffset(hasFab, rowHeight, footerPlacement) + rowHeight + Gap.xl),
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

export function getFixedPlayerFooterStyle(hasFab: boolean, rowHeight: number, footerPlacement: FixedLeaderboardFooterPlacement = 'default'): CSSProperties {
  if (hasFab) {
    return {
      ...s.playerFooterFab,
      bottom: safeAreaBottomOffset(getPlayerFooterBottomOffset(hasFab, rowHeight, footerPlacement)),
    };
  }

  return s.desktopPlayerFooter;
}