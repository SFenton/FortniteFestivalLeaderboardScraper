import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import HamburgerButton from '../../../../src/components/shell/HamburgerButton';
import { GeneralSize } from '@festival/theme';

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

  it('commits touch presses on pointerup and suppresses the following click', () => {
    const onClick = vi.fn();
    render(<HamburgerButton onClick={onClick} />);
    const button = screen.getByRole('button');

    fireEvent.pointerDown(button, { pointerId: 1, pointerType: 'touch', button: 0, clientX: 32, clientY: 104 });
    fireEvent.pointerUp(button, { pointerId: 1, pointerType: 'touch', button: 0, clientX: 33, clientY: 104 });

    expect(onClick).toHaveBeenCalledTimes(1);

    fireEvent.click(button);
    expect(onClick).toHaveBeenCalledTimes(1);
  });

  it('has an aria-label', () => {
    render(<HamburgerButton onClick={() => {}} />);
    expect(screen.getByRole('button').getAttribute('aria-label')).toBeTruthy();
  });

  it('uses a thumb-sized touch target', () => {
    render(<HamburgerButton onClick={() => {}} />);
    const button = screen.getByRole('button');
    expect(button.style.width).toBe(`${GeneralSize.thumb}px`);
    expect(button.style.height).toBe(`${GeneralSize.thumb}px`);
  });
});
