/**
 * Tests for filter toggle components.
 * Exercises toggle, selectAll, and clearAll callbacks.
 */
import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { DifficultyToggles } from '../../../../../src/pages/songs/modals/components/filters/DifficultyToggles';
import { PercentileToggles } from '../../../../../src/pages/songs/modals/components/filters/PercentileToggles';
import { StarsToggles } from '../../../../../src/pages/songs/modals/components/filters/StarsToggles';

// SeasonToggles needs FestivalContext for useAvailableSeasons
vi.mock('../../../../../src/hooks/data/useAvailableSeasons', () => ({
  useAvailableSeasons: () => [1, 2, 3, 4, 5],
}));
import { SeasonToggles } from '../../../../../src/pages/songs/modals/components/filters/SeasonToggles';

describe('DifficultyToggles', () => {
  it('renders all difficulty rows', () => {
    const onChange = vi.fn();
    render(<DifficultyToggles difficultyFilter={{}} onChange={onChange} />);
    // Should render toggle rows
    const toggles = screen.getAllByRole('button');
    expect(toggles.length).toBeGreaterThanOrEqual(8); // 8 difficulties + 2 bulk
  });

  it('calls onChange on toggle click', () => {
    const onChange = vi.fn();
    render(<DifficultyToggles difficultyFilter={{}} onChange={onChange} />);
    const toggles = screen.getAllByRole('button');
    // Click first toggle row (after the 2 bulk action buttons)
    fireEvent.click(toggles[2]!);
    expect(onChange).toHaveBeenCalled();
  });

  it('selectAll sets all keys to true', () => {
    const onChange = vi.fn();
    render(<DifficultyToggles difficultyFilter={{}} onChange={onChange} />);
    // First bulk button is selectAll
    fireEvent.click(screen.getAllByRole('button')[0]!);
    expect(onChange).toHaveBeenCalled();
    const arg = onChange.mock.calls[0]![0] as Record<number, boolean>;
    expect(Object.values(arg).every(v => v === true)).toBe(true);
  });

  it('clearAll sets all keys to false', () => {
    const onChange = vi.fn();
    render(<DifficultyToggles difficultyFilter={{}} onChange={onChange} />);
    // Second bulk button is clearAll
    fireEvent.click(screen.getAllByRole('button')[1]!);
    expect(onChange).toHaveBeenCalled();
    const arg = onChange.mock.calls[0]![0] as Record<number, boolean>;
    expect(Object.values(arg).every(v => v === false)).toBe(true);
  });
});

describe('PercentileToggles', () => {
  it('renders toggle rows', () => {
    render(<PercentileToggles percentileFilter={{}} onChange={vi.fn()} />);
    const toggles = screen.getAllByRole('button');
    expect(toggles.length).toBeGreaterThanOrEqual(3);
  });

  it('toggle calls onChange with toggled value', () => {
    const onChange = vi.fn();
    render(<PercentileToggles percentileFilter={{}} onChange={onChange} />);
    const toggles = screen.getAllByRole('button');
    fireEvent.click(toggles[2]!); // First toggle after bulk
    expect(onChange).toHaveBeenCalled();
  });

  it('selectAll and clearAll work', () => {
    const onChange = vi.fn();
    render(<PercentileToggles percentileFilter={{}} onChange={onChange} />);
    const buttons = screen.getAllByRole('button');
    fireEvent.click(buttons[0]!); // selectAll
    expect(onChange).toHaveBeenCalledTimes(1);
    fireEvent.click(buttons[1]!); // clearAll
    expect(onChange).toHaveBeenCalledTimes(2);
  });
});

describe('SeasonToggles', () => {
  it('renders toggle rows for each season', () => {
    render(<SeasonToggles seasonFilter={{}} onChange={vi.fn()} />);
    const toggles = screen.getAllByRole('button');
    expect(toggles.length).toBeGreaterThanOrEqual(3);
  });

  it('toggle calls onChange', () => {
    const onChange = vi.fn();
    render(<SeasonToggles seasonFilter={{}} onChange={onChange} />);
    const toggles = screen.getAllByRole('button');
    fireEvent.click(toggles[2]!);
    expect(onChange).toHaveBeenCalled();
  });

  it('selectAll and clearAll work', () => {
    const onChange = vi.fn();
    render(<SeasonToggles seasonFilter={{}} onChange={onChange} />);
    const buttons = screen.getAllByRole('button');
    fireEvent.click(buttons[0]!);
    expect(onChange).toHaveBeenCalledTimes(1);
    fireEvent.click(buttons[1]!);
    expect(onChange).toHaveBeenCalledTimes(2);
  });
});

describe('StarsToggles', () => {
  it('renders toggle rows for stars', () => {
    render(<StarsToggles starsFilter={{}} onChange={vi.fn()} />);
    const toggles = screen.getAllByRole('button');
    expect(toggles.length).toBeGreaterThanOrEqual(3);
  });

  it('toggle calls onChange', () => {
    const onChange = vi.fn();
    render(<StarsToggles starsFilter={{}} onChange={onChange} />);
    const toggles = screen.getAllByRole('button');
    fireEvent.click(toggles[2]!);
    expect(onChange).toHaveBeenCalled();
  });

  it('selectAll and clearAll work', () => {
    const onChange = vi.fn();
    render(<StarsToggles starsFilter={{}} onChange={onChange} />);
    const buttons = screen.getAllByRole('button');
    fireEvent.click(buttons[0]!);
    expect(onChange).toHaveBeenCalledTimes(1);
    fireEvent.click(buttons[1]!);
    expect(onChange).toHaveBeenCalledTimes(2);
  });
});
