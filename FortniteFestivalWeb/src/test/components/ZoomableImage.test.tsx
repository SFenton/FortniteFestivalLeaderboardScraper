import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, fireEvent } from '@testing-library/react';
import { ZoomableImage } from '../../pages/songinfo/components/path/ZoomableImage';

// Mock the CSS module
vi.mock('../../pages/songinfo/components/path/PathsModal.module.css', () => ({
  default: { pathImg: 'pathImg' },
}));

// Mock @festival/theme
vi.mock('@festival/theme', () => ({
  TRANSITION_MS: 300,
}));

function createTouchList(...points: Array<{ clientX: number; clientY: number }>): React.TouchList {
  const list = points.map((p, i) => ({
    identifier: i,
    clientX: p.clientX,
    clientY: p.clientY,
    pageX: p.clientX,
    pageY: p.clientY,
    screenX: p.clientX,
    screenY: p.clientY,
    radiusX: 0,
    radiusY: 0,
    rotationAngle: 0,
    force: 1,
    target: document.createElement('div'),
  })) as unknown as Touch[];

  return Object.assign(list, {
    item: (i: number) => list[i] ?? null,
    length: list.length,
    [Symbol.iterator]: list[Symbol.iterator].bind(list),
  }) as unknown as React.TouchList;
}

