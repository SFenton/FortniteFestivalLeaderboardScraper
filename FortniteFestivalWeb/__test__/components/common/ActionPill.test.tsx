import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { ActionPill } from '../../../src/components/common/ActionPill';

describe('ActionPill', () => {
  it('renders icon and label', () => {
    render(<ActionPill icon={<span data-testid="icon">I</span>} label="Sort" onClick={vi.fn()} />);
    expect(screen.getByTestId('icon')).toBeTruthy();
    expect(screen.getByText('Sort')).toBeTruthy();
  });

  it('calls onClick when clicked', () => {
    const onClick = vi.fn();
    render(<ActionPill icon={<span>I</span>} label="Sort" onClick={onClick} />);
    fireEvent.click(screen.getByRole('button'));
    expect(onClick).toHaveBeenCalledTimes(1);
  });

  it('applies active class when active', () => {
    const { container } = render(<ActionPill icon={<span>I</span>} label="Sort" onClick={vi.fn()} active />);
    const btn = container.querySelector('button');
    expect(btn?.className).toContain('Active');
  });

  it('applies inactive class when not active', () => {
    const { container } = render(<ActionPill icon={<span>I</span>} label="Sort" onClick={vi.fn()} />);
    const btn = container.querySelector('button');
    expect(btn?.className).not.toContain('Active');
  });

  it('renders dot when dot is true', () => {
    const { container } = render(<ActionPill icon={<span>I</span>} label="Sort" onClick={vi.fn()} dot />);
    expect(container.querySelector('[class*="dot"]')).toBeTruthy();
  });

  it('applies custom className', () => {
    const { container } = render(<ActionPill icon={<span>I</span>} label="Sort" onClick={vi.fn()} className="custom" />);
    const btn = container.querySelector('button');
    expect(btn?.className).toContain('custom');
  });

  it('applies style prop', () => {
    const { container } = render(<ActionPill icon={<span>I</span>} label="Sort" onClick={vi.fn()} style={{ color: 'red' }} />);
    const btn = container.querySelector('button');
    expect(btn?.style.color).toBe('red');
  });

  it('sets aria-label', () => {
    render(<ActionPill icon={<span>I</span>} label="Filter" onClick={vi.fn()} />);
    expect(screen.getByLabelText('Filter')).toBeTruthy();
  });
});
