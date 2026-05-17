import { describe, expect, it, vi, beforeEach } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import type { ReactNode } from 'react';
import type { SongBandLeaderboardResponse } from '@festival/core/api/serverTypes';
import SongBandLeaderboardPage from '../../../../src/pages/leaderboard/band/SongBandLeaderboardPage';
import { BandFilterActionProvider } from '../../../../src/contexts/BandFilterActionContext';
import { FeatureFlagsProvider } from '../../../../src/contexts/FeatureFlagsContext';
import type { AppliedBandComboFilter } from '../../../../src/types/bandFilter';
import { SELECTED_PROFILE_STORAGE_KEY } from '../../../../src/state/selectedProfile';

const mockGetSongBandLeaderboard = vi.hoisted(() => vi.fn());
const mockScrollTo = vi.hoisted(() => vi.fn());
const mockScrollElement = vi.hoisted(() => ({
  clientHeight: 800,
  style: {} as Record<string, string>,
  getBoundingClientRect: () => ({ top: 0 }),
  scrollTo: mockScrollTo,
}));
const mockUseIsMobile = vi.hoisted(() => vi.fn(() => false));
const mockUseIsMobileChrome = vi.hoisted(() => vi.fn(() => false));
const mockFestivalValue = vi.hoisted(() => ({
  state: {
    currentSeason: 9,
    songs: [{ songId: 'song-a', title: 'Song A', albumArt: '/song-a.png' }],
  },
}));

vi.mock('../../../../src/api/client', () => ({
  api: {
    getSongBandLeaderboard: mockGetSongBandLeaderboard,
  },
}));

vi.mock('../../../../src/components/display/InstrumentIcons', () => ({
  InstrumentIcon: ({ instrument }: { instrument: string }) => <span data-testid={`instrument-icon-${instrument}`}>{instrument}</span>,
}));

vi.mock('../../../../src/components/songs/headers/SongInfoHeader', () => ({
  default: ({ subtitle2 }: { subtitle2?: string }) => <header>{subtitle2}</header>,
}));

vi.mock('../../../../src/contexts/FestivalContext', async () => {
  const { createContext } = await import('react');
  return {
    FestivalContext: createContext(mockFestivalValue),
    useFestival: () => mockFestivalValue,
  };
});

vi.mock('../../../../src/contexts/ScrollContainerContext', () => ({
  useScrollContainer: () => ({ current: mockScrollElement }),
}));

vi.mock('../../../../src/hooks/navigation/useNavigateToSongDetail', () => ({
  useNavigateToSongDetail: () => vi.fn(),
}));

vi.mock('../../../../src/hooks/ui/useContainerWidth', () => ({
  useContainerWidth: () => 800,
}));

vi.mock('../../../../src/hooks/ui/useIsMobile', () => ({
  useIsMobile: mockUseIsMobile,
  useIsMobileChrome: mockUseIsMobileChrome,
  useIsWideDesktop: () => false,
}));

vi.mock('../../../../src/hooks/ui/usePageTransition', () => ({
  usePageTransition: () => ({ phase: 'contentIn', shouldStagger: false }),
}));

vi.mock('../../../../src/hooks/ui/useStagger', () => ({
  useStagger: () => ({ forIndex: () => ({}), clearAnim: vi.fn() }),
}));

vi.mock('../../../../src/pages/Page', () => ({
  default: ({ before, children }: { before?: ReactNode; children?: ReactNode }) => (
    <main>
      {before}
      {children}
    </main>
  ),
  PageBackground: () => null,
}));

