/**
 * InstrumentCard tests — exercises rendering with entries, player scores,
 * error states, and responsive column widths.
 */
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import InstrumentCard from '../../../../src/pages/songinfo/components/InstrumentCard';
import type { LeaderboardEntry, PlayerScore, ServerInstrumentKey } from '@festival/core/api/serverTypes';

/** Set window.innerWidth and override matchMedia to evaluate min-width queries. */
function setViewportWidth(width: number) {
  Object.defineProperty(window, 'innerWidth', { value: width, writable: true });
  window.matchMedia = (query: string) => {
    const match = query.match(/\(min-width:\s*(\d+)px\)/);
    const matches = match ? width >= Number(match[1]) : false;
    return { matches, media: query, onchange: null, addEventListener: () => {}, removeEventListener: () => {}, addListener: () => {}, removeListener: () => {}, dispatchEvent: () => false } as MediaQueryList;
  };
}

vi.mock('../../../../src/components/display/InstrumentIcons', () => ({
  InstrumentIcon: ({ instrument }: any) => <span data-testid={`icon-${instrument}`}>{instrument}</span>,
}));
vi.mock('../../../../src/components/songs/metadata/AccuracyDisplay', () => ({
  default: ({ accuracy }: any) => <span data-testid="accuracy">{accuracy}</span>,
}));
vi.mock('../../../../src/components/songs/metadata/SeasonPill', () => ({
  default: ({ season }: any) => <span data-testid="season">S{season}</span>,
}));

const inst: ServerInstrumentKey = 'Solo_Guitar';

function makeEntry(rank: number, overrides: Partial<LeaderboardEntry> = {}): LeaderboardEntry {
  return {
    accountId: `acc-${rank}`,
    displayName: `Player ${rank}`,
    score: 150000 - rank * 1000,
    rank,
    accuracy: 950000,
    isFullCombo: false,
    stars: 5,
    season: 5,
    ...overrides,
  };
}

const baseProps = {
  songId: 'song-1',
  instrument: inst,
  baseDelay: 0,
  windowWidth: 900,
  prefetchedEntries: [] as LeaderboardEntry[],
  prefetchedError: null as string | null,
  skipAnimation: true,
  scoreWidth: '8ch',
  playerScore: undefined as PlayerScore | undefined,
  playerName: undefined as string | undefined,
  playerAccountId: undefined as string | undefined,
};

function renderCard(overrides: Partial<typeof baseProps> = {}) {
  return render(
    <MemoryRouter>
      <InstrumentCard {...baseProps} {...overrides} />
    </MemoryRouter>,
  );
}

