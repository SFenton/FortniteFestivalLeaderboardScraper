import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import type { ChartPoint } from '../../../../../src/hooks/chart/useChartData';

/** Set window.innerWidth and override matchMedia to evaluate min-width queries. */
function setViewportWidth(width: number) {
  Object.defineProperty(window, 'innerWidth', { value: width, writable: true });
  window.matchMedia = (query: string) => {
    const match = query.match(/\(min-width:\s*(\d+)px\)/);
    const matches = match ? width >= Number(match[1]) : false;
    return { matches, media: query, onchange: null, addEventListener: () => {}, removeEventListener: () => {}, addListener: () => {}, removeListener: () => {}, dispatchEvent: () => false } as MediaQueryList;
  };
}

// ScoreHistoryChart.module.css no longer imported by ScoreCardList (styles are inline)

vi.mock('../../../../../src/components/songs/metadata/AccuracyDisplay', () => ({
  default: ({ accuracy }: any) => <span data-testid="accuracy">{accuracy}</span>,
}));
vi.mock('../../../../../src/components/songs/metadata/SeasonPill', () => ({
  default: ({ season }: any) => <span data-testid="season">S{season}</span>,
}));
vi.mock('../../../../../src/contexts/FeatureFlagsContext', () => ({
  useFeatureFlags: () => ({ rivals: true, compete: true, leaderboards: true, firstRun: true, difficulty: true }),
  FeatureFlagsProvider: ({ children }: any) => children,
}));

import ScoreCardList from '../../../../../src/pages/songinfo/components/chart/ScoreCardList';

function makePoint(overrides: Partial<ChartPoint> = {}): ChartPoint {
  return {
    date: '2025-01-01',
    dateLabel: 'Jan 1',
    timestamp: 1735700000000,
    score: 100000,
    accuracy: 95,
    isFullCombo: false,
    ...overrides,
  };
}

describe('ScoreCardList', () => {
  beforeEach(() => setViewportWidth(900));

  it('returns null when no cards and zero height', () => {
    const { container } = render(
      <ScoreCardList displayedCards={[]} listHeight={0} listPhase="idle" />,
    );
    expect(container.innerHTML).toBe('');
  });

  it('renders cards in idle phase without animation', () => {
    const cards = [makePoint({ date: '2025-01-01', score: 150000 })];
    const { container } = render(
      <ScoreCardList displayedCards={cards} listHeight={200} listPhase="idle" />,
    );
    expect(container.querySelector('div > div')).toBeTruthy();
    expect(container.textContent).toContain('150,000');
  });

  it('applies fadeInUp animation in "in" phase', () => {
    const cards = [makePoint()];
    const { container } = render(
      <ScoreCardList displayedCards={cards} listHeight={200} listPhase="in" />,
    );
    const card = container.querySelector(':scope > div > div > div');
    expect(card?.getAttribute('style')).toContain('fadeInUp');
  });

  it('applies translateY out animation in "out" phase', () => {
    const cards = [makePoint()];
    const { container } = render(
      <ScoreCardList displayedCards={cards} listHeight={200} listPhase="out" />,
    );
    const card = container.querySelector(':scope > div > div > div');
    const style = card?.getAttribute('style') ?? '';
    expect(style).toContain('translateY');
    expect(style).toContain('opacity: 0');
  });

  it('renders multiple cards with staggered delays', () => {
    const cards = [
      makePoint({ date: '2025-01-01' }),
      makePoint({ date: '2025-01-02' }),
    ];
    const { container } = render(
      <ScoreCardList displayedCards={cards} listHeight={300} listPhase="in" />,
    );
    const cardEls = container.querySelectorAll(':scope > div > div > div');
    expect(cardEls.length).toBe(2);
  });

  it('renders even when displayedCards is empty but listHeight > 0', () => {
    const { container } = render(
      <ScoreCardList displayedCards={[]} listHeight={100} listPhase="idle" />,
    );
    expect(container.firstElementChild).toBeTruthy();
  });

  it('renders season pill when season is present', () => {
    const cards = [makePoint({ season: 5 })];
    render(<ScoreCardList displayedCards={cards} listHeight={200} listPhase="idle" />);
    expect(screen.getByTestId('season')).toBeTruthy();
  });
});
