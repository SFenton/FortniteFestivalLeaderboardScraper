import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { NavigationType } from 'react-router-dom';
import styles from './RouteAccessibility.module.css';

export default function RouteAccessibilityRuntime({
  pathname,
  titleOverride,
  navigationType,
}: {
  pathname: string;
  titleOverride?: string | null;
  navigationType: NavigationType;
}) {
  const { t } = useTranslation();
  const [metadataTitle, setMetadataTitle] = useState<{
    pathname: string;
    title: string;
  } | null>(null);
  const routeTitle = titleOverride
    ?? (metadataTitle?.pathname === pathname ? metadataTitle.title : null);
  const committedRouteKeyRef = useRef<string | null>(null);
  const pendingAnnouncementKeyRef = useRef<string | null>(null);
  const commitFrameRef = useRef(0);
  const focusFrameRef = useRef(0);
  const routeTitleRef = useRef(routeTitle);
  const translateRef = useRef(t);
  routeTitleRef.current = routeTitle;
  translateRef.current = t;
  const [announcedPathname, setAnnouncedPathname] = useState<string | null>(null);

  useEffect(() => {
    if (titleOverride) {
      return;
    }
    let active = true;
    void import('../../routeMetadata').then(({ matchRouteMetadata }) => {
      if (!active) return;
      const metadata = matchRouteMetadata(pathname);
      setMetadataTitle({
        pathname,
        title: t(metadata[0], metadata[1]),
      });
    }).catch(() => {
      if (!active) return;
      setMetadataTitle({
        pathname,
        title: t('common.mainContent', 'Main content'),
      });
    });
    return () => {
      active = false;
    };
  }, [pathname, t, titleOverride]);

  useEffect(() => {
    if (!routeTitle) return;
    document.title = `${routeTitle} | ${t('common.brandName')}`;
  }, [routeTitle, t]);

  useEffect(() => {
    if (!routeTitle || pendingAnnouncementKeyRef.current !== pathname) return;
    pendingAnnouncementKeyRef.current = null;
    setAnnouncedPathname(pathname);
  }, [pathname, routeTitle, t]);

  useEffect(() => {
    cancelAnimationFrame(commitFrameRef.current);
    cancelAnimationFrame(focusFrameRef.current);
    commitFrameRef.current = requestAnimationFrame(() => {
      commitFrameRef.current = 0;
      const previousRouteKey = committedRouteKeyRef.current;
      committedRouteKeyRef.current = pathname;
      if (previousRouteKey === null || previousRouteKey === pathname) return;

      const committedTitle = routeTitleRef.current;
      if (committedTitle) {
        setAnnouncedPathname(pathname);
      } else {
        pendingAnnouncementKeyRef.current = pathname;
      }
      if (navigationType === 'POP') return;
      const focusOrigin = document.activeElement;
      let waitedForModal = false;
      const focusMain = (attempt: number) => {
        if (document.querySelector('[aria-modal="true"]')) {
          waitedForModal = true;
          if (attempt < 60) {
            focusFrameRef.current = requestAnimationFrame(() => focusMain(attempt + 1));
          }
          return;
        }

        const main = document.getElementById('main-content');
        if (!main) return;
        const activeElement = document.activeElement;
        const restoredControlOwnsFocus = waitedForModal
          && activeElement instanceof HTMLElement
          && activeElement !== focusOrigin
          && activeElement.isConnected
          && activeElement.tabIndex >= 0;
        if (!restoredControlOwnsFocus) main.focus({ preventScroll: true });
      };
      focusMain(0);
    });
    return () => {
      cancelAnimationFrame(commitFrameRef.current);
      cancelAnimationFrame(focusFrameRef.current);
    };
  }, [navigationType, pathname]);

  return (
    <div className={styles.visuallyHidden} aria-live="polite" aria-atomic="true">
      {announcedPathname === pathname && routeTitle
        ? <span key={pathname}>{translateRef.current('common.routeChanged', { title: routeTitle })}</span>
        : null}
    </div>
  );
}
