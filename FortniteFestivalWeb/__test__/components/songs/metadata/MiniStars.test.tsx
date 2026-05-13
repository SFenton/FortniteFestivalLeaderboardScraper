import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import { Colors } from '@festival/theme';
import MiniStars from '../../../../src/components/songs/metadata/MiniStars';

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

  it('does not use gold outlines for five-star full combos', () => {
    const { container } = render(<MiniStars starsCount={5} isFullCombo />);
    const imgs = container.querySelectorAll('img');
    const circles = container.querySelectorAll('span span');

    expect(imgs).toHaveLength(5);
    expect(imgs[0]?.src).toContain('star_white');
    expect(circles).toHaveLength(5);
    circles.forEach(circle => {
      expect((circle as HTMLElement).style.border).toContain('transparent');
    });
  });

  it('uses gold outlines for gold stars without requiring a full combo', () => {
    const { container } = render(<MiniStars starsCount={6} isFullCombo={false} />);
    const img = container.querySelector('img');
    const circle = container.querySelector('span span') as HTMLElement | null;
    const expected = document.createElement('span');
    expected.style.color = Colors.gold;

    expect(img?.src).toContain('star_gold');
    expect(circle?.style.borderColor).toBe(expected.style.color);
  });
});
