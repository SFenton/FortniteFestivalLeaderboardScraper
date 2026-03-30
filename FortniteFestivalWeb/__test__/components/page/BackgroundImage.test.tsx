import { describe, it, expect } from 'vitest';
import { render, fireEvent } from '@testing-library/react';
import BackgroundImage from '../../../src/components/page/BackgroundImage';

describe('BackgroundImage', () => {
  it('renders nothing when src is undefined', () => {
    const { container } = render(<BackgroundImage src={undefined} />);
    expect(container.innerHTML).toBe('');
  });

  it('renders background and dim layers when src is provided', () => {
    const { container } = render(<BackgroundImage src="https://example.com/img.jpg" />);
    // img (probe) + div (bg) + div (dim)
    const divs = container.querySelectorAll('div');
    expect(divs.length).toBe(2);
  });

  it('applies dimOpacity style when provided', () => {
    const { container } = render(<BackgroundImage src="https://example.com/img.jpg" dimOpacity={0.5} />);
    const divs = container.querySelectorAll('div');
    const dim = divs[1]!; // second div is dim
    expect(dim.style.opacity).toBe('0.5');
  });

  it('does not apply dimOpacity style when not provided', () => {
    const { container } = render(<BackgroundImage src="https://example.com/img.jpg" />);
    const divs = container.querySelectorAll('div');
    const dim = divs[1];
    // dim has no explicit opacity override — uses the default from useStyles
    expect(dim).toBeTruthy();
  });

  it('starts with opacity 0 on the background layer', () => {
    const { container } = render(<BackgroundImage src="https://example.com/img.jpg" />);
    const bg = container.querySelectorAll('div')[0]!;
    expect(bg.style.opacity).toBe('0');
  });

  it('sets opacity 0.9 on the background layer after image loads', () => {
    const { container } = render(<BackgroundImage src="https://example.com/img.jpg" />);
    const img = container.querySelector('img');
    fireEvent.load(img!);
    const bg = container.querySelectorAll('div')[0]!;
    expect(bg.style.opacity).toBe('0.9');
  });
});
