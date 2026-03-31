import { describe, it, expect, vi } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { useNavigateToSongDetail } from '../../../src/hooks/navigation/useNavigateToSongDetail';

const mockNavigate = vi.fn();

vi.mock('react-router-dom', async () => {
  const actual = await vi.importActual<typeof import('react-router-dom')>('react-router-dom');
  return { ...actual, useNavigate: () => mockNavigate };
});

function wrapper(initialEntries: Array<string | { pathname: string; state?: unknown }>) {
  return ({ children }: { children: React.ReactNode }) => (
    <MemoryRouter initialEntries={initialEntries}>{children}</MemoryRouter>
  );
}

describe('useNavigateToSongDetail', () => {
  beforeEach(() => {
    mockNavigate.mockClear();
  });

  it('navigates back when backTo matches song detail route', () => {
    const { result } = renderHook(
      () => useNavigateToSongDetail('song-1'),
      { wrapper: wrapper([{ pathname: '/songs/song-1/Solo_Guitar', state: { backTo: '/songs/song-1' } }]) },
    );
    act(() => result.current());
    expect(mockNavigate).toHaveBeenCalledWith(-1);
  });

  it('pushes forward when backTo does not match song detail route', () => {
    const { result } = renderHook(
      () => useNavigateToSongDetail('song-1'),
      { wrapper: wrapper([{ pathname: '/songs/song-1/Solo_Guitar', state: { backTo: '/songs' } }]) },
    );
    act(() => result.current());
    expect(mockNavigate).toHaveBeenCalledWith('/songs/song-1');
  });

  it('pushes forward when no backTo state exists', () => {
    const { result } = renderHook(
      () => useNavigateToSongDetail('song-1'),
      { wrapper: wrapper(['/songs/song-1/Solo_Guitar']) },
    );
    act(() => result.current());
    expect(mockNavigate).toHaveBeenCalledWith('/songs/song-1');
  });

  it('does nothing when songId is undefined', () => {
    const { result } = renderHook(
      () => useNavigateToSongDetail(undefined),
      { wrapper: wrapper(['/songs']) },
    );
    act(() => result.current());
    expect(mockNavigate).not.toHaveBeenCalled();
  });
});
