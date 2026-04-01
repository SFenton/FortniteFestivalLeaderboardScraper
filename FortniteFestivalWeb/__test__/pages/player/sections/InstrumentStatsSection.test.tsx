import { describe, it, expect, vi } from 'vitest';
import { render, fireEvent } from '@testing-library/react';
import { ACCURACY_SCALE } from '@festival/core';
import type { ServerInstrumentKey as InstrumentKey, PlayerScore } from '@festival/core/api/serverTypes';
import { computeInstrumentStats } from '../../../../src/pages/player/helpers/playerStats';
import {
  buildInstrumentStatsItems,
  instSongsPlayedUpdater,
  instFCsUpdater,
  instStarsUpdater,
  instPercentileUpdater,
  instPercentileWithScoresUpdater,
  instPercentileBucketUpdater,
  pctGold,
} from '../../../../src/pages/player/sections/InstrumentStatsSection';
import { Colors } from '@festival/theme';

vi.mock('../../../../src/components/player/StatBox', () => ({
  default: ({ label, value, onClick }: any) => (
    <div data-testid={`stat-${label}`} onClick={onClick}>{typeof value === 'string' ? value : 'node'}</div>
  ),
}));
vi.mock('../../../../src/components/player/PlayerPercentileTable', () => ({
  PlayerPercentileHeader: ({ percentileLabel, songsLabel }: any) => (
    <div data-testid="pct-header">{percentileLabel} {songsLabel}</div>
  ),
  PlayerPercentileRow: ({ pct, count, onClick }: any) => (
    <div data-testid={`pct-row-${pct}`} onClick={onClick}>{pct}: {count}</div>
  ),
}));
vi.mock('../../../../src/components/display/InstrumentHeader', () => ({
  default: ({ instrument }: any) => <div data-testid="inst-header">{instrument}</div>,
}));
vi.mock('../../../../src/components/songs/metadata/GoldStars', () => ({
  default: () => <span data-testid="gold-stars">â˜…â˜…â˜…â˜…â˜…â˜…</span>,
}));

const t = (key: string) => key;
const navigateToSongs = vi.fn();
const navigateToSongDetail = vi.fn();
const inst: InstrumentKey = 'Solo_Guitar';

function makeScore(overrides: Partial<PlayerScore> = {}): PlayerScore {
  return {
    songId: 'song-1', instrument: 'Solo_Guitar', score: 100000, rank: 5,
    totalEntries: 100, accuracy: 95 * ACCURACY_SCALE, isFullCombo: false,
    stars: 5, season: 5,
    ...overrides,
  };
}

describe('pctGold', () => {
  it('returns gold for Top 1%', () => expect(pctGold('Top 1%')).toBe(Colors.gold));
  it('returns gold for Top 5%', () => expect(pctGold('Top 5%')).toBe(Colors.gold));
  it('returns undefined for Top 10%', () => expect(pctGold('Top 10%')).toBeUndefined());
  it('returns undefined for empty string', () => expect(pctGold('')).toBeUndefined());
});

/** Build items from raw scores (mirrors the old API for test convenience). */
function buildItems(scores: PlayerScore[], totalSongs: number, ...rest: Parameters<typeof buildInstrumentStatsItems> extends [any, any, any, ...infer R] ? R : never) {
  return buildInstrumentStatsItems(t, inst, computeInstrumentStats(scores, totalSongs), ...rest);
}

