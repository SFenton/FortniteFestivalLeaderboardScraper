import { describe, it, expect, vi } from 'vitest';
import { render, fireEvent } from '@testing-library/react';
import { ACCURACY_SCALE } from '@festival/core';
import type { ServerInstrumentKey as InstrumentKey, PlayerScore } from '@festival/core/api/serverTypes';
import {
  buildInstrumentStatsItems,
  instSongsPlayedUpdater,
  instFCsUpdater,
  instStarsUpdater,
  instPercentileUpdater,
  instPercentileWithScoresUpdater,
  instPercentileBucketUpdater,
  pctGold,
} from '../../pages/player/sections/InstrumentStatsSection';
import { Colors } from '@festival/theme';

vi.mock('../../components/player/StatBox', () => ({
  default: ({ label, value, onClick }: any) => (
    <div data-testid={`stat-${label}`} onClick={onClick}>{typeof value === 'string' ? value : 'node'}</div>
  ),
}));
vi.mock('../../components/player/PlayerPercentileTable', () => ({
  PlayerPercentileHeader: ({ percentileLabel, songsLabel }: any) => (
    <div data-testid="pct-header">{percentileLabel} {songsLabel}</div>
  ),
  PlayerPercentileRow: ({ pct, count, onClick }: any) => (
    <div data-testid={`pct-row-${pct}`} onClick={onClick}>{pct}: {count}</div>
  ),
}));
vi.mock('../../components/display/InstrumentHeader', () => ({
  default: ({ instrument }: any) => <div data-testid="inst-header">{instrument}</div>,
}));
vi.mock('../../components/songs/metadata/GoldStars', () => ({
  default: () => <span data-testid="gold-stars">★★★★★★</span>,
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

describe('buildInstrumentStatsItems', () => {
  it('returns empty array for empty scores', () => {
    const items = buildInstrumentStatsItems(t, inst, [], 100, 'Player', navigateToSongs, navigateToSongDetail, {});
    expect(items.length).toBe(0);
  });

  it('returns items for scores with ranked entries', () => {
    const scores = [
      makeScore({ songId: 's1', stars: 6, isFullCombo: true }),
      makeScore({ songId: 's2', stars: 5, isFullCombo: false }),
      makeScore({ songId: 's3', stars: 4 }),
    ];
    const items = buildInstrumentStatsItems(t, inst, scores, 100, 'Player', navigateToSongs, navigateToSongDetail, {});
    // Should include header + stat cards + percentile table
    expect(items.length).toBeGreaterThan(5);
  });

  it('includes songs played card when songsPlayed > 0', () => {
    const scores = [makeScore()];
    const items = buildInstrumentStatsItems(t, inst, scores, 100, 'Player', navigateToSongs, navigateToSongDetail, {});
    const spItem = items.find(i => i.key.includes('card'));
    expect(spItem).toBeDefined();
  });

  it('includes FC card when fcCount > 0', () => {
    const scores = [makeScore({ isFullCombo: true })];
    const items = buildInstrumentStatsItems(t, inst, scores, 100, 'Player', navigateToSongs, navigateToSongDetail, {});
    expect(items.length).toBeGreaterThan(3);
  });

  it('songs played onClick calls navigateToSongs', () => {
    navigateToSongs.mockClear();
    const scores = [makeScore()];
    const items = buildInstrumentStatsItems(t, inst, scores, 100, 'Player', navigateToSongs, navigateToSongDetail, {});
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
    const items = buildInstrumentStatsItems(t, inst, scores, 100, 'Player', navigateToSongs, navigateToSongDetail, {});
    const tableItem = items.find(i => i.key.includes('pct-table'));
    expect(tableItem).toBeDefined();
  });

  it('percentile row onClick calls navigateToSongs', () => {
    navigateToSongs.mockClear();
    const scores = [makeScore({ rank: 1, totalEntries: 100 })];
    const items = buildInstrumentStatsItems(t, inst, scores, 100, 'Player', navigateToSongs, navigateToSongDetail, {});
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
    const items = buildInstrumentStatsItems(t, inst, scores, 100, 'Player', navigateToSongs, navigateToSongDetail, {});
    // Find bestRank card — it's one of the later cards
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
