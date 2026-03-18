import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, act } from '@testing-library/react';
import { AnimatedBackground } from '../../components/shell/AnimatedBackground';
import type { ServerSong as Song } from '@festival/core/api/serverTypes';

// Minimal song factory
function makeSong(id: string, albumArt?: string): Song {
  return {
    songId: id,
    title: `Song ${id}`,
    artist: `Artist ${id}`,
    year: 2024,
    albumArt: albumArt ?? `https://example.com/${id}.jpg`,
  } as Song;
}

beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  // Stub Web Animations API
  HTMLElement.prototype.animate = vi.fn().mockReturnValue({ cancel: vi.fn(), pause: vi.fn(), play: vi.fn() });
  HTMLElement.prototype.getAnimations = vi.fn().mockReturnValue([]);
  // Stub requestAnimationFrame to fire synchronously
  vi.spyOn(window, 'requestAnimationFrame').mockImplementation((cb) => { cb(0); return 0; });
});

afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
});

describe('AnimatedBackground', () => {
  it('renders nothing when songs have no album art', () => {
    const songs = [{ songId: 's1', title: 'S1', artist: 'A1', year: 2024 } as Song];
    const { container } = render(<AnimatedBackground songs={songs} />);
    expect(container.innerHTML).toBe('');
  });

  it('renders nothing when songs array is empty', () => {
    const { container } = render(<AnimatedBackground songs={[]} />);
    expect(container.innerHTML).toBe('');
  });

  it('renders a single layer when only one image is available', () => {
    const songs = [makeSong('s1')];
    const { container } = render(<AnimatedBackground songs={songs} />);
    const layers = container.querySelectorAll('[class*="layer"]');
    expect(layers.length).toBe(1);
  });

  it('renders two layers when multiple images are available', () => {
    const songs = [makeSong('s1'), makeSong('s2'), makeSong('s3')];
    const { container } = render(<AnimatedBackground songs={songs} />);
    const layers = container.querySelectorAll('[class*="layer"]');
    expect(layers.length).toBe(2);
  });

  it('renders dim overlay with specified opacity', () => {
    const songs = [makeSong('s1')];
    const { container } = render(<AnimatedBackground songs={songs} dimOpacity={0.5} />);
    const dim = container.querySelector('[class*="dim"]');
    expect(dim).toBeTruthy();
    expect((dim as HTMLElement).style.opacity).toBe('0.5');
  });

  it('uses default 0.7 dim opacity', () => {
    const songs = [makeSong('s1')];
    const { container } = render(<AnimatedBackground songs={songs} />);
    const dim = container.querySelector('[class*="dim"]');
    expect((dim as HTMLElement).style.opacity).toBe('0.7');
  });

  it('sets background image on layer A', () => {
    const songs = [makeSong('s1', 'https://example.com/art.jpg')];
    const { container } = render(<AnimatedBackground songs={songs} />);
    const layer = container.querySelector('[class*="layer"]') as HTMLElement;
    expect(layer.style.backgroundImage).toContain('https://example.com/art.jpg');
  });

  it('starts motion animation on mount', () => {
    const songs = [makeSong('s1'), makeSong('s2')];
    render(<AnimatedBackground songs={songs} />);
    expect(HTMLElement.prototype.animate).toHaveBeenCalled();
  });

  it('transitions between layers on timer', () => {
    const songs = [makeSong('s1'), makeSong('s2'), makeSong('s3')];
    const { container } = render(<AnimatedBackground songs={songs} />);

    const layers = container.querySelectorAll('[class*="layer"]');
    const layerA = layers[0] as HTMLElement;
    const layerB = layers[1] as HTMLElement;

    // Initially A is visible, B is hidden
    expect(layerA.style.opacity).toBe('1');
    expect(layerB.style.opacity).toBe('0');

    // Advance past DISPLAY_DURATION (5000ms)
    act(() => { vi.advanceTimersByTime(5100); });

    // After transition, one should be 0 and the other 1
    const opA = parseFloat(layerA.style.opacity);
    const opB = parseFloat(layerB.style.opacity);
    expect(opA + opB).toBe(1);
  });

  it('pauses animations when document becomes hidden', () => {
    const pauseMock = vi.fn();
    HTMLElement.prototype.getAnimations = vi.fn().mockReturnValue([{ pause: pauseMock, play: vi.fn(), cancel: vi.fn() }]);

    const songs = [makeSong('s1'), makeSong('s2')];
    render(<AnimatedBackground songs={songs} />);

    // Simulate visibility change to hidden
    Object.defineProperty(document, 'hidden', { value: true, configurable: true });
    document.dispatchEvent(new Event('visibilitychange'));
    expect(pauseMock).toHaveBeenCalled();

    // Restore
    Object.defineProperty(document, 'hidden', { value: false, configurable: true });
  });

  it('resumes animations when document becomes visible again', () => {
    const playMock = vi.fn();
    const pauseMock = vi.fn();
    HTMLElement.prototype.getAnimations = vi.fn().mockReturnValue([{ pause: pauseMock, play: playMock, cancel: vi.fn() }]);

    const songs = [makeSong('s1'), makeSong('s2')];
    render(<AnimatedBackground songs={songs} />);

    // Hide then show
    Object.defineProperty(document, 'hidden', { value: true, configurable: true });
    document.dispatchEvent(new Event('visibilitychange'));
    Object.defineProperty(document, 'hidden', { value: false, configurable: true });
    document.dispatchEvent(new Event('visibilitychange'));
    expect(playMock).toHaveBeenCalled();
  });

  it('fades in the container after images are available', () => {
    const songs = [makeSong('s1')];
    const { container } = render(<AnimatedBackground songs={songs} />);
    const wrapper = container.firstChild as HTMLElement;
    // After rAF fires (which we stubbed), opacity should be 1
    expect(wrapper.style.opacity).toBe('1');
  });

  it('handles songs with mixed album art presence', () => {
    const songs = [
      { songId: 's1', title: 'S1', artist: 'A1', year: 2024, albumArt: 'https://example.com/a.jpg' } as Song,
      { songId: 's2', title: 'S2', artist: 'A2', year: 2024 } as Song,
      { songId: 's3', title: 'S3', artist: 'A3', year: 2024, albumArt: 'https://example.com/c.jpg' } as Song,
    ];
    const { container } = render(<AnimatedBackground songs={songs} />);
    // Should render — only images with albumArt are used
    const layers = container.querySelectorAll('[class*="layer"]');
    expect(layers.length).toBe(2);
  });

  it('cycles images after multiple transitions', () => {
    const songs = Array.from({ length: 5 }, (_, i) => makeSong(`s${i}`));
    const { container } = render(<AnimatedBackground songs={songs} />);

    // Advance through several cycles
    act(() => { vi.advanceTimersByTime(5100); });
    act(() => { vi.advanceTimersByTime(1100); }); // Wait for FADE_DURATION preload
    act(() => { vi.advanceTimersByTime(5100); });
    act(() => { vi.advanceTimersByTime(1100); });

    // Should still be rendering
    const layers = container.querySelectorAll('[class*="layer"]');
    expect(layers.length).toBe(2);
  });

  it('cleans up intervals on unmount', () => {
    const songs = [makeSong('s1'), makeSong('s2')];
    const { unmount } = render(<AnimatedBackground songs={songs} />);
    // Should not throw
    unmount();
  });

  it('resets layers when image URIs change', () => {
    const songs1 = [makeSong('s1'), makeSong('s2')];
    const songs2 = [makeSong('s3'), makeSong('s4'), makeSong('s5')];
    const { rerender, container } = render(<AnimatedBackground songs={songs1} />);
    rerender(<AnimatedBackground songs={songs2} />);
    const layers = container.querySelectorAll('[class*="layer"]');
    expect(layers.length).toBe(2);
  });
});
