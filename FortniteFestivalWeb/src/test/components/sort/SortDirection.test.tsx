import { describe, it, expect } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import SortDirection from '../../../components/sort/SortDirection';

describe('SortDirection', () => {
  it('renders title', () => {
    render(<SortDirection ascending={true} onChange={() => {}} title="Sort Direction" />);
    expect(screen.getByText('Sort Direction')).toBeDefined();
  });

  it('shows ascending label when ascending', () => {
    render(<SortDirection ascending={true} onChange={() => {}} ascLabel="A→Z" descLabel="Z→A" />);
    expect(screen.getByText('A→Z')).toBeDefined();
  });

  it('shows descending label when not ascending', () => {
    render(<SortDirection ascending={false} onChange={() => {}} ascLabel="A→Z" descLabel="Z→A" />);
    expect(screen.getByText('Z→A')).toBeDefined();
  });

  it('calls onChange(true) when ascending button clicked', () => {
    let value: boolean | null = null;
    render(<SortDirection ascending={false} onChange={(v) => { value = v; }} />);
    const buttons = screen.getAllByRole('button');
    // First button is ascending
    fireEvent.click(buttons[0]!);
    expect(value).toBe(true);
  });

  it('calls onChange(false) when descending button clicked', () => {
    let value: boolean | null = null;
    render(<SortDirection ascending={true} onChange={(v) => { value = v; }} />);
    const buttons = screen.getAllByRole('button');
    // Second button is descending
    fireEvent.click(buttons[1]!);
    expect(value).toBe(false);
  });
});
