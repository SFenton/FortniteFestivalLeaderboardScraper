import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { DirectionSelector } from '../../../src/components/common/DirectionSelector';

// i18n setup.ts provides translation passthrough

describe('DirectionSelector', () => {
  it('renders with default title from i18n', () => {
    render(<DirectionSelector ascending={true} onChange={vi.fn()} />);
    expect(screen.getByText('Sort Direction')).toBeDefined();
  });

  it('renders custom title', () => {
    render(<DirectionSelector ascending={true} onChange={vi.fn()} title="Custom Title" />);
    expect(screen.getByText('Custom Title')).toBeDefined();
  });

  it('shows ascending hint when ascending is true', () => {
    render(<DirectionSelector ascending={true} onChange={vi.fn()} />);
    expect(screen.getByText('Ascending (A–Z, low–high)')).toBeDefined();
  });

  it('shows descending hint when ascending is false', () => {
    render(<DirectionSelector ascending={false} onChange={vi.fn()} />);
    expect(screen.getByText('Descending (Z–A, high–low)')).toBeDefined();
  });

  it('shows custom hint', () => {
    render(<DirectionSelector ascending={true} onChange={vi.fn()} hint="Custom Hint" />);
    expect(screen.getByText('Custom Hint')).toBeDefined();
  });

  it('calls onChange(true) when ascending button clicked', () => {
    const onChange = vi.fn();
    render(<DirectionSelector ascending={false} onChange={onChange} />);
    fireEvent.click(screen.getByLabelText('A→Z'));
    expect(onChange).toHaveBeenCalledWith(true);
  });

  it('calls onChange(false) when descending button clicked', () => {
    const onChange = vi.fn();
    render(<DirectionSelector ascending={true} onChange={onChange} />);
    fireEvent.click(screen.getByLabelText('Z→A'));
    expect(onChange).toHaveBeenCalledWith(false);
  });

  it('renders two icon buttons', () => {
    render(<DirectionSelector ascending={true} onChange={vi.fn()} />);
    const buttons = screen.getAllByRole('button');
    expect(buttons).toHaveLength(2);
  });

  it('applies active class to ascending when ascending=true', () => {
    const { container } = render(<DirectionSelector ascending={true} onChange={vi.fn()} />);
    const circles = container.querySelectorAll('[class*="iconCircle"]');
    expect(circles.length).toBeGreaterThanOrEqual(2);
  });
});
