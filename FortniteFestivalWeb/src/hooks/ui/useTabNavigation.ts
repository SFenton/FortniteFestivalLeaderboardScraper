/**
 * Hook that manages per-tab route memory for the mobile bottom nav.
 * Persists tab routes to sessionStorage so they survive page refreshes.
 */
import { useState, useEffect, useRef, useCallback, useMemo } from 'react';
import { useLocation, useNavigate, useNavigationType } from 'react-router-dom';
import { TabKey } from '@festival/core';
import { markTapDiagnosticsAction } from '../../diagnostics/tapDiagnostics';
export type { TabKey };

export const TAB_ROOTS: Record<TabKey, string> = {
  [TabKey.Songs]: '/songs',
  [TabKey.Suggestions]: '/suggestions',
  [TabKey.Compete]: '/compete',
  [TabKey.Leaderboards]: '/leaderboards',
  [TabKey.Rivals]: '/rivals',
  [TabKey.Statistics]: '/statistics',
  [TabKey.Settings]: '/settings',
};

const STORAGE_KEY = 'fst:tabRoutes';

/** Infer which tab owns a route. Profile detail routes are neutral unless represented by /statistics. */
export function inferTab(pathname: string): TabKey | null {
  const path = pathname.split(/[?#]/, 1)[0] || '/';
  if (path === '/songs' || path.startsWith('/songs/')) return TabKey.Songs;
  if (path === '/shop') return TabKey.Songs;
  if (path === '/suggestions') return TabKey.Suggestions;
  if (path === '/compete') return TabKey.Compete;
  if (path === '/leaderboards' || path.startsWith('/leaderboards/')) return TabKey.Leaderboards;
  if (path === '/rivals' || path.startsWith('/rivals/')) return TabKey.Rivals;
  if (path === '/statistics') return TabKey.Statistics;
  if (path === '/settings') return TabKey.Settings;
  return null;
}

function migrateTabRoutes(routes: Record<TabKey, string>): Record<TabKey, string> {
  const competeRoute = routes[TabKey.Compete];
  const next = { ...routes };
  if (competeRoute?.startsWith('/leaderboards')) next[TabKey.Leaderboards] = competeRoute;
  if (competeRoute?.startsWith('/rivals')) next[TabKey.Rivals] = competeRoute;
  return next;
}

function loadTabRoutes(): Record<TabKey, string> {
  try {
    const raw = sessionStorage.getItem(STORAGE_KEY);
    if (raw) {
      const parsed = JSON.parse(raw);
      return migrateTabRoutes({ ...TAB_ROOTS, ...parsed });
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
  const currentPath = `${location.pathname}${location.search}`;

  const [activeTab, setActiveTab] = useState<TabKey | null>(
    () => inferTab(location.pathname),
  );
  const [tabRoutes, setTabRoutes] = useState<Record<TabKey, string>>(loadTabRoutes);
  const pendingNavigationRef = useRef<{ tab: TabKey; from: string; to: string; replace: boolean } | null>(null);

  // Persist to sessionStorage on change
  useEffect(() => {
    saveTabRoutes(tabRoutes);
  }, [tabRoutes]);

  // Keep tabRoutes in sync as user navigates
  const prevPathRef = useRef(location.pathname);
  useEffect(() => {
    const pendingNavigation = pendingNavigationRef.current;
    if (pendingNavigation && currentPath !== pendingNavigation.from) {
      pendingNavigationRef.current = null;
      markTapDiagnosticsAction('nav:location-change', 'success', {
        source: 'bottom-nav',
        tab: pendingNavigation.tab,
        from: pendingNavigation.from,
        to: currentPath,
        target: pendingNavigation.to,
        matched: currentPath === pendingNavigation.to || location.pathname === pendingNavigation.to,
        replace: pendingNavigation.replace,
      });
    }

    if (location.pathname === prevPathRef.current) return;
    const previousPath = prevPathRef.current;
    prevPathRef.current = location.pathname;

    const landedTab = inferTab(location.pathname);

    if (!landedTab) {
      if (activeTab) {
        const previousOwner = inferTab(previousPath);
        if (previousOwner === activeTab) {
          setTabRoutes(prev => ({ ...prev, [activeTab]: previousPath }));
        }
      }
      setActiveTab(null);
      return;
    }

    // On POP navigation, check if we landed on a route that belongs to a different tab
    if (navType === 'POP') {
      if (landedTab && landedTab !== activeTab) {
        setActiveTab(landedTab);
        setTabRoutes(prev => ({ ...prev, [landedTab]: location.pathname }));
        return;
      }
    }

    // For PUSH/REPLACE that crosses to a different tab
    if (landedTab && landedTab !== activeTab && navType !== 'POP') {
      setActiveTab(landedTab);
      setTabRoutes(prev => {
        const next = { ...prev, [landedTab]: location.pathname };
        if (activeTab && inferTab(previousPath) === activeTab) {
          next[activeTab] = previousPath;
        }
        return next;
      });
      return;
    }

    // Within the current tab, update the saved route
    if (activeTab) setTabRoutes(prev => ({ ...prev, [activeTab]: location.pathname }));
  }, [currentPath, location.pathname, navType, activeTab]);

  const markBottomNavStart = useCallback((tab: TabKey, target: string, replace: boolean) => {
    pendingNavigationRef.current = { tab, from: currentPath, to: target, replace };
    markTapDiagnosticsAction('nav:start', 'start', {
      source: 'bottom-nav',
      tab,
      from: currentPath,
      to: target,
      replace,
    });
  }, [currentPath]);

  const handleTabClick = useCallback((tab: TabKey, rootOverride?: string) => {
    const root = rootOverride ?? TAB_ROOTS[tab];
    if (tab === activeTab) {
      // Re-tap: pop to tab root
      if (location.pathname !== root) {
        markBottomNavStart(tab, root, true);
        navigate(root, { replace: true });
        setTabRoutes(prev => ({ ...prev, [tab]: root }));
      }
      return;
    }
    // Save current location to current tab (except Statistics — always reset to root)
    setTabRoutes(prev => {
      if (!activeTab || inferTab(location.pathname) !== activeTab) return prev;
      return {
        ...prev,
        [activeTab]: activeTab === TabKey.Statistics ? TAB_ROOTS.statistics : location.pathname,
      };
    });
    setActiveTab(tab);
    const saved = tab === TabKey.Statistics ? root : tabRoutes[tab];
    // If saved route is just the default root and caller provided an override, use the override
    const target = (rootOverride && saved === TAB_ROOTS[tab]) ? root : saved;
    // Guard: if the saved route belongs to a different tab (stale/corrupted), reset to tab root
    const owner = inferTab(target);
    const safeTarget = owner === tab ? target : root;
    markBottomNavStart(tab, safeTarget, true);
    navigate(safeTarget, { replace: true });
    if (safeTarget !== target) setTabRoutes(prev => ({ ...prev, [tab]: safeTarget }));
  }, [activeTab, location.pathname, markBottomNavStart, navigate, tabRoutes]);

  return useMemo(() => ({ activeTab, handleTabClick, tabRoutes }), [activeTab, handleTabClick, tabRoutes]);
}
