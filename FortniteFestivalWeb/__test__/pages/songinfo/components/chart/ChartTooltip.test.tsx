import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';

import ChartTooltip from '../../../../../src/pages/songinfo/components/chart/ChartTooltip';

const MOCK_POINT = {
  date: '2025-01-15T10:00:00Z',
  dateLabel: 'Jan 15, 2025',
  timestamp: new Date('2025-01-15T10:00:00Z').getTime(),
  score: 145000,
  accuracy: 99.5,
  isFullCombo: true,
  stars: 6,
  season: 5,
};

describe('ChartTooltip', () => {
  it('returns null when not active', () => {
    const { container } = render(<ChartTooltip active={false} payload={[]} />);
    expect(container.innerHTML).toBe('');
  });

  it('returns null when payload is empty', () => {
    const { container } = render(<ChartTooltip active payload={[]} />);
    expect(container.innerHTML).toBe('');
  });

  it('renders score and accuracy when active with payload', () => {
    render(<ChartTooltip active payload={[{ payload: MOCK_POINT }]} />);
    expect(screen.getByText(/145,000/)).toBeTruthy();
    expect(screen.getByText(/99.5%/)).toBeTruthy();
  });

  it('shows FC badge for full combo', () => {
    render(<ChartTooltip active payload={[{ payload: MOCK_POINT }]} />);
    expect(screen.getByText('FC')).toBeTruthy();
  });

  it('shows stars', () => {
    render(<ChartTooltip active payload={[{ payload: MOCK_POINT }]} />);
    expect(screen.getByText('★★★★★★')).toBeTruthy();
  });

  it('shows season', () => {
    render(<ChartTooltip active payload={[{ payload: MOCK_POINT }]} />);
    expect(screen.getByText(/S5/)).toBeTruthy();
  });

  it('renders integer accuracy without decimals', () => {
    const intPoint = { ...MOCK_POINT, accuracy: 100, isFullCombo: false, stars: undefined };
    render(<ChartTooltip active payload={[{ payload: intPoint }]} />);
    expect(screen.getByText(/100%/)).toBeTruthy();
  });

  it('hides season when null', () => {
    const noSeason = { ...MOCK_POINT, season: undefined, stars: undefined };
    render(<ChartTooltip active payload={[{ payload: noSeason }]} />);
    expect(screen.queryByText(/· S/)).toBeNull();
  });

  it('returns null when payload is undefined', () => {
    const { container } = render(<ChartTooltip active />);
    expect(container.innerHTML).toBe('');
  });

  it('renders without stars when stars is null', () => {
    const noStars = { ...MOCK_POINT, stars: null, isFullCombo: false };
    const { container } = render(<ChartTooltip active payload={[{ payload: noStars as any }]} />);
    expect(container.textContent).not.toContain('★');
  });
});
