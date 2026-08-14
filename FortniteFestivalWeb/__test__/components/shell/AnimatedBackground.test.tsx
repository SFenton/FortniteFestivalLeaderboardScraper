import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, act } from '@testing-library/react';
import { AnimatedBackground } from '../../../src/components/shell/AnimatedBackground';
import type { ServerSong as Song } from '@festival/core/api';

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

/** Find layer divs (have backgroundSize: cover from abStyles.layer). */
function findLayers(el: HTMLElement): HTMLElement[] {
  const wrapper = el.lastElementChild;
  if (!wrapper) return [];
  return Array.from(wrapper.children).filter(
    (c) => (c as HTMLElement).style.backgroundSize === 'cover',
  ) as HTMLElement[];
}

/** Find the dim overlay (has backgroundColor, no backgroundSize). */
function findDim(el: HTMLElement): HTMLElement | null {
  const wrapper = el.lastElementChild;
  if (!wrapper) return null;
  return (Array.from(wrapper.children).find(
    (c) => !!(c as HTMLElement).style.backgroundColor && !(c as HTMLElement).style.backgroundSize,
  ) as HTMLElement) ?? null;
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
  Reflect.deleteProperty(navigator, 'connection');
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
    const layers = findLayers(container);
    expect(layers.length).toBe(1);
  });

  it('uses one full-screen stack for the body and safe areas', () => {
    const songs = [makeSong('s1')];
    const { container } = render(<AnimatedBackground songs={songs} />);
    expect(container.children).toHaveLength(1);
    expect((container.firstElementChild as HTMLElement).style.top).toContain('--sat');
  });

  it('renders two layers when multiple images are available', () => {
    const songs = [makeSong('s1'), makeSong('s2'), makeSong('s3')];
    const { container } = render(<AnimatedBackground songs={songs} />);
    const layers = findLayers(container);
    expect(layers.length).toBe(2);
  });

  it('renders dim overlay with specified opacity', () => {
    const songs = [makeSong('s1')];
    const { container } = render(<AnimatedBackground songs={songs} dimOpacity={0.5} />);
    const dim = findDim(container);
    expect(dim).toBeTruthy();
    expect(dim!.style.opacity).toBe('0.5');
  });

  it('uses default 0.7 dim opacity', () => {
    const songs = [makeSong('s1')];
    const { container } = render(<AnimatedBackground songs={songs} />);
    const dim = findDim(container);
    expect((dim as HTMLElement).style.opacity).toBe('0.7');
  });

  it('sets background image on layer A', () => {
    const songs = [makeSong('s1', 'https://example.com/art.jpg')];
    const { container } = render(<AnimatedBackground songs={songs} />);
    const layer = findLayers(container)[0]!;
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

    const layers = findLayers(container);
    const layerA = layers[0]!;
    const layerB = layers[1]!;

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

  it('keeps the active image visible when reduced motion is enabled mid-cycle', () => {
    const listeners = new Set<() => void>();
    const mediaQuery = {
      matches: false,
      media: '(prefers-reduced-motion: reduce)',
      onchange: null,
      addEventListener: (_event: string, listener: () => void) => {
        listeners.add(listener);
      },
      removeEventListener: (_event: string, listener: () => void) => {
        listeners.delete(listener);
      },
      addListener: vi.fn(),
      removeListener: vi.fn(),
      dispatchEvent: vi.fn(),
    };
    vi.spyOn(window, 'matchMedia').mockReturnValue(mediaQuery as unknown as MediaQueryList);
    const songs = [makeSong('s1'), makeSong('s2')];
    const { container } = render(<AnimatedBackground songs={songs} />);

    act(() => { vi.advanceTimersByTime(5_100); });
    const activeBackground = findLayers(container)[1]!.style.backgroundImage;
    expect(findLayers(container)[1]!.style.opacity).toBe('1');

    act(() => {
      mediaQuery.matches = true;
      listeners.forEach(listener => listener());
    });

    const layers = findLayers(container);
    expect(layers).toHaveLength(1);
    expect(layers[0]!.style.opacity).toBe('1');
    expect(layers[0]!.style.backgroundImage).toBe(activeBackground);
  });

  it('pauses animations when document becomes hidden', () => {
    const pauseMock = vi.fn();
    HTMLElement.prototype.getAnimations = vi.fn().mockReturnValue([{ pause: pauseMock, play: vi.fn(), cancel: vi.fn() }]);

    const songs = [makeSong('s1'), makeSong('s2')];
    render(<AnimatedBackground songs={songs} />);

    // Simulate visibility change to hidden
    Object.defineProperty(document, 'hidden', { value: true, configurable: true });
    act(() => {
      document.dispatchEvent(new Event('visibilitychange'));
    });
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
    act(() => {
      document.dispatchEvent(new Event('visibilitychange'));
    });
    Object.defineProperty(document, 'hidden', { value: false, configurable: true });
    act(() => {
      document.dispatchEvent(new Event('visibilitychange'));
    });
    expect(playMock).toHaveBeenCalled();
  });

  it('omits remote album art when Save-Data is enabled', () => {
    const connection = new EventTarget() as EventTarget & { saveData: boolean };
    connection.saveData = true;
    Object.defineProperty(navigator, 'connection', {
      configurable: true,
      value: connection,
    });

    const { container } = render(
      <AnimatedBackground songs={[makeSong('s1'), makeSong('s2')]} />,
    );
    expect(container.innerHTML).toBe('');
  });

  it('fades in the container after images are available', () => {
    const songs = [makeSong('s1')];
    const { container } = render(<AnimatedBackground songs={songs} />);
    const wrapper = container.lastElementChild as HTMLElement;
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
    const layers = findLayers(container);
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
    const layers = findLayers(container);
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
    const layers = findLayers(container);
    expect(layers.length).toBe(2);
  });
});
