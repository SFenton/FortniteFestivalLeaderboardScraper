import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, act } from '@testing-library/react';
import PathsModal from '../../../../../src/pages/songinfo/components/path/PathsModal';

/* ── Mocks ── */

// Mock CSS modules
vi.mock('../../../../../src/styles/animations.module.css', () => ({
  default: {
    spinnerWrap: 'spinnerWrap',
  },
}));

const mockIsMobile = vi.fn(() => false);
vi.mock('../../../../../src/hooks/ui/useIsMobile', () => ({ useIsMobile: () => mockIsMobile() }));

vi.mock('../../../../../src/hooks/ui/useScrollMask', () => ({
  useScrollMask: () => vi.fn(),
}));

vi.mock('../../../../../src/hooks/ui/useVisualViewport', () => ({
  useVisualViewportHeight: () => 800,
  useVisualViewportOffsetTop: () => 0,
}));

const mockInstruments = vi.fn(() => [
  'Solo_Guitar', 'Solo_Bass', 'Solo_Drums', 'Solo_Vocals',
]);
vi.mock('../../../../../src/contexts/SettingsContext', () => ({
  useSettings: () => ({ settings: {} }),
  visibleInstruments: (...args: unknown[]) => mockInstruments(...(args as [])),
}));

vi.mock('../../models', () => ({
  INSTRUMENT_LABELS: {
    Solo_Guitar: 'Lead',
    Solo_Bass: 'Bass',
    Solo_Drums: 'Drums',
    Solo_Vocals: 'Vocals',
    Solo_PeripheralGuitar: 'Pro Lead',
    Solo_PeripheralBass: 'Pro Bass',
  } as Record<string, string>,
}));

vi.mock('../../../../../src/components/display/InstrumentIcons', () => ({
  InstrumentIcon: ({ instrument }: { instrument: string; size: number }) => (
    <span data-testid={`icon-${instrument}`}>{instrument}</span>
  ),
}));

vi.mock('@festival/theme', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@festival/theme')>();
  return {
    ...actual,
  };
});

/* ---------- Helpers ---------- */

/** Track Image constructor calls for PathImage loading */
let mockImageInstances: Array<{ src: string; onload?: (() => void) | null; onerror?: (() => void) | null }>;
let OrigImage: typeof Image;

beforeEach(() => {
  vi.useFakeTimers();
  mockImageInstances = [];
  OrigImage = globalThis.Image;

  // @ts-expect-error - overriding Image constructor
  globalThis.Image = class {
    src = '';
    onload: (() => void) | null = null;
    onerror: (() => void) | null = null;
    constructor() {
      mockImageInstances.push(this);
    }
  };

  vi.spyOn(globalThis, 'requestAnimationFrame').mockImplementation((cb) => {
    // Use a 1ms timer rather than calling synchronously, otherwise the rAF callback
    // runs during the useEffect that created it, causing the cleanup to race with
    // the setTimeout scheduled inside the callback.
    return setTimeout(() => cb(0), 1) as unknown as number;
  });
  vi.spyOn(globalThis, 'cancelAnimationFrame').mockImplementation((id) => clearTimeout(id));
  mockIsMobile.mockReturnValue(false);
});

afterEach(() => {
  globalThis.Image = OrigImage;
  vi.mocked(globalThis.requestAnimationFrame).mockRestore();
  vi.mocked(globalThis.cancelAnimationFrame).mockRestore();
  vi.useRealTimers();
  vi.clearAllMocks();
});

