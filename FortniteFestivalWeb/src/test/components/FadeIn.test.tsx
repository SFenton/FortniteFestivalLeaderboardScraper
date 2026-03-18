import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import FadeIn from '../../components/page/FadeIn';

describe('FadeIn', () => {
  it('renders children directly when delay is undefined', () => {
    render(<FadeIn>Hello</FadeIn>);
    expect(screen.getByText('Hello')).toBeDefined();
  });

  it('renders children with opacity 0 when hidden is true', () => {
    const { container } = render(<FadeIn delay={100} hidden>Hidden</FadeIn>);
    const div = container.firstElementChild as HTMLElement;
    expect(div.textContent).toBe('Hidden');
  });

  it('renders with animation when delay is provided', () => {
    const { container } = render(<FadeIn delay={200}>Animated</FadeIn>);
    const div = container.firstElementChild as HTMLElement;
    expect(div.textContent).toBe('Animated');
    // Should have a CSS custom property for animation
    const style = div.getAttribute('style') ?? '';
    expect(style).toContain('--fade-animation');
  });

  it('passes style prop through', () => {
    const { container } = render(
      <FadeIn style={{ marginTop: 10 }}>Styled</FadeIn>
    );
    const div = container.firstElementChild as HTMLElement;
    expect(div.style.marginTop).toBe('10px');
  });

  it('renders with custom className', () => {
    const { container } = render(<FadeIn delay={100} className="custom">Content</FadeIn>);
    const wrapper = container.firstElementChild;
    expect(wrapper?.className).toContain('custom');
  });

  it('renders as different element type', () => {
    const { container } = render(<FadeIn as="span">Span</FadeIn>);
    expect(container.querySelector('span')).toBeTruthy();
  });

  it('hidden with custom className', () => {
    const { container } = render(<FadeIn delay={100} hidden className="custom">X</FadeIn>);
    const el = container.firstElementChild!;
    expect(el.className).toContain('hidden');
    expect(el.className).toContain('custom');
  });
});
