import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';

vi.mock('../../hooks/ui/useIsMobile', () => ({ useIsMobile: () => false, useIsMobileChrome: () => false }));
vi.mock('../../hooks/ui/useIsMobile', () => ({ useIsMobile: () => false, useIsMobileChrome: () => false }));

import SongHeader from '../../pages/songinfo/components/SongHeader';

const MOCK_SONG = {
  songId: 'song-1',
  title: 'Test Song',
  artist: 'Test Artist',
  year: 2024,
  albumArt: 'https://example.com/art.jpg',
  difficulty: {},
};

describe('SongHeader', () => {
  it('renders song title and artist', () => {
    render(
      <MemoryRouter>
        <SongHeader song={MOCK_SONG as any} songId="song-1" collapsed={false} onOpenPaths={vi.fn()} />
      </MemoryRouter>,
    );
    expect(screen.getByText('Test Song')).toBeTruthy();
    expect(screen.getByText(/Test Artist/)).toBeTruthy();
  });

  it('renders songId when song is undefined', () => {
    render(
      <MemoryRouter>
        <SongHeader song={undefined} songId="song-1" collapsed={false} onOpenPaths={vi.fn()} />
      </MemoryRouter>,
    );
    expect(screen.getByText('song-1')).toBeTruthy();
  });

  it('renders album art image', () => {
    const { container } = render(
      <MemoryRouter>
        <SongHeader song={MOCK_SONG as any} songId="song-1" collapsed={false} onOpenPaths={vi.fn()} />
      </MemoryRouter>,
    );
    const img = container.querySelector('img');
    expect(img).toBeTruthy();
    expect(img?.src).toContain('art.jpg');
  });

  it('renders placeholder when no album art', () => {
    const song = { ...MOCK_SONG, albumArt: undefined };
    const { container } = render(
      <MemoryRouter>
        <SongHeader song={song as any} songId="song-1" collapsed={false} onOpenPaths={vi.fn()} />
      </MemoryRouter>,
    );
    expect(container.querySelector('img')).toBeNull();
  });

  it('renders collapsed state', () => {
    render(
      <MemoryRouter>
        <SongHeader song={MOCK_SONG as any} songId="song-1" collapsed={true} onOpenPaths={vi.fn()} />
      </MemoryRouter>,
    );
    expect(screen.getByText('Test Song')).toBeTruthy();
  });

  it('shows View Paths button on desktop', () => {
    render(
      <MemoryRouter>
        <SongHeader song={MOCK_SONG as any} songId="song-1" collapsed={false} onOpenPaths={vi.fn()} />
      </MemoryRouter>,
    );
    // The button text comes from i18n — look for button role
    const buttons = screen.getAllByRole('button');
    expect(buttons.length).toBeGreaterThanOrEqual(1);
  });
});

describe('SongHeader — noTransition branches', () => {
  it('renders with noTransition=true and collapsed=true', () => {
    const { container } = render(
      <MemoryRouter>
        <SongHeader song={MOCK_SONG as any} songId="song-1" collapsed noTransition onOpenPaths={vi.fn()} />
      </MemoryRouter>,
    );
    expect(container.firstElementChild).toBeTruthy();
  });

  it('renders with noTransition=false and collapsed=false', () => {
    render(
      <MemoryRouter>
        <SongHeader song={MOCK_SONG as any} songId="song-1" collapsed={false} noTransition={false} onOpenPaths={vi.fn()} />
      </MemoryRouter>,
    );
    expect(screen.getByText('Test Song')).toBeTruthy();
  });
});
