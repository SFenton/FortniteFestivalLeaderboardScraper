import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, act } from '@testing-library/react';
import MarqueeText from '../../../src/components/common/MarqueeText';

let resizeCallback: ResizeObserverCallback | null = null;
let observedElements: Element[] = [];
let rangeWidth = 0;

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
  rangeWidth = 0;
  vi.stubGlobal('ResizeObserver', MockResizeObserver);
  vi.spyOn(document, 'createRange').mockReturnValue({
    selectNodeContents: vi.fn(),
    getBoundingClientRect: vi.fn(() => ({
      x: 0,
      y: 0,
      top: 0,
      left: 0,
      right: rangeWidth,
      bottom: 20,
      width: rangeWidth,
      height: 20,
      toJSON: () => ({}),
    })),
    detach: vi.fn(),
  } as unknown as Range);
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
function mockWidths(el: HTMLElement, clientWidth: number, scrollWidth: number, rectWidth = scrollWidth, textWidth = scrollWidth) {
  Object.defineProperty(el, 'clientWidth', { configurable: true, get: () => clientWidth });
  Object.defineProperty(el, 'scrollWidth', { configurable: true, get: () => scrollWidth });
  Object.defineProperty(el, 'getBoundingClientRect', {
    configurable: true,
    value: () => ({
      x: 0,
      y: 0,
      top: 0,
      left: 0,
      right: rectWidth,
      bottom: 20,
      width: rectWidth,
      height: 20,
      toJSON: () => ({}),
    }),
  });
  rangeWidth = textWidth;
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

  it('measures block semantic text by content width, not element box width', () => {
    const { container } = render(<MarqueeText text="A very long song title that overflows" as="h1" />);
    const wrapper = container.firstElementChild as HTMLElement;

    const inner = wrapper.querySelector('h1')!;
    mockWidths(wrapper, 200, 200);
    mockWidths(inner, 200, 500, 200, 500);
    fireResize();

    const track = wrapper.querySelector('[class*="track"]') as HTMLElement;
    expect(track).not.toBeNull();
    expect(track.style.getPropertyValue('--marquee-translate')).toBe('-528px');
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

  it('rounds sub-pixel text width in the translate distance', () => {
    const { container } = render(<MarqueeText text="Long text here" gap={40} cycleDuration={8} />);
    const wrapper = container.firstElementChild as HTMLElement;

    const inner = wrapper.querySelector('span')!;
    mockWidths(wrapper, 50, 50);
    mockWidths(inner, 200.5, 200.5);
    fireResize();

    const track = container.querySelector('[class*="track"]') as HTMLElement;
    expect(track.style.getPropertyValue('--marquee-translate')).toBe('-241px');
    expect(track.style.getPropertyValue('--marquee-gap')).toBe('40.5px');
  });

  it('rounds synchronized translate distances consistently', () => {
    const { container } = render(<MarqueeText text="Long text here" cycleDuration={8} syncDistance={240.7} />);
    const wrapper = container.firstElementChild as HTMLElement;

    const inner = wrapper.querySelector('span')!;
    mockWidths(wrapper, 50, 50);
    mockWidths(inner, 200, 200);
    fireResize();

    const track = container.querySelector('[class*="track"]') as HTMLElement;
    expect(track.style.getPropertyValue('--marquee-translate')).toBe('-241px');
    expect(track.style.getPropertyValue('--marquee-gap')).toBe('41px');
  });

  it('keeps marquee phase stable across parent rerenders', () => {
    const baseNow = Date.now();
    const nowSpy = vi.spyOn(Date, 'now').mockReturnValue(baseNow + 1_000);
    const { container, rerender } = render(<MarqueeText text="A long player name" cycleDuration={8} />);
    const wrapper = container.firstElementChild as HTMLElement;

    const inner = wrapper.querySelector('span')!;
    mockWidths(wrapper, 50, 50);
    mockWidths(inner, 200, 200);
    fireResize();

    const firstTrack = container.querySelector('[class*="track"]') as HTMLElement;
    const firstDelay = firstTrack.style.getPropertyValue('--marquee-delay');

    nowSpy.mockReturnValue(baseNow + 4_000);
    rerender(<MarqueeText text="A long player name" cycleDuration={8} />);

    const secondTrack = container.querySelector('[class*="track"]') as HTMLElement;
    expect(secondTrack.style.getPropertyValue('--marquee-delay')).toBe(firstDelay);
  });

  it('recomputes marquee phase when the effective translate distance changes', () => {
    const baseNow = Date.now();
    const nowSpy = vi.spyOn(Date, 'now').mockReturnValue(baseNow + 1_000);
    const { container, rerender } = render(<MarqueeText text="A long player name" cycleDuration={8} syncDistance={240} />);
    const wrapper = container.firstElementChild as HTMLElement;

    const inner = wrapper.querySelector('span')!;
    mockWidths(wrapper, 50, 50);
    mockWidths(inner, 200, 200);
    fireResize();

    const firstTrack = container.querySelector('[class*="track"]') as HTMLElement;
    const firstDelay = firstTrack.style.getPropertyValue('--marquee-delay');

    nowSpy.mockReturnValue(baseNow + 4_000);
    rerender(<MarqueeText text="A long player name" cycleDuration={8} syncDistance={320} />);

    const secondTrack = container.querySelector('[class*="track"]') as HTMLElement;
    expect(secondTrack.style.getPropertyValue('--marquee-delay')).not.toBe(firstDelay);
  });
});