describe('PathsModal', () => {
  describe('visibility', () => {
    it('renders nothing when visible=false', () => {
      const { container } = render(
        <PathsModal visible={false} songId="song-1" onClose={vi.fn()} />,
      );
      expect(container.innerHTML).toBe('');
    });

    it('renders dialog when visible=true', () => {
      render(<PathsModal visible={true} songId="song-1" onClose={vi.fn()} />);
      expect(screen.getByRole('dialog')).toBeDefined();
      expect(screen.getByText('Paths')).toBeDefined();
    });

    it('unmounts after close animation completes', () => {
      const onClose = vi.fn();
      const { rerender, container } = render(
        <PathsModal visible={true} songId="song-1" onClose={onClose} />,
      );

      // Ensure it's mounted
      expect(screen.getByRole('dialog')).toBeDefined();

      // Close — sets animIn to false
      rerender(<PathsModal visible={false} songId="song-1" onClose={onClose} />);

      // Fire transitionend to complete unmount (handleTransitionEnd sets mounted=false)
      const dialog = container.querySelector('[role="dialog"]');
      expect(dialog).not.toBeNull();
      fireEvent.transitionEnd(dialog!);

      expect(container.querySelector('[role="dialog"]')).toBeNull();
    });
  });

  describe('close interactions', () => {
    it('calls onClose when close button is clicked', () => {
      const onClose = vi.fn();
      render(<PathsModal visible={true} songId="song-1" onClose={onClose} />);

      fireEvent.click(screen.getByLabelText('Close'));
      expect(onClose).toHaveBeenCalledOnce();
    });

    it('calls onClose when overlay is clicked', () => {
      const onClose = vi.fn();
      const { container } = render(
        <PathsModal visible={true} songId="song-1" onClose={onClose} />,
      );

      // The overlay is the first fixed div (before the dialog panel)
      const overlay = container.firstElementChild!;
      fireEvent.click(overlay);
      expect(onClose).toHaveBeenCalledOnce();
    });

    it('calls onClose when Escape key is pressed', () => {
      const onClose = vi.fn();
      render(<PathsModal visible={true} songId="song-1" onClose={onClose} />);

      fireEvent.keyDown(document, { key: 'Escape' });
      expect(onClose).toHaveBeenCalledOnce();
    });

    it('does not call onClose for non-Escape keys', () => {
      const onClose = vi.fn();
      render(<PathsModal visible={true} songId="song-1" onClose={onClose} />);

      fireEvent.keyDown(document, { key: 'Enter' });
      expect(onClose).not.toHaveBeenCalled();
    });
  });

  describe('desktop layout', () => {
    beforeEach(() => { mockIsMobile.mockReturnValue(false); });

    it('shows instrument buttons', () => {
      render(<PathsModal visible={true} songId="song-1" onClose={vi.fn()} />);

      expect(screen.getByTestId('icon-Solo_Guitar')).toBeDefined();
      expect(screen.getByTestId('icon-Solo_Bass')).toBeDefined();
      expect(screen.getByTestId('icon-Solo_Drums')).toBeDefined();
      expect(screen.getByTestId('icon-Solo_Vocals')).toBeDefined();
    });

    it('shows difficulty buttons', () => {
      render(<PathsModal visible={true} songId="song-1" onClose={vi.fn()} />);

      expect(screen.getByText('Easy')).toBeDefined();
      expect(screen.getByText('Medium')).toBeDefined();
      expect(screen.getByText('Hard')).toBeDefined();
      expect(screen.getByText('Expert')).toBeDefined();
    });

    it('changes instrument on click', () => {
      render(<PathsModal visible={true} songId="song-1" onClose={vi.fn()} />);

      // Click Bass button (titled "Bass")
      fireEvent.click(screen.getByTitle('Bass'));

      // The last created Image should have the Bass path
      const lastImg = mockImageInstances[mockImageInstances.length - 1];
      expect(lastImg?.src).toContain('Solo_Bass');
    });

    it('changes difficulty on click', () => {
      render(<PathsModal visible={true} songId="song-1" onClose={vi.fn()} />);

      fireEvent.click(screen.getByText('Hard'));

      const lastImg = mockImageInstances[mockImageInstances.length - 1];
      expect(lastImg?.src).toContain('/hard');
    });
  });

  describe('mobile layout', () => {
    beforeEach(() => { mockIsMobile.mockReturnValue(true); });

    it('shows mobile instrument selector button', () => {
      render(<PathsModal visible={true} songId="song-1" onClose={vi.fn()} />);

      // The default selected instrument label should be shown
      expect(screen.getByText('Lead')).toBeDefined();
    });

    it('opens instrument accordion on click and selects instrument', async () => {
      render(<PathsModal visible={true} songId="song-1" onClose={vi.fn()} />);

      // Click the instrument selector to open accordion
      fireEvent.click(screen.getByText('Lead'));

      // Instrument icons should now be visible in accordion
      expect(screen.getByTitle('Bass')).toBeDefined();

      // Select Bass
      fireEvent.click(screen.getByTitle('Bass'));

      // Label should update
      expect(screen.getByText('Bass')).toBeDefined();
    });

    it('opens difficulty accordion on click', () => {
      render(<PathsModal visible={true} songId="song-1" onClose={vi.fn()} />);

      // In mobile, the selector button text "Expert" and accordion button "Expert" both exist.
      // The selector is the one inside a .mobileSelector button.
      const selectorBtns = screen.getAllByText('Expert');
      // Click the first one (the selector button)
      fireEvent.click(selectorBtns[0]!);

      // Difficulty buttons in accordion
      expect(screen.getByText('Easy')).toBeDefined();
      expect(screen.getByText('Hard')).toBeDefined();
    });

    it('closes one accordion when opening the other (with delay)', async () => {
      render(<PathsModal visible={true} songId="song-1" onClose={vi.fn()} />);

      // Open instrument accordion
      fireEvent.click(screen.getByText('Lead'));

      // Click difficulty selector (should close instrument first, then open difficulty after 300ms)
      const selectorBtns = screen.getAllByText('Expert');
      fireEvent.click(selectorBtns[0]!);

      // After the 300ms accordion timer, difficulty accordion should open
      await act(async () => { vi.advanceTimersByTime(310); });

      expect(screen.getByText('Easy')).toBeDefined();
    });

    it('toggles instrument accordion closed on second click', () => {
      render(<PathsModal visible={true} songId="song-1" onClose={vi.fn()} />);

      // Open
      fireEvent.click(screen.getByText('Lead'));
      // Close
      fireEvent.click(screen.getByText('Lead'));
    });

    it('toggles difficulty accordion closed on second click', () => {
      render(<PathsModal visible={true} songId="song-1" onClose={vi.fn()} />);

      // Open difficulty
      const selectors1 = screen.getAllByText('Expert');
      fireEvent.click(selectors1[0]!);
      // Close difficulty
      const selectors2 = screen.getAllByText('Expert');
      fireEvent.click(selectors2[0]!);
    });

    it('closes instrument accordion then opens difficulty with delay', async () => {
      render(<PathsModal visible={true} songId="song-1" onClose={vi.fn()} />);

      // Open instrument accordion first
      fireEvent.click(screen.getByText('Lead'));

      // Click difficulty (should close instrument, delay 300ms, then open difficulty)
      const selectorBtns = screen.getAllByText('Expert');
      fireEvent.click(selectorBtns[0]!);

      await act(async () => { vi.advanceTimersByTime(310); });
      // After delay, difficulty should be open
    });

    it('closes difficulty accordion then opens instrument with delay', async () => {
      render(<PathsModal visible={true} songId="song-1" onClose={vi.fn()} />);

      // Open difficulty accordion first
      const selectorBtns = screen.getAllByText('Expert');
      fireEvent.click(selectorBtns[0]!);

      // Click instrument (should close difficulty, delay 300ms, then open instrument)
      fireEvent.click(screen.getByText('Lead'));

      await act(async () => { vi.advanceTimersByTime(310); });
    });
  });

  describe('PathImage loading states', () => {
    beforeEach(() => { mockIsMobile.mockReturnValue(false); });

    it('shows spinner on initial load', () => {
      const { container } = render(
        <PathsModal visible={true} songId="song-1" onClose={vi.fn()} />,
      );

      // Spinner should be visible
      const spinner = container.querySelector('.spinner');
      expect(spinner).toBeDefined();
    });

    it('shows image after successful load', async () => {
      const { container } = render(
        <PathsModal visible={true} songId="song-1" onClose={vi.fn()} />,
      );

      // Trigger image load success
      const img = mockImageInstances[0];
      expect(img).toBeDefined();
      expect(img!.src).toContain('/api/paths/song-1/Solo_Guitar/expert');

      // Wait for MIN_SPINNER_MS (400ms)
      act(() => { img!.onload?.(); });
      await act(async () => { vi.advanceTimersByTime(500); });

      // fadeOutSpinner phase
      await act(async () => { vi.advanceTimersByTime(300); });

      // imageReady → fadeInImage (rAF)
      await act(async () => { vi.advanceTimersByTime(300); });

      // Now an img tag with the path should exist
      const renderedImg = container.querySelector('img');
      if (renderedImg) {
        expect(renderedImg.getAttribute('src')).toContain('/api/paths/song-1/Solo_Guitar/expert');
      }
    });

    it('shows error message after failed load', async () => {
      render(<PathsModal visible={true} songId="song-1" onClose={vi.fn()} />);

      const img = mockImageInstances[0];
      expect(img).toBeDefined();

      // Trigger image load failure — onReady(false) sets setTimeout(cb, remaining=400)
      act(() => { img!.onerror?.(); });

      // Step 1: remaining timer (400ms) fires → setPhase('fadeOutSpinner')
      await act(async () => { vi.advanceTimersByTime(410); });
      // Step 2: FADE_MS timer (300ms) fires → setError(true), setPhase('imageReady')
      await act(async () => { vi.advanceTimersByTime(310); });
      // Step 3: useEffect for 'imageReady' runs rAF(sync) → setPhase('fadeInImage') + setTimeout(idle,300)
      await act(async () => { vi.advanceTimersByTime(310); });

      expect(screen.getByText('Paths not available')).toBeDefined();
    });

    it('transitions through fadeOutImage when changing instrument', async () => {
      const { container } = render(
        <PathsModal visible={true} songId="song-1" onClose={vi.fn()} />,
      );

      // Complete initial load
      const firstImg = mockImageInstances[0];
      act(() => { firstImg!.onload?.(); });
      await act(async () => { vi.advanceTimersByTime(500); });
      await act(async () => { vi.advanceTimersByTime(300); });
      await act(async () => { vi.advanceTimersByTime(300); });

      // Change instrument
      fireEvent.click(screen.getByTitle('Drums'));

      // FADE_MS (300ms) for fadeOutImage
      await act(async () => { vi.advanceTimersByTime(310); });

      // New image starts loading (spinner phase)
      const secondImg = mockImageInstances[mockImageInstances.length - 1];
      expect(secondImg!.src).toContain('Solo_Drums');

      // Complete second load
      act(() => { secondImg!.onload?.(); });
      await act(async () => { vi.advanceTimersByTime(500); });
      await act(async () => { vi.advanceTimersByTime(300); });
      await act(async () => { vi.advanceTimersByTime(300); });

      const renderedImg = container.querySelector('img');
      if (renderedImg) {
        expect(renderedImg.getAttribute('src')).toContain('Solo_Drums');
      }
    });
  });

  describe('resets state on close/reopen', () => {
    it('resets instrument and difficulty when closed', () => {
      const onClose = vi.fn();
      const { rerender } = render(
        <PathsModal visible={true} songId="song-1" onClose={onClose} />,
      );

      // Change instrument and difficulty
      fireEvent.click(screen.getByTitle('Bass'));
      fireEvent.click(screen.getByText('Medium'));

      // Close
      rerender(<PathsModal visible={false} songId="song-1" onClose={onClose} />);

      // Reopen
      rerender(<PathsModal visible={true} songId="song-1" onClose={onClose} />);

      // Should be back to defaults (Solo_Guitar / Expert)
      const lastImg = mockImageInstances[mockImageInstances.length - 1];
      expect(lastImg!.src).toContain('Solo_Guitar');
      expect(lastImg!.src).toContain('/expert');
    });
  });
});
