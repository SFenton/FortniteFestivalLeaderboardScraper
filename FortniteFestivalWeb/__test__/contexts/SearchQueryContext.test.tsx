import { describe, it, expect } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import type { ReactNode } from 'react';
import { SearchQueryProvider, useSearchQuery } from '../../src/contexts/SearchQueryContext';

function wrapper({ children }: { children: ReactNode }) {
  return <SearchQueryProvider>{children}</SearchQueryProvider>;
}

describe('SearchQueryContext', () => {
  it('starts with empty query', () => {
    const { result } = renderHook(() => useSearchQuery(), { wrapper });
    expect(result.current.query).toBe('');
  });

  it('updates query', () => {
    const { result } = renderHook(() => useSearchQuery(), { wrapper });
    act(() => { result.current.setQuery('test search'); });
    expect(result.current.query).toBe('test search');
  });

  it('clears query', () => {
    const { result } = renderHook(() => useSearchQuery(), { wrapper });
    act(() => { result.current.setQuery('hello'); });
    act(() => { result.current.setQuery(''); });
    expect(result.current.query).toBe('');
  });
});
