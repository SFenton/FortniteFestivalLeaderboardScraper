import {
  useLayoutEffect,
  useState,
  type CSSProperties,
  type MouseEvent,
  type ReactNode,
} from 'react';
import type { NavigationType } from 'react-router-dom';
import styles from './RouteAccessibility.module.css';
import RouteAccessibilityRuntime from './RouteAccessibilityRuntime';

const MAIN_CONTENT_ID = 'main-content';

export type RouteAccessibilityProps = {
  pathname: string;
  titleOverride?: string | null;
  navigationType: NavigationType;
  skipLabel: string;
};

export function RouteAccessibility({
  pathname,
  titleOverride,
  navigationType,
  skipLabel,
}: RouteAccessibilityProps) {
  const handleSkip = (event: MouseEvent<HTMLAnchorElement>) => {
    event.preventDefault();
    document.getElementById(MAIN_CONTENT_ID)?.focus({ preventScroll: true });
  };

  return (
    <>
      <a className={styles.skipLink} href={`#${MAIN_CONTENT_ID}`} onClick={handleSkip}>
        {skipLabel}
      </a>
      <RouteAccessibilityRuntime
        pathname={pathname}
        titleOverride={titleOverride}
        navigationType={navigationType}
      />
    </>
  );
}

export function RouteMain({
  children,
  routeTitle,
  fallbackHeading,
  style,
}: {
  children: ReactNode;
  routeTitle: string;
  fallbackHeading: boolean;
  style?: CSSProperties;
}) {
  const [showFallbackHeading, setShowFallbackHeading] = useState(fallbackHeading);

  useLayoutEffect(() => {
    if (!fallbackHeading) {
      setShowFallbackHeading(false);
      return;
    }
    const updateHeadingOwnership = () => {
      const pageHeading = document.querySelector('h1:not([data-route-fallback-heading])');
      setShowFallbackHeading(!pageHeading);
    };
    updateHeadingOwnership();
    const observer = new MutationObserver(updateHeadingOwnership);
    observer.observe(document.body, { childList: true, subtree: true });
    return () => observer.disconnect();
  }, [fallbackHeading, routeTitle]);

  return (
    <main
      id={MAIN_CONTENT_ID}
      tabIndex={-1}
      aria-label={routeTitle}
      style={style}
    >
      {showFallbackHeading && (
        <h1 data-route-fallback-heading className={styles.visuallyHidden}>
          {routeTitle}
        </h1>
      )}
      {children}
    </main>
  );
}
