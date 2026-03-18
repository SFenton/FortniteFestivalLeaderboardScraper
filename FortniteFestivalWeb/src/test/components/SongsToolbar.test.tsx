import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { SongsToolbar } from '../../pages/songs/components/SongsToolbar';
import type { ServerInstrumentKey } from '@festival/core/api/serverTypes';

describe('SongsToolbar', () => {
  const defaults = {
    search: '',
    onSearchChange: vi.fn(),
    instrument: null as ServerInstrumentKey | null,
    filtersActive: false,
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
    expect(screen.queryByLabelText(/filter/i)).toBeNull();
  });

  it('shows filtered count when filtersActive and counts differ', () => {
    render(<SongsToolbar {...defaults} filtersActive hasPlayer filteredCount={50} totalCount={100} />);
    expect(screen.getByText('50 of 100 songs')).toBeTruthy();
  });

  it('hides count when filtersActive but counts are equal', () => {
    render(<SongsToolbar {...defaults} filtersActive hasPlayer filteredCount={100} totalCount={100} />);
    expect(screen.queryByText(/of.*songs/)).toBeNull();
  });
});

describe('SongsToolbar — instrument & filter branches', () => {
  const baseProps = {
    search: '',
    onSearchChange: vi.fn(),
    instrument: null as ServerInstrumentKey | null,
    filtersActive: false,
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
