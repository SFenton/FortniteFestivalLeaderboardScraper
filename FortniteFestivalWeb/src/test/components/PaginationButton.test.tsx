/**
 * Tests for simple presentational components at 0% coverage.
 */
import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { PaginationButton } from '../../components/common/PaginationButton';

describe('PaginationButton', () => {
  it('renders children', () => {
    render(<PaginationButton onClick={vi.fn()}>Next</PaginationButton>);
    expect(screen.getByText('Next')).toBeTruthy();
  });

  it('renders enabled button and handles click', () => {
    const onClick = vi.fn();
    render(<PaginationButton onClick={onClick}>Next</PaginationButton>);
    fireEvent.click(screen.getByRole('button'));
    expect(onClick).toHaveBeenCalledTimes(1);
  });

  it('renders disabled button', () => {
    const onClick = vi.fn();
    render(<PaginationButton disabled onClick={onClick}>Next</PaginationButton>);
    const btn = screen.getByRole('button');
    expect(btn).toBeDisabled();
  });
});
