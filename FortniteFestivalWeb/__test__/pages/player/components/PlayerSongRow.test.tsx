import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';

let mockIsNarrow = false;

vi.mock('../../../../src/hooks/ui/useIsMobile', () => ({
  useIsNarrow: () => mockIsNarrow,
}));

vi.mock('../../../../src/components/songs/metadata/SongInfo', () => ({
  default: ({ title, minWidth }: { title: string; minWidth?: number }) => (
    <div data-testid="song-info" data-min-width={minWidth == null ? '' : String(minWidth)}>{title}</div>
  ),
}));

vi.mock('../../../../src/components/songs/metadata/PercentilePill', () => ({
  default: ({ display }: { display?: string | null }) => <div data-testid="percentile-pill">{display}</div>,
}));

import PlayerSongRow from '../../../../src/pages/player/components/PlayerSongRow';

describe('PlayerSongRow', () => {
  beforeEach(() => {
    mockIsNarrow = false;
  });

  it('passes minWidth=0 to SongInfo in narrow two-row mode', () => {
    mockIsNarrow = true;
    render(
      <PlayerSongRow
        songId="song-1"
        href="#"
        albumArt="art.jpg"
        title="Beyond the Flame"
        artist="Epic Games"
        year={2024}
        percentile={1.2}
        onClick={vi.fn()}
      />,
    );

    expect(screen.getByTestId('song-info').getAttribute('data-min-width')).toBe('0');
    expect(screen.getByTestId('percentile-pill').textContent).toBe('Top 2%');
  });

  it('does not force SongInfo to shrink in the one-row layout', () => {
    render(
      <PlayerSongRow
        songId="song-1"
        href="#"
        albumArt="art.jpg"
        title="Beyond the Flame"
        artist="Epic Games"
        year={2024}
        percentile={1.2}
        onClick={vi.fn()}
      />,
    );

    expect(screen.getByTestId('song-info').getAttribute('data-min-width')).toBe('');
    expect(screen.getByTestId('percentile-pill').textContent).toBe('Top 2%');
  });
});