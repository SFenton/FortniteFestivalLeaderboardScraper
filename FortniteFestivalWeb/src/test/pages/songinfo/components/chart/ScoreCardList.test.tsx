import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import type { ChartPoint } from '../../../../../hooks/chart/useChartData';

vi.mock('../../../../../pages/songinfo/components/chart/ScoreHistoryChart.module.css', () => ({
  default: { scoreCardList: 'scoreCardList', scoreListCard: 'scoreListCard', scoreCardDate: 'scoreCardDate', scoreCardScore: 'scoreCardScore' },
}));

vi.mock('../../../../../components/songs/metadata/AccuracyDisplay', () => ({
  default: ({ accuracy }: any) => <span data-testid="accuracy">{accuracy}</span>,
}));
vi.mock('../../../../../components/songs/metadata/SeasonPill', () => ({
  default: ({ season }: any) => <span data-testid="season">S{season}</span>,
}));

import ScoreCardList from '../../../../../pages/songinfo/components/chart/ScoreCardList';

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
    expect(container.querySelector('.scoreCardList')).toBeTruthy();
    expect(container.textContent).toContain('150,000');
  });

  it('applies fadeInUp animation in "in" phase', () => {
    const cards = [makePoint()];
    const { container } = render(
      <ScoreCardList displayedCards={cards} listHeight={200} listPhase="in" />,
    );
    const card = container.querySelector('.scoreListCard');
    expect(card?.getAttribute('style')).toContain('fadeInUp');
  });

  it('applies translateY out animation in "out" phase', () => {
    const cards = [makePoint()];
    const { container } = render(
      <ScoreCardList displayedCards={cards} listHeight={200} listPhase="out" />,
    );
    const card = container.querySelector('.scoreListCard');
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
    const cardEls = container.querySelectorAll('.scoreListCard');
    expect(cardEls.length).toBe(2);
  });

  it('renders even when displayedCards is empty but listHeight > 0', () => {
    const { container } = render(
      <ScoreCardList displayedCards={[]} listHeight={100} listPhase="idle" />,
    );
    expect(container.querySelector('.scoreCardList')).toBeTruthy();
  });

  it('renders season pill when season is present', () => {
    const cards = [makePoint({ season: 5 })];
    render(<ScoreCardList displayedCards={cards} listHeight={200} listPhase="idle" />);
    expect(screen.getByTestId('season')).toBeTruthy();
  });
});
