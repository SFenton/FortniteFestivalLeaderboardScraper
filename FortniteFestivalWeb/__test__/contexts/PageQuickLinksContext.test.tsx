import { describe, it, expect, vi } from 'vitest';
import { act, renderHook } from '@testing-library/react';
import type { ReactNode } from 'react';
import { PageQuickLinksProvider, usePageQuickLinksController } from '../../src/contexts/PageQuickLinksContext';

function wrapper({ children }: { children: ReactNode }) {
  return <PageQuickLinksProvider>{children}</PageQuickLinksProvider>;
}

describe('PageQuickLinksContext', () => {
  it('provides default no-op functions', () => {
    const { result } = renderHook(() => usePageQuickLinksController(), { wrapper });
    expect(result.current.pageQuickLinks).toBeNull();
    expect(result.current.hasPageQuickLinks).toBe(false);
    expect(typeof result.current.registerPageQuickLinks).toBe('function');
    expect(typeof result.current.openPageQuickLinks).toBe('function');
  });

  it('registers and opens page quick links', () => {
    const { result } = renderHook(() => usePageQuickLinksController(), { wrapper });
    const openQuickLinks = vi.fn();

    act(() => {
      result.current.registerPageQuickLinks({ title: 'Quick Links', openQuickLinks });
    });

    expect(result.current.hasPageQuickLinks).toBe(true);
    expect(result.current.pageQuickLinks?.title).toBe('Quick Links');

    act(() => {
      result.current.openPageQuickLinks();
    });

    expect(openQuickLinks).toHaveBeenCalledTimes(1);
  });

  it('clears registration when null is provided', () => {
    const { result } = renderHook(() => usePageQuickLinksController(), { wrapper });

    act(() => {
      result.current.registerPageQuickLinks({ title: 'Quick Links', openQuickLinks: () => {} });
    });
    expect(result.current.hasPageQuickLinks).toBe(true);

    act(() => {
      result.current.registerPageQuickLinks(null);
    });

    expect(result.current.hasPageQuickLinks).toBe(false);
    expect(result.current.pageQuickLinks).toBeNull();
  });
});