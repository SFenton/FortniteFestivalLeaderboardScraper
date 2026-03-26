import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import RivalSongRow from '../../../../src/pages/rivals/components/RivalSongRow';
import type { RivalSongComparison } from '@festival/core/api/serverTypes';

vi.mock('react-i18next', () => ({
  useTranslation: () => ({ t: (key: string) => {
    if (key === 'rivals.detail.you') return 'You';
    if (key === 'rivals.detail.them') return 'Them';
    return key;
  } }),
}));

function makeSong(overrides: Partial<RivalSongComparison> = {}): RivalSongComparison {
  return {
    songId: 'song-1',
    title: 'Test Song',
    artist: 'Test Artist',
    instrument: 'Solo_Guitar',
    userRank: 10,
    rivalRank: 12,
    userScore: 150000,
    rivalScore: 148000,
    rankDelta: 2,
    scoreDelta: 2000,
    ...overrides,
  };
}

describe('RivalSongRow', () => {
  describe('standalone mode', () => {
    it('renders song title and artist with year', () => {
      render(<RivalSongRow song={makeSong()} albumArt="https://example.com/art.jpg" year={2024} onClick={vi.fn()} standalone />);
      expect(screen.getByText('Test Song')).toBeDefined();
      expect(screen.getByText(/Test Artist/)).toBeDefined();
      expect(screen.getByText(/2024/)).toBeDefined();
    });

    it('renders player and rival names', () => {
      render(<RivalSongRow song={makeSong()} playerName="Alice" rivalName="Bob" onClick={vi.fn()} standalone />);
      expect(screen.getByText('Alice')).toBeDefined();
      expect(screen.getByText('Bob')).toBeDefined();
    });

    it('renders default labels when names not provided', () => {
      render(<RivalSongRow song={makeSong()} onClick={vi.fn()} standalone />);
      expect(screen.getByText('You')).toBeDefined();
      expect(screen.getByText('Them')).toBeDefined();
    });

    it('renders ranks and scores', () => {
      render(<RivalSongRow song={makeSong({ userRank: 5, rivalRank: 8, userScore: 200000, rivalScore: 195000 })} onClick={vi.fn()} standalone />);
      expect(screen.getByText('#5')).toBeDefined();
      expect(screen.getByText('#8')).toBeDefined();
      expect(screen.getByText('200,000')).toBeDefined();
      expect(screen.getByText('195,000')).toBeDefined();
    });

    it('renders positive rank delta with + sign', () => {
      render(<RivalSongRow song={makeSong({ rankDelta: 5 })} onClick={vi.fn()} standalone />);
      expect(screen.getByText('+5')).toBeDefined();
    });

    it('renders negative rank delta', () => {
      render(<RivalSongRow song={makeSong({ rankDelta: -3 })} onClick={vi.fn()} standalone />);
      expect(screen.getByText('-3')).toBeDefined();
    });

    it('renders zero rank delta', () => {
      render(<RivalSongRow song={makeSong({ rankDelta: 0 })} onClick={vi.fn()} standalone />);
      expect(screen.getByText('0')).toBeDefined();
    });

    it('renders score diff with minus sign for negative', () => {
      render(<RivalSongRow song={makeSong({ userScore: 100, rivalScore: 200 })} onClick={vi.fn()} standalone />);
      // Score diff should contain the unicode minus sign
      expect(screen.getByText(/\u2212/)).toBeDefined();
    });

    it('applies winning tint when user wins', () => {
      const { container } = render(<RivalSongRow song={makeSong({ rankDelta: 5 })} onClick={vi.fn()} standalone />);
      const row = container.firstChild as HTMLElement;
      expect(row.className).toContain('Winning');
    });

    it('applies losing tint when rival wins', () => {
      const { container } = render(<RivalSongRow song={makeSong({ rankDelta: -5 })} onClick={vi.fn()} standalone />);
      const row = container.firstChild as HTMLElement;
      expect(row.className).toContain('Losing');
    });

    it('no tint class for tie', () => {
      const { container } = render(<RivalSongRow song={makeSong({ rankDelta: 0 })} onClick={vi.fn()} standalone />);
      const row = container.firstChild as HTMLElement;
      expect(row.className).not.toContain('Winning');
      expect(row.className).not.toContain('Losing');
    });

    it('renders art placeholder when no albumArt', () => {
      const { container } = render(<RivalSongRow song={makeSong()} onClick={vi.fn()} standalone />);
      // Art placeholder has the purple dark background
      const placeholder = container.querySelector('div[style*="background"]');
      expect(placeholder).toBeTruthy();
    });

    it('renders album art image', () => {
      const { container } = render(<RivalSongRow song={makeSong()} albumArt="https://example.com/art.jpg" onClick={vi.fn()} standalone />);
      expect(container.querySelector('img')?.getAttribute('src')).toBe('https://example.com/art.jpg');
    });

    it('calls onClick on click', () => {
      const onClick = vi.fn();
      render(<RivalSongRow song={makeSong()} onClick={onClick} standalone />);
      fireEvent.click(screen.getByRole('button'));
      expect(onClick).toHaveBeenCalledOnce();
    });

    it('calls onClick on Enter key', () => {
      const onClick = vi.fn();
      render(<RivalSongRow song={makeSong()} onClick={onClick} standalone />);
      fireEvent.keyDown(screen.getByRole('button'), { key: 'Enter' });
      expect(onClick).toHaveBeenCalledOnce();
    });

    it('applies scoreDeltaWidth to delta pills', () => {
      const { container } = render(<RivalSongRow song={makeSong()} onClick={vi.fn()} standalone scoreDeltaWidth="6ch" />);
      const allSpans = container.querySelectorAll('span');
      const withWidth = Array.from(allSpans).filter(p => (p as HTMLElement).style.minWidth === '6ch');
      expect(withWidth.length).toBeGreaterThanOrEqual(2);
    });

    it('forwards style and onAnimationEnd', () => {
      const { container } = render(
        <RivalSongRow song={makeSong()} onClick={vi.fn()} standalone style={{ opacity: 0 }} />,
      );
      const row = container.querySelector('[role="button"]') as HTMLElement;
      expect(row.style.opacity).toBe('0');
    });

    it('renders songId when title is null', () => {
      render(<RivalSongRow song={makeSong({ title: undefined, songId: 'my-song-id' })} onClick={vi.fn()} standalone />);
      expect(screen.getByText('my-song-id')).toBeDefined();
    });

    it('handles null userScore gracefully', () => {
      render(<RivalSongRow song={makeSong({ userScore: null as unknown as number })} onClick={vi.fn()} standalone />);
      // Should not crash — empty score entry
      expect(screen.getByText('#10')).toBeDefined();
    });

    it('handles null rivalScore gracefully', () => {
      render(<RivalSongRow song={makeSong({ rivalScore: null as unknown as number })} onClick={vi.fn()} standalone />);
      expect(screen.getByText('#12')).toBeDefined();
    });
  });

  describe('inline mode (no standalone)', () => {
    it('renders inline layout with scores', () => {
      render(<RivalSongRow song={makeSong()} onClick={vi.fn()} />);
      expect(screen.getByText('You')).toBeDefined();
      expect(screen.getByText('Them')).toBeDefined();
      expect(screen.getByText('#10')).toBeDefined();
    });

    it('renders album art in inline mode', () => {
      const { container } = render(<RivalSongRow song={makeSong()} albumArt="https://example.com/art.jpg" onClick={vi.fn()} />);
      expect(container.querySelector('img')).toBeTruthy();
    });

    it('renders art placeholder in inline mode', () => {
      const { container } = render(<RivalSongRow song={makeSong()} onClick={vi.fn()} />);
      // No album art img (the only img is the instrument icon)
      const images = container.querySelectorAll('img');
      const albumArtImg = Array.from(images).find(img => img.alt === '');
      expect(albumArtImg).toBeFalsy();
    });

    it('calls onClick on click in inline mode', () => {
      const onClick = vi.fn();
      render(<RivalSongRow song={makeSong()} onClick={onClick} />);
      fireEvent.click(screen.getByRole('button'));
      expect(onClick).toHaveBeenCalledOnce();
    });

    it('calls onClick on Enter key in inline mode', () => {
      const onClick = vi.fn();
      render(<RivalSongRow song={makeSong()} onClick={onClick} />);
      fireEvent.keyDown(screen.getByRole('button'), { key: 'Enter' });
      expect(onClick).toHaveBeenCalledOnce();
    });

    it('hides score when userScore is null in inline mode', () => {
      render(<RivalSongRow song={makeSong({ userScore: null as unknown as number })} onClick={vi.fn()} />);
      // With null userScore, rivalScore (148,000) should still be visible
      expect(screen.getByText('148,000')).toBeDefined();
    });

    it('hides score when rivalScore is null in inline mode', () => {
      render(<RivalSongRow song={makeSong({ rivalScore: null as unknown as number })} onClick={vi.fn()} />);
      // With null rivalScore, userScore (150,000) should still be visible
      expect(screen.getByText('150,000')).toBeDefined();
    });

    it('renders year in artist line', () => {
      render(<RivalSongRow song={makeSong()} year={2023} onClick={vi.fn()} />);
      expect(screen.getByText(/2023/)).toBeDefined();
    });

    it('renders without year', () => {
      render(<RivalSongRow song={makeSong({ artist: 'Solo Artist' })} onClick={vi.fn()} />);
      expect(screen.getByText('Solo Artist')).toBeDefined();
    });

    it('renders songId as fallback in inline mode', () => {
      render(<RivalSongRow song={makeSong({ title: undefined, songId: 'fallback-id' })} onClick={vi.fn()} />);
      expect(screen.getByText('fallback-id')).toBeDefined();
    });
  });
});
