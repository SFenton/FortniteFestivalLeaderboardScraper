import { afterEach, describe, it, expect, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import type { AccountRankingDto, AccountRankingEntry, RankingMetric, ServerInstrumentKey } from '@festival/core/api/serverTypes';
import RankingCard from '../../../../src/pages/leaderboards/components/RankingCard';
import { computeRankWidth } from '../../../../src/pages/leaderboards/helpers/rankingHelpers';
import { TestProviders } from '../../../helpers/TestProviders';
import { Colors, Gap } from '@festival/theme';
import { stubMatchMedia, stubResizeObserver } from '../../../helpers/browserStubs';

const instrument: ServerInstrumentKey = 'Solo_Guitar';
const metric: RankingMetric = 'totalscore';

function makeEntry(rank: number, overrides: Partial<AccountRankingEntry> & Partial<Pick<AccountRankingDto, 'instrument' | 'totalRankedAccounts'>> = {}): AccountRankingEntry {
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

function mockMeasuredCardWidth(width: number) {
  stubMatchMedia(false);
  stubResizeObserver({ width, height: 600 });
  vi.spyOn(Element.prototype, 'getBoundingClientRect').mockReturnValue({
    top: 0,
    left: 0,
    bottom: 600,
    right: width,
    width,
    height: 600,
    x: 0,
    y: 0,
    toJSON() { return this; },
  } as DOMRect);
  Object.defineProperty(HTMLElement.prototype, 'clientWidth', {
    configurable: true,
    get() { return width; },
  });
}

afterEach(() => {
  vi.restoreAllMocks();
});

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
        instrument,
        totalRankedAccounts: 12345,
      }) as AccountRankingDto,
    });

    expect(screen.getByText('#1')).toHaveStyle({ width: `${expectedWidth}px` });
    expect(screen.getByText('#12,345')).toHaveStyle({ width: `${expectedWidth}px` });
  });

  it('renders Max Score % values with the same top-tier styling as history cards', () => {
    renderCard({
      metric: 'maxscore',
      entries: [makeEntry(1, { maxScorePercent: 0.991, rawMaxScorePercent: 0.991 })],
      totalAccounts: 1,
    });

    expect(screen.getByText('99.1%').style.fontStyle).toBe('italic');
  });

  it('renders FC count numerator in gold for FC Rate rows', () => {
    renderCard({
      metric: 'fcrate',
      entries: [makeEntry(1, { fullComboCount: 65, totalChartedSongs: 160 })],
      totalAccounts: 1,
    });

    expect(screen.getByText('65')).toHaveStyle({ color: Colors.gold });
  });

  it('renders adjusted percentile rows with songs, percentile value, and Bayesian rank', () => {
    renderCard({
      metric: 'adjusted',
      entries: [
        makeEntry(17, { rawSkillRating: 0.0056, adjustedSkillRating: 0.0409, songsPlayed: 123, totalChartedSongs: 500, adjustedSkillRank: 17 }),
        makeEntry(18, { rawSkillRating: 0.12, adjustedSkillRating: 0.9, songsPlayed: 12, totalChartedSongs: 500, adjustedSkillRank: 18 }),
      ],
      totalAccounts: 1000,
    });

    expect(screen.getByText('123 / 500')).toBeTruthy();
    expect(screen.getByText('Top 0.56%')).toBeTruthy();
    expect(screen.getAllByText('Bayesian-Calculated Rank:')).toHaveLength(2);
    expect(screen.getByText('0.0409')).toBeTruthy();
    expect(screen.getByText('Top 0.56%').style.minWidth).toBe(screen.getByText('Top 12%').style.minWidth);
    expect(screen.getByText('0.0409').style.minWidth).toBe(screen.getByText('0.90').style.minWidth);
  });

  it('uses two-row percentile metadata on desktop when measured card width is too narrow', async () => {
    mockMeasuredCardWidth(620);

    renderCard({
      metric: 'adjusted',
      entries: [makeEntry(17, { rawSkillRating: 0.0056, adjustedSkillRating: 0.0409, songsPlayed: 123, totalChartedSongs: 500, adjustedSkillRank: 17 })],
      totalAccounts: 1000,
    });

    await waitFor(() => expect(screen.getByText('Player 17').closest('a')).toHaveStyle({ height: '76px' }));
    const metadata = screen.getByText('Bayesian-Calculated Rank:').parentElement;
    expect(metadata?.style.paddingTop).toBe('');
    expect(metadata?.parentElement).toHaveStyle({ gap: `${Gap.xl}px` });
  });

  it('keeps one-row percentile metadata on desktop when measured card width is wide enough', async () => {
    mockMeasuredCardWidth(720);

    renderCard({
      metric: 'adjusted',
      entries: [makeEntry(17, { rawSkillRating: 0.0056, adjustedSkillRating: 0.0409, songsPlayed: 123, totalChartedSongs: 500, adjustedSkillRank: 17 })],
      totalAccounts: 1000,
    });

    await waitFor(() => expect(screen.getByText('Player 17').closest('a')).toHaveStyle({ height: '48px' }));
  });
});