const response: SongBandLeaderboardResponse = {
  songId: 'song-a',
  bandType: 'Band_Duets',
  showLeaderboardEntryTotals: false,
  count: 2,
  totalEntries: 42,
  localEntries: 42,
  entries: [
    {
      bandId: 'band-1',
      bandType: 'Band_Duets',
      teamKey: 'acct-a:acct-b',
      comboId: 'Solo_Guitar+Solo_Bass',
      score: 1_234_567,
      rank: 1,
      percentile: 2.4,
      accuracy: 987_654,
      isFullCombo: true,
      stars: 5,
      season: 9,
      difficulty: 3,
      endTime: '2026-04-27T00:00:00Z',
      members: [
        { accountId: 'acct-a', displayName: 'Alpha', instruments: ['Solo_Guitar'], score: 654_321, accuracy: 901_234, difficulty: 2, season: 9, stars: 3, isFullCombo: false },
        { accountId: 'acct-b', displayName: 'Beta', instruments: ['Solo_Bass'], score: 580_246, accuracy: 1_000_000, difficulty: 3, season: 9, stars: 6, isFullCombo: true },
      ],
    },
    {
      bandId: 'band-2',
      bandType: 'Band_Duets',
      teamKey: 'acct-c:acct-d',
      comboId: 'Solo_Guitar+Solo_Bass',
      score: 98_765,
      rank: 2,
      percentile: 4.8,
      accuracy: 945_000,
      isFullCombo: false,
      stars: 5,
      season: 7,
      difficulty: 3,
      endTime: '2026-04-27T00:00:00Z',
      members: [
        { accountId: 'acct-c', displayName: 'Gamma', instruments: ['Solo_Guitar'], score: 50_000, accuracy: 876_543, difficulty: 1, season: 7, stars: 4, isFullCombo: false },
        { accountId: 'acct-d', displayName: 'Delta', instruments: ['Solo_Bass'], score: 48_765, accuracy: 765_432, difficulty: 1, season: 7, stars: 2, isFullCombo: false },
      ],
    },
  ],
};

function renderPage(bandFilter?: AppliedBandComboFilter | null) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });

  return render(
    <QueryClientProvider client={queryClient}>
      <FeatureFlagsProvider>
      <BandFilterActionProvider value={{
        visible: false,
        label: 'Filter Band Type',
        selectedInstruments: bandFilter?.assignments.map(assignment => assignment.instrument) ?? [],
        appliedFilter: bandFilter ?? null,
        onPress: () => {},
      }}>
      <MemoryRouter initialEntries={['/songs/song-a/bands/Band_Duets']}>
        <Routes>
          <Route path="/songs/:songId/bands/:bandType" element={<SongBandLeaderboardPage />} />
        </Routes>
      </MemoryRouter>
      </BandFilterActionProvider>
      </FeatureFlagsProvider>
    </QueryClientProvider>,
  );
}

