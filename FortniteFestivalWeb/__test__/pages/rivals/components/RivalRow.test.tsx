import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import RivalRow from '../../../../src/pages/rivals/components/RivalRow';
import type { RivalSummary } from '@festival/core/api/serverTypes';

vi.mock('react-i18next', () => ({
  useTranslation: () => ({ t: (key: string, opts?: Record<string, unknown>) => {
    if (key === 'rivals.sharedSongs') return `${opts?.count ?? 0} shared songs`;
    if (key === 'rivals.songsAhead') return 'songs ahead';
    if (key === 'rivals.songsBehind') return 'songs behind';
    return key;
  } }),
}));

function makeRival(overrides: Partial<RivalSummary> = {}): RivalSummary {
  return {
    accountId: 'rival-1',
    displayName: 'TestRival',
    sharedSongCount: 10,
    rivalScore: 500,
    aheadCount: 3,
    behindCount: 7,
    avgSignedDelta: 0,
    ...overrides,
  };
}

describe('RivalRow', () => {
  it('renders rival name and song count', () => {
    render(<RivalRow rival={makeRival()} direction="below" onClick={vi.fn()} />);
    expect(screen.getByText('TestRival')).toBeDefined();
    expect(screen.getByText('10 shared songs')).toBeDefined();
  });

  it('renders "Unknown Player" when displayName is null', () => {
    render(<RivalRow rival={makeRival({ displayName: null })} direction="below" onClick={vi.fn()} />);
    expect(screen.getByText('Unknown Player')).toBeDefined();
  });

  it('calls onClick on click', () => {
    const onClick = vi.fn();
    render(<RivalRow rival={makeRival()} direction="below" onClick={onClick} />);
    fireEvent.click(screen.getByRole('button'));
    expect(onClick).toHaveBeenCalledOnce();
  });

  it('calls onClick on Enter key', () => {
    const onClick = vi.fn();
    render(<RivalRow rival={makeRival()} direction="above" onClick={onClick} />);
    fireEvent.keyDown(screen.getByRole('button'), { key: 'Enter' });
    expect(onClick).toHaveBeenCalledOnce();
  });

  it('does not fire onClick on other keys', () => {
    const onClick = vi.fn();
    render(<RivalRow rival={makeRival()} direction="above" onClick={onClick} />);
    fireEvent.keyDown(screen.getByRole('button'), { key: 'Space' });
    expect(onClick).not.toHaveBeenCalled();
  });

  it('applies winning tint class for direction "below"', () => {
    const { container } = render(<RivalRow rival={makeRival()} direction="below" onClick={vi.fn()} />);
    const row = container.firstChild as HTMLElement;
    expect(row.className).toContain('Winning');
  });

  it('applies losing tint class for direction "above"', () => {
    const { container } = render(<RivalRow rival={makeRival()} direction="above" onClick={vi.fn()} />);
    const row = container.firstChild as HTMLElement;
    expect(row.className).toContain('Losing');
  });

  it('renders ahead and behind counts', () => {
    render(<RivalRow rival={makeRival({ aheadCount: 5, behindCount: 8 })} direction="below" onClick={vi.fn()} />);
    expect(screen.getByText(/8/)).toBeDefined(); // behindCount → songs ahead
    expect(screen.getByText(/5/)).toBeDefined(); // aheadCount → songs behind
  });

  it('wires the shared compact-layout classes onto the inner row elements', () => {
    render(<RivalRow rival={makeRival()} direction="below" onClick={vi.fn()} />);

    const content = screen.getByText('TestRival').closest('div') as HTMLElement;
    const shared = screen.getByText('10 shared songs') as HTMLElement;
    const pillRow = screen.getByText(/7/).closest('div') as HTMLElement;

    expect(content.className).toContain('rivalRowContent');
    expect(shared.className).toContain('rivalRowShared');
    expect(pillRow.className).toContain('rivalRowPillRow');
    expect(content.style.gridTemplateColumns).toBe('');
    expect(shared.style.gridColumn).toBe('');
    expect(pillRow.style.gridRow).toBe('');
  });

  it('forwards style and onAnimationEnd', () => {
    const onAnimEnd = vi.fn();
    const { container } = render(
      <RivalRow rival={makeRival()} direction="below" onClick={vi.fn()} style={{ opacity: 0 }} onAnimationEnd={onAnimEnd} />,
    );
    const row = container.querySelector('[role="button"]') as HTMLElement;
    expect(row.style.opacity).toBe('0');
  });
});
