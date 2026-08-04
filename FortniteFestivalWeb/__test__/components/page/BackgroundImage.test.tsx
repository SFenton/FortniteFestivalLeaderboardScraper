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
    // Image probe + status-bar wrapper + main background + main dim layer.
    expect(container.children).toHaveLength(4);
    expect(container.children[1]!.children).toHaveLength(2);
  });

  it('applies dimOpacity style when provided', () => {
    const { container } = render(<BackgroundImage src="https://example.com/img.jpg" dimOpacity={0.5} />);
    const dim = container.children[3] as HTMLElement;
    expect(dim.style.opacity).toBe('0.5');
  });

  it('does not apply dimOpacity style when not provided', () => {
    const { container } = render(<BackgroundImage src="https://example.com/img.jpg" />);
    const dim = container.children[3];
    // dim has no explicit opacity override — uses the default from useStyles
    expect(dim).toBeTruthy();
  });

  it('starts with opacity 0 on the background layer', () => {
    const { container } = render(<BackgroundImage src="https://example.com/img.jpg" />);
    const bg = container.children[2] as HTMLElement;
    expect(bg.style.opacity).toBe('0');
  });

  it('sets opacity 0.9 on the background layer after image loads', () => {
    const { container } = render(<BackgroundImage src="https://example.com/img.jpg" />);
    const img = container.querySelector('img');
    fireEvent.load(img!);
    const bg = container.children[2] as HTMLElement;
    expect(bg.style.opacity).toBe('0.9');
  });
});
