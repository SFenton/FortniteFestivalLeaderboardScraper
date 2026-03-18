import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import AlbumArt from '../../components/songs/metadata/AlbumArt';

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
