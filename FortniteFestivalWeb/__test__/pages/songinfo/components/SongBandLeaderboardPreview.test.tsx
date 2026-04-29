import { describe, expect, it, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import type { SongBandData } from '../../../../src/api/pageCache';
import SongBandLeaderboardPreview from '../../../../src/pages/songinfo/components/SongBandLeaderboardPreview';

vi.mock('../../../../src/hooks/ui/useContainerWidth', () => ({
  useContainerWidth: () => 800,
}));

vi.mock('../../../../src/components/display/InstrumentIcons', () => ({
  InstrumentIcon: ({ instrument }: { instrument: string }) => <span data-testid={`instrument-icon-${instrument}`}>{instrument}</span>,
}));

const data: SongBandData = {
  loading: false,
  error: null,
  totalEntries: 42,
  localEntries: 5,
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

describe('SongBandLeaderboardPreview', () => {
  it('renders band cards with score metadata and a view-all route', () => {
    render(
      <MemoryRouter>
        <SongBandLeaderboardPreview
          songId="song-a"
          bandType="Band_Duets"
          data={data}
          baseDelay={0}
          skipAnimation
        />
      </MemoryRouter>,
    );

    expect(screen.getByRole('heading', { name: 'Duos' })).toBeInTheDocument();
    expect(screen.getByText('Alpha')).toBeInTheDocument();
    expect(screen.getByText('Beta')).toBeInTheDocument();
    expect(screen.getByText('Gamma')).toBeInTheDocument();
    expect(screen.getByLabelText('Rank 1, season 9, score 1,234,567, 5 stars, 98.8%')).toBeInTheDocument();
    expect(screen.getByTestId('song-band-preview-list-Band_Duets')).toHaveStyle({ display: 'flex', flexDirection: 'column' });
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

    const viewAll = screen.getByRole('link', { name: /View full leaderboard/ });
    expect(viewAll).toHaveAttribute('href', '/songs/song-a/bands/Band_Duets');
  });

  it('renders a selected player band score when it is outside the preview entries', () => {
    const selectedPlayerEntry = {
      ...data.entries[1],
      bandId: 'band-selected',
      teamKey: 'acct-player:acct-z',
      score: 9_999_999,
      rank: 27,
      members: [
        { accountId: 'acct-player', displayName: 'Selected Player', instruments: ['Solo_Drums'], score: 5_000_000, accuracy: 999_000, difficulty: 3, season: 9, stars: 5, isFullCombo: true },
        { accountId: 'acct-z', displayName: 'Zeta', instruments: ['Solo_Vocals'], score: 4_999_999, accuracy: 998_000, difficulty: 3, season: 9, stars: 5, isFullCombo: true },
      ],
    };

    render(
      <MemoryRouter>
        <SongBandLeaderboardPreview
          songId="song-a"
          bandType="Band_Duets"
          data={{ ...data, selectedPlayerEntry }}
          selectedAccountId="acct-player"
          baseDelay={0}
          skipAnimation
        />
      </MemoryRouter>,
    );

    const selectedRow = screen.getByTestId('song-band-selected-entry-Band_Duets');
    expect(within(selectedRow).getByText('Selected Player')).toBeTruthy();
    expect(within(selectedRow).getByText('Zeta')).toBeTruthy();
    expect(within(selectedRow).getByText('#27')).toBeTruthy();
    expect(screen.getAllByTestId('song-band-score-container')).toHaveLength(3);
  });

  it('renders a selected band score when the selected profile is a band', () => {
    const selectedBandEntry = {
      ...data.entries[1],
      bandId: 'band-selected-band',
      teamKey: 'acct-band-a:acct-band-b',
      score: 8_888_888,
      rank: 19,
      members: [
        { accountId: 'acct-band-a', displayName: 'Band Alpha', instruments: ['Solo_Drums'], score: 4_500_000, accuracy: 999_000, difficulty: 3, season: 9, stars: 5, isFullCombo: true },
        { accountId: 'acct-band-b', displayName: 'Band Beta', instruments: ['Solo_Vocals'], score: 4_388_888, accuracy: 998_000, difficulty: 3, season: 9, stars: 5, isFullCombo: true },
      ],
    };

    render(
      <MemoryRouter>
        <SongBandLeaderboardPreview
          songId="song-a"
          bandType="Band_Duets"
          data={{ ...data, selectedBandEntry }}
          baseDelay={0}
          skipAnimation
        />
      </MemoryRouter>,
    );

    const selectedRow = screen.getByTestId('song-band-selected-entry-Band_Duets');
    expect(within(selectedRow).getByText('Band Alpha')).toBeTruthy();
    expect(within(selectedRow).getByText('Band Beta')).toBeTruthy();
    expect(within(selectedRow).getByText('#19')).toBeTruthy();
  });

  it('does not duplicate the selected player band score when it is already in the preview entries', () => {
    render(
      <MemoryRouter>
        <SongBandLeaderboardPreview
          songId="song-a"
          bandType="Band_Duets"
          data={{ ...data, selectedPlayerEntry: data.entries[0] }}
          selectedAccountId="acct-a"
          baseDelay={0}
          skipAnimation
        />
      </MemoryRouter>,
    );

    expect(screen.queryByTestId('song-band-selected-entry-Band_Duets')).toBeNull();
    expect(screen.getAllByText('Alpha')).toHaveLength(1);
  });
});
