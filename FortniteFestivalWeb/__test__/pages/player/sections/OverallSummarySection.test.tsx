import { describe, it, expect, vi } from 'vitest';
import { render, fireEvent } from '@testing-library/react';
import {
  buildOverallSummaryItems,
  songsPlayedUpdater,
  fullCombosUpdater,
  type OverallStats,
} from '../../../../src/pages/player/sections/OverallSummarySection';
import { ACCURACY_SCALE } from '@festival/core';
import type { ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';

vi.mock('../../../../src/components/player/StatBox', () => ({
  default: ({ label, value, onClick }: any) => (
    <div data-testid={`stat-${label}`} onClick={onClick}>{value}</div>
  ),
}));

const t = (key: string) => key;
const visibleKeys: InstrumentKey[] = ['Solo_Guitar', 'Solo_Bass'];
const navigateToSongs = vi.fn();
const navigateToSongDetail = vi.fn();

function makeStats(overrides: Partial<OverallStats> = {}): OverallStats {
  return {
    songsPlayed: 50,
    fcCount: 10,
    fcPercent: '20.0',
    goldStarCount: 5,
    avgAccuracy: 95 * ACCURACY_SCALE,
    bestRank: 1,
    bestRankSongId: 'song-1',
    bestRankInstrument: 'Solo_Guitar',
    ...overrides,
  };
}

describe('buildOverallSummaryItems', () => {
  it('returns 5 items for full stats', () => {
    const items = buildOverallSummaryItems(t, makeStats(), 100, visibleKeys, navigateToSongs, navigateToSongDetail, {});
    expect(items.length).toBe(5);
  });

  it('calls navigateToSongs on songsPlayed click', () => {
    navigateToSongs.mockClear();
    const items = buildOverallSummaryItems(t, makeStats(), 100, visibleKeys, navigateToSongs, navigateToSongDetail, {});
    const songsItem = items.find(i => i.key.includes('player.songsPlayed'));
    const { container } = render(<>{songsItem!.node}</>);
    fireEvent.click(container.querySelector('[data-testid]')!);
    expect(navigateToSongs).toHaveBeenCalled();
  });

  it('calls navigateToSongs on fullCombos click', () => {
    navigateToSongs.mockClear();
    const items = buildOverallSummaryItems(t, makeStats(), 100, visibleKeys, navigateToSongs, navigateToSongDetail, {});
    const fcItem = items.find(i => i.key.includes('player.fullCombos'));
    const { container } = render(<>{fcItem!.node}</>);
    fireEvent.click(container.querySelector('[data-testid]')!);
    expect(navigateToSongs).toHaveBeenCalled();
  });

  it('calls navigateToSongDetail on bestRank click', () => {
    navigateToSongDetail.mockClear();
    const items = buildOverallSummaryItems(t, makeStats(), 100, visibleKeys, navigateToSongs, navigateToSongDetail, {});
    const rankItem = items.find(i => i.key.includes('player.bestSongRank'));
    const { container } = render(<>{rankItem!.node}</>);
    fireEvent.click(container.querySelector('[data-testid]')!);
    expect(navigateToSongDetail).toHaveBeenCalled();
  });

  it('shows green color when all songs are played', () => {
    const items = buildOverallSummaryItems(t, makeStats({ songsPlayed: 100 }), 100, visibleKeys, navigateToSongs, navigateToSongDetail, {});
    expect(items.length).toBe(5);
  });

  it('shows gold color when FC is 100%', () => {
    const items = buildOverallSummaryItems(t, makeStats({ fcPercent: '100.0', fcCount: 100 }), 100, visibleKeys, navigateToSongs, navigateToSongDetail, {});
    const fcItem = items.find(i => i.key.includes('player.fullCombos'));
    expect(fcItem).toBeDefined();
  });

  it('shows gold color for accuracy when perfect', () => {
    const items = buildOverallSummaryItems(t, makeStats({ avgAccuracy: 100 * ACCURACY_SCALE, fcPercent: '100.0' }), 100, visibleKeys, navigateToSongs, navigateToSongDetail, {});
    expect(items.length).toBe(5);
  });

  it('shows dash for zero accuracy', () => {
    const items = buildOverallSummaryItems(t, makeStats({ avgAccuracy: 0 }), 100, visibleKeys, navigateToSongs, navigateToSongDetail, {});
    expect(items.length).toBe(5);
  });

  it('shows dash for zero bestRank and no onClick', () => {
    navigateToSongDetail.mockClear();
    const items = buildOverallSummaryItems(t, makeStats({ bestRank: 0, bestRankSongId: null }), 100, visibleKeys, navigateToSongs, navigateToSongDetail, {});
    const rankItem = items.find(i => i.key.includes('player.bestSongRank'));
    const { container } = render(<>{rankItem!.node}</>);
    fireEvent.click(container.querySelector('[data-testid]')!);
    expect(navigateToSongDetail).not.toHaveBeenCalled();
  });
});

describe('songsPlayedUpdater', () => {
  it('sets hasScores for visible instruments', () => {
    const updater = songsPlayedUpdater(visibleKeys);
    const result = updater({ instrument: 'Solo_Guitar', sortMode: 'score', sortAscending: false, filters: {} } as any);
    expect(result.filters.hasScores['Solo_Guitar']).toBe(true);
    expect(result.filters.hasScores['Solo_Bass']).toBe(true);
    expect(result.instrument).toBeNull();
  });
});

describe('fullCombosUpdater', () => {
  it('sets hasFCs for visible instruments', () => {
    const updater = fullCombosUpdater(visibleKeys);
    const result = updater({ instrument: 'Solo_Guitar', sortMode: 'score', sortAscending: false, filters: {} } as any);
    expect(result.filters.hasFCs['Solo_Guitar']).toBe(true);
    expect(result.filters.hasFCs['Solo_Bass']).toBe(true);
  });
});
