import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import type { AccountRankingEntry, RankingMetric, ServerInstrumentKey } from '@festival/core/api/serverTypes';
import RankingCard from '../../../../src/pages/leaderboards/components/RankingCard';
import { computeRankWidth } from '../../../../src/pages/leaderboards/helpers/rankingHelpers';
import { TestProviders } from '../../../helpers/TestProviders';

const instrument: ServerInstrumentKey = 'Solo_Guitar';
const metric: RankingMetric = 'totalscore';

function makeEntry(rank: number, overrides: Partial<AccountRankingEntry> = {}): AccountRankingEntry {
  return {
    accountId: `acc-${rank}`,
    displayName: `Player ${rank}`,
    songsPlayed: 120,
    totalChartedSongs: 160,
    coverage: 0.75,
    rawSkillRating: 0.9234,
    adjustedSkillRating: 0.9234,
    adjustedSkillRank: rank,
    weightedRating: 0.9123,
    weightedRank: rank,
    fcRate: 0.42,
    fcRateRank: rank,
    totalScore: 1234567 - rank,
    totalScoreRank: rank,
    maxScorePercent: 0.885,
    maxScorePercentRank: rank,
    avgAccuracy: 0.972,
    fullComboCount: 65,
    avgStars: 5.6,
    bestRank: 1,
    avgRank: 7.4,
    rawMaxScorePercent: 0.885,
    rawWeightedRating: 0.9123,
    computedAt: '2026-04-18T00:00:00Z',
    ...overrides,
  };
}

type RenderOverrides = Partial<React.ComponentProps<typeof RankingCard>>;

function renderCard(overrides: RenderOverrides = {}) {
  return render(
    <TestProviders>
      <RankingCard
        instrument={instrument}
        metric={metric}
        entries={[]}
        totalAccounts={0}
        {...overrides}
      />
    </TestProviders>,
  );
}

describe('RankingCard', () => {
  it('shows counted view-all label when totalAccounts is available', () => {
    renderCard({
      entries: [makeEntry(1), makeEntry(2)],
      totalAccounts: 10030,
    });

    expect(screen.getByText('View all rankings (10,030)')).toBeTruthy();
  });

  it('falls back to the plain label when totalAccounts is zero', () => {
    renderCard({
      entries: [makeEntry(1)],
      totalAccounts: 0,
    });

    expect(screen.getByText('View all rankings')).toBeTruthy();
  });

  it('does not render a view-all CTA when the card has no entries', () => {
    renderCard({
      entries: [],
      totalAccounts: 10030,
    });

    expect(screen.queryByText('View all rankings (10,030)')).toBeNull();
    expect(screen.queryByText('View all rankings')).toBeNull();
  });

  it('shares one rank width between top rows and the player row for the same instrument card', () => {
    const expectedWidth = computeRankWidth([1, 2, 12345]);

    renderCard({
      entries: [makeEntry(1), makeEntry(2)],
      totalAccounts: 12345,
      playerAccountId: 'tracked-player',
      playerRanking: makeEntry(12345, {
        accountId: 'tracked-player',
        displayName: 'Tracked Player',
      }),
    });

    expect(screen.getByText('#1')).toHaveStyle({ width: `${expectedWidth}px` });
    expect(screen.getByText('#12,345')).toHaveStyle({ width: `${expectedWidth}px` });
  });
});