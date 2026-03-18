/**
 * Hook that manages per-tab route memory for the mobile bottom nav.
 * Persists tab routes to sessionStorage so they survive page refreshes.
 */
import { useState, useEffect, useRef, useCallback, useMemo } from 'react';
import { useLocation, useNavigate, useNavigationType } from 'react-router-dom';
import { TabKey } from '@festival/core';
export type { TabKey };

export const TAB_ROOTS: Record<TabKey, string> = {
  [TabKey.Songs]: '/songs',
  [TabKey.Suggestions]: '/suggestions',
  [TabKey.Statistics]: '/statistics',
  [TabKey.Settings]: '/settings',
};

const STORAGE_KEY = 'fst:tabRoutes';

/** Infer which tab owns a route. Detail pages under /songs belong to songs; /player belongs to the active tab. */
export function inferTab(pathname: string): TabKey | null {
  if (pathname === '/songs' || pathname.startsWith('/songs/')) return TabKey.Songs;
  if (pathname === '/suggestions') return TabKey.Suggestions;
  if (pathname === '/statistics') return TabKey.Statistics;
  if (pathname === '/settings') return TabKey.Settings;
  return null; // /player/:id — ambiguous, owned by the currently active tab
}

function loadTabRoutes(): Record<TabKey, string> {
  try {
    const raw = sessionStorage.getItem(STORAGE_KEY);
    if (raw) {
      const parsed = JSON.parse(raw);
      return { ...TAB_ROOTS, ...parsed };
    }
  } catch { /* ignore */ }
  return { ...TAB_ROOTS };
}

function saveTabRoutes(routes: Record<TabKey, string>) {
  try {
    sessionStorage.setItem(STORAGE_KEY, JSON.stringify(routes));
  } catch { /* ignore */ }
}

export function useTabNavigation() {
  const location = useLocation();
  const navigate = useNavigate();
  const navType = useNavigationType();

  const [activeTab, setActiveTab] = useState<TabKey>(
    () => inferTab(location.pathname) ?? TabKey.Songs,
  );
  const [tabRoutes, setTabRoutes] = useState<Record<TabKey, string>>(loadTabRoutes);

  // Persist to sessionStorage on change
  useEffect(() => {
    saveTabRoutes(tabRoutes);
  }, [tabRoutes]);

  // Keep tabRoutes in sync as user navigates
  const prevPathRef = useRef(location.pathname);
  useEffect(() => {
    if (location.pathname === prevPathRef.current) return;
    const previousPath = prevPathRef.current;
    prevPathRef.current = location.pathname;

    // On POP navigation, check if we landed on a route that belongs to a different tab
    if (navType === 'POP') {
      const landedTab = inferTab(location.pathname);
      if (landedTab && landedTab !== activeTab) {
        setActiveTab(landedTab);
        setTabRoutes(prev => ({ ...prev, [landedTab]: location.pathname }));
        return;
      }
    }

    // For PUSH/REPLACE that crosses to a different tab
    const landedTab = inferTab(location.pathname);
    if (landedTab && landedTab !== activeTab && navType !== 'POP') {
      setActiveTab(landedTab);
      setTabRoutes(prev => ({
        ...prev,
        [activeTab]: previousPath,
        [landedTab]: location.pathname,
      }));
      return;
    }

    // Within the current tab, update the saved route
    setTabRoutes(prev => ({ ...prev, [activeTab]: location.pathname }));
  }, [location.pathname, navType, activeTab]);

  const handleTabClick = useCallback((tab: TabKey) => {
    if (tab === activeTab) {
      // Re-tap: pop to tab root
      const root = TAB_ROOTS[tab];
      if (location.pathname !== root) {
        navigate(root, { replace: true });
        setTabRoutes(prev => ({ ...prev, [tab]: root }));
      }
      return;
    }
    // Save current location to current tab (except Statistics — always reset to root)
    setTabRoutes(prev => ({
      ...prev,
      [activeTab]: activeTab === TabKey.Statistics ? TAB_ROOTS.statistics : location.pathname,
    }));
    setActiveTab(tab);
    // Statistics always navigates to root
    const target = tab === TabKey.Statistics ? TAB_ROOTS.statistics : tabRoutes[tab];
    navigate(target, { replace: true });
  }, [activeTab, location.pathname, navigate, tabRoutes]);

  return useMemo(() => ({ activeTab, handleTabClick, tabRoutes }), [activeTab, handleTabClick, tabRoutes]);
}
