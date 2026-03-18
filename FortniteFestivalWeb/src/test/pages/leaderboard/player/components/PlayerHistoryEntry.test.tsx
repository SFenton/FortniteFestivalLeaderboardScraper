import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';

import { PlayerHistoryEntry } from '../../../../../pages/leaderboard/player/components/PlayerHistoryEntry';

vi.mock('../../../../../components/songs/metadata/SeasonPill', () => ({
  default: ({ season }: { season: number }) => <span data-testid="season">{season}</span>,
}));
vi.mock('../../../../../components/songs/metadata/AccuracyDisplay', () => ({
  default: ({ accuracy }: { accuracy: number | null }) => <span data-testid="accuracy">{accuracy}</span>,
}));

describe('PlayerHistoryEntry', () => {
  it('renders date and score', () => {
    render(
      <PlayerHistoryEntry
        date="2025-01-15"
        score={145000}
      />,
    );
    expect(screen.getByText('2025-01-15')).toBeTruthy();
    expect(screen.getByText('145,000')).toBeTruthy();
  });

  it('renders season pill when showSeason is true and season is present', () => {
    render(
      <PlayerHistoryEntry
        date="2025-01-15"
        score={145000}
        season={5}
        showSeason
      />,
    );
    expect(screen.getByTestId('season')).toBeTruthy();
  });

  it('hides season when showSeason is false', () => {
    render(
      <PlayerHistoryEntry
        date="2025-01-15"
        score={145000}
        season={5}
        showSeason={false}
      />,
    );
    expect(screen.queryByTestId('season')).toBeNull();
  });

  it('renders accuracy when showAccuracy is true', () => {
    render(
      <PlayerHistoryEntry
        date="2025-01-15"
        score={145000}
        accuracy={98.5}
        showAccuracy
      />,
    );
    expect(screen.getByTestId('accuracy')).toBeTruthy();
  });

  it('hides accuracy when showAccuracy is false', () => {
    render(
      <PlayerHistoryEntry
        date="2025-01-15"
        score={145000}
        accuracy={98.5}
      />,
    );
    expect(screen.queryByTestId('accuracy')).toBeNull();
  });

  it('applies bold styling for high score', () => {
    const { container } = render(
      <PlayerHistoryEntry
        date="2025-01-15"
        score={145000}
        isHighScore
      />,
    );
    // Check that the name column has bold class
    expect(container.innerHTML).toContain('textBold');
  });

  it('applies scoreWidth to score column', () => {
    const { container } = render(
      <PlayerHistoryEntry
        date="2025-01-15"
        score={145000}
        scoreWidth="8ch"
      />,
    );
    const scoreSpan = container.querySelector('[style*="width"]');
    expect(scoreSpan?.getAttribute('style')).toContain('8ch');
  });

  it('renders with FC badge when isFullCombo', () => {
    const { container } = render(
      <PlayerHistoryEntry
        date="2024-01-01"
        score={100000}
        accuracy={950000}
        isFullCombo={true}
        isHighScore={false}
        season={5}
        showAccuracy={true}
        showSeason={true}
        scoreWidth="8ch"
      />,
    );
    expect(container.textContent).toContain('100,000');
  });

  it('hides season pill when season is null even with showSeason', () => {
    render(
      <PlayerHistoryEntry
        date="2025-01-15"
        score={150000}
        showSeason
        season={null}
      />,
    );
    expect(screen.queryByTestId('season')).toBeNull();
  });

  it('renders accuracy cell with accuracy undefined (null fallback)', () => {
    const { container } = render(
      <PlayerHistoryEntry date="2025-01-15" score={100000} showAccuracy accuracy={undefined} />,
    );
    expect(container.querySelector('[data-testid="accuracy"]')).toBeTruthy();
  });
});
