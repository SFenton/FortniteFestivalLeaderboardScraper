import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, fireEvent, act } from '@testing-library/react';
import React from 'react';
import PlayerSearchBar from '../../components/player/PlayerSearchBar';

vi.mock('../../api/client', () => ({
  api: {
    searchAccounts: vi.fn(),
  },
}));

import { api } from '../../api/client';
const mockSearch = vi.mocked(api.searchAccounts);

describe('PlayerSearchBar', () => {
  beforeEach(() => { vi.useFakeTimers(); mockSearch.mockReset(); });
  afterEach(() => { vi.useRealTimers(); });

  it('renders a search input with placeholder', () => {
    const { container } = render(
      React.createElement(PlayerSearchBar, { onSelect: vi.fn() }),
    );
    const input = container.querySelector('input');
    expect(input).not.toBeNull();
    expect(input?.getAttribute('placeholder')).toBeTruthy();
  });

  it('renders with custom placeholder', () => {
    const { container } = render(
      React.createElement(PlayerSearchBar, { onSelect: vi.fn(), placeholder: 'Find player' }),
    );
    expect(container.querySelector('input')?.getAttribute('placeholder')).toBe('Find player');
  });

  it('shows dropdown results after typing', async () => {
    mockSearch.mockResolvedValue({ results: [{ accountId: 'a1', displayName: 'TestPlayer' }] });
    const onSelect = vi.fn();
    const { container } = render(
      React.createElement(PlayerSearchBar, { onSelect }),
    );
    const input = container.querySelector('input')!;
    fireEvent.change(input, { target: { value: 'Test' } });
    await act(async () => { vi.advanceTimersByTime(300); });
    await act(async () => { await vi.advanceTimersByTimeAsync(0); });
    const buttons = container.querySelectorAll('button');
    expect(buttons.length).toBeGreaterThan(0);
    expect(buttons[0]!.textContent).toBe('TestPlayer');
  });

  it('calls onSelect when a result is clicked', async () => {
    mockSearch.mockResolvedValue({ results: [{ accountId: 'a1', displayName: 'TestPlayer' }] });
    const onSelect = vi.fn();
    const { container } = render(
      React.createElement(PlayerSearchBar, { onSelect }),
    );
    const input = container.querySelector('input')!;
    fireEvent.change(input, { target: { value: 'Test' } });
    await act(async () => { vi.advanceTimersByTime(300); });
    await act(async () => { await vi.advanceTimersByTimeAsync(0); });
    const button = container.querySelector('button')!;
    fireEvent.click(button);
    expect(onSelect).toHaveBeenCalledWith({ accountId: 'a1', displayName: 'TestPlayer' });
  });

  it('applies custom classNames', () => {
    const { container } = render(
      React.createElement(PlayerSearchBar, {
        onSelect: vi.fn(),
        className: 'outer',
        searchClassName: 'bar',
        inputClassName: 'inp',
      }),
    );
    expect(container.firstElementChild?.classList.contains('outer')).toBe(true);
  });

  it('does not show dropdown when no results', async () => {
    mockSearch.mockResolvedValue({ results: [] });
    const { container } = render(
      React.createElement(PlayerSearchBar, { onSelect: vi.fn() }),
    );
    const input = container.querySelector('input')!;
    fireEvent.change(input, { target: { value: 'zzz' } });
    await act(async () => { vi.advanceTimersByTime(300); });
    await act(async () => { await vi.advanceTimersByTimeAsync(0); });
    expect(container.querySelectorAll('button')).toHaveLength(0);
  });

  it('highlights active result on ArrowDown keyboard navigation', async () => {
    mockSearch.mockResolvedValue({ results: [
      { accountId: 'a1', displayName: 'Player1' },
      { accountId: 'a2', displayName: 'Player2' },
    ] });
    const { container } = render(
      React.createElement(PlayerSearchBar, { onSelect: vi.fn() }),
    );
    const input = container.querySelector('input')!;
    fireEvent.change(input, { target: { value: 'Player' } });
    await act(async () => { vi.advanceTimersByTime(300); });
    await act(async () => { await vi.advanceTimersByTimeAsync(0); });
    // Press ArrowDown to activate first result
    fireEvent.keyDown(input, { key: 'ArrowDown' });
    const buttons = container.querySelectorAll('button');
    expect(buttons[0]!.className).toContain('Active');
  });
});