describe('InstrumentCard', () => {
  beforeEach(() => setViewportWidth(900));

  it('renders instrument label', () => {
    renderCard();
    expect(screen.getByText('Lead')).toBeTruthy();
  });

  it('shows error message when prefetchedError is set', () => {
    renderCard({ prefetchedError: 'Failed to load' });
    expect(screen.getByText('Failed to load')).toBeTruthy();
  });

  it('shows no entries message with instrument name when entries are empty', () => {
    renderCard({ prefetchedEntries: [] });
    expect(screen.getByText('No Lead entries')).toBeTruthy();
  });

  it('empty card is not clickable', () => {
    renderCard({ prefetchedEntries: [] });
    const card = screen.getByText('No Lead entries').parentElement!;
    expect(card.style.cursor).not.toBe('pointer');
  });

  it('renders leaderboard entries with rank, name, score', () => {
    const entries = [makeEntry(1), makeEntry(2)];
    renderCard({ prefetchedEntries: entries });
    expect(screen.getByText('#1')).toBeTruthy();
    expect(screen.getByText('Player 1')).toBeTruthy();
    expect(screen.getByText('#2')).toBeTruthy();
  });

  it('renders season pill when window is wide enough', () => {
    setViewportWidth(1200);
    const entries = [makeEntry(1, { season: 5 })];
    renderCard({ prefetchedEntries: entries, windowWidth: 1200 });
    expect(screen.getByTestId('season')).toBeTruthy();
  });

  it('hides season pill on narrow width', () => {
    setViewportWidth(400);
    const entries = [makeEntry(1, { season: 5 })];
    renderCard({ prefetchedEntries: entries, windowWidth: 400 });
    expect(screen.queryByTestId('season')).toBeNull();
  });

  it('renders accuracy when card is wide enough', () => {
    setViewportWidth(900);
    const entries = [makeEntry(1)];
    renderCard({ prefetchedEntries: entries, windowWidth: 900 });
    expect(screen.getByTestId('accuracy')).toBeTruthy();
  });

  it('hides accuracy on narrow width', () => {
    setViewportWidth(300);
    const entries = [makeEntry(1)];
    renderCard({ prefetchedEntries: entries, windowWidth: 300 });
    expect(screen.queryByTestId('accuracy')).toBeNull();
  });

  it('highlights player entry when player is in top entries', () => {
    const entries = [makeEntry(1, { accountId: 'player-1' })];
    renderCard({ prefetchedEntries: entries, playerAccountId: 'player-1', playerName: 'MyPlayer' });
    // Player row should have bold styling via inline style
    const rank = screen.getByText('#1');
    expect(rank.style.fontWeight).toBe('700');
  });

  it('renders separate player score row when player not in top', () => {
    const entries = [makeEntry(1)];
    const playerScore: PlayerScore = {
      songId: 'song-1', instrument: 'Solo_Guitar', score: 120000, rank: 50,
      accuracy: 900000, isFullCombo: false, stars: 4, season: 5,
    };
    renderCard({
      prefetchedEntries: entries,
      playerScore,
      playerName: 'MyPlayer',
      playerAccountId: 'my-player',
    });
    expect(screen.getByText('MyPlayer')).toBeTruthy();
    expect(screen.getByText('#50')).toBeTruthy();
  });

  it('uses accountId prefix when displayName is missing', () => {
    const entries = [makeEntry(1, { displayName: undefined, accountId: 'abcdef12345678' })];
    renderCard({ prefetchedEntries: entries });
    expect(screen.getByText('abcdef12')).toBeTruthy();
  });

  it('renders with animation styles when skipAnimation is false', () => {
    const entries = [makeEntry(1)];
    const { container } = renderCard({ prefetchedEntries: entries, skipAnimation: false });
    const animated = container.querySelector('[style*="fadeInUp"]');
    expect(animated).toBeTruthy();
  });

  it('renders mobile layout on narrow width', () => {
    const entries = [makeEntry(1)];
    const { container } = renderCard({ prefetchedEntries: entries, windowWidth: 300 });
    expect(container.innerHTML).toBeTruthy();
  });

  it('shows view-all button when entries exist', () => {
    const entries = [makeEntry(1), makeEntry(2)];
    renderCard({ prefetchedEntries: entries });
    expect(screen.getByText('View full leaderboard')).toBeTruthy();
  });

  it('rank falls back to index+1 when rank is undefined', () => {
    const entries = [makeEntry(1, { rank: undefined as any })];
    renderCard({ prefetchedEntries: entries });
    expect(screen.getByText('#1')).toBeTruthy();
  });

  it('shows season in player row on wide width', () => {
    const entries = [makeEntry(1)];
    const playerScore: PlayerScore = {
      songId: 'song-1', instrument: 'Solo_Guitar', score: 100000, rank: 50,
      accuracy: 880000, isFullCombo: false, stars: 4, season: 3,
    };
    renderCard({
      prefetchedEntries: entries,
      windowWidth: 1200,
      playerScore,
      playerName: 'TestPlayer',
      playerAccountId: 'my-player',
    });
    expect(screen.getByText('TestPlayer')).toBeTruthy();
  });

  it('stopPropagation on entry link click', () => {
    const entries = [makeEntry(1)];
    renderCard({ prefetchedEntries: entries });
    const link = screen.getByText('Player 1').closest('a')!;
    const stopProp = vi.spyOn(MouseEvent.prototype, 'stopPropagation');
    fireEvent.click(link);
    stopProp.mockRestore();
  });
});
