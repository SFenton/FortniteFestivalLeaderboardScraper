import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, fireEvent, act } from '@testing-library/react';
import React from 'react';
import PlayerSearchBar from '../../../src/components/player/PlayerSearchBar';

vi.mock('../../../src/api/client', () => ({
  api: {
    searchAccounts: vi.fn(),
  },
}));

// Mock useFadeSpinner so spinner.visible tracks active synchronously (no rAF/transitionEnd in jsdom)
vi.mock('../../../src/hooks/ui/useFadeSpinner', () => ({
  useFadeSpinner: (active: boolean) => ({
    visible: active,
    opacity: active ? 1 : 0,
    onTransitionEnd: () => {},
    reset: () => {},
  }),
}));

import { api } from '../../../src/api/client';
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
    fireEvent.focus(input);
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
    fireEvent.focus(input);
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

  it('does not show result buttons when no results', async () => {
    mockSearch.mockResolvedValue({ results: [] });
    const { container } = render(
      React.createElement(PlayerSearchBar, { onSelect: vi.fn() }),
    );
    const input = container.querySelector('input')!;
    fireEvent.focus(input);
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
    fireEvent.focus(input);
    fireEvent.change(input, { target: { value: 'Player' } });
    await act(async () => { vi.advanceTimersByTime(300); });
    await act(async () => { await vi.advanceTimersByTimeAsync(0); });
    // Press ArrowDown to activate first result
    fireEvent.keyDown(input, { key: 'ArrowDown' });
    const buttons = container.querySelectorAll('button');
    // Active result has a background-color style applied
    expect((buttons[0] as HTMLElement).style.backgroundColor).toBeTruthy();
  });
});

