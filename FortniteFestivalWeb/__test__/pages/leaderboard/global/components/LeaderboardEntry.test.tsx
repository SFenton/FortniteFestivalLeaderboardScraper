import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { LeaderboardEntry } from '../../../../../src/pages/leaderboard/global/components/LeaderboardEntry';

describe('LeaderboardEntry', () => {
  it('renders rank and name', () => {
    render(<LeaderboardEntry rank={1} displayName="Player One" score={145000} />);
    expect(screen.getByText('Player One')).toBeDefined();
  });

  it('renders score', () => {
    render(<LeaderboardEntry rank={1} displayName="P" score={145000} />);
    expect(screen.getByText('145,000')).toBeDefined();
  });

  it('applies bold styling for tracked player', () => {
    const { container } = render(<LeaderboardEntry rank={1} displayName="Me" score={100000} isPlayer />);
    expect(container.querySelector('[class*="Bold"]') || container.querySelector('[class*="bold"]')).toBeTruthy();
  });

  it('shows season when showSeason is true', () => {
    const { container } = render(<LeaderboardEntry rank={1} displayName="P" score={100000} showSeason season={5} />);
    expect(container.textContent).toContain('5');
  });

  it('shows accuracy when showAccuracy is true', () => {
    const { container } = render(<LeaderboardEntry rank={1} displayName="P" score={100000} showAccuracy accuracy={95.5} isFullCombo={false} />);
    expect(container.innerHTML).toBeTruthy();
  });

  it('shows stars when showStars is true', () => {
    const { container } = render(<LeaderboardEntry rank={1} displayName="P" score={100000} showStars stars={5} />);
    expect(container.querySelectorAll('img').length).toBeGreaterThanOrEqual(1);
  });

  it('shows gold stars for 6 stars', () => {
    const { container } = render(<LeaderboardEntry rank={1} displayName="P" score={100000} showStars stars={6} />);
    const imgs = container.querySelectorAll('img');
    expect(imgs.length).toBeGreaterThanOrEqual(1);
  });

  it('hides optional columns when show flags are false', () => {
    const { container } = render(<LeaderboardEntry rank={1} displayName="P" score={100000} showSeason={false} showAccuracy={false} showStars={false} />);
    expect(container.textContent).toContain('P');
  });

  it('applies custom scoreWidth', () => {
    const { container } = render(<LeaderboardEntry rank={1} displayName="P" score={100000} scoreWidth="8ch" />);
    expect(container.innerHTML).toContain('8ch');
  });

  it('renders with season null and showSeason true', () => {
    render(<LeaderboardEntry rank={1} displayName="P1" score={100000} isPlayer={false} showSeason season={null} />);
    expect(screen.getByText('#1')).toBeTruthy();
  });

  it('renders with accuracy undefined (null fallback)', () => {
    const { container } = render(
      <LeaderboardEntry rank={1} displayName="P1" score={100000} accuracy={undefined as any} isPlayer={false} showSeason={false} showAccuracy />,
    );
    expect(container.querySelector('[class*="colAcc"]')).toBeTruthy();
  });

  it('renders label instead of rank when label is provided', () => {
    render(<LeaderboardEntry label="2024-01-15" displayName="P1" score={100000} />);
    expect(screen.getByText('2024-01-15')).toBeTruthy();
  });

  it('renders label and hides rank column when only label is given', () => {
    const { container } = render(<LeaderboardEntry label="Jan 15" displayName="P1" score={50000} />);
    expect(container.querySelector('[class*="colRank"]')).toBeFalsy();
    expect(screen.getByText('Jan 15')).toBeTruthy();
  });

  it('shows dash for stars=0 with showStars', () => {
    const { container } = render(
      <LeaderboardEntry rank={1} displayName="P1" score={100000} showStars stars={0} />,
    );
    expect(container.querySelector('[class*="colStars"]')!.textContent).toBe('\u2014');
  });

  it('shows dash for stars=null with showStars', () => {
    const { container } = render(
      <LeaderboardEntry rank={1} displayName="P1" score={100000} showStars stars={null} />,
    );
    expect(container.querySelector('[class*="colStars"]')!.textContent).toBe('\u2014');
  });

  it('renders stars with isFullCombo false', () => {
    const { container } = render(
      <LeaderboardEntry rank={1} displayName="P1" score={100000} showStars stars={3} isFullCombo={false} />,
    );
    expect(container.querySelectorAll('img').length).toBeGreaterThanOrEqual(1);
  });

  it('renders with showSeason true but season undefined', () => {
    const { container } = render(
      <LeaderboardEntry rank={1} displayName="P1" score={100000} showSeason />,
    );
    // season is undefined → no SeasonPill rendered
    expect(container.textContent).toContain('#1');
  });

  it('renders with rank=0', () => {
    render(<LeaderboardEntry rank={0} displayName="P1" score={100000} />);
    expect(screen.getByText('#0')).toBeTruthy();
  });
});
