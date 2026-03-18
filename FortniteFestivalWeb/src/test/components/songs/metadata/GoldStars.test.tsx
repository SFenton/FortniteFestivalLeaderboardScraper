import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import GoldStars from '../../../../components/songs/metadata/GoldStars';

describe('GoldStars', () => {
  it('renders 5 star images by default', () => {
    const { container } = render(<GoldStars />);
    const imgs = container.querySelectorAll('img');
    expect(imgs).toHaveLength(5);
  });

  it('renders custom count', () => {
    const { container } = render(<GoldStars count={3} />);
    expect(container.querySelectorAll('img')).toHaveLength(3);
  });

  it('uses gold star src', () => {
    const { container } = render(<GoldStars />);
    const img = container.querySelector('img');
    expect(img?.src).toContain('star_gold.png');
  });

  it('applies custom size', () => {
    const { container } = render(<GoldStars size={24} />);
    const img = container.querySelector('img');
    expect(img?.getAttribute('width')).toBe('24');
    expect(img?.getAttribute('height')).toBe('24');
  });
});
