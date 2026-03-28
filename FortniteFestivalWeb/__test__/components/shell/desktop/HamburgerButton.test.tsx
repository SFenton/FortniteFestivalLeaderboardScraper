import { describe, it, expect } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import HamburgerButton from '../../../../src/components/shell/HamburgerButton';

describe('HamburgerButton', () => {
  it('renders a button with an icon', () => {
    const { container } = render(<HamburgerButton onClick={() => {}} />);
    const svg = container.querySelector('svg');
    expect(svg).toBeTruthy();
  });

  it('calls onClick when pressed', () => {
    let clicked = false;
    render(<HamburgerButton onClick={() => { clicked = true; }} />);
    fireEvent.click(screen.getByRole('button'));
    expect(clicked).toBe(true);
  });

  it('has an aria-label', () => {
    render(<HamburgerButton onClick={() => {}} />);
    expect(screen.getByRole('button').getAttribute('aria-label')).toBeTruthy();
  });
});
