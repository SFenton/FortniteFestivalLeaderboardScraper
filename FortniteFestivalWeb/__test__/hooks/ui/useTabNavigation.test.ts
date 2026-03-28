import { describe, it, expect, beforeEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import React from 'react';
import { MemoryRouter, useNavigate } from 'react-router-dom';
import { useTabNavigation, inferTab, TAB_ROOTS } from '../../../src/hooks/ui/useTabNavigation';
import { TabKey } from '@festival/core';

function wrapper(route = '/songs') {
  return ({ children }: { children: React.ReactNode }) =>
    React.createElement(MemoryRouter, { initialEntries: [route] }, children);
}

describe('inferTab', () => {
  it('returns songs for /songs', () => expect(inferTab('/songs')).toBe(TabKey.Songs));
  it('returns songs for /songs/detail', () => expect(inferTab('/songs/abc123')).toBe(TabKey.Songs));
  it('returns songs for /shop', () => expect(inferTab('/shop')).toBe(TabKey.Songs));
  it('returns suggestions', () => expect(inferTab('/suggestions')).toBe(TabKey.Suggestions));
  it('returns compete for /rivals', () => expect(inferTab('/rivals')).toBe(TabKey.Compete));
  it('returns compete for /rivals/detail', () => expect(inferTab('/rivals/abc')).toBe(TabKey.Compete));
  it('returns statistics', () => expect(inferTab('/statistics')).toBe(TabKey.Statistics));
  it('returns settings', () => expect(inferTab('/settings')).toBe(TabKey.Settings));
  it('returns null for /player', () => expect(inferTab('/player/abc')).toBeNull());
  it('returns null for /', () => expect(inferTab('/')).toBeNull());
});

describe('TAB_ROOTS', () => {
  it('has correct root paths', () => {
    expect(TAB_ROOTS[TabKey.Songs]).toBe('/songs');
    expect(TAB_ROOTS[TabKey.Settings]).toBe('/settings');
  });
});

describe('useTabNavigation', () => {
  beforeEach(() => { sessionStorage.clear(); });

  it('initializes activeTab from route', () => {
    const { result } = renderHook(() => useTabNavigation(), { wrapper: wrapper('/songs') });
    expect(result.current.activeTab).toBe(TabKey.Songs);
  });

  it('initializes from /settings route', () => {
    const { result } = renderHook(() => useTabNavigation(), { wrapper: wrapper('/settings') });
    expect(result.current.activeTab).toBe(TabKey.Settings);
  });

  it('defaults to Songs for unknown route', () => {
    const { result } = renderHook(() => useTabNavigation(), { wrapper: wrapper('/player/abc') });
    expect(result.current.activeTab).toBe(TabKey.Songs);
  });

  it('handleTabClick switches tab', () => {
    const { result } = renderHook(() => useTabNavigation(), { wrapper: wrapper('/songs') });
    act(() => { result.current.handleTabClick(TabKey.Settings); });
    expect(result.current.activeTab).toBe(TabKey.Settings);
  });

  it('handleTabClick on active tab resets to root', () => {
    const { result } = renderHook(() => useTabNavigation(), { wrapper: wrapper('/songs') });
    act(() => { result.current.handleTabClick(TabKey.Songs); });
    expect(result.current.tabRoutes[TabKey.Songs]).toBe('/songs');
  });

  it('tabRoutes persists to sessionStorage', () => {
    const { result } = renderHook(() => useTabNavigation(), { wrapper: wrapper('/songs') });
    act(() => { result.current.handleTabClick(TabKey.Settings); });
    const stored = sessionStorage.getItem('fst:tabRoutes');
    expect(stored).toBeTruthy();
    expect(JSON.parse(stored!)).toHaveProperty(TabKey.Songs);
  });

  it('Statistics always navigates to root', () => {
    const { result } = renderHook(() => useTabNavigation(), { wrapper: wrapper('/songs') });
    act(() => { result.current.handleTabClick(TabKey.Statistics); });
    expect(result.current.activeTab).toBe(TabKey.Statistics);
    expect(result.current.tabRoutes[TabKey.Statistics]).toBe('/statistics');
  });

  it('loads routes from sessionStorage', () => {
    sessionStorage.setItem('fst:tabRoutes', JSON.stringify({ [TabKey.Settings]: '/settings/debug' }));
    const { result } = renderHook(() => useTabNavigation(), { wrapper: wrapper('/songs') });
    expect(result.current.tabRoutes[TabKey.Settings]).toBe('/settings/debug');
  });

  it('returns tabRoutes with defaults', () => {
    const { result } = renderHook(() => useTabNavigation(), { wrapper: wrapper('/songs') });
    expect(result.current.tabRoutes).toHaveProperty(TabKey.Songs);
    expect(result.current.tabRoutes).toHaveProperty(TabKey.Settings);
  });

  it('gracefully handles corrupted sessionStorage', () => {
    sessionStorage.setItem('fst:tabRoutes', 'not-json');
    const { result } = renderHook(() => useTabNavigation(), { wrapper: wrapper('/songs') });
    expect(result.current.tabRoutes[TabKey.Songs]).toBe('/songs');
  });

  it('re-tap on already-at-root does nothing', () => {
    const { result } = renderHook(() => useTabNavigation(), { wrapper: wrapper('/songs') });
    // Snapshot routes before tap
    act(() => { result.current.handleTabClick(TabKey.Songs); });
    // Still at /songs root, routes unchanged
    expect(result.current.tabRoutes[TabKey.Songs]).toBe('/songs');
  });

  it('switching saves current path as previous tab route', () => {
    const { result } = renderHook(() => useTabNavigation(), { wrapper: wrapper('/songs/detail/123') });
    act(() => { result.current.handleTabClick(TabKey.Settings); });
    // The songs tab should have saved its route
    expect(result.current.tabRoutes[TabKey.Songs]).toBeDefined();
  });

  it('switching from Statistics saves root, not detail', () => {
    const { result } = renderHook(() => useTabNavigation(), { wrapper: wrapper('/statistics') });
    act(() => { result.current.handleTabClick(TabKey.Settings); });
    expect(result.current.tabRoutes[TabKey.Statistics]).toBe('/statistics');
  });

  it('handleTabClick on songs when at /songs/detail navigates to /songs', () => {
    // Helper hook that exposes navigate so we can push programmatically
    function useTestHook() {
      const tab = useTabNavigation();
      const nav = useNavigate();
      return { tab, nav };
    }
    const { result } = renderHook(() => useTestHook(), { wrapper: wrapper('/songs') });
    // Push to detail so we're no longer at root
    act(() => { result.current.nav('/songs/detail/123'); });
    // Re-tap songs → should navigate back to /songs
    act(() => { result.current.tab.handleTabClick(TabKey.Songs); });
    expect(result.current.tab.tabRoutes[TabKey.Songs]).toBe('/songs');
  });

  it('tracks route updates within the active tab on PUSH', () => {
    function useTestHook() {
      const tab = useTabNavigation();
      const nav = useNavigate();
      return { tab, nav };
    }
    const { result } = renderHook(() => useTestHook(), { wrapper: wrapper('/songs') });
    act(() => { result.current.nav('/songs/abc/details'); });
    // The songs tab route should be updated to the new path
    expect(result.current.tab.tabRoutes[TabKey.Songs]).toBe('/songs/abc/details');
  });

  it('PUSH to a different tab updates activeTab', () => {
    function useTestHook() {
      const tab = useTabNavigation();
      const nav = useNavigate();
      return { tab, nav };
    }
    const { result } = renderHook(() => useTestHook(), { wrapper: wrapper('/songs') });
    // PUSH to /settings directly (e.g. from a link)
    act(() => { result.current.nav('/settings'); });
    expect(result.current.tab.activeTab).toBe(TabKey.Settings);
  });

  it('POP navigation to a different tab switches activeTab', () => {
    // Start with history: /songs → /settings; index=1 (at /settings)
    function wrapperWithHistory({ children }: { children: React.ReactNode }) {
      return React.createElement(MemoryRouter, { initialEntries: ['/songs', '/settings'], initialIndex: 1 }, children);
    }
    function useTestHook() {
      const tab = useTabNavigation();
      const nav = useNavigate();
      return { tab, nav };
    }
    const { result } = renderHook(() => useTestHook(), { wrapper: wrapperWithHistory });
    expect(result.current.tab.activeTab).toBe(TabKey.Settings);
    // Simulate browser back (POP to /songs)
    act(() => { result.current.nav(-1); });
    expect(result.current.tab.activeTab).toBe(TabKey.Songs);
  });

  it('resets to tab root when saved route belongs to a different tab', () => {
    // Simulate stale sessionStorage: Suggestions tab has /shop (a Songs route)
    sessionStorage.setItem('fst:tabRoutes', JSON.stringify({ [TabKey.Suggestions]: '/shop' }));
    const { result } = renderHook(() => useTabNavigation(), { wrapper: wrapper('/songs') });
    act(() => { result.current.handleTabClick(TabKey.Suggestions); });
    // Should navigate to /suggestions (tab root), not /shop
    expect(result.current.tabRoutes[TabKey.Suggestions]).toBe('/suggestions');
    expect(result.current.activeTab).toBe(TabKey.Suggestions);
  });

  it('uses rootOverride instead of TAB_ROOTS when provided', () => {
    const { result } = renderHook(() => useTabNavigation(), { wrapper: wrapper('/songs') });
    act(() => { result.current.handleTabClick(TabKey.Compete, '/leaderboards'); });
    expect(result.current.activeTab).toBe(TabKey.Compete);
    expect(result.current.tabRoutes[TabKey.Compete]).toBe('/leaderboards');
  });

  it('rootOverride is used for stale-route guard fallback', () => {
    // Stale sessionStorage: Compete tab has /shop (a Songs route)
    sessionStorage.setItem('fst:tabRoutes', JSON.stringify({ [TabKey.Compete]: '/shop' }));
    const { result } = renderHook(() => useTabNavigation(), { wrapper: wrapper('/songs') });
    act(() => { result.current.handleTabClick(TabKey.Compete, '/leaderboards'); });
    // Should fall back to /leaderboards (the rootOverride), not /compete (TAB_ROOTS)
    expect(result.current.tabRoutes[TabKey.Compete]).toBe('/leaderboards');
    expect(result.current.activeTab).toBe(TabKey.Compete);
  });

  it('re-tap with rootOverride navigates to override root', () => {
    const { result } = renderHook(() => useTabNavigation(), { wrapper: wrapper('/leaderboards') });
    // First switch to Compete tab with override
    act(() => { result.current.handleTabClick(TabKey.Compete, '/leaderboards'); });
    // Now navigate deeper within the tab
    function useTestHook() {
      const tab = useTabNavigation();
      const nav = useNavigate();
      return { tab, nav };
    }
    const { result: result2 } = renderHook(() => useTestHook(), { wrapper: wrapper('/leaderboards') });
    act(() => { result2.current.nav('/leaderboards/all?instrument=guitar'); });
    // Re-tap with override should go to /leaderboards
    act(() => { result2.current.tab.handleTabClick(TabKey.Compete, '/leaderboards'); });
    expect(result2.current.tab.tabRoutes[TabKey.Compete]).toBe('/leaderboards');
  });
});
