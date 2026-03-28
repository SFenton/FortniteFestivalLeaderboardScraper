import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import EmptyState from '../../../src/components/common/EmptyState';

describe('EmptyState', () => {
  it('renders title', () => {
    render(<EmptyState title="No items found" />);
    expect(screen.getByText('No items found')).toBeTruthy();
  });

  it('renders subtitle when provided', () => {
    render(<EmptyState title="Empty" subtitle="Try again later" />);
    expect(screen.getByText('Try again later')).toBeTruthy();
  });

  it('does not render subtitle when omitted', () => {
    const { container } = render(<EmptyState title="Empty" />);
    // title div only (no subtitle div)
    const root = container.firstElementChild!;
    expect(root.children).toHaveLength(1);
  });

  it('renders icon when provided', () => {
    render(<EmptyState title="Empty" icon={<span data-testid="icon">★</span>} />);
    expect(screen.getByTestId('icon')).toBeTruthy();
  });

  it('does not apply minHeight by default', () => {
    const { container } = render(<EmptyState title="Empty" />);
    const root = container.firstElementChild as HTMLElement;
    expect(root.style.minHeight).toBe('');
  });

  it('applies minHeight when fullPage is true', () => {
    const { container } = render(<EmptyState title="Empty" fullPage />);
    const root = container.firstElementChild as HTMLElement;
    expect(root.style.minHeight).toContain('calc(100vh');
  });

  it('merges style prop with root styles', () => {
    const { container } = render(<EmptyState title="Empty" style={{ marginTop: '20px' }} />);
    const root = container.firstElementChild as HTMLElement;
    expect(root.style.marginTop).toBe('20px');
    // Root styles still present
    expect(root.style.textAlign).toBe('center');
  });

  it('applies className', () => {
    const { container } = render(<EmptyState title="Empty" className="custom" />);
    expect(container.firstElementChild!.className).toBe('custom');
  });
});
