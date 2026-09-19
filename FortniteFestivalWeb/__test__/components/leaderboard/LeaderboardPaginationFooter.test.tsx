import { render, renderHook, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  FixedLeaderboardPagination,
  FixedLeaderboardPlayerFooter,
  useLeaderboardFooterScrollMargin,
} from '../../../src/components/leaderboard/LeaderboardPaginationFooter';
import { createScrollContainerWrapper } from '../../helpers/scrollContainerWrapper';
import { StartupEntranceProvider } from '../../../src/contexts/StartupEntranceContext';

describe('leaderboard footer client-viewport clearance', () => {
  afterEach(() => vi.unstubAllGlobals());

  it.each([
    { borderTop: 0, rectTop: 114 },
    { borderTop: 64, rectTop: 50 },
  ])('preserves clearance with a $borderTop px gutter border', ({ borderTop, rectTop }) => {
    vi.stubGlobal('innerHeight', 800);
    const { wrapper, mockEl } = createScrollContainerWrapper();
    Object.defineProperty(mockEl, 'clientHeight', { value: 600, configurable: true });
    Object.defineProperty(mockEl, 'clientTop', { value: borderTop });
    mockEl.getBoundingClientRect = () => new DOMRect(0, rectTop, 1440, 600 + borderTop);
    const { unmount } = renderHook(() => useLeaderboardFooterScrollMargin({
      hasFab: false,
      hasPagination: true,
      reserveBottomSpace: false,
    }), { wrapper });
    expect(mockEl.style.marginBottom).toBe('70px');
    unmount();
    expect(mockEl.style.marginBottom).toBe('');
  });

  it('keeps fixed body portals unmounted until startup entry completes', () => {
    const { rerender } = render(
      <StartupEntranceProvider complete={false}>
        <FixedLeaderboardPagination
          page={1}
          totalPages={2}
          onGoToPage={() => {}}
          isMobile={false}
          hasFab={false}
        />
        <FixedLeaderboardPlayerFooter hasFab={false}>
          {props => <div {...props}>Tracked player</div>}
        </FixedLeaderboardPlayerFooter>
      </StartupEntranceProvider>,
    );

    expect(screen.queryByTestId('leaderboard-fixed-pagination')).not.toBeInTheDocument();
    expect(screen.queryByTestId('leaderboard-fixed-player-footer')).not.toBeInTheDocument();

    rerender(
      <StartupEntranceProvider complete>
        <FixedLeaderboardPagination
          page={1}
          totalPages={2}
          onGoToPage={() => {}}
          isMobile={false}
          hasFab={false}
        />
        <FixedLeaderboardPlayerFooter hasFab={false}>
          {props => <div {...props}>Tracked player</div>}
        </FixedLeaderboardPlayerFooter>
      </StartupEntranceProvider>,
    );

    expect(screen.getByTestId('leaderboard-fixed-pagination')).toBeInTheDocument();
    expect(screen.getByTestId('leaderboard-fixed-player-footer')).toBeInTheDocument();
  });
});
