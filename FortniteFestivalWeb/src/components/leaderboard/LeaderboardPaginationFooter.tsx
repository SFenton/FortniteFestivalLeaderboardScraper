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
  rowHeight?: number;
};

type FixedLeaderboardPlayerFooterProps = {
  hasFab: boolean;
  reserveFabSpace?: boolean;
  rowHeight?: number;
  children: (props: { className: string; style: CSSProperties }) => ReactNode;
};

function getPaginationBottomOffset(hasFab: boolean, hasPlayerFooter: boolean, rowHeight: number, safeAreaBottom: number): number {
  if (hasFab) {
    return hasPlayerFooter
      ? Layout.fabBottom + safeAreaBottom + (Layout.fabSize - rowHeight) / 2 + rowHeight + Gap.sm
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
          getPaginationBottomOffset(hasFab, hasPlayerFooter, rowHeight, safeAreaBottom) + Layout.paginationHeight,
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
        const paginationCssBottom = getPaginationBottomOffset(hasFab, hasPlayerFooter, rowHeight, safeAreaBottom);
        const paginationTop = paginationCssBottom + Layout.paginationHeight;
        margin = addMarginForFixedControl(el, margin, paginationTop, Gap.sm);
      }
    } else {
      margin = Layout.fabPaddingBottom;
      if (hasPlayerFooter) margin += rowHeight + Gap.xl;
      if (hasPagination) margin += Layout.paginationHeight + Gap.xl;
    }

    el.style.marginBottom = `${margin}px`;
    return () => { el.style.marginBottom = ''; };
  }, [hasFab, hasPagination, hasPlayerFooter, reserveBottomSpace, rowHeight, scrollContainerRef]);
}

export function FixedLeaderboardPagination({
  page,
  totalPages,
  onGoToPage,
  isMobile,
  hasFab,
  reserveFabSpace = hasFab,
  hasPlayerFooter = false,
  rowHeight = Layout.entryRowHeight,
}: FixedLeaderboardPaginationProps) {
  const isWideDesktop = useIsWideDesktop();
  const wideOverride = isWideDesktop ? fixedFooterWide : undefined;

  return createPortal(
    <div data-testid="leaderboard-fixed-pagination" style={{ ...getFixedPaginationStyle(hasFab, hasPlayerFooter, rowHeight), ...wideOverride }}>
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
  rowHeight = Layout.entryRowHeight,
  children,
}: FixedLeaderboardPlayerFooterProps) {
  const isWideDesktop = useIsWideDesktop();
  const wideOverride = isWideDesktop ? fixedFooterWide : undefined;
  const rowHeightStyle: CSSProperties | undefined = rowHeight !== Layout.entryRowHeight
    ? { height: rowHeight, boxSizing: 'border-box' }
    : undefined;

  return createPortal(
    <div data-testid="leaderboard-fixed-player-footer" style={{ ...getFixedPlayerFooterStyle(hasFab, rowHeight), ...wideOverride }}>
      {children({
        className: reserveFabSpace ? 'fab-player-footer' : '',
        style: { ...s.playerFooterRow, ...rowHeightStyle },
      })}
    </div>,
    document.body,
  );
}

export function getFixedPaginationStyle(hasFab: boolean, hasPlayerFooter: boolean, rowHeight: number): CSSProperties {
  if (hasFab) {
    if (hasPlayerFooter) {
      return {
        ...s.mobilePagination,
        bottom: safeAreaBottomOffset(Layout.fabBottom + (Layout.fabSize - rowHeight) / 2 + rowHeight + Gap.sm),
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

export function getFixedPlayerFooterStyle(hasFab: boolean, rowHeight: number): CSSProperties {
  if (hasFab) {
    return {
      ...s.playerFooterFab,
      bottom: safeAreaBottomOffset(Layout.fabBottom + (Layout.fabSize - rowHeight) / 2),
    };
  }

  return s.desktopPlayerFooter;
}