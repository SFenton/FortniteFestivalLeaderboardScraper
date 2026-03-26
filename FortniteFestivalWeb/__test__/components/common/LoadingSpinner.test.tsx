import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import ArcSpinner, { SpinnerSize } from '../../../src/components/common/ArcSpinner';

describe('ArcSpinner', () => {
  it('renders a div element', () => {
    const { container } = render(<ArcSpinner />);
    expect(container.querySelector('div')).toBeTruthy();
  });

  it('defaults to lg size', () => {
    const { container } = render(<ArcSpinner />);
    const el = container.firstElementChild as HTMLElement;
    expect(el.style.width).toBe('48px');
  });

  it('accepts size prop', () => {
    const { container: sm } = render(<ArcSpinner size={SpinnerSize.SM} />);
    const { container: md } = render(<ArcSpinner size={SpinnerSize.MD} />);
    const { container: lg } = render(<ArcSpinner size={SpinnerSize.LG} />);
    expect((sm.firstElementChild as HTMLElement).style.width).toBe('24px');
    expect((md.firstElementChild as HTMLElement).style.width).toBe('36px');
    expect((lg.firstElementChild as HTMLElement).style.width).toBe('48px');
  });

  it('accepts extra className', () => {
    const { container } = render(<ArcSpinner className="custom" />);
    const el = container.firstElementChild as HTMLElement;
    expect(el.className).toContain('custom');
  });
});