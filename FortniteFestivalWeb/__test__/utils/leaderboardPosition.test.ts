import { describe, expect, it } from 'vitest';
import { getLeaderboardPageForRank, SONG_LEADERBOARD_PAGE_SIZE } from '../../src/utils/leaderboardPosition';

describe('getLeaderboardPageForRank', () => {
  it('maps ranks to 1-based pages of the full song leaderboards', () => {
    expect(SONG_LEADERBOARD_PAGE_SIZE).toBe(25);
    expect(getLeaderboardPageForRank(1)).toBe(1);
    expect(getLeaderboardPageForRank(25)).toBe(1);
    expect(getLeaderboardPageForRank(26)).toBe(2);
    expect(getLeaderboardPageForRank(12_345)).toBe(494);
    expect(getLeaderboardPageForRank(11, 10)).toBe(2);
  });

  it('returns null for unknown or invalid ranks', () => {
    expect(getLeaderboardPageForRank(undefined)).toBeNull();
    expect(getLeaderboardPageForRank(null)).toBeNull();
    expect(getLeaderboardPageForRank(0)).toBeNull();
    expect(getLeaderboardPageForRank(Number.NaN)).toBeNull();
  });
});