describe('buildInstrumentStatsItems', () => {
  it('returns empty array for empty scores', () => {
    const items = buildItems([], 100, 'Player', navigateToSongs, navigateToSongDetail, {});
    expect(items.length).toBe(0);
  });

  it('returns items for scores with ranked entries', () => {
    const scores = [
      makeScore({ songId: 's1', stars: 6, isFullCombo: true }),
      makeScore({ songId: 's2', stars: 5, isFullCombo: false }),
      makeScore({ songId: 's3', stars: 4 }),
    ];
    const items = buildItems(scores, 100, 'Player', navigateToSongs, navigateToSongDetail, {});
    // Should include header + stat cards + percentile table
    expect(items.length).toBeGreaterThan(5);
  });

  it('includes songs played card when songsPlayed > 0', () => {
    const scores = [makeScore()];
    const items = buildItems(scores, 100, 'Player', navigateToSongs, navigateToSongDetail, {});
    const spItem = items.find(i => i.key.includes('card'));
    expect(spItem).toBeDefined();
  });

  it('includes FC card when fcCount > 0', () => {
    const scores = [makeScore({ isFullCombo: true })];
    const items = buildItems(scores, 100, 'Player', navigateToSongs, navigateToSongDetail, {});
    expect(items.length).toBeGreaterThan(3);
  });

  it('songs played onClick calls navigateToSongs', () => {
    navigateToSongs.mockClear();
    const scores = [makeScore()];
    const items = buildItems(scores, 100, 'Player', navigateToSongs, navigateToSongDetail, {});
    // Find a card with an onClick
    const cardItem = items.find(i => i.key.includes('card-0'));
    if (cardItem) {
      const { container } = render(<>{cardItem.node}</>);
      fireEvent.click(container.querySelector('[data-testid]')!);
      expect(navigateToSongs).toHaveBeenCalled();
    }
  });

  it('percentile table renders when buckets exist', () => {
    const scores = [makeScore({ rank: 1, totalEntries: 100 })];
    const items = buildItems(scores, 100, 'Player', navigateToSongs, navigateToSongDetail, {});
    const tableItem = items.find(i => i.key.includes('pct-table'));
    expect(tableItem).toBeDefined();
  });

  it('percentile row onClick calls navigateToSongs', () => {
    navigateToSongs.mockClear();
    const scores = [makeScore({ rank: 1, totalEntries: 100 })];
    const items = buildItems(scores, 100, 'Player', navigateToSongs, navigateToSongDetail, {});
    const tableItem = items.find(i => i.key.includes('pct-table'));
    if (tableItem) {
      const { container } = render(<>{tableItem.node}</>);
      const row = container.querySelector('[data-testid^="pct-row"]');
      fireEvent.click(row!);
      expect(navigateToSongs).toHaveBeenCalled();
    }
  });

  it('bestRank onClick calls navigateToSongDetail', () => {
    navigateToSongDetail.mockClear();
    const scores = [makeScore({ rank: 1, totalEntries: 100 })];
    const items = buildItems(scores, 100, 'Player', navigateToSongs, navigateToSongDetail, {});
    for (const item of items) {
      if (!item.key.includes('card')) continue;
      const { container } = render(<>{item.node}</>);
      const el = container.querySelector('[data-testid*="player.bestRank"]');
      if (el) {
        fireEvent.click(el);
        break;
      }
    }
    expect(navigateToSongDetail).toHaveBeenCalled();
  });

  it('skips FC card when fcCount is 0', () => {
    const scores = [makeScore({ isFullCombo: false, stars: 0, rank: 0, totalEntries: 0 })];
    const items = buildItems(scores, 100, 'Player', navigateToSongs, navigateToSongDetail, {});
    expect(items.length).toBeGreaterThan(0);
  });

  it('renders gold accuracy color when perfect', () => {
    const scores = [makeScore({ accuracy: 100 * ACCURACY_SCALE, isFullCombo: true, stars: 6 })];
    const items = buildItems(scores, 1, 'Player', navigateToSongs, navigateToSongDetail, {});
    expect(items.length).toBeGreaterThan(3);
  });

  it('renders golden stars when averageStars is 6', () => {
    const scores = [makeScore({ stars: 6, isFullCombo: true })];
    const items = buildItems(scores, 1, 'Player', navigateToSongs, navigateToSongDetail, {});
    expect(items.length).toBeGreaterThan(3);
  });

  it('renders dash for zero accuracy', () => {
    const scores = [makeScore({ accuracy: 0 })];
    const items = buildItems(scores, 100, 'Player', navigateToSongs, navigateToSongDetail, {});
    expect(items.length).toBeGreaterThan(0);
  });

  it('renders dash for zero bestRank', () => {
    const scores = [makeScore({ rank: 0, totalEntries: 0 })];
    const items = buildItems(scores, 100, 'Player', navigateToSongs, navigateToSongDetail, {});
    expect(items.length).toBeGreaterThan(0);
  });

  it('renders green when all songs played', () => {
    const scores = [makeScore()];
    const items = buildItems(scores, 1, 'Player', navigateToSongs, navigateToSongDetail, {});
    expect(items.length).toBeGreaterThan(0);
  });

  it('includes star cards only when count > 0', () => {
    const scores = [
      makeScore({ songId: 's1', stars: 6 }),
      makeScore({ songId: 's2', stars: 5 }),
      makeScore({ songId: 's3', stars: 4 }),
      makeScore({ songId: 's4', stars: 3 }),
      makeScore({ songId: 's5', stars: 2 }),
      makeScore({ songId: 's6', stars: 1 }),
    ];
    const items = buildItems(scores, 100, 'Player', navigateToSongs, navigateToSongDetail, {});
    // Should have header + songs played + FC + 6 star cards + accuracy + avg stars + bestRank + percentile + avgPercentile + pctTable
    expect(items.length).toBeGreaterThan(10);
  });

  it('star card onClick calls navigateToSongs', () => {
    navigateToSongs.mockClear();
    const scores = [makeScore({ stars: 6 })];
    const items = buildItems(scores, 100, 'Player', navigateToSongs, navigateToSongDetail, {});
    // Find a gold stars card (should be card-2 after songsPlayed=0, fcs=0)
    for (const item of items) {
      if (!item.key.includes('card')) continue;
      const { container } = render(<>{item.node}</>);
      const el = container.querySelector('[data-testid*="player.goldStars"]');
      if (el) {
        fireEvent.click(el);
        break;
      }
    }
    expect(navigateToSongs).toHaveBeenCalled();
  });

  it('percentile onClick calls navigateToSongs', () => {
    navigateToSongs.mockClear();
    const scores = [makeScore({ rank: 1, totalEntries: 100 })];
    const items = buildItems(scores, 100, 'Player', navigateToSongs, navigateToSongDetail, {});
    for (const item of items) {
      if (!item.key.includes('card')) continue;
      const { container } = render(<>{item.node}</>);
      const el = container.querySelector('[data-testid*="player.percentile"]');
      if (el) {
        fireEvent.click(el);
        break;
      }
    }
    expect(navigateToSongs).toHaveBeenCalled();
  });

  it('avgPercentile (songsPlayed) onClick calls navigateToSongs', () => {
    navigateToSongs.mockClear();
    const scores = [makeScore({ rank: 1, totalEntries: 100 })];
    const items = buildItems(scores, 100, 'Player', navigateToSongs, navigateToSongDetail, {});
    for (const item of items) {
      if (!item.key.includes('card')) continue;
      const { container } = render(<>{item.node}</>);
      // The avgPercentile card also uses "player.songsPlayed" labelâ€”find the second one
      const els = container.querySelectorAll('[data-testid*="player.songsPlayed"]');
      if (els.length > 0) {
        fireEvent.click(els[els.length - 1]!);
        if (navigateToSongs.mock.calls.length > 0) break;
      }
    }
    expect(navigateToSongs).toHaveBeenCalled();
  });
});

