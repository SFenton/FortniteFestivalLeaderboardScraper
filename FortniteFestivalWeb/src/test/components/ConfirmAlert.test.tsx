import { describe, it, expect } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import ConfirmAlert from '../../components/modals/ConfirmAlert';

type ConfirmAlertProps = { title: string; message: string; onNo: () => void; onYes: () => void };

describe('ConfirmAlert', () => {
  const defaultProps: ConfirmAlertProps = {
    title: 'Confirm Action',
    message: 'Are you sure?',
    onNo: () => {},
    onYes: () => {},
  };

  it('renders title and message', () => {
    render(<ConfirmAlert {...defaultProps} />);
    expect(screen.getByText('Confirm Action')).toBeDefined();
    expect(screen.getByText('Are you sure?')).toBeDefined();
  });

  it('renders Yes and No buttons', () => {
    render(<ConfirmAlert {...defaultProps} />);
    expect(screen.getByText('Yes')).toBeDefined();
    expect(screen.getByText('No')).toBeDefined();
  });

  it('calls onNo when No is clicked', () => {
    let called = false;
    render(<ConfirmAlert {...defaultProps} onNo={() => { called = true; }} />);
    fireEvent.click(screen.getByText('No'));
    expect(called).toBe(true);
  });

  it('calls onYes when Yes is clicked', () => {
    let called = false;
    render(<ConfirmAlert {...defaultProps} onYes={() => { called = true; }} />);
    fireEvent.click(screen.getByText('Yes'));
    expect(called).toBe(true);
  });

  it('calls onNo when Escape is pressed', () => {
    let called = false;
    render(<ConfirmAlert {...defaultProps} onNo={() => { called = true; }} />);
    fireEvent.keyDown(document, { key: 'Escape' });
    expect(called).toBe(true);
  });
});
