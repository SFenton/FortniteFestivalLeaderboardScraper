import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import RivalSongRow, { formatRankDelta } from '../../../../src/pages/rivals/components/RivalSongRow';
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
      expect(screen.getByText('\u22123')).toBeDefined();
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

    it('applies fixed minWidth to delta pills', () => {
      const { container } = render(<RivalSongRow song={makeSong()} onClick={vi.fn()} standalone />);
      const allSpans = container.querySelectorAll('span');
      const withWidth = Array.from(allSpans).filter(p => (p as HTMLElement).style.minWidth === '9ch');
      expect(withWidth.length).toBeGreaterThanOrEqual(2);
    });

    it('forwards style and onAnimationEnd', () => {
      const { container } = render(
        <RivalSongRow song={makeSong()} onClick={vi.fn()} standalone style={{ opacity: 0 }} />,
      );
      const row = container.querySelector('[role="button"]') as HTMLElement;
      expect(row.style.opacity).toBe('0');
    });

    it('applies compact standalone layout hooks without inline overrides that would block container queries', () => {
      const { container } = render(<RivalSongRow song={makeSong()} onClick={vi.fn()} standalone />);

      const compareRow = container.querySelector('.rivalSongCompareRow, [class*="rivalSongCompareRow"]') as HTMLElement;
      const deltaCenter = container.querySelector('.rivalSongDeltaCenter, [class*="rivalSongDeltaCenter"]') as HTMLElement;
      const pillGroups = container.querySelectorAll('.rivalSongDeltaPillGroup, [class*="rivalSongDeltaPillGroup"]');
      const rankGroup = container.querySelector('.rivalSongDeltaPillGroupRank, [class*="rivalSongDeltaPillGroupRank"]') as HTMLElement;

      expect(compareRow).toBeTruthy();
      expect(compareRow.style.gridTemplateColumns).toBe('');
      expect(deltaCenter).toBeTruthy();
      expect(rankGroup).toBeTruthy();
      expect(pillGroups.length).toBeGreaterThanOrEqual(2);
      Array.from(pillGroups).forEach(group => {
        expect((group as HTMLElement).style.flexDirection).toBe('');
      });
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

    it('formats inline ranks with commas for large values', () => {
      render(<RivalSongRow song={makeSong({ userRank: 29921, rivalRank: 15000 })} onClick={vi.fn()} />);
      expect(screen.getByText('#29,921')).toBeDefined();
      expect(screen.getByText('#15,000')).toBeDefined();
    });
  });

  describe('formatRankDelta', () => {
    it('formats small positive values with +', () => {
      expect(formatRankDelta(5)).toBe('+5');
    });

    it('formats small negative values with unicode minus', () => {
      expect(formatRankDelta(-3)).toBe('\u22123');
    });

    it('formats zero without sign', () => {
      expect(formatRankDelta(0)).toBe('0');
    });

    it('locale-formats values under 10K', () => {
      expect(formatRankDelta(9999)).toBe('+9,999');
      expect(formatRankDelta(-9999)).toBe('\u22129,999');
    });

    it('abbreviates 10K+ as K', () => {
      expect(formatRankDelta(10000)).toBe('+10K');
      expect(formatRankDelta(50000)).toBe('+50K');
      expect(formatRankDelta(-25000)).toBe('\u221225K');
    });

    it('abbreviates 1M+ as M', () => {
      expect(formatRankDelta(1000000)).toBe('+1M');
      expect(formatRankDelta(1500000)).toBe('+1.5M');
      expect(formatRankDelta(99000000)).toBe('+99M');
      expect(formatRankDelta(-2000000)).toBe('\u22122M');
    });

    it('rounds K values', () => {
      expect(formatRankDelta(12500)).toBe('+13K');
      expect(formatRankDelta(999999)).toBe('+1000K');
    });
  });
});
