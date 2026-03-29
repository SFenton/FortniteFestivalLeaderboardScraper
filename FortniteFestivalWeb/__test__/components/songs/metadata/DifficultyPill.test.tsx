import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import DifficultyPill from '../../../../src/components/songs/metadata/DifficultyPill';

describe('DifficultyPill', () => {
  it('renders E for Easy (0)', () => {
    render(<DifficultyPill difficulty={0} />);
    expect(screen.getByText('E')).toBeDefined();
  });

  it('renders M for Medium (1)', () => {
    render(<DifficultyPill difficulty={1} />);
    expect(screen.getByText('M')).toBeDefined();
  });

  it('renders H for Hard (2)', () => {
    render(<DifficultyPill difficulty={2} />);
    expect(screen.getByText('H')).toBeDefined();
  });

  it('renders X for Expert (3)', () => {
    render(<DifficultyPill difficulty={3} />);
    expect(screen.getByText('X')).toBeDefined();
  });

  it('renders ? for unknown difficulty value', () => {
    render(<DifficultyPill difficulty={99} />);
    expect(screen.getByText('?')).toBeDefined();
  });

  it('applies a background color', () => {
    const { container } = render(<DifficultyPill difficulty={0} />);
    const span = container.querySelector('span') as HTMLElement;
    expect(span.style.backgroundColor).toBeTruthy();
  });

  it('has different background colors for each difficulty', () => {
    const colors = [0, 1, 2, 3].map((d) => {
      const { container } = render(<DifficultyPill difficulty={d} />);
      const span = container.querySelector('span') as HTMLElement;
      return span.style.backgroundColor;
    });
    const unique = new Set(colors);
    expect(unique.size).toBe(4);
  });
});
