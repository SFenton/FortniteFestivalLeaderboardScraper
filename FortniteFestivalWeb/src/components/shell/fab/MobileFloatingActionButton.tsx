import { useEffect, useRef, useState } from 'react';
import FloatingActionButton, { type ActionItem } from './FloatingActionButton';
import { useFabVisibility } from '../../../contexts/FabVisibilityContext';
import { useIsMobileChrome } from '../../../hooks/ui/useIsMobile';
import { hasVisitedPage, markPageVisited } from '../../../hooks/ui/usePageTransition';

export type { ActionItem };

type FabProps = React.ComponentProps<typeof FloatingActionButton>;

type Props = FabProps & {
  /**
   * Stable key identifying this FAB's owning page surface.
   * Used for reveal memory: when the page is revisited within the same
   * session, the FAB mounts already-revealed (no measurement flicker).
   *
  * Stable for each route-owned FAB surface. The hybrid implementation keeps
  * most FAB configuration in `App.tsx`, while some complex pages may own
  * their own FAB directly.
   */
  pageKey?: string;
};

/**
 * Mobile FAB wrapper.
 *
 * App shell and page-owned FABs both use this wrapper so global visibility,
 * page-keyed reveal memory, and first-visit/revisit behaviour stay consistent.
 *
 * Behaviour:
 * - Returns null when the global mobile FAB visibility is suppressed
 *   (e.g. notifications drawer open, not in mobile chrome).
 * - Returns null when there is nothing to render (no actions, no surface).
 * - Tracks per-`pageKey` reveal state in a session-scoped Set (shared with
 *   the page-content stagger memory). On revisit, `initialRevealed` is
 *   passed through so the inner FAB skips its measurement gate.
 */
export default function MobileFloatingActionButton({ pageKey, ...props }: Props) {
  const isMobile = useIsMobileChrome();
  const { mobileFabHidden } = useFabVisibility();
  const fabKey = pageKey ? `fab:${pageKey}` : null;
  const initialRevealedRef = useRef<{ key: string | null; value: boolean }>({ key: null, value: false });
  if (initialRevealedRef.current.key !== fabKey) {
    initialRevealedRef.current = { key: fabKey, value: fabKey ? hasVisitedPage(fabKey) : false };
  }
  const initialRevealed = initialRevealedRef.current.value;

  // Defer the page-ready signal by one render so the inner FAB always sees a
  // false→true transition on first mount (even if the page synchronously
  // declared itself ready — e.g. cached data on refresh). This guarantees the
  // reveal-stagger animation fires. On revisit, `initialRevealed` short-circuits
  // this so we mount fully-visible with no animation.
  const incomingReady = props.ready ?? true;
  const [deferredReadyState, setDeferredReadyState] = useState({ key: fabKey, ready: initialRevealed });
  const deferredReady = deferredReadyState.key === fabKey ? deferredReadyState.ready : initialRevealed;
  useEffect(() => {
    const nextReady = initialRevealed ? true : Boolean(incomingReady);
    setDeferredReadyState(previous => (
      previous.key === fabKey && previous.ready === nextReady ? previous : { key: fabKey, ready: nextReady }
    ));
  }, [fabKey, incomingReady, initialRevealed]);

  // Mark as visited only after this FAB has actually reached ready state.
  // Marking during a hidden/unready first render made fresh pages look like
  // revisits and skipped the first reveal animation.
  useEffect(() => {
    if (!fabKey || !incomingReady) return;
    markPageVisited(fabKey);
  }, [fabKey, incomingReady]);

  const actionGroups = props.actionGroups ?? [];
  const hasActions = actionGroups.some(group => group.length > 0);
  const hasSideActions = (props.sideActions?.length ?? 0) > 0;
  const hasAnyContent = props.defaultOpen || hasActions || hasSideActions || props.directAction;
  if (!isMobile || mobileFabHidden) return null;
  // Suppress the FAB only when:
  //   • content is ready (so the page committed to "no FAB on this route"), AND
  //   • no actions / surface are present.
  // If the page is still warming up (`ready === false`) we keep mounting the
  // inner FAB even with empty actions, so it captures the not-ready state and
  // plays the reveal stagger when actions register and `ready` flips true.
  if (incomingReady && !hasAnyContent) return null;
  return <FloatingActionButton key={fabKey ?? 'floating-action-button'} {...props} actionGroups={actionGroups} ready={deferredReady} initialRevealed={initialRevealed} />;
}
