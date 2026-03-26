import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import PercentilePill from '../../../../src/components/songs/metadata/PercentilePill';

describe('PercentilePill', () => {
  it('returns null for null display', () => {
    const { container } = render(<PercentilePill display={null} />);
    expect(container.innerHTML).toBe('');
  });

  it('returns null for undefined display', () => {
    const { container } = render(<PercentilePill display={undefined} />);
    expect(container.innerHTML).toBe('');
  });

  it('renders display text', () => {
    const { container } = render(<PercentilePill display="Top 10%" />);
    expect(container.textContent).toBe('Top 10%');
  });

  it('applies top1 style for Top 1%', () => {
    const { container } = render(<PercentilePill display="Top 1%" />);
    const span = container.querySelector('span');
    expect(span).toBeTruthy();
    expect(span?.style.fontStyle).toBe('italic');
  });

  it('applies top5 style for Top 2-5%', () => {
    for (const pct of ['Top 2%', 'Top 3%', 'Top 4%', 'Top 5%']) {
      const { container } = render(<PercentilePill display={pct} />);
      const span = container.querySelector('span');
      expect(span).toBeTruthy();
      expect(span?.style.fontWeight).toBe('700');
    }
  });

  it('applies default style for other percentiles', () => {
    const { container } = render(<PercentilePill display="Top 25%" />);
    const span = container.querySelector('span');
    expect(span).toBeTruthy();
    expect(span?.style.fontWeight).toBe('600');
  });
});
