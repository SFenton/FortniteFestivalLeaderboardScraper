import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';

const mockNavigate = vi.fn();
vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal() as Record<string, unknown>;
  return { ...actual, useNavigate: () => mockNavigate };
});

const mockHandleChange = vi.fn();
const mockHandleKeyDown = vi.fn();
const mockSetActiveIndex = vi.fn();
let _onSelect: ((r: { accountId: string }) => void) | null = null;

vi.mock('../../hooks/data/useAccountSearch', () => ({
  useAccountSearch: (onSelect: (r: { accountId: string }) => void) => {
    _onSelect = onSelect;
    return {
      query: mockState.query,
      setQuery: vi.fn(),
      results: mockState.results,
      isOpen: mockState.isOpen,
      activeIndex: mockState.activeIndex,
      setActiveIndex: mockSetActiveIndex,
      loading: false,
      handleChange: mockHandleChange,
      handleKeyDown: mockHandleKeyDown,
      selectResult: (r: { accountId: string }) => { _onSelect?.(r); },
      close: vi.fn(),
      containerRef: { current: null },
    };
  },
}));

let mockState = {
  query: '' as string,
  results: [] as { accountId: string; displayName: string }[],
  isOpen: false,
  activeIndex: -1,
};

import HeaderSearch from '../../components/shell/desktop/HeaderSearch';

describe('HeaderSearch', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    _onSelect = null;
    mockState = { query: '', results: [], isOpen: false, activeIndex: -1 };
  });

  it('renders search bar', () => {
    render(<MemoryRouter><HeaderSearch /></MemoryRouter>);
    expect(screen.getByPlaceholderText(/search player/i)).toBeTruthy();
  });

  it('shows dropdown results when isOpen is true', () => {
    mockState = {
      query: 'Test',
      results: [
        { accountId: 'acc-1', displayName: 'TestPlayer' },
        { accountId: 'acc-2', displayName: 'AnotherPlayer' },
      ],
      isOpen: true,
      activeIndex: -1,
    };
    render(<MemoryRouter><HeaderSearch /></MemoryRouter>);
    expect(screen.getByText('TestPlayer')).toBeTruthy();
    expect(screen.getByText('AnotherPlayer')).toBeTruthy();
  });

  it('navigates to player page on result click', () => {
    mockState = {
      query: 'Test',
      results: [{ accountId: 'acc-1', displayName: 'TestPlayer' }],
      isOpen: true,
      activeIndex: -1,
    };
    render(<MemoryRouter><HeaderSearch /></MemoryRouter>);
    fireEvent.click(screen.getByText('TestPlayer'));
    expect(mockNavigate).toHaveBeenCalledWith('/player/acc-1');
  });

  it('hides dropdown when isOpen is false', () => {
    mockState = { query: '', results: [], isOpen: false, activeIndex: -1 };
    render(<MemoryRouter><HeaderSearch /></MemoryRouter>);
    expect(screen.queryByText('TestPlayer')).toBeNull();
  });

  it('calls setActiveIndex on mouse enter', () => {
    mockState = {
      query: 'Test',
      results: [
        { accountId: 'acc-1', displayName: 'TestPlayer' },
        { accountId: 'acc-2', displayName: 'AnotherPlayer' },
      ],
      isOpen: true,
      activeIndex: -1,
    };
    render(<MemoryRouter><HeaderSearch /></MemoryRouter>);
    fireEvent.mouseEnter(screen.getByText('AnotherPlayer'));
    expect(mockSetActiveIndex).toHaveBeenCalledWith(1);
  });

  it('calls handleChange on focus when results exist but closed', () => {
    mockState = {
      query: 'Test',
      results: [{ accountId: 'acc-1', displayName: 'TestPlayer' }],
      isOpen: false,
      activeIndex: -1,
    };
    render(<MemoryRouter><HeaderSearch /></MemoryRouter>);
    fireEvent.focus(screen.getByPlaceholderText(/search player/i));
    expect(mockHandleChange).toHaveBeenCalledWith('Test');
  });

  it('does not call handleChange on focus when no results', () => {
    mockState = { query: 'Test', results: [], isOpen: false, activeIndex: -1 };
    render(<MemoryRouter><HeaderSearch /></MemoryRouter>);
    fireEvent.focus(screen.getByPlaceholderText(/search player/i));
    expect(mockHandleChange).not.toHaveBeenCalled();
  });

  it('does not call handleChange on focus when already open', () => {
    mockState = {
      query: 'Test',
      results: [{ accountId: 'acc-1', displayName: 'TestPlayer' }],
      isOpen: true,
      activeIndex: -1,
    };
    render(<MemoryRouter><HeaderSearch /></MemoryRouter>);
    fireEvent.focus(screen.getByPlaceholderText(/search player/i));
    expect(mockHandleChange).not.toHaveBeenCalled();
  });

  it('highlights the active result', () => {
    mockState = {
      query: 'Test',
      results: [
        { accountId: 'acc-1', displayName: 'Player1' },
        { accountId: 'acc-2', displayName: 'Player2' },
      ],
      isOpen: true,
      activeIndex: 0,
    };
    render(<MemoryRouter><HeaderSearch /></MemoryRouter>);
    // First result should have active class
    const buttons = screen.getAllByRole('button');
    expect(buttons[0]?.className).toContain('Active');
  });
});
