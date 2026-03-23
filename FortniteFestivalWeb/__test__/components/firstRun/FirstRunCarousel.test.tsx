import { describe, it, expect, vi, beforeAll } from 'vitest';
import { render, screen, fireEvent, act } from '@testing-library/react';
import FirstRunCarousel from '../../../src/components/firstRun/FirstRunCarousel';
import { stubResizeObserver, stubElementDimensions } from '../../helpers/browserStubs';

// i18n is loaded in setup.ts — t() returns the key as-is for untranslated keys

function makeSlides(count: number) {
  return Array.from({ length: count }, (_, i) => ({
    id: `slide-${i}`,
    version: 1,
    title: `slide.title.${i}`,
    description: `slide.desc.${i}`,
    render: () => <div data-testid={`slide-content-${i}`}>Content {i}</div>,
    contentStaggerCount: i === 0 ? 2 : undefined,
  }));
}

beforeAll(() => {
  stubResizeObserver();
  stubElementDimensions(600);
});

describe('FirstRunCarousel', () => {
  it('renders null when slides array is empty', () => {
    const { container } = render(
      <FirstRunCarousel slides={[]} onDismiss={vi.fn()} />,
    );
    // Empty slides means slide = slides[0] is undefined → returns null
    expect(container.innerHTML).toBe('');
  });

  it('renders close button', () => {
    render(<FirstRunCarousel slides={makeSlides(1)} onDismiss={vi.fn()} />);
    expect(screen.getByLabelText('Close')).toBeDefined();
  });

  it('renders pagination dots matching slide count', () => {
    const slides = makeSlides(3);
    render(<FirstRunCarousel slides={slides} onDismiss={vi.fn()} />);
    const dots = screen.getAllByLabelText(/Slide \d/);
    expect(dots).toHaveLength(3);
  });

  it('renders back and forward navigation buttons', () => {
    render(<FirstRunCarousel slides={makeSlides(3)} onDismiss={vi.fn()} />);
    expect(screen.getByLabelText('Back one entry')).toBeDefined();
    expect(screen.getByLabelText('Forward one entry')).toBeDefined();
  });

  it('disables back button on first slide', () => {
    render(<FirstRunCarousel slides={makeSlides(3)} onDismiss={vi.fn()} />);
    const backButton = screen.getByLabelText('Back one entry');
    expect(backButton).toBeDisabled();
  });

  it('disables forward button on last slide', () => {
    render(<FirstRunCarousel slides={makeSlides(1)} onDismiss={vi.fn()} />);
    const fwd = screen.getByLabelText('Forward one entry');
    expect(fwd).toBeDisabled();
  });

  it('calls onDismiss when close button clicked', () => {
    const onDismiss = vi.fn();
    render(<FirstRunCarousel slides={makeSlides(1)} onDismiss={onDismiss} />);
    fireEvent.click(screen.getByLabelText('Close'));
    expect(onDismiss).toHaveBeenCalled();
  });

  it('navigates forward when forward button clicked', async () => {
    vi.useFakeTimers();
    render(<FirstRunCarousel slides={makeSlides(3)} onDismiss={vi.fn()} />);

    // Wait for entrance animation to complete
    act(() => { vi.advanceTimersByTime(500); });

    const fwd = screen.getByLabelText('Forward one entry');
    fireEvent.click(fwd);

    // Advance through FAST_FADE_MS
    act(() => { vi.advanceTimersByTime(200); });

    // Second slide dot should now be active
    const dots = screen.getAllByLabelText(/Slide \d/);
    expect(dots[1]?.className).toContain('Active');

    vi.useRealTimers();
  });

  it('dismisses on Escape key', () => {
    const onDismiss = vi.fn();
    render(<FirstRunCarousel slides={makeSlides(1)} onDismiss={onDismiss} />);
    fireEvent.keyDown(document, { key: 'Escape' });
    expect(onDismiss).toHaveBeenCalled();
  });

  it('dismisses on overlay click', () => {
    const onDismiss = vi.fn();
    const { container } = render(<FirstRunCarousel slides={makeSlides(1)} onDismiss={onDismiss} />);
    // The overlay is the outermost div
    const overlay = container.firstElementChild!;
    fireEvent.click(overlay);
    expect(onDismiss).toHaveBeenCalled();
  });

  it('calls onExitComplete after exit animation', () => {
    vi.useFakeTimers();
    const onDismiss = vi.fn();
    const onExitComplete = vi.fn();
    render(<FirstRunCarousel slides={makeSlides(1)} onDismiss={onDismiss} onExitComplete={onExitComplete} />);

    fireEvent.click(screen.getByLabelText('Close'));
    expect(onDismiss).toHaveBeenCalled();

    // Advance past TRANSITION_MS
    act(() => { vi.advanceTimersByTime(500); });
    expect(onExitComplete).toHaveBeenCalled();
    vi.useRealTimers();
  });

  it('clicking dot navigates to that slide', () => {
    vi.useFakeTimers();
    render(<FirstRunCarousel slides={makeSlides(3)} onDismiss={vi.fn()} />);
    act(() => { vi.advanceTimersByTime(500); });

    const dots = screen.getAllByLabelText(/Slide \d/);
    fireEvent.click(dots[2]!);
    act(() => { vi.advanceTimersByTime(200); });

    // Forward button should be disabled on last slide
    expect(screen.getByLabelText('Forward one entry')).toBeDisabled();
    vi.useRealTimers();
  });

  it('clicking active dot does nothing', () => {
    vi.useFakeTimers();
    render(<FirstRunCarousel slides={makeSlides(3)} onDismiss={vi.fn()} />);
    act(() => { vi.advanceTimersByTime(500); });

    // Click the first dot (already active) — should not navigate
    const dots = screen.getAllByLabelText(/Slide \d/);
    fireEvent.click(dots[0]!);
    // Back button should still be disabled (still on first slide)
    expect(screen.getByLabelText('Back one entry')).toBeDisabled();
    vi.useRealTimers();
  });

  it('ArrowLeft and ArrowRight keys navigate', () => {
    vi.useFakeTimers();
    render(<FirstRunCarousel slides={makeSlides(3)} onDismiss={vi.fn()} />);
    act(() => { vi.advanceTimersByTime(500); });

    fireEvent.keyDown(document, { key: 'ArrowRight' });
    act(() => { vi.advanceTimersByTime(200); });

    // Now on slide 2, back should be enabled
    expect(screen.getByLabelText('Back one entry')).not.toBeDisabled();

    fireEvent.keyDown(document, { key: 'ArrowLeft' });
    act(() => { vi.advanceTimersByTime(200); });

    // Back on slide 1, back should be disabled
    expect(screen.getByLabelText('Back one entry')).toBeDisabled();
    vi.useRealTimers();
  });
});
