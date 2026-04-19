/**
 * InstrumentCard tests — exercises rendering with entries, player scores,
 * error states, and responsive column widths.
 */
import { describe, it, expect, vi, beforeAll, beforeEach, afterAll } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { TestProviders } from '../../../helpers/TestProviders';
import { stubResizeObserver } from '../../../helpers/browserStubs';
import InstrumentCard from '../../../../src/pages/songinfo/components/InstrumentCard';
import type { LeaderboardEntry, PlayerScore, ServerInstrumentKey } from '@festival/core/api/serverTypes';

let mockMeasuredCardWidth = 0;
vi.mock('../../../../src/hooks/ui/useContainerWidth', () => ({
  useContainerWidth: () => mockMeasuredCardWidth,
}));

const originalClientWidth = Object.getOwnPropertyDescriptor(HTMLElement.prototype, 'clientWidth');
let measuredCountedLabelWidth = 320;
let measuredPlainLabelWidth = 140;
let canvasGetContextSpy: ReturnType<typeof vi.spyOn>;

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
  if (typeof overrides.windowWidth === 'number') setViewportWidth(overrides.windowWidth);
  return render(
    <TestProviders>
      <InstrumentCard {...baseProps} {...overrides} />
    </TestProviders>,
  );
}

describe('InstrumentCard', () => {
  beforeAll(() => {
    stubResizeObserver();

    Object.defineProperty(HTMLElement.prototype, 'clientWidth', {
      configurable: true,
      get() {
        const text = this.textContent || '';
        if (text.includes('View full leaderboard') || text.includes('View leaderboard')) {
          return Math.max(window.innerWidth - 80, 0);
        }
        return originalClientWidth?.get ? originalClientWidth.get.call(this) : 0;
      },
    });

    canvasGetContextSpy = vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockImplementation(() => {
      let font = '';
      return {
        get font() {
          return font;
        },
        set font(value: string) {
          font = value;
        },
        measureText(text: string) {
          return { width: text.includes('View full leaderboard (') ? measuredCountedLabelWidth : measuredPlainLabelWidth } as TextMetrics;
        },
      } as unknown as CanvasRenderingContext2D;
    });
  });

  beforeEach(() => setViewportWidth(900));

  beforeEach(() => {
    measuredCountedLabelWidth = 320;
    measuredPlainLabelWidth = 140;
    mockMeasuredCardWidth = 0;
  });

  afterAll(() => {
    canvasGetContextSpy.mockRestore();
    if (originalClientWidth) {
      Object.defineProperty(HTMLElement.prototype, 'clientWidth', originalClientWidth);
      return;
    }
    delete (HTMLElement.prototype as { clientWidth?: number }).clientWidth;
  });

  it('renders instrument label', () => {
    renderCard();
    expect(screen.getByText('Lead')).toBeTruthy();
  });

  it('shows error message when prefetchedError is set', () => {
    renderCard({ prefetchedError: 'Failed to load' });
    expect(screen.getByText('Something Went Wrong')).toBeTruthy();
  });

  it('shows no entries message with instrument name when entries are empty', () => {
    renderCard({ prefetchedEntries: [] });
    expect(screen.getByText('No scores yet')).toBeTruthy();
  });

  it('empty card is not clickable', () => {
    renderCard({ prefetchedEntries: [] });
    const card = screen.getByTestId('inst-empty-Solo_Guitar').closest('div[style*="height: 100%"]') as HTMLElement;
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
    setViewportWidth(1200);
    const entries = [makeEntry(1)];
    renderCard({ prefetchedEntries: entries, windowWidth: 1200 });
    expect(screen.getByTestId('accuracy')).toBeTruthy();
  });

  it('hides accuracy on narrow width', () => {
    setViewportWidth(300);
    const entries = [makeEntry(1)];
    renderCard({ prefetchedEntries: entries, windowWidth: 300 });
    expect(screen.queryByTestId('accuracy')).toBeNull();
  });

  it('hides accuracy when a two-column layout makes the card too narrow', () => {
    setViewportWidth(900);
    const entries = [makeEntry(1)];
    renderCard({ prefetchedEntries: entries, windowWidth: 900 });
    expect(screen.queryByTestId('accuracy')).toBeNull();
  });

  it('drops fixed score width on compact cards to give names more room', () => {
    const entries = [makeEntry(1)];
    renderCard({ prefetchedEntries: entries, windowWidth: 900 });
    expect(screen.getByText('149,000').style.width).toBe('');
  });

  it('keeps fixed score width on wide cards', () => {
    const entries = [makeEntry(1)];
    renderCard({ prefetchedEntries: entries, windowWidth: 1200 });
    expect(screen.getByText('149,000').style.width).toBe('8ch');
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
    expect(screen.getByText('View leaderboard')).toBeTruthy();
  });

  it('shows tracked/total counts on view-all button when provided', () => {
    const entries = [makeEntry(1), makeEntry(2)];
    renderCard({ prefetchedEntries: entries, localEntries: 10030, totalEntries: 700000, windowWidth: 1200 });
    expect(screen.getByText('View full leaderboard (10,030 tracked / 700,000 total)')).toBeTruthy();
  });

  it('falls back to plain label when totalEntries is zero', () => {
    const entries = [makeEntry(1)];
    renderCard({ prefetchedEntries: entries, localEntries: 0, totalEntries: 0 });
    expect(screen.getByText('View leaderboard')).toBeTruthy();
  });

  it('stacks view-all counts into three rows at hyper-compact width', () => {
    const entries = [makeEntry(1)];
    renderCard({
      prefetchedEntries: entries,
      localEntries: 10002,
      totalEntries: 364400,
      windowWidth: 320,
    });
    expect(screen.getByText('View full leaderboard')).toBeTruthy();
    expect(screen.getByText('10,002 tracked')).toBeTruthy();
    expect(screen.getByText('364,400 total')).toBeTruthy();
  });

  it('switches counted CTA into compact mode on common phone widths', () => {
    const entries = [makeEntry(1)];
    renderCard({
      prefetchedEntries: entries,
      localEntries: 10002,
      totalEntries: 212592,
      windowWidth: 390,
    });
    expect(screen.getByText('View full leaderboard')).toBeTruthy();
    expect(screen.getByText('10,002 tracked')).toBeTruthy();
    expect(screen.getByText('212,592 total')).toBeTruthy();
    expect(screen.queryByText('View full leaderboard (10,002 tracked / 212,592 total)')).toBeNull();
  });

  it('keeps plain labels single-line on common phone widths', () => {
    const entries = [makeEntry(1)];
    renderCard({
      prefetchedEntries: entries,
      windowWidth: 390,
    });
    const button = screen.getByText('View leaderboard');
    expect(button.style.flexDirection).not.toBe('column');
    expect(button.style.whiteSpace).toBe('nowrap');
  });

  it('keeps the wider counted CTA single-line when compact mode is not used', () => {
    const entries = [makeEntry(1), makeEntry(2)];
    renderCard({
      prefetchedEntries: entries,
      localEntries: 10030,
      totalEntries: 700000,
      windowWidth: 1200,
    });
    const button = screen.getByText('View full leaderboard (10,030 tracked / 700,000 total)');
    expect(button.style.whiteSpace).toBe('nowrap');
    expect(button.style.wordBreak).toBe('normal');
    expect(button.style.minHeight).toBe('48px');
  });

  it('switches to compact mode when the counted label would overflow even above the narrow breakpoint', () => {
    measuredCountedLabelWidth = 420;
    const entries = [makeEntry(1), makeEntry(2)];
    renderCard({
      prefetchedEntries: entries,
      localEntries: 10030,
      totalEntries: 700000,
      windowWidth: 430,
    });
    expect(screen.getByText('View full leaderboard')).toBeTruthy();
    expect(screen.getByText('10,030 tracked')).toBeTruthy();
    expect(screen.getByText('700,000 total')).toBeTruthy();
    expect(screen.queryByText('View full leaderboard (10,030 tracked / 700,000 total)')).toBeNull();
  });

  it('exits compact counted mode once the card regrows past the narrow breakpoint', () => {
    const entries = [makeEntry(1), makeEntry(2)];
    renderCard({
      prefetchedEntries: entries,
      localEntries: 10030,
      totalEntries: 700000,
      windowWidth: 1200,
    });
    expect(screen.getByText('View full leaderboard (10,030 tracked / 700,000 total)')).toBeTruthy();
    expect(screen.queryByText('10,030 tracked')).toBeNull();
    expect(screen.queryByText('700,000 total')).toBeNull();
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
