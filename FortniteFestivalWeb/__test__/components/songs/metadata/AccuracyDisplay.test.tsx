import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import AccuracyDisplay from '../../../../src/components/songs/metadata/AccuracyDisplay';

describe('AccuracyDisplay', () => {
  it('renders 0% pill for null accuracy', () => {
    const { container } = render(<AccuracyDisplay accuracy={null} />);
    expect(container.textContent).toBe('0%');
    const span = container.querySelector('span');
    expect(span?.style.backgroundColor).toMatch(/^rgba\(220/);
  });

  it('renders 0% pill for zero accuracy', () => {
    const { container } = render(<AccuracyDisplay accuracy={0} />);
    expect(container.textContent).toBe('0%');
    const span = container.querySelector('span');
    expect(span?.style.backgroundColor).toMatch(/^rgba\(220/);
  });

  it('renders 0% pill for undefined accuracy', () => {
    const { container } = render(<AccuracyDisplay accuracy={undefined} />);
    expect(container.textContent).toBe('0%');
  });

  it('renders formatted percentage for normal accuracy', () => {
    // 986500 = 98.65%
    const { container } = render(<AccuracyDisplay accuracy={986500} />);
    expect(container.textContent).toBe('98.7%');
  });

  it('renders integer percentage when .0', () => {
    // 1000000 = 100%
    const { container } = render(<AccuracyDisplay accuracy={1000000} />);
    expect(container.textContent).toBe('100%');
  });

  it('applies FC badge class when isFullCombo', () => {
    const { container } = render(
      <AccuracyDisplay accuracy={990000} isFullCombo />,
    );
    const span = container.querySelector('span');
    expect(span).toBeTruthy();
    expect(span?.textContent).toBe('99%');
    // Should have FC badge styling applied via inline style
    expect(span?.style.fontStyle).toBe('italic');
  });

  it('applies accuracy pill style for non-FC accuracy', () => {
    const { container } = render(<AccuracyDisplay accuracy={500000} />);
    const span = container.querySelector('span');
    expect(span?.style.backgroundColor).toMatch(/^rgba\(/);
    expect(span?.style.minWidth).toBeTruthy();
  });
});
