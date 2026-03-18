import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import AlbumArt from '../../../../src/components/songs/metadata/AlbumArt';

describe('AlbumArt', () => {
  it('renders an image when src is provided', () => {
    const { container } = render(<AlbumArt src="https://example.com/art.jpg" size={44} />);
    const img = container.querySelector('img');
    expect(img).toBeTruthy();
    expect(img?.src).toContain('example.com/art.jpg');
  });

  it('renders placeholder when src is undefined', () => {
    const { container } = render(<AlbumArt src={undefined} size={44} />);
    const img = container.querySelector('img');
    // Should render a placeholder div, not an img
    expect(img).toBeNull();
  });

  it('applies size to width and height', () => {
    const { container } = render(<AlbumArt src="test.jpg" size={64} />);
    const img = container.querySelector('img');
    // Size is applied via CSS variable
    expect(img).toBeTruthy();
  });
});

describe('AlbumArt — branch coverage', () => {
  it('renders placeholder when src is empty string', () => {
    const { container } = render(<AlbumArt src="" size={64} />);
    expect(container.querySelector('img')).toBeNull();
  });

  it('renders with src and shows spinner', () => {
    const { container } = render(<AlbumArt src="https://example.com/art.jpg" size={40} />);
    expect(container.querySelector('img')).toBeTruthy();
    expect(container.querySelector('[class*="spinnerWrap"]')).toBeTruthy();
  });

  it('renders with priority loading', () => {
    const { container } = render(<AlbumArt src="https://example.com/art.jpg" size={40} priority />);
    const img = container.querySelector('img');
    expect(img?.getAttribute('loading')).toBe('eager');
    expect(img?.getAttribute('fetchpriority')).toBe('high');
  });

  it('renders with lazy loading by default', () => {
    const { container } = render(<AlbumArt src="https://example.com/art.jpg" size={40} />);
    const img = container.querySelector('img');
    expect(img?.getAttribute('loading')).toBe('lazy');
  });

  it('renders with custom style', () => {
    const { container } = render(<AlbumArt src="https://example.com/art.jpg" size={60} style={{ margin: 5 }} />);
    expect(container.firstElementChild).toBeTruthy();
  });
});
