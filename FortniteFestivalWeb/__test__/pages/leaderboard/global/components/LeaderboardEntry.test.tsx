import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { LeaderboardEntry } from '../../../../../src/pages/leaderboard/global/components/LeaderboardEntry';
import { TestProviders as W } from '../../../../helpers/TestProviders';

describe('LeaderboardEntry', () => {
  it('renders rank and name', () => {
    render(<W><LeaderboardEntry rank={1} displayName="Player One" score={145000} /></W>);
    expect(screen.getByText('Player One')).toBeDefined();
  });

  it('renders score', () => {
    render(<W><LeaderboardEntry rank={1} displayName="P" score={145000} /></W>);
    expect(screen.getByText('145,000')).toBeDefined();
  });

  it('applies bold styling for tracked player', () => {
    const { container } = render(<W><LeaderboardEntry rank={1} displayName="Me" score={100000} isPlayer /></W>);
    const nameEl = screen.getByText('Me');
    expect(nameEl.style.fontWeight).toBe('700');
  });

  it('shows season when showSeason is true', () => {
    const { container } = render(<W><LeaderboardEntry rank={1} displayName="P" score={100000} showSeason season={5} /></W>);
    expect(container.textContent).toContain('5');
  });

  it('shows accuracy when showAccuracy is true', () => {
    const { container } = render(<W><LeaderboardEntry rank={1} displayName="P" score={100000} showAccuracy accuracy={95.5} isFullCombo={false} /></W>);
    expect(container.innerHTML).toBeTruthy();
  });

  it('shows stars when showStars is true', () => {
    const { container } = render(<W><LeaderboardEntry rank={1} displayName="P" score={100000} showStars stars={5} /></W>);
    expect(container.querySelectorAll('img').length).toBeGreaterThanOrEqual(1);
  });

  it('shows gold stars for 6 stars', () => {
    const { container } = render(<W><LeaderboardEntry rank={1} displayName="P" score={100000} showStars stars={6} /></W>);
    const imgs = container.querySelectorAll('img');
    expect(imgs.length).toBeGreaterThanOrEqual(1);
  });

  it('hides optional columns when show flags are false', () => {
    const { container } = render(<W><LeaderboardEntry rank={1} displayName="P" score={100000} showSeason={false} showAccuracy={false} showStars={false} /></W>);
    expect(container.textContent).toContain('P');
  });

  it('applies custom scoreWidth', () => {
    const { container } = render(<W><LeaderboardEntry rank={1} displayName="P" score={100000} scoreWidth="8ch" /></W>);
    expect(container.innerHTML).toContain('8ch');
  });

  it('renders hidden season placeholder when season is null and showSeason true', () => {
    const { container } = render(<W><LeaderboardEntry rank={1} displayName="P1" score={100000} isPlayer={false} showSeason season={null} /></W>);
    expect(screen.getByText('#1')).toBeTruthy();
    const hidden = container.querySelector('[aria-hidden="true"]');
    expect(hidden).toBeTruthy();
    expect((hidden as HTMLElement).style.visibility).toBe('hidden');
  });

  it('renders with accuracy undefined (null fallback)', () => {
    const { container } = render(
      <W><LeaderboardEntry rank={1} displayName="P1" score={100000} accuracy={undefined as any} isPlayer={false} showSeason={false} showAccuracy /></W>,
    );
    const accCol = [...container.querySelectorAll('span')].find(el => (el as HTMLElement).style.textAlign === 'center');
    expect(accCol).toBeTruthy();
  });

  it('renders label instead of rank when label is provided', () => {
    render(<W><LeaderboardEntry label="2024-01-15" displayName="P1" score={100000} /></W>);
    expect(screen.getByText('2024-01-15')).toBeTruthy();
  });

  it('renders label and hides rank column when only label is given', () => {
    const { container } = render(<W><LeaderboardEntry label="Jan 15" displayName="P1" score={50000} /></W>);
    expect(screen.queryByText(/#\d/)).toBeFalsy();
    expect(screen.getByText('Jan 15')).toBeTruthy();
  });

  it('shows dash for stars=0 with showStars and fixed width', () => {
    const { container } = render(<W><LeaderboardEntry rank={1} displayName="P1" score={100000} showStars stars={0} /></W>);
    expect(screen.getByText('\u2014')).toBeTruthy();
    const starsCol = screen.getByText('\u2014').closest('span')!;
    expect(starsCol.style.width).toBe('132px');
  });

  it('shows dash for stars=null with showStars and fixed width', () => {
    const { container } = render(<W><LeaderboardEntry rank={1} displayName="P1" score={100000} showStars stars={null} /></W>);
    expect(screen.getByText('\u2014')).toBeTruthy();
    const starsCol = screen.getByText('\u2014').closest('span')!;
    expect(starsCol.style.width).toBe('132px');
  });

  it('renders stars with isFullCombo false', () => {
    const { container } = render(<W><LeaderboardEntry rank={1} displayName="P1" score={100000} showStars stars={3} isFullCombo={false} /></W>);
    expect(container.querySelectorAll('img').length).toBeGreaterThanOrEqual(1);
  });

  it('renders hidden season placeholder when showSeason true but season undefined', () => {
    const { container } = render(<W><LeaderboardEntry rank={1} displayName="P1" score={100000} showSeason /></W>);
    expect(container.textContent).toContain('#1');
    const hidden = container.querySelector('[aria-hidden="true"]');
    expect(hidden).toBeTruthy();
    expect((hidden as HTMLElement).style.visibility).toBe('hidden');
  });

  it('renders with rank=0', () => {
    render(<W><LeaderboardEntry rank={0} displayName="P1" score={100000} /></W>);
    expect(screen.getByText('#0')).toBeTruthy();
  });

  it('shows difficulty pill when showDifficulty is true and difficulty is provided', () => {
    const { container } = render(<W><LeaderboardEntry rank={1} displayName="P1" score={100000} showDifficulty difficulty={2} /></W>);
    expect(screen.getByText('H')).toBeTruthy();
  });

  it('hides difficulty pill when showDifficulty is false', () => {
    render(<W><LeaderboardEntry rank={1} displayName="P1" score={100000} showDifficulty={false} difficulty={2} /></W>);
    expect(screen.queryByText('H')).toBeFalsy();
  });

  it('renders hidden placeholder when difficulty is null', () => {
    const { container } = render(<W><LeaderboardEntry rank={1} displayName="P1" score={100000} showDifficulty difficulty={null} /></W>);
    const hidden = container.querySelector('[aria-hidden="true"]');
    expect(hidden).toBeTruthy();
    expect((hidden as HTMLElement).style.visibility).toBe('hidden');
  });

  it('renders hidden placeholder when difficulty is -1 (unset)', () => {
    const { container } = render(<W><LeaderboardEntry rank={1} displayName="P1" score={100000} showDifficulty difficulty={-1} /></W>);
    const hidden = container.querySelector('[aria-hidden="true"]');
    expect(hidden).toBeTruthy();
    expect((hidden as HTMLElement).style.visibility).toBe('hidden');
  });

  it('renders difficulty pill for each difficulty value including Easy', () => {
    const labels = ['E', 'M', 'H', 'X'];
    labels.forEach((label, i) => {
      const { unmount } = render(<W><LeaderboardEntry rank={1} displayName="P" score={100000} showDifficulty difficulty={i} /></W>);
      expect(screen.getByText(label)).toBeTruthy();
      unmount();
    });
  });

  it('applies custom rankWidth to the rank column', () => {
    const { container } = render(<W><LeaderboardEntry rank={12345} displayName="P1" score={100000} rankWidth={100} /></W>);
    const rankSpan = screen.getByText('#12,345');
    expect(rankSpan.style.width).toBe('100px');
  });

  it('uses default rank column width when rankWidth is not provided', () => {
    render(<W><LeaderboardEntry rank={1} displayName="P1" score={100000} /></W>);
    const rankSpan = screen.getByText('#1');
    expect(rankSpan.style.width).toBe('48px');
  });
});
