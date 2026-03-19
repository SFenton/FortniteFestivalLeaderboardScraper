import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import ScorePill from '../../../src/components/songs/metadata/ScorePill';

describe('ScorePill', () => {
  it('renders the score formatted with locale separators', () => {
    const { container } = render(<ScorePill score={145000} />);
    expect(container.textContent).toBe('145,000');
  });

  it('renders zero score', () => {
    const { container } = render(<ScorePill score={0} />);
    expect(container.textContent).toBe('0');
  });

  it('renders small scores without separators', () => {
    const { container } = render(<ScorePill score={999} />);
    expect(container.textContent).toBe('999');
  });

  it('renders large scores with separators', () => {
    const { container } = render(<ScorePill score={1234567} />);
    expect(container.textContent).toBe('1,234,567');
  });

  it('applies base CSS class by default', () => {
    const { container } = render(<ScorePill score={100} />);
    const span = container.querySelector('span')!;
    expect(span.className).toContain('base');
    expect(span.className).not.toContain('bold');
  });

  it('applies bold CSS class when bold prop is true', () => {
    const { container } = render(<ScorePill score={100} bold />);
    const span = container.querySelector('span')!;
    expect(span.className).toContain('bold');
  });

  it('applies custom width via style', () => {
    const { container } = render(<ScorePill score={100} width="78px" />);
    const span = container.querySelector('span')!;
    expect(span.style.width).toBe('78px');
  });

  it('applies ch-based width', () => {
    const { container } = render(<ScorePill score={100} width="6ch" />);
    const span = container.querySelector('span')!;
    expect(span.style.width).toBe('6ch');
  });

  it('does not set width style when width prop is omitted', () => {
    const { container } = render(<ScorePill score={100} />);
    const span = container.querySelector('span')!;
    expect(span.style.width).toBe('');
  });

  it('appends custom className to base class', () => {
    const { container } = render(<ScorePill score={100} className="custom" />);
    const span = container.querySelector('span')!;
    expect(span.className).toContain('base');
    expect(span.className).toContain('custom');
  });

  it('appends custom className to bold class', () => {
    const { container } = render(<ScorePill score={100} bold className="custom" />);
    const span = container.querySelector('span')!;
    expect(span.className).toContain('bold');
    expect(span.className).toContain('custom');
  });

  it('renders as an inline span element', () => {
    const { container } = render(<ScorePill score={50000} />);
    const el = container.firstElementChild!;
    expect(el.tagName).toBe('SPAN');
  });

  it('combines width and bold props', () => {
    const { container } = render(<ScorePill score={245139} width="78px" bold />);
    const span = container.querySelector('span')!;
    expect(span.className).toContain('bold');
    expect(span.style.width).toBe('78px');
    expect(span.textContent).toBe('245,139');
  });
});
