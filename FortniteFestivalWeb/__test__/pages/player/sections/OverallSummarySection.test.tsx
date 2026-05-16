import { describe, it, expect, vi } from 'vitest';
import { render, fireEvent } from '@testing-library/react';
import {
  buildFamilyGlobalStatisticsItems,
  buildOverallSummaryItems,
  resolveVisibleFamilyRankSections,
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

vi.mock('../../../../src/pages/player/sections/PlayerSectionHeading', () => ({
  default: ({ title, description, instruments }: any) => (
    <section data-testid={`heading-${title}`} data-instruments={(instruments ?? []).join(',')} data-description={description}>{title}</section>
  ),
}));

const t = (key: string) => key === 'player.globalStatistics' ? 'Global Statistics' : key;
const visibleKeys: InstrumentKey[] = ['Solo_Guitar', 'Solo_Bass'];
const navigateToSongs = vi.fn();
const navigateToSongDetail = vi.fn();
const navigateToFamilyLeaderboard = vi.fn();

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

  it('keeps global family rank cards out of the personal summary items', () => {
    const items = buildOverallSummaryItems(
      t,
      makeStats(),
      100,
      visibleKeys,
      navigateToSongs,
      navigateToSongDetail,
      {},
    );

    expect(items.find(i => i.key.includes('player.totalScoreRank'))).toBeUndefined();
  });
});

describe('buildFamilyGlobalStatisticsItems', () => {
  it('passes the metric and rank to family leaderboard navigation', () => {
    navigateToFamilyLeaderboard.mockClear();
    const items = buildFamilyGlobalStatisticsItems(
      t,
      visibleKeys,
      {},
      { pad: { scopeId: 'pad', adjusted: 4, totalScore: 26 } },
      false,
      navigateToFamilyLeaderboard,
    );

    const totalScoreItem = items.find(i => i.key === 'family-pad-totalscore');
    const { getByTestId } = render(<>{totalScoreItem!.node}</>);
    fireEvent.click(getByTestId('stat-player.totalScoreRank'));

    expect(navigateToFamilyLeaderboard).toHaveBeenCalledWith('pad', 'totalscore', 26);
  });

  it('renders family section headings with active instruments', () => {
    const items = buildFamilyGlobalStatisticsItems(
      t,
      ['Solo_Guitar', 'Solo_Bass', 'Solo_PeripheralGuitar', 'Solo_PeripheralDrums'],
      {},
      {
        pad: { scopeId: 'pad', adjusted: 4, totalScore: 26 },
        pro_strings: { scopeId: 'pro_strings', adjusted: 5, totalScore: 27 },
        pro_drums: { scopeId: 'pro_drums', adjusted: 6, totalScore: 28 },
      },
      false,
    );

    const { getByTestId, queryByTestId } = render(<>{items.map(item => <div key={item.key}>{item.node}</div>)}</>);

    expect(getByTestId('heading-Pad Global Statistics')).toHaveAttribute('data-instruments', 'Solo_Guitar,Solo_Bass');
    expect(getByTestId('heading-Pro Strings Global Statistics')).toHaveAttribute('data-instruments', 'Solo_PeripheralGuitar');
    expect(getByTestId('heading-Pro Drums Global Statistics')).toHaveAttribute('data-instruments', 'Solo_PeripheralDrums');
    expect(queryByTestId('heading-Pro Vocals Global Statistics')).toBeNull();
  });

  it('renders family section subtitle copy explaining selected icons and full-family scope', () => {
    const items = buildFamilyGlobalStatisticsItems(
      t,
      ['Solo_Guitar', 'Solo_PeripheralGuitar', 'Solo_PeripheralDrums'],
      {},
      {
        pad: { scopeId: 'pad', adjusted: 4, totalScore: 26 },
        pro_strings: { scopeId: 'pro_strings', adjusted: 5, totalScore: 27 },
        pro_drums: { scopeId: 'pro_drums', adjusted: 6, totalScore: 28 },
      },
      false,
    );

    const { getByTestId } = render(<>{items.map(item => <div key={item.key}>{item.node}</div>)}</>);

    expect(getByTestId('heading-Pad Global Statistics')).toHaveAttribute('data-description', 'The overall rankings for Lead, Bass, Drums, and Tap Vocals. Selected instrument icons indicate instruments enabled in app settings, but these statistics cards apply to all pad instruments combined.');
    expect(getByTestId('heading-Pro Strings Global Statistics')).toHaveAttribute('data-description', 'The overall rankings for Pro Lead and Pro Bass. Selected instrument icons indicate instruments enabled in app settings, but these statistics cards apply to all pro strings instruments combined.');
    expect(getByTestId('heading-Pro Drums Global Statistics')).toHaveAttribute('data-description', 'The overall rankings for Pro Drums and Pro Cymbals. Selected instrument icons indicate instruments enabled in app settings, but these statistics cards apply to all pro drums instruments combined.');
  });

  it('omits family leaderboard cards when the rank is not positive', () => {
    const items = buildFamilyGlobalStatisticsItems(
      t,
      visibleKeys,
      {},
      { pad: { scopeId: 'pad', adjusted: 4, totalScore: 0 } },
      false,
    );

    expect(items.find(i => i.key.includes('player.totalScoreRank'))).toBeUndefined();
  });
});

describe('resolveVisibleFamilyRankSections', () => {
  it('excludes Pro Vocals from global family sections', () => {
    expect(resolveVisibleFamilyRankSections(['Solo_PeripheralVocals']).map(section => section.scopeId)).toEqual([]);
  });

  it('returns only active instruments for each rendered family section', () => {
    expect(resolveVisibleFamilyRankSections(['Solo_Guitar', 'Solo_PeripheralBass', 'Solo_PeripheralCymbals'])).toEqual([
      { scopeId: 'pad', activeInstruments: ['Solo_Guitar'] },
      { scopeId: 'pro_strings', activeInstruments: ['Solo_PeripheralBass'] },
      { scopeId: 'pro_drums', activeInstruments: ['Solo_PeripheralCymbals'] },
    ]);
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
