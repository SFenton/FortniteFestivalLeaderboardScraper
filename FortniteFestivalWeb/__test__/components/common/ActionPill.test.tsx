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

  it('activates from touch pointerup and suppresses the follow-up click', () => {
    const onClick = vi.fn();
    render(<ActionPill icon={<span>I</span>} label="Sort" onClick={onClick} />);
    const button = screen.getByRole('button');

    fireEvent.pointerDown(button, { pointerId: 1, pointerType: 'touch', button: 0, clientX: 10, clientY: 10 });
    fireEvent.pointerUp(button, { pointerId: 1, pointerType: 'touch', button: 0, clientX: 10, clientY: 10 });
    fireEvent.click(button);

    expect(onClick).toHaveBeenCalledTimes(1);
  });

  it('cancels touch activation when the pointer moves too far', () => {
    const onClick = vi.fn();
    render(<ActionPill icon={<span>I</span>} label="Sort" onClick={onClick} />);
    const button = screen.getByRole('button');

    fireEvent.pointerDown(button, { pointerId: 1, pointerType: 'touch', button: 0, clientX: 10, clientY: 10 });
    fireEvent.pointerUp(button, { pointerId: 1, pointerType: 'touch', button: 0, clientX: 30, clientY: 10 });

    expect(onClick).not.toHaveBeenCalled();
  });

  it('applies active class when active', () => {
    const { container } = render(<ActionPill icon={<span>I</span>} label="Sort" onClick={vi.fn()} active />);
    const btn = container.querySelector('button');
    // Active pill sets backgroundImage to 'none' (removes frosted noise)
    expect(btn?.style.backgroundImage).toBe('none');
  });

  it('applies inactive class when not active', () => {
    const { container } = render(<ActionPill icon={<span>I</span>} label="Sort" onClick={vi.fn()} />);
    const btn = container.querySelector('button');
    expect(btn?.style.backgroundImage).not.toBe('none');
  });

  it('renders dot when dot is true', () => {
    const { container } = render(<ActionPill icon={<span>I</span>} label="Sort" onClick={vi.fn()} dot />);
    // Dot is a small circle span (6px wide with border-radius: 50%)
    const spans = container.querySelectorAll('button span');
    const dotSpan = spans[spans.length - 1] as HTMLElement | undefined;
    expect(dotSpan?.style.width).toBe('6px');
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
