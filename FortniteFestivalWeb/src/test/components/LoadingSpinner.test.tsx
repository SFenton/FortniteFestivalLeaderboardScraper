import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import ArcSpinner from '../../components/common/ArcSpinner';

describe('ArcSpinner', () => {
  it('renders a div element', () => {
    const { container } = render(<ArcSpinner />);
    expect(container.querySelector('div')).toBeTruthy();
  });

  it('defaults to lg size', () => {
    const { container } = render(<ArcSpinner />);
    const el = container.firstElementChild as HTMLElement;
    expect(el.className).toBeTruthy();
  });

  it('accepts size prop', () => {
    const { container: sm } = render(<ArcSpinner size="sm" />);
    const { container: md } = render(<ArcSpinner size="md" />);
    const { container: lg } = render(<ArcSpinner size="lg" />);
    const smClass = (sm.firstElementChild as HTMLElement).className;
    const mdClass = (md.firstElementChild as HTMLElement).className;
    const lgClass = (lg.firstElementChild as HTMLElement).className;
    expect(smClass).not.toBe(mdClass);
    expect(mdClass).not.toBe(lgClass);
  });

  it('accepts extra className', () => {
    const { container } = render(<ArcSpinner className="custom" />);
    const el = container.firstElementChild as HTMLElement;
    expect(el.className).toContain('custom');
  });
});