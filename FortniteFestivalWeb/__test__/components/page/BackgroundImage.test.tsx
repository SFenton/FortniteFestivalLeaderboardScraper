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
    const bg = container.querySelector('[class*="bg"]');
    const dim = container.querySelector('[class*="dim"]');
    expect(bg).toBeTruthy();
    expect(dim).toBeTruthy();
  });

  it('applies dimOpacity style when provided', () => {
    const { container } = render(<BackgroundImage src="https://example.com/img.jpg" dimOpacity={0.5} />);
    const dim = container.querySelector('[class*="dim"]');
    expect(dim).toBeTruthy();
    expect((dim as HTMLElement).style.opacity).toBe('0.5');
  });

  it('does not apply dimOpacity style when not provided', () => {
    const { container } = render(<BackgroundImage src="https://example.com/img.jpg" />);
    const dim = container.querySelector('[class*="dim"]');
    expect(dim).toBeTruthy();
    expect((dim as HTMLElement).style.opacity).toBe('');
  });

  it('starts with opacity 0 on the background layer', () => {
    const { container } = render(<BackgroundImage src="https://example.com/img.jpg" />);
    const bg = container.querySelector('[class*="bg"]');
    expect((bg as HTMLElement).style.opacity).toBe('0');
  });

  it('sets opacity 0.9 on the background layer after image loads', () => {
    const { container } = render(<BackgroundImage src="https://example.com/img.jpg" />);
    const img = container.querySelector('img');
    fireEvent.load(img!);
    const bg = container.querySelector('[class*="bg"]');
    expect((bg as HTMLElement).style.opacity).toBe('0.9');
  });
});
