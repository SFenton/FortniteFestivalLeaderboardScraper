import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, act } from '@testing-library/react';
import MarqueeText from '../../../src/components/common/MarqueeText';

let resizeCallback: ResizeObserverCallback | null = null;
let observedElements: Element[] = [];

class MockResizeObserver {
  constructor(cb: ResizeObserverCallback) {
    resizeCallback = cb;
  }
  observe(el: Element) {
    observedElements.push(el);
  }
  unobserve() {}
  disconnect() {
    observedElements = [];
  }
}

beforeEach(() => {
  resizeCallback = null;
  observedElements = [];
  vi.stubGlobal('ResizeObserver', MockResizeObserver);
});

afterEach(() => {
  vi.restoreAllMocks();
});

/** Simulate ResizeObserver firing on the container. */
function fireResize() {
  if (resizeCallback && observedElements[0]) {
    act(() => {
      resizeCallback!(
        [{ target: observedElements[0], contentRect: { width: 200, height: 20 } } as unknown as ResizeObserverEntry],
        {} as ResizeObserver,
      );
    });
  }
}

/** Set scrollWidth on an element while keeping clientWidth at its current value. */
function mockWidths(el: HTMLElement, clientWidth: number, scrollWidth: number) {
  Object.defineProperty(el, 'clientWidth', { configurable: true, get: () => clientWidth });
  Object.defineProperty(el, 'scrollWidth', { configurable: true, get: () => scrollWidth });
}

describe('MarqueeText', () => {
  it('renders plain text when it fits', () => {
    const { container } = render(<MarqueeText text="Short" />);
    // Before ResizeObserver fires, text should render without marquee
    expect(container.textContent).toBe('Short');
    expect(container.querySelector('[class*="track"]')).toBeNull();
  });

  it('renders the correct semantic element via as prop', () => {
    const { container: c1 } = render(<MarqueeText text="Title" as="h1" />);
    expect(c1.querySelector('h1')).not.toBeNull();
    expect(c1.querySelector('h1')!.textContent).toBe('Title');

    const { container: c2 } = render(<MarqueeText text="Artist" as="p" />);
    expect(c2.querySelector('p')).not.toBeNull();
    expect(c2.querySelector('p')!.textContent).toBe('Artist');

    const { container: c3 } = render(<MarqueeText text="Inline" />);
    expect(c3.querySelector('span')).not.toBeNull();
  });

  it('forwards className and style', () => {
    const { container } = render(
      <MarqueeText text="Styled" className="my-class" style={{ color: 'red' }} />,
    );
    const wrapper = container.firstElementChild as HTMLElement;
    expect(wrapper.classList.contains('my-class')).toBe(true);
    expect(wrapper.style.color).toBe('red');
  });

  it('activates marquee when text overflows', () => {
    const { container } = render(<MarqueeText text="A very long song title that overflows" as="h1" />);
    const wrapper = container.firstElementChild as HTMLElement;

    // Make inner h1 report a wide scrollWidth
    const inner = wrapper.querySelector('h1')!;
    mockWidths(wrapper, 200, 200);
    mockWidths(inner, 500, 500);

    fireResize();

    // Should now have the track with two copies
    const track = wrapper.querySelector('[class*="track"]');
    expect(track).not.toBeNull();
    const headings = wrapper.querySelectorAll('h1');
    expect(headings).toHaveLength(2);
    expect(headings[1]!.getAttribute('aria-hidden')).toBe('true');
  });

  it('deactivates marquee when text stops overflowing', () => {
    const { container } = render(<MarqueeText text="Text" as="p" />);
    const wrapper = container.firstElementChild as HTMLElement;

    // First: overflow
    const inner = wrapper.querySelector('p')!;
    mockWidths(wrapper, 100, 100);
    mockWidths(inner, 300, 300);
    fireResize();
    expect(wrapper.querySelector('[class*="track"]')).not.toBeNull();

    // Now: fits (need to re-get wrapper after re-render)
    const wrapper2 = container.firstElementChild as HTMLElement;
    const inner2 = wrapper2.querySelector('p')!;
    mockWidths(wrapper2, 400, 400);
    mockWidths(inner2, 100, 100);
    fireResize();
    expect(container.querySelector('[class*="track"]')).toBeNull();
  });

  it('disconnects ResizeObserver on unmount', () => {
    const { unmount } = render(<MarqueeText text="Hello" />);
    expect(observedElements.length).toBe(1);
    unmount();
    expect(observedElements.length).toBe(0);
  });

  it('sets custom properties for duration and gap on the track', () => {
    const { container } = render(<MarqueeText text="Long text here" gap={40} cycleDuration={8} />);
    const wrapper = container.firstElementChild as HTMLElement;

    const inner = wrapper.querySelector('span')!;
    mockWidths(wrapper, 50, 50);
    mockWidths(inner, 200, 200);
    fireResize();

    const track = container.querySelector('[class*="track"]') as HTMLElement;
    expect(track).not.toBeNull();
    expect(track.style.getPropertyValue('--marquee-gap')).toBe('40px');
    // Fixed cycle duration = 8s regardless of translate distance
    const dur = parseFloat(track.style.getPropertyValue('--marquee-duration'));
    expect(dur).toBe(8);
  });
});