describe('ZoomableImage', () => {
  beforeEach(() => { vi.clearAllMocks(); });

  it('renders image with correct src and alt', () => {
    const { container } = render(
      <ZoomableImage src="/img/test.png" alt="test path" visible={true} />,
    );
    const img = container.querySelector('img')!;
    expect(img).toBeDefined();
    expect(img.getAttribute('src')).toBe('/img/test.png');
    expect(img.getAttribute('alt')).toBe('test path');
    expect(img.getAttribute('draggable')).toBe('false');
  });

  it('sets opacity 1 when visible', () => {
    const { container } = render(
      <ZoomableImage src="/img/test.png" alt="test" visible={true} />,
    );
    const img = container.querySelector('img')!;
    expect(img.style.opacity).toBe('1');
  });

  it('sets opacity 0 when not visible', () => {
    const { container } = render(
      <ZoomableImage src="/img/test.png" alt="test" visible={false} />,
    );
    const img = container.querySelector('img')!;
    expect(img.style.opacity).toBe('0');
  });

  it('includes translateY(16px) in transform when not visible', () => {
    const { container } = render(
      <ZoomableImage src="/img/test.png" alt="test" visible={false} />,
    );
    const img = container.querySelector('img')!;
    expect(img.style.transform).toContain('translateY(16px)');
  });

  it('does not include translateY(16px) in transform when visible', () => {
    const { container } = render(
      <ZoomableImage src="/img/test.png" alt="test" visible={true} />,
    );
    const img = container.querySelector('img')!;
    expect(img.style.transform).not.toContain('translateY(16px)');
    expect(img.style.transform).toContain('scale(1)');
  });

  describe('double-click zoom', () => {
    it('zooms in to 2.5 on double-click', () => {
      const { container } = render(
        <ZoomableImage src="/img/test.png" alt="test" visible={true} />,
      );
      const wrapper = container.firstElementChild!;
      fireEvent.doubleClick(wrapper);

      const img = container.querySelector('img')!;
      expect(img.style.transform).toContain('scale(2.5)');
    });

    it('resets zoom on second double-click', () => {
      const { container } = render(
        <ZoomableImage src="/img/test.png" alt="test" visible={true} />,
      );
      const wrapper = container.firstElementChild!;

      // Zoom in
      fireEvent.doubleClick(wrapper);
      let img = container.querySelector('img')!;
      expect(img.style.transform).toContain('scale(2.5)');

      // Zoom out
      fireEvent.doubleClick(wrapper);
      img = container.querySelector('img')!;
      expect(img.style.transform).toContain('scale(1)');
      expect(img.style.transform).toContain('translate(0px, 0px)');
    });

    it('sets touchAction to none when zoomed in', () => {
      const { container } = render(
        <ZoomableImage src="/img/test.png" alt="test" visible={true} />,
      );
      const wrapper = container.firstElementChild as HTMLElement;

      expect(wrapper.style.touchAction).toBe('pan-y');

      fireEvent.doubleClick(wrapper);
      expect(wrapper.style.touchAction).toBe('none');
    });
  });

  describe('ctrl+wheel zoom', () => {
    it('zooms in with ctrl+wheel (negative deltaY)', () => {
      const { container } = render(
        <ZoomableImage src="/img/test.png" alt="test" visible={true} />,
      );
      const wrapper = container.firstElementChild!;

      // Zoom in: deltaY < 0 → factor 1.1
      fireEvent.wheel(wrapper, { deltaY: -100, ctrlKey: true });

      const img = container.querySelector('img')!;
      expect(img.style.transform).toContain('scale(1.1)');
    });

    it('zooms out with ctrl+wheel (positive deltaY)', () => {
      const { container } = render(
        <ZoomableImage src="/img/test.png" alt="test" visible={true} />,
      );
      const wrapper = container.firstElementChild!;

      // First zoom in
      fireEvent.doubleClick(wrapper);

      // Then zoom out
      fireEvent.wheel(wrapper, { deltaY: 100, ctrlKey: true });

      const img = container.querySelector('img')!;
      // 2.5 * 0.9 = 2.25
      expect(img.style.transform).toContain('scale(2.25)');
    });

    it('does nothing without ctrl key', () => {
      const { container } = render(
        <ZoomableImage src="/img/test.png" alt="test" visible={true} />,
      );
      const wrapper = container.firstElementChild!;

      fireEvent.wheel(wrapper, { deltaY: -100, ctrlKey: false });

      const img = container.querySelector('img')!;
      expect(img.style.transform).toContain('scale(1)');
    });

    it('zooms with metaKey (macOS)', () => {
      const { container } = render(
        <ZoomableImage src="/img/test.png" alt="test" visible={true} />,
      );
      const wrapper = container.firstElementChild!;

      fireEvent.wheel(wrapper, { deltaY: -100, metaKey: true });

      const img = container.querySelector('img')!;
      expect(img.style.transform).toContain('scale(1.1)');
    });

    it('clamps zoom to max 5', () => {
      const { container } = render(
        <ZoomableImage src="/img/test.png" alt="test" visible={true} />,
      );
      const wrapper = container.firstElementChild!;

      // Zoom in many times
      for (let i = 0; i < 50; i++) {
        fireEvent.wheel(wrapper, { deltaY: -100, ctrlKey: true });
      }

      const img = container.querySelector('img')!;
      expect(img.style.transform).toContain('scale(5)');
    });

    it('clamps zoom to min 1', () => {
      const { container } = render(
        <ZoomableImage src="/img/test.png" alt="test" visible={true} />,
      );
      const wrapper = container.firstElementChild!;

      // Zoom out many times
      for (let i = 0; i < 50; i++) {
        fireEvent.wheel(wrapper, { deltaY: 100, ctrlKey: true });
      }

      const img = container.querySelector('img')!;
      expect(img.style.transform).toContain('scale(1)');
    });
  });

  describe('src change resets zoom', () => {
    it('resets scale and translate when src changes', () => {
      const { container, rerender } = render(
        <ZoomableImage src="/img/a.png" alt="test" visible={true} />,
      );
      const wrapper = container.firstElementChild!;

      // Zoom in
      fireEvent.doubleClick(wrapper);
      let img = container.querySelector('img')!;
      expect(img.style.transform).toContain('scale(2.5)');

      // Change src
      rerender(<ZoomableImage src="/img/b.png" alt="test" visible={true} />);

      img = container.querySelector('img')!;
      expect(img.style.transform).toContain('scale(1)');
      expect(img.style.transform).toContain('translate(0px, 0px)');
    });
  });

  describe('transition style', () => {
    it('includes transform transition when at default zoom', () => {
      const { container } = render(
        <ZoomableImage src="/img/test.png" alt="test" visible={true} />,
      );
      const img = container.querySelector('img')!;
      expect(img.style.transition).toContain('transform');
    });

    it('omits transform transition when zoomed in', () => {
      const { container } = render(
        <ZoomableImage src="/img/test.png" alt="test" visible={true} />,
      );
      const wrapper = container.firstElementChild!;
      fireEvent.doubleClick(wrapper);

      const img = container.querySelector('img')!;
      // When zoomed, transition should only be opacity, not transform
      expect(img.style.transition).not.toContain('transform');
    });
  });

  describe('forwarded ref', () => {
    it('works with callback ref', () => {
      let imgElement: HTMLImageElement | null = null;
      render(
        <ZoomableImage
          ref={(el) => { imgElement = el; }}
          src="/img/test.png"
          alt="test"
          visible={true}
        />,
      );
      expect(imgElement).toBeInstanceOf(HTMLImageElement);
    });

    it('works with object ref', () => {
      const ref = { current: null as HTMLImageElement | null };
      render(
        <ZoomableImage ref={ref} src="/img/test.png" alt="test" visible={true} />,
      );
      expect(ref.current).toBeInstanceOf(HTMLImageElement);
    });
  });

  describe('touch gestures', () => {
    it('ignores single-touch events', () => {
      const { container } = render(
        <ZoomableImage src="/img/test.png" alt="test" visible={true} />,
      );
      const wrapper = container.firstElementChild!;
      const singleTouch = createTouchList({ clientX: 100, clientY: 100 });

      fireEvent.touchStart(wrapper, { touches: singleTouch });
      fireEvent.touchMove(wrapper, { touches: singleTouch });

      const img = container.querySelector('img')!;
      expect(img.style.transform).toContain('scale(1)');
    });

    it('handles two-finger pinch to zoom', () => {
      const { container } = render(
        <ZoomableImage src="/img/test.png" alt="test" visible={true} />,
      );
      const wrapper = container.firstElementChild!;

      // Start with fingers 100px apart
      const startTouches = createTouchList(
        { clientX: 100, clientY: 200 },
        { clientX: 200, clientY: 200 },
      );
      fireEvent.touchStart(wrapper, { touches: startTouches });

      // Move fingers 200px apart (double the distance → double the scale)
      const moveTouches = createTouchList(
        { clientX: 50, clientY: 200 },
        { clientX: 250, clientY: 200 },
      );
      fireEvent.touchMove(wrapper, { touches: moveTouches });

      const img = container.querySelector('img')!;
      // Scale should be approximately 2 (200/100 = 2)
      expect(img.style.transform).toContain('scale(2)');
    });
  });
});
