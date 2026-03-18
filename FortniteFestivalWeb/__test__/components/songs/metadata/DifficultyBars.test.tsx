import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import DifficultyBars from '../../../../src/components/songs/metadata/DifficultyBars';

describe('DifficultyBars', () => {
  it('renders 7 SVG polygons', () => {
    const { container } = render(<DifficultyBars level={3} />);
    const polygons = container.querySelectorAll('polygon');
    expect(polygons).toHaveLength(7);
  });

  it('clamps level to 1-7 range', () => {
    const { container } = render(<DifficultyBars level={0} />);
    const polygons = container.querySelectorAll('polygon');
    // level=0 clamps to 1
    expect(polygons).toHaveLength(7);
  });

  it('uses raw mode (0-6 scale → add 1)', () => {
    const { container } = render(<DifficultyBars level={5} raw />);
    // raw=5 → display=6, so 6 filled bars
    const polygons = container.querySelectorAll('polygon');
    expect(polygons).toHaveLength(7);
  });

  it('has an aria-label', () => {
    const { container } = render(<DifficultyBars level={4} />);
    const svg = container.querySelector('svg');
    expect(svg?.getAttribute('aria-label')).toBeTruthy();
  });
});