describe('PlayerSearchBar — animated dropdown', () => {
  beforeEach(() => { vi.useFakeTimers(); mockSearch.mockReset(); });
  afterEach(() => { vi.useRealTimers(); });

  /** Helper: the dropdown is the first div child after the SearchBar wrapper. */
  const getDropdown = (container: HTMLElement) =>
    container.querySelector('[style*="position: absolute"]') as HTMLElement | null;

  it('dropdown is closed (opacity 0) when input is not focused', () => {
    const { container } = render(
      React.createElement(PlayerSearchBar, { onSelect: vi.fn() }),
    );
    const dropdown = getDropdown(container);
    expect(dropdown).not.toBeNull();
    expect(dropdown!.style.opacity).toBe('0');
  });

  it('dropdown animates open immediately on focus', () => {
    const { container } = render(
      React.createElement(PlayerSearchBar, { onSelect: vi.fn() }),
    );
    const input = container.querySelector('input')!;
    fireEvent.focus(input);

    const dropdown = getDropdown(container)!;
    expect(dropdown.style.opacity).toBe('1');
    expect(dropdown.style.pointerEvents).toBe('auto');
  });

  it('dropdown shows idle hint after grow animation completes', async () => {
    const { container } = render(
      React.createElement(PlayerSearchBar, { onSelect: vi.fn() }),
    );
    const input = container.querySelector('input')!;
    fireEvent.focus(input);

    const dropdown = getDropdown(container)!;
    expect(dropdown.style.opacity).toBe('1');
    // Hint not visible yet — waiting for grow animation
    expect(dropdown.textContent).toBe('');

    // Advance past the dropdown animation delay (300ms)
    await act(async () => { vi.advanceTimersByTime(300); });
    expect(dropdown.textContent).toBeTruthy();
  });

  it('idle hint is cancelled when user starts typing before it appears', async () => {
    const { container } = render(
      React.createElement(PlayerSearchBar, { onSelect: vi.fn() }),
    );
    const input = container.querySelector('input')!;
    fireEvent.focus(input);

    // Advance partway — hint not yet visible
    await act(async () => { vi.advanceTimersByTime(100); });
    const dropdown = getDropdown(container)!;
    expect(dropdown.textContent).toBe('');

    // Start typing — cancels the hint timer and triggers debounce
    fireEvent.change(input, { target: { value: 'Te' } });
    await act(async () => { vi.advanceTimersByTime(300); });
    // Hint should not appear since user is now typing (debouncing/loading)
    expect(dropdown.textContent).not.toContain('Enter a username');
  });

  it('dropdown closes when backdrop is clicked', async () => {
    mockSearch.mockResolvedValue({ results: [{ accountId: 'a1', displayName: 'P' }] });
    const { container } = render(
      React.createElement(PlayerSearchBar, { onSelect: vi.fn() }),
    );
    const input = container.querySelector('input')!;
    fireEvent.focus(input);
    fireEvent.change(input, { target: { value: 'Test' } });
    await act(async () => { vi.advanceTimersByTime(300); });
    await act(async () => { await vi.advanceTimersByTimeAsync(0); });

    // Dropdown is open
    expect(getDropdown(container)!.style.opacity).toBe('1');

    // Click backdrop → dropdown closes
    const backdrop = container.querySelector('[style*="position: fixed"]') as HTMLElement;
    expect(backdrop).not.toBeNull();
    fireEvent.click(backdrop);
    expect(getDropdown(container)!.style.opacity).toBe('0');
  });

  it('renders spinner while debouncing', () => {
    const { container } = render(
      React.createElement(PlayerSearchBar, { onSelect: vi.fn() }),
    );
    const input = container.querySelector('input')!;
    fireEvent.focus(input);
    fireEvent.change(input, { target: { value: 'Test' } });

    // During debounce, spinner should be present
    const spinner = container.querySelector('[data-testid="arc-spinner"]');
    expect(spinner).not.toBeNull();
  });

  it('shows no-results hint when search returns empty', async () => {
    mockSearch.mockResolvedValue({ results: [] });
    const { container } = render(
      React.createElement(PlayerSearchBar, { onSelect: vi.fn() }),
    );
    const input = container.querySelector('input')!;
    fireEvent.focus(input);
    fireEvent.change(input, { target: { value: 'zzz' } });
    await act(async () => { vi.advanceTimersByTime(300); });
    await act(async () => { await vi.advanceTimersByTimeAsync(0); });

    const dropdown = getDropdown(container)!;
    expect(dropdown.textContent).toBeTruthy();
    expect(container.querySelectorAll('button')).toHaveLength(0);
  });

  it('result buttons have stagger animation with resultSeq key', async () => {
    mockSearch.mockResolvedValue({ results: [
      { accountId: 'a1', displayName: 'Player1' },
      { accountId: 'a2', displayName: 'Player2' },
    ] });
    const { container } = render(
      React.createElement(PlayerSearchBar, { onSelect: vi.fn() }),
    );
    const input = container.querySelector('input')!;
    fireEvent.focus(input);
    fireEvent.change(input, { target: { value: 'Player' } });
    await act(async () => { vi.advanceTimersByTime(300); });
    await act(async () => { await vi.advanceTimersByTimeAsync(0); });

    const buttons = container.querySelectorAll('button');
    expect(buttons.length).toBe(2);
    // Each button should have a fadeInUp animation with stagger delay
    const style0 = (buttons[0] as HTMLElement).style;
    const style1 = (buttons[1] as HTMLElement).style;
    expect(style0.animation).toContain('fadeInUp');
    expect(style0.animation).toContain('0ms');
    expect(style1.animation).toContain('fadeInUp');
    expect(style1.animation).toContain('50ms');
  });

  it('backdrop is not rendered when dropdown is closed', () => {
    const { container } = render(
      React.createElement(PlayerSearchBar, { onSelect: vi.fn() }),
    );
    const backdrop = container.querySelector('[style*="position: fixed"]');
    expect(backdrop).toBeNull();
  });

  it('backdrop appears when dropdown is open', () => {
    const { container } = render(
      React.createElement(PlayerSearchBar, { onSelect: vi.fn() }),
    );
    const input = container.querySelector('input')!;
    fireEvent.focus(input);
    const backdrop = container.querySelector('[style*="position: fixed"]');
    expect(backdrop).not.toBeNull();
  });
});