describe('SongBandLeaderboardPage', () => {
  beforeEach(() => {
    localStorage.clear();
    mockScrollTo.mockClear();
    mockScrollElement.style.marginBottom = '';
    mockUseIsMobile.mockReturnValue(false);
    mockUseIsMobileChrome.mockReturnValue(false);
    mockGetSongBandLeaderboard.mockResolvedValue(response);
  });

  it('renders full song-band leaderboard rows as one ranked column', async () => {
    renderPage();

    const list = await screen.findByTestId('song-band-leaderboard-list');

    expect(list).toHaveStyle({ display: 'flex', flexDirection: 'column' });
    expect(screen.getByText('Alpha')).toBeInTheDocument();
    expect(screen.getByText('Beta')).toBeInTheDocument();
    expect(screen.getByText('Gamma')).toBeInTheDocument();
    const rankRails = screen.getAllByTestId('band-rank-rail');
    const rankedCards = screen.getAllByTestId('band-ranked-card-content');
    const memberBlocks = screen.getAllByTestId('band-card-member-content');
    const firstRankRail = rankRails[0]!;
    const firstRankedCard = rankedCards[0]!;
    const firstMemberBlock = memberBlocks[0]!;
    expect(firstRankRail).toHaveTextContent('#1');
    expect(firstRankedCard).toContainElement(firstRankRail);
    expect(firstRankedCard).toContainElement(screen.getByLabelText('Rank 1, season 9, score 1,234,567, 5 stars, 98.8%'));
    expect(firstMemberBlock).not.toContainElement(firstRankRail);
    expect(screen.getAllByTestId('song-band-score-container')).toHaveLength(2);
    for (const scoreContainer of screen.getAllByTestId('song-band-score-container')) {
      expect(scoreContainer).toHaveStyle({ width: '9ch', paddingLeft: '4px', paddingRight: '4px' });
    }
    expect(screen.getAllByTestId('song-band-member-score-container')).toHaveLength(4);
    for (const scoreContainer of screen.getAllByTestId('song-band-member-score-container')) {
      expect(scoreContainer).toHaveStyle({ width: '7ch', paddingLeft: '4px', paddingRight: '4px' });
    }
    expect(screen.getAllByTestId('song-band-member-stars-container')).toHaveLength(4);
    for (const starsContainer of screen.getAllByTestId('song-band-member-stars-container')) {
      expect(starsContainer).toHaveStyle({ width: '132px' });
    }
    expect(screen.getAllByTestId('song-band-member-accuracy-container')).toHaveLength(4);
    for (const accuracyContainer of screen.getAllByTestId('song-band-member-accuracy-container')) {
      expect(accuracyContainer).toHaveStyle({ width: '3.75em' });
    }
    const firstTrailing = screen.getAllByTestId('band-member-trailing')[0]!;
    const firstInlineMetadata = within(firstTrailing).getByTestId('song-band-member-metadata');
    const firstInstrument = within(firstTrailing).getByTestId('instrument-icon-Solo_Guitar');
    const difficulty = within(firstInlineMetadata).getByText('H');
    const season = within(firstInlineMetadata).getByText('S9');
    const score = within(firstInlineMetadata).getByText('654,321');
    const stars = within(firstInlineMetadata).getByTestId('song-band-member-stars-container');
    const accuracy = within(firstInlineMetadata).getByTestId('song-band-member-accuracy-container');
    expect(firstInlineMetadata.compareDocumentPosition(firstInstrument) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(difficulty.compareDocumentPosition(season) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(season.compareDocumentPosition(score) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(score.compareDocumentPosition(stars) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(stars.compareDocumentPosition(accuracy) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(accuracy.compareDocumentPosition(firstInstrument) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(stars.querySelectorAll('img')).toHaveLength(3);
    expect(accuracy).toHaveTextContent('90.1%');
    expect(firstInlineMetadata).not.toHaveTextContent('1,234,567');
    expect(firstInlineMetadata).not.toHaveTextContent('98.8%');

    const fixedPagination = screen.getByTestId('leaderboard-fixed-pagination');
    expect(fixedPagination).toHaveStyle({ position: 'fixed', bottom: '96px' });
    expect(fixedPagination.style.touchAction).toBe('none');
    expect(list).not.toContainElement(fixedPagination);
    expect(screen.getByTestId('leaderboard-page-info')).toHaveTextContent('1 / 2');
    expect(mockScrollElement.style.marginBottom).toBe('164px');

    mockGetSongBandLeaderboard.mockClear();
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await waitFor(() => {
      expect(mockGetSongBandLeaderboard).toHaveBeenCalledWith('song-a', 'Band_Duets', 25, 25, undefined, undefined, undefined);
    });
    expect(mockScrollTo).toHaveBeenCalledWith(0, 0);
  });

  it('shows the band header count only when leaderboard totals are enabled', async () => {
    mockGetSongBandLeaderboard.mockResolvedValue({
      ...response,
      showLeaderboardEntryTotals: true,
    });

    renderPage();

    await screen.findByTestId('song-band-leaderboard-list');
    expect(screen.getByText('Duos • 42 entries')).toBeInTheDocument();
  });

  it('hides the band header count when leaderboard totals are disabled', async () => {
    mockGetSongBandLeaderboard.mockResolvedValue({
      ...response,
      showLeaderboardEntryTotals: false,
    });

    renderPage();

    await screen.findByTestId('song-band-leaderboard-list');
    expect(screen.getByText('Duos')).toBeInTheDocument();
    expect(screen.queryByText('Duos • 42 entries')).toBeNull();
  });

  it('hides the band header count when leaderboard total visibility is omitted', async () => {
    const { showLeaderboardEntryTotals: _omitted, ...responseWithoutVisibility } = response;
    mockGetSongBandLeaderboard.mockResolvedValue(responseWithoutVisibility);

    renderPage();

    await screen.findByTestId('song-band-leaderboard-list');
    expect(screen.getByText('Duos')).toBeInTheDocument();
    expect(screen.queryByText('Duos • 42 entries')).toBeNull();
  });

  it('shows only member score before instrument icons on mobile song-band rows', async () => {
    mockUseIsMobile.mockReturnValue(true);

    renderPage();

    await screen.findByTestId('song-band-leaderboard-list');

    expect(screen.getByText('Alpha')).toBeInTheDocument();
    expect(screen.getByText('Beta')).toBeInTheDocument();

    const firstTrailing = screen.getAllByTestId('band-member-trailing')[0]!;
    const firstMetadata = within(firstTrailing).getByTestId('song-band-member-metadata');
    const firstInstrument = within(firstTrailing).getByTestId('instrument-icon-Solo_Guitar');
    const score = within(firstMetadata).getByText('654,321');

    expect(firstMetadata).toContainElement(score);
    expect(score.compareDocumentPosition(firstInstrument) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(within(firstMetadata).queryByText('H')).toBeNull();
    expect(within(firstMetadata).queryByText('S9')).toBeNull();
    expect(within(firstMetadata).queryByTestId('song-band-member-stars-container')).toBeNull();
    expect(within(firstMetadata).queryByTestId('song-band-member-accuracy-container')).toBeNull();

    expect(screen.getAllByTestId('song-band-member-score-container')).toHaveLength(4);
    expect(screen.queryAllByTestId('song-band-member-stars-container')).toHaveLength(0);
    expect(screen.queryAllByTestId('song-band-member-accuracy-container')).toHaveLength(0);
  });

  it('passes the applied selected-band combo for the matching band type', async () => {
    renderPage({
      bandId: 'band-1',
      bandType: 'Band_Duets',
      teamKey: 'acct-a:acct-b',
      comboId: 'Solo_Guitar+Solo_Bass',
      assignments: [
        { accountId: 'acct-a', instrument: 'Solo_Guitar' },
        { accountId: 'acct-b', instrument: 'Solo_Bass' },
      ],
    });

    await waitFor(() => {
      expect(mockGetSongBandLeaderboard).toHaveBeenCalledWith('song-a', 'Band_Duets', 25, 0, undefined, undefined, 'Solo_Guitar+Solo_Bass');
    });
  });

  it('requests and renders the selected band entry when it is outside the visible page', async () => {
    mockUseIsMobileChrome.mockReturnValue(true);
    localStorage.setItem(SELECTED_PROFILE_STORAGE_KEY, JSON.stringify({
      type: 'band',
      bandId: 'selected-band',
      bandType: 'Band_Duets',
      teamKey: 'acct-selected:acct-partner',
      displayName: 'Selected + Partner',
      members: [
        { accountId: 'acct-selected', displayName: 'Selected' },
        { accountId: 'acct-partner', displayName: 'Partner' },
      ],
    }));
    mockGetSongBandLeaderboard.mockResolvedValue({
      ...response,
      selectedBandEntry: {
        bandId: 'selected-band',
        bandType: 'Band_Duets',
        teamKey: 'acct-selected:acct-partner',
        comboId: 'Solo_Guitar+Solo_Bass',
        score: 777_777,
        rank: 13,
        accuracy: 912_345,
        stars: 5,
        season: 9,
        difficulty: 3,
        members: [
          { accountId: 'acct-selected', displayName: 'Selected', instruments: ['Solo_Guitar'], score: 400_000, accuracy: 910_000, difficulty: 3, season: 9, stars: 5, isFullCombo: false },
          { accountId: 'acct-partner', displayName: 'Partner', instruments: ['Solo_Bass'], score: 377_777, accuracy: 914_000, difficulty: 3, season: 9, stars: 5, isFullCombo: false },
        ],
      },
    });

    renderPage();

    await waitFor(() => {
      expect(mockGetSongBandLeaderboard).toHaveBeenCalledWith('song-a', 'Band_Duets', 25, 0, undefined, 'acct-selected:acct-partner', undefined);
    });

    const list = await screen.findByTestId('song-band-leaderboard-list');
    expect(within(list).queryByText('Selected')).toBeNull();
    const footer = await screen.findByTestId('leaderboard-fixed-player-footer');
    expect(footer).toHaveStyle({ position: 'fixed' });
    expect(footer.style.bottom).toContain('84px');
    expect(footer.style.touchAction).toBe('none');
    const pagination = await screen.findByTestId('leaderboard-fixed-pagination');
    expect(pagination.style.bottom).toContain('136px');
    expect(pagination.style.touchAction).toBe('none');
    expect(within(footer).getByText('Selected + Partner')).toBeTruthy();
    expect(within(footer).getByText('#13')).toBeTruthy();
    const footerLink = within(footer).getByText('Selected + Partner').closest('a');
    expect(footerLink).toBeTruthy();
    expect(footerLink).not.toHaveClass('fab-player-footer');
    const score = within(footer).getByText('777,777');
    const stars = within(footer).getByTestId('leaderboard-stars-after-score');
    const accuracy = within(footer).getByText('91.2%');
    expect(stars.querySelectorAll('img')).toHaveLength(5);
    expect(score.compareDocumentPosition(stars) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(stars.compareDocumentPosition(accuracy) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });
});
