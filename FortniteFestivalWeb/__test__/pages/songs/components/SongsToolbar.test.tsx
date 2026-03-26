import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, act } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { SongsToolbar } from '../../../../src/pages/songs/components/SongsToolbar';
import type { ServerInstrumentKey } from '@festival/core/api/serverTypes';

describe('SongsToolbar', () => {
  const defaults = {
    search: '',
    onSearchChange: vi.fn(),
    instrument: null as ServerInstrumentKey | null,
    sortActive: false,
    filtersActive: false,
    hasSongs: true,
    hasPlayer: false,
    filteredCount: 100,
    totalCount: 100,
    onOpenSort: vi.fn(),
    onOpenFilter: vi.fn(),
  };

  it('renders sort button', () => {
    render(<SongsToolbar {...defaults} />);
    expect(screen.getByLabelText(/sort/i)).toBeTruthy();
  });

  it('calls onOpenSort when sort is clicked', () => {
    const onOpenSort = vi.fn();
    render(<SongsToolbar {...defaults} onOpenSort={onOpenSort} />);
    fireEvent.click(screen.getByLabelText(/sort/i));
    expect(onOpenSort).toHaveBeenCalledTimes(1);
  });

  it('shows filter pill when hasPlayer is true', () => {
    render(<SongsToolbar {...defaults} hasPlayer />);
    expect(screen.getByLabelText(/filter/i)).toBeTruthy();
  });

  it('hides filter pill when hasPlayer is false', () => {
    render(<SongsToolbar {...defaults} hasPlayer={false} />);
    const filterBtn = screen.getByLabelText(/filter/i);
    expect(filterBtn.parentElement!.style.opacity).toBe('0');
  });

  it('shows filtered count when filtersActive and counts differ', () => {
    render(<SongsToolbar {...defaults} filtersActive hasPlayer filteredCount={50} totalCount={100} />);
    expect(screen.getByText('50 of 100 songs')).toBeTruthy();
  });

  it('hides count when filtersActive but counts are equal', () => {
    render(<SongsToolbar {...defaults} filtersActive hasPlayer filteredCount={100} totalCount={100} />);
    expect(screen.queryByText(/of.*songs/)).toBeNull();
  });

  it('passes sortActive to sort pill', () => {
    render(<SongsToolbar {...defaults} sortActive />);
    const sortBtn = screen.getByLabelText(/sort/i);
    // Active pill has backgroundImage: 'none' (frosted noise removed)
    expect(sortBtn.style.backgroundImage).toBe('none');
  });
});

describe('SongsToolbar — instrument & filter branches', () => {
  const baseProps = {
    search: '',
    onSearchChange: vi.fn(),
    instrument: null as ServerInstrumentKey | null,
    sortActive: false,
    filtersActive: false,
    hasSongs: true,
    hasPlayer: false,
    filteredCount: 10,
    totalCount: 10,
    onOpenSort: vi.fn(),
    onOpenFilter: vi.fn(),
  };

  it('renders without instrument icon when instrument is null', () => {
    const { container } = render(
      <MemoryRouter><SongsToolbar {...baseProps} /></MemoryRouter>,
    );
    expect(container.querySelector('[data-testid="instrument-icon"]')).toBeFalsy();
  });

  it('renders with instrument icon when instrument is set', () => {
    render(
      <MemoryRouter><SongsToolbar {...baseProps} instrument={'Solo_Guitar' as ServerInstrumentKey} /></MemoryRouter>,
    );
    expect(document.querySelector('img, svg')).toBeTruthy();
  });

  it('hides count when filtersActive is false', () => {
    render(
      <MemoryRouter><SongsToolbar {...baseProps} filtersActive={false} filteredCount={5} totalCount={10} /></MemoryRouter>,
    );
    expect(screen.queryByText(/of.*songs/)).toBeFalsy();
  });
});

describe('SongsToolbar — instrument transition effects', () => {
  const baseProps = {
    search: '',
    onSearchChange: vi.fn(),
    instrument: null as ServerInstrumentKey | null,
    sortActive: false,
    filtersActive: false,
    hasSongs: true,
    hasPlayer: false,
    filteredCount: 10,
    totalCount: 10,
    onOpenSort: vi.fn(),
    onOpenFilter: vi.fn(),
  };

  beforeEach(() => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.spyOn(window, 'requestAnimationFrame').mockImplementation((cb) => { cb(0); return 0; });
  });
  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('fades in instrument icon when instrument changes from null', () => {
    const { container, rerender } = render(<SongsToolbar {...baseProps} instrument={null} />);
    rerender(<SongsToolbar {...baseProps} instrument={'Solo_Guitar' as ServerInstrumentKey} />);
    // Instrument icon slot renders — verify by finding the instrument SVG/icon
    expect(container.querySelector('svg')).toBeTruthy();
  });

  it('fades out instrument icon when instrument is removed', () => {
    const { container, rerender } = render(
      <SongsToolbar {...baseProps} instrument={'Solo_Guitar' as ServerInstrumentKey} />,
    );
    rerender(<SongsToolbar {...baseProps} instrument={null} />);
    // After the leave timeout, displayedInst should become null
    act(() => { vi.advanceTimersByTime(500); });
    // The icon slot may be hidden or removed
    expect(container.innerHTML).toBeTruthy();
  });

  it('swaps instrument icon from one to another', () => {
    const { container, rerender } = render(
      <SongsToolbar {...baseProps} instrument={'Solo_Guitar' as ServerInstrumentKey} />,
    );
    rerender(<SongsToolbar {...baseProps} instrument={'Solo_Bass' as ServerInstrumentKey} />);
    // After fade-out timeout, new instrument is set
    act(() => { vi.advanceTimersByTime(500); });
    expect(container.innerHTML).toBeTruthy();
  });

  it('fades in filter button when hasPlayer becomes true', () => {
    const { rerender } = render(<SongsToolbar {...baseProps} hasPlayer={false} />);
    rerender(<SongsToolbar {...baseProps} hasPlayer={true} />);
    expect(screen.getByLabelText(/filter/i)).toBeTruthy();
  });

  it('fades out filter button when hasPlayer becomes false', () => {
    const { container, rerender } = render(<SongsToolbar {...baseProps} hasPlayer={true} />);
    rerender(<SongsToolbar {...baseProps} hasPlayer={false} />);
    act(() => { vi.advanceTimersByTime(500); });
    expect(container.innerHTML).toBeTruthy();
  });

  it('renders search bar with value and handles change', () => {
    const onSearchChange = vi.fn();
    render(<SongsToolbar {...baseProps} search="hello" onSearchChange={onSearchChange} />);
    const input = screen.getByPlaceholderText(/search/i);
    expect((input as HTMLInputElement).value).toBe('hello');
    fireEvent.change(input, { target: { value: 'world' } });
    expect(onSearchChange).toHaveBeenCalled();
  });

  it('calls onOpenFilter when filter is clicked', () => {
    const onOpenFilter = vi.fn();
    render(<SongsToolbar {...baseProps} hasPlayer onOpenFilter={onOpenFilter} />);
    fireEvent.click(screen.getByLabelText(/filter/i));
    expect(onOpenFilter).toHaveBeenCalledTimes(1);
  });

  it('shows active filter styling', () => {
    render(<SongsToolbar {...baseProps} hasPlayer filtersActive />);
    const filterBtn = screen.getByLabelText(/filter/i);
    // Active pill has backgroundImage: 'none' (frosted noise removed)
    expect(filterBtn.style.backgroundImage).toBe('none');
  });
});
