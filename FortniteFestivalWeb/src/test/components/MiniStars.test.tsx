import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import MiniStars from '../../components/songs/metadata/MiniStars';

describe('MiniStars', () => {
  it('renders correct number of stars for 3', () => {
    const { container } = render(<MiniStars starsCount={3} isFullCombo={false} />);
    const imgs = container.querySelectorAll('img');
    expect(imgs).toHaveLength(3);
  });

  it('renders 5 stars for gold (count >= 6)', () => {
    const { container } = render(<MiniStars starsCount={6} isFullCombo={false} />);
    const imgs = container.querySelectorAll('img');
    expect(imgs).toHaveLength(5);
  });

  it('renders 1 star minimum for count=1', () => {
    const { container } = render(<MiniStars starsCount={1} isFullCombo={false} />);
    const imgs = container.querySelectorAll('img');
    expect(imgs).toHaveLength(1);
  });

  it('uses gold star src for 6+ stars', () => {
    const { container } = render(<MiniStars starsCount={6} isFullCombo={false} />);
    const img = container.querySelector('img');
    expect(img?.src).toContain('star_gold');
  });

  it('uses white star src for < 6 stars', () => {
    const { container } = render(<MiniStars starsCount={4} isFullCombo={false} />);
    const img = container.querySelector('img');
    expect(img?.src).toContain('star_white');
  });
});
