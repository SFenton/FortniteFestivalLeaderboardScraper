import { renderHook } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { useLeaderboardFooterScrollMargin } from '../../../src/components/leaderboard/LeaderboardPaginationFooter';
import { createScrollContainerWrapper } from '../../helpers/scrollContainerWrapper';

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
});