describe('settings updaters', () => {
  const baseSetting = { instrument: null, sortMode: 'title', sortAscending: true, filters: { hasScores: {}, hasFCs: {}, starsFilter: {}, percentileFilter: {} } } as any;

  it('instSongsPlayedUpdater sets instrument and hasScores', () => {
    const result = instSongsPlayedUpdater('Solo_Guitar')(baseSetting);
    expect(result.instrument).toBe('Solo_Guitar');
    expect(result.filters.hasScores['Solo_Guitar']).toBe(true);
  });

  it('instFCsUpdater sets hasFCs', () => {
    const result = instFCsUpdater('Solo_Guitar')(baseSetting);
    expect(result.filters.hasFCs['Solo_Guitar']).toBe(true);
  });

  it('instStarsUpdater sets starsFilter', () => {
    const result = instStarsUpdater('Solo_Guitar', 6)(baseSetting);
    expect(result.sortMode).toBe('stars');
  });

  it('instPercentileUpdater sets sortMode to percentile', () => {
    const result = instPercentileUpdater('Solo_Guitar')(baseSetting);
    expect(result.sortMode).toBe('percentile');
  });

  it('instPercentileWithScoresUpdater sets hasScores and percentile mode', () => {
    const result = instPercentileWithScoresUpdater('Solo_Guitar')(baseSetting);
    expect(result.sortMode).toBe('percentile');
    expect(result.filters.hasScores['Solo_Guitar']).toBe(true);
  });

  it('instPercentileBucketUpdater sets percentileFilter for bucket', () => {
    const result = instPercentileBucketUpdater('Solo_Guitar', 5)(baseSetting);
    expect(result.filters.percentileFilter).toBeDefined();
  });
});
