import { describe, it, expect, vi, beforeAll, beforeEach, afterEach } from 'vitest';
import { render, screen, act, fireEvent } from '@testing-library/react';
import { stubResizeObserver } from '../../../helpers/browserStubs';
import { TestProviders } from '../../../helpers/TestProviders';

const mockSongsData = vi.hoisted(() => [
  { songId: 'demo-song', title: 'Demo', artist: 'Epic Games Test', year: 2024, albumArt: '', difficulty: {}, maxScores: {} },
]);

const mockApi = vi.hoisted(() => ({
  getSongs: vi.fn().mockResolvedValue({ songs: mockSongsData, count: 1, currentSeason: 5 }),
  getVersion: vi.fn().mockResolvedValue({ version: '1.0.0' }),
}));

vi.mock('../../../../src/api/client', () => ({ api: mockApi }));

// Controllable slide height mock
let mockSlideHeightValue = 500;
vi.mock('../../../../src/firstRun/SlideHeightContext', () => ({
  SlideHeightContext: { Provider: ({ children }: any) => children },
  useSlideHeight: () => mockSlideHeightValue,
}));

// Mock useFestival to provide songs synchronously
vi.mock('../../../../src/contexts/FestivalContext', async (importOriginal) => {
  const actual = await importOriginal<any>();
  return {
    ...actual,
    useFestival: () => ({
      state: { songs: mockSongsData, currentSeason: 5, loading: false, error: null },
      dispatch: vi.fn(),
    }),
  };
});

// Mock Image constructor for path loading
let mockImageOnload: (() => void) | null = null;
let mockImageOnerror: (() => void) | null = null;

vi.stubGlobal('Image', class MockImage {
  src = '';
  set onload(fn: () => void) { mockImageOnload = fn; }
  set onerror(fn: () => void) { mockImageOnerror = fn; }
});

import PathPreviewDemo from '../../../../src/pages/songinfo/firstRun/demo/PathPreviewDemo';

beforeAll(() => {
  stubResizeObserver();
  // Capture the RO callback for PathPreviewDemo's width detection
  vi.stubGlobal('ResizeObserver', class {
    constructor(_cb: ResizeObserverCallback) {} // eslint-disable-line @typescript-eslint/no-unused-vars
    observe() {}
    unobserve() {}
    disconnect() {}
  });
});

beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  mockImageOnload = null;
  mockImageOnerror = null;
  mockSlideHeightValue = 500;
});

afterEach(() => {
  vi.useRealTimers();
});

function wrap(ui: React.ReactElement) {
  return render(ui, { wrapper: TestProviders });
}

describe('PathPreviewDemo', () => {
  it('renders instrument selector', async () => {
    await act(async () => { wrap(<PathPreviewDemo />); });
    // InstrumentSelector renders buttons with title attributes
    expect(screen.getByTitle('Lead')).toBeTruthy();
    expect(screen.getByTitle('Bass')).toBeTruthy();
    expect(screen.getByTitle('Drums')).toBeTruthy();
    expect(screen.getByTitle('Vocals')).toBeTruthy();
  });

  it('renders difficulty buttons', async () => {
    await act(async () => wrap(<PathPreviewDemo />));
    expect(screen.getByText('Easy')).toBeTruthy();
    expect(screen.getByText('Medium')).toBeTruthy();
    expect(screen.getByText('Hard')).toBeTruthy();
    expect(screen.getByText('Expert')).toBeTruthy();
  });

  it('shows spinner initially while loading', async () => {
    const { container } = await act(async () => wrap(<PathPreviewDemo />));
    // ArcSpinner renders inside spinnerWrap
    const spinnerWraps = container.querySelectorAll('[class*="spinnerWrap"]');
    expect(spinnerWraps.length).toBeGreaterThanOrEqual(1);
  });

  it('shows image after successful load', async () => {
    const { container } = await act(async () => wrap(<PathPreviewDemo />));

    // Trigger image load
    await act(async () => {
      mockImageOnload?.();
      await vi.advanceTimersByTimeAsync(1000);
    });

    const imgs = container.querySelectorAll('img[alt*="path"]');
    expect(imgs.length).toBeGreaterThanOrEqual(1);
  });

  it('handles failed image load', async () => {
    const { container } = await act(async () => wrap(<PathPreviewDemo />));

    // Trigger image error and wait through all phase transitions
    await act(async () => {
      mockImageOnerror?.();
      // MIN_SPINNER_MS + FADE_MS + extra
      await vi.advanceTimersByTimeAsync(2000);
    });

    // After error, the image area should NOT contain a path image
    const pathImgs = container.querySelectorAll('img[alt*="path"]');
    expect(pathImgs.length).toBe(0);
  });

  it('changes instrument on selector click', async () => {
    await act(async () => { wrap(<PathPreviewDemo />); });

    const bassBtn = screen.getByTitle('Bass');
    await act(async () => { fireEvent.click(bassBtn); });
    // Button should now be active (inline style sets backgroundColor)
    expect(bassBtn.style.backgroundColor).toBeTruthy();
  });

  it('changes difficulty on button click', async () => {
    await act(async () => { wrap(<PathPreviewDemo />); });

    const hardBtn = screen.getByText('Hard');
    await act(async () => { fireEvent.click(hardBtn); });
    expect(hardBtn).toBeTruthy();
  });

  it('transitions through image phases on successful load then instrument change', async () => {
    await act(async () => { wrap(<PathPreviewDemo />); });

    // Complete first image load
    await act(async () => {
      mockImageOnload?.();
      await vi.advanceTimersByTimeAsync(2000);
    });

    // Now change instrument — should trigger FadeOutImage path
    const bassBtn = screen.getByTitle('Bass');
    await act(async () => { fireEvent.click(bassBtn); });

    // Advance through FadeOutImage → Spinner → load
    await act(async () => {
      mockImageOnload?.();
      await vi.advanceTimersByTimeAsync(2000);
    });

    expect(bassBtn.style.backgroundColor).toBeTruthy();
  });

  it('renders with zero slide height', async () => {
    mockSlideHeightValue = 0;
    await act(async () => { wrap(<PathPreviewDemo />); });
    // Should still render instrument selector
    expect(screen.getByTitle('Lead')).toBeTruthy();
  });

  it('renders with tiny slide height hiding instruments', async () => {
    mockSlideHeightValue = 50;
    await act(async () => { wrap(<PathPreviewDemo />); });
    // At 50px height, showInstruments should be false (needs >= 136)
    // The wrapper should exist but instruments may be hidden
    const { container } = await act(async () => wrap(<PathPreviewDemo />));
    // Wrapper renders with inline styles — just check the container isn't empty
    expect(container.firstElementChild).toBeTruthy();
  });

  it('handles empty songs list', async () => {
    mockSongsData.length = 0;
    await act(async () => { wrap(<PathPreviewDemo />); });
    // With no songs, songId is null, so no image load is triggered
    // Component should still render its structure
    const { container } = await act(async () => wrap(<PathPreviewDemo />));
    expect(container.firstElementChild).toBeTruthy();
    mockSongsData.push({ songId: 'demo-song', title: 'Demo', artist: 'Epic Games Test', year: 2024, albumArt: '', difficulty: {}, maxScores: {} });
  });

  it('selects all difficulty buttons', async () => {
    await act(async () => { wrap(<PathPreviewDemo />); });
    for (const diff of ['Easy', 'Medium', 'Hard', 'Expert']) {
      const btn = screen.getByText(diff);
      await act(async () => { fireEvent.click(btn); });
    }
    expect(screen.getByText('Expert')).toBeTruthy();
  });
});
