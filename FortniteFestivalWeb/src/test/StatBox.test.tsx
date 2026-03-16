import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import StatBox from '../components/player/StatBox';

describe('StatBox', () => {
  it('renders label and value', () => {
    render(<StatBox label="Songs Played" value="42" />);
    expect(screen.getByText('Songs Played')).toBeDefined();
    expect(screen.getByText('42')).toBeDefined();
  });

  it('applies custom color to value', () => {
    const { container } = render(<StatBox label="Accuracy" value="95%" color="rgb(46,204,113)" />);
    const value = container.querySelector('span');
    expect(value?.style.color).toBe('rgb(46, 204, 113)');
  });

  it('renders clickable wrapper with chevron when onClick provided', () => {
    const onClick = vi.fn();
    const { container } = render(<StatBox label="Songs" value="10" onClick={onClick} />);

    // Should have an SVG chevron
    expect(container.querySelector('svg')).toBeDefined();

    // Click should trigger the handler
    fireEvent.click(container.firstElementChild!);
    expect(onClick).toHaveBeenCalledOnce();
  });

  it('does not render chevron when onClick is not provided', () => {
    const { container } = render(<StatBox label="Score" value="1000" />);
    expect(container.querySelector('svg')).toBeNull();
  });

  it('renders ReactNode values', () => {
    render(<StatBox label="Stars" value={<span data-testid="stars">★★★</span>} />);
    expect(screen.getByTestId('stars')).toBeDefined();
  });
});
