import { describe, it, expect } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import HeaderProfileButton from '../../../../src/components/shell/desktop/HeaderProfileButton';

describe('HeaderProfileButton', () => {
  it('renders a button', () => {
    render(<HeaderProfileButton hasPlayer={false} onClick={() => {}} />);
    expect(screen.getByRole('button')).toBeDefined();
  });

  it('calls onClick when pressed', () => {
    let clicked = false;
    render(<HeaderProfileButton hasPlayer={false} onClick={() => { clicked = true; }} />);
    fireEvent.click(screen.getByRole('button'));
    expect(clicked).toBe(true);
  });

  it('has an aria-label', () => {
    render(<HeaderProfileButton hasPlayer={true} onClick={() => {}} />);
    expect(screen.getByRole('button').getAttribute('aria-label')).toBeTruthy();
  });
});
