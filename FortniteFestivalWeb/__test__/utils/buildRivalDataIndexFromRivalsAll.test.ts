import { describe, it, expect } from 'vitest';
import { buildRivalDataIndexFromRivalsAll } from '../../src/utils/suggestionAdapter';
import type { RivalsAllResponse } from '@festival/core/api/serverTypes';

describe('buildRivalDataIndexFromRivalsAll', () => {
  const response: RivalsAllResponse = {
    accountId: 'user1',
    songs: ['song_a', 'song_b', 'song_c'],
    combos: [
      {
        combo: 'Solo_Guitar',
        above: [
          {
            accountId: 'rival1',
            displayName: 'Rival One',
            direction: 'above',
            sharedSongCount: 10,
            aheadCount: 6,
            behindCount: 4,
            rivalScore: 100,
            samples: [
              { s: 0, i: 'Solo_Guitar', ur: 5, rr: 3, us: 90000, rs: 92000 },
              { s: 1, i: 'Solo_Guitar', ur: 8, rr: 12, us: 85000, rs: 80000 },
            ],
          },
        ],
        below: [
          {
            accountId: 'rival2',
            displayName: 'Rival Two',
            direction: 'below',
            sharedSongCount: 5,
            aheadCount: 2,
            behindCount: 3,
            rivalScore: 80,
            samples: [
              { s: 2, i: 'Solo_Guitar', ur: 3, rr: 7, us: 95000, rs: 88000 },
            ],
          },
        ],
      },
    ],
  };

  it('produces songRivals with correct direction', () => {
    const index = buildRivalDataIndexFromRivalsAll(response);
    expect(index.songRivals).toHaveLength(2);
    expect(index.songRivals[0]!.direction).toBe('above');
    expect(index.songRivals[1]!.direction).toBe('below');
  });

  it('expands song index to full songId', () => {
    const index = buildRivalDataIndexFromRivalsAll(response);
    const rival1Matches = index.byRival.get('rival1')!;
    expect(rival1Matches[0]!.songId).toBe('song_a'); // index 0
    expect(rival1Matches[1]!.songId).toBe('song_b'); // index 1

    const rival2Matches = index.byRival.get('rival2')!;
    expect(rival2Matches[0]!.songId).toBe('song_c'); // index 2
  });

  it('maps instrument to core key', () => {
    const index = buildRivalDataIndexFromRivalsAll(response);
    const matches = index.byRival.get('rival1')!;
    expect(matches[0]!.instrument).toBe('guitar');
  });

  it('computes rankDelta from ur - rr', () => {
    const index = buildRivalDataIndexFromRivalsAll(response);
    const matches = index.byRival.get('rival1')!;
    expect(matches[0]!.rankDelta).toBe(2);  // 5 - 3
    expect(matches[1]!.rankDelta).toBe(-4); // 8 - 12
  });

  it('tracks closest rival by song', () => {
    const index = buildRivalDataIndexFromRivalsAll(response);
    const closest = index.closestRivalBySong.get('song_a:guitar');
    expect(closest).toBeDefined();
    expect(closest!.rival.accountId).toBe('rival1');
  });

  it('filters by combo when provided', () => {
    const multiCombo: RivalsAllResponse = {
      ...response,
      combos: [
        ...response.combos,
        {
          combo: 'Solo_Bass',
          above: [{
            accountId: 'rival3',
            displayName: 'Rival Three',
            direction: 'above',
            sharedSongCount: 3,
            aheadCount: 2,
            behindCount: 1,
            rivalScore: 90,
            samples: [{ s: 0, i: 'Solo_Bass', ur: 2, rr: 1, us: 88000, rs: 89000 }],
          }],
          below: [],
        },
      ],
    };

    // Filter to Solo_Guitar only
    const index = buildRivalDataIndexFromRivalsAll(multiCombo, 'Solo_Guitar');
    expect(index.songRivals.find(r => r.accountId === 'rival3')).toBeUndefined();
    expect(index.songRivals.find(r => r.accountId === 'rival1')).toBeDefined();
  });

  it('respects limit parameter', () => {
    const manyRivals: RivalsAllResponse = {
      accountId: 'user1',
      songs: ['s1'],
      combos: [{
        combo: 'Solo_Guitar',
        above: Array.from({ length: 10 }, (_, i) => ({
          accountId: `r${i}`,
          displayName: `R${i}`,
          direction: 'above' as const,
          sharedSongCount: 1,
          aheadCount: 1,
          behindCount: 0,
          rivalScore: 100 - i,
          samples: [{ s: 0, i: 'Solo_Guitar', ur: 5, rr: i, us: 90000, rs: 80000 }],
        })),
        below: [],
      }],
    };

    const index = buildRivalDataIndexFromRivalsAll(manyRivals, undefined, 3);
    const above = index.songRivals.filter(r => r.direction === 'above');
    expect(above).toHaveLength(3);
  });

  it('returns empty index for empty response', () => {
    const empty: RivalsAllResponse = { accountId: 'user1', songs: [], combos: [] };
    const index = buildRivalDataIndexFromRivalsAll(empty);
    expect(index.songRivals).toHaveLength(0);
    expect(index.byRival.size).toBe(0);
  });
});
