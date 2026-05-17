import { afterEach, describe, it, expect, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import type { AccountRankingDto, AccountRankingEntry, RankingMetric, ServerInstrumentKey } from '@festival/core/api/serverTypes';
import RankingCard from '../../../../src/pages/leaderboards/components/RankingCard';
import { computeRankWidth } from '../../../../src/pages/leaderboards/helpers/rankingHelpers';
import { TestProviders } from '../../../helpers/TestProviders';
import { Colors, Gap } from '@festival/theme';
import { stubMatchMedia, stubResizeObserver } from '../../../helpers/browserStubs';

const mockNavigate = vi.hoisted(() => vi.fn());

vi.mock('react-router-dom', async () => {
  const actual = await vi.importActual<typeof import('react-router-dom')>('react-router-dom');
  return {
    ...actual,
    useNavigate: () => mockNavigate,
  };
});

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

function expectBefore(first: Element, second: Element) {
  expect(first.compareDocumentPosition(second) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
}

afterEach(() => {
  vi.restoreAllMocks();
  mockNavigate.mockReset();
});

describe('RankingCard', () => {
  it('shows counted view-all label when totalAccounts is available', () => {
    renderCard({
      entries: [makeEntry(1), makeEntry(2)],
      totalAccounts: 10030,
    });

    expect(screen.getByText('View all rankings (10,030)')).toBeTruthy();
  });

  it('activates view-all from touch pointerup without double navigating on click', () => {
    renderCard({
      entries: [makeEntry(1), makeEntry(2)],
      totalAccounts: 10030,
    });
    const viewAll = screen.getByRole('button', { name: 'View all rankings (10,030)' });

    fireEvent.pointerDown(viewAll, { pointerId: 1, pointerType: 'touch', isPrimary: true, button: 0, clientX: 20, clientY: 20 });
    fireEvent.pointerUp(viewAll, { pointerId: 1, pointerType: 'touch', isPrimary: true, button: 0, clientX: 20, clientY: 20 });

    expect(mockNavigate).toHaveBeenCalledTimes(1);
    expect(mockNavigate).toHaveBeenCalledWith('/leaderboards/all?instrument=Solo_Guitar&rankBy=totalscore');

    fireEvent.click(viewAll);
    expect(mockNavigate).toHaveBeenCalledTimes(1);
  });

  it('does not activate view-all when touch movement becomes a scroll', () => {
    renderCard({
      entries: [makeEntry(1), makeEntry(2)],
      totalAccounts: 10030,
    });
    const viewAll = screen.getByRole('button', { name: 'View all rankings (10,030)' });

    fireEvent.pointerDown(viewAll, { pointerId: 1, pointerType: 'touch', isPrimary: true, button: 0, clientX: 20, clientY: 20 });
    fireEvent.pointerMove(viewAll, { pointerId: 1, pointerType: 'touch', isPrimary: true, button: 0, clientX: 20, clientY: 28 });
    fireEvent.pointerUp(viewAll, { pointerId: 1, pointerType: 'touch', isPrimary: true, button: 0, clientX: 20, clientY: 28 });

    expect(mockNavigate).not.toHaveBeenCalled();
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

  it('highlights selected member ranking rows when they are already in the top rows', () => {
    renderCard({
      entries: [makeEntry(1, { accountId: 'member-1' })],
      totalAccounts: 100,
      spotlightRankings: [makeEntry(1, {
        accountId: 'member-1',
        displayName: 'Member One',
        instrument,
        totalRankedAccounts: 100,
      }) as AccountRankingDto],
    });

    expect(screen.getByText('#1').style.fontWeight).toBe('700');
  });

  it('renders selected member ranking rows below the top rows when needed', () => {
    renderCard({
      entries: [makeEntry(1)],
      totalAccounts: 100,
      spotlightRankings: [makeEntry(50, {
        accountId: 'member-1',
        displayName: 'Member One',
        instrument,
        totalRankedAccounts: 100,
      }) as AccountRankingDto],
    });

    expect(screen.getByText('Member One')).toBeTruthy();
    expect(screen.getByText('#50')).toBeTruthy();
  });

  it('sorts appended selected member ranking rows by active metric rank ascending', () => {
    renderCard({
      entries: [makeEntry(1)],
      totalAccounts: 100,
      spotlightRankings: [
        makeEntry(90, {
          accountId: 'member-lowest',
          displayName: 'Lower Ranked Member',
          instrument,
          totalRankedAccounts: 100,
        }) as AccountRankingDto,
        makeEntry(12, {
          accountId: 'member-highest',
          displayName: 'Higher Ranked Member',
          instrument,
          totalRankedAccounts: 100,
        }) as AccountRankingDto,
      ],
    });

    expectBefore(screen.getByText('Higher Ranked Member'), screen.getByText('Lower Ranked Member'));
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
    const primaryRow = screen.getByTestId('ranking-compact-primary-row');
    const primaryMetadata = screen.getByTestId('ranking-compact-primary-metadata');
    const bayesianRow = screen.getByTestId('ranking-compact-bayesian-row');
    expect(within(primaryRow).getByText('Player 17')).toBeTruthy();
    expect(within(primaryMetadata).getByText('123 / 500')).toBeTruthy();
    expect(within(primaryMetadata).getByText('Top 0.56%')).toBeTruthy();
    expect(within(primaryRow).queryByText('Bayesian-Calculated Rank:')).toBeNull();
    expect(within(bayesianRow).getByText('Bayesian-Calculated Rank:')).toBeTruthy();
    expect(within(bayesianRow).getByText('0.0409')).toBeTruthy();
    expect(bayesianRow).toHaveStyle({ justifyContent: 'flex-end' });
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