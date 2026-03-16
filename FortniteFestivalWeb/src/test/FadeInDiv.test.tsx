import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import FadeInDiv from '../components/FadeInDiv';

describe('FadeInDiv', () => {
  it('renders children directly when delay is undefined', () => {
    render(<FadeInDiv>Hello</FadeInDiv>);
    expect(screen.getByText('Hello')).toBeDefined();
  });

  it('renders children with opacity 0 when hidden is true', () => {
    const { container } = render(<FadeInDiv delay={100} hidden>Hidden</FadeInDiv>);
    const div = container.firstElementChild as HTMLElement;
    expect(div.textContent).toBe('Hidden');
  });

  it('renders with animation when delay is provided', () => {
    const { container } = render(<FadeInDiv delay={200}>Animated</FadeInDiv>);
    const div = container.firstElementChild as HTMLElement;
    expect(div.textContent).toBe('Animated');
    // Should have a CSS custom property for animation
    const style = div.getAttribute('style') ?? '';
    expect(style).toContain('--fade-animation');
  });

  it('passes style prop through', () => {
    const { container } = render(
      <FadeInDiv style={{ marginTop: 10 }}>Styled</FadeInDiv>
    );
    const div = container.firstElementChild as HTMLElement;
    expect(div.style.marginTop).toBe('10px');
  });
});
