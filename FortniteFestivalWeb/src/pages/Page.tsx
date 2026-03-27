/**
 * Shared page shell that provides the standard layout, scroll infrastructure,
 * scroll-mask fade, stagger-rush behaviour, and scroll restoration used by every page.
 *
 * Usage:
 *   <Page scrollRestoreKey="songs" scrollDeps={[items.length]}>
 *     {content}
 *   </Page>
 *
 * Pages still own their own state, data fetching, and load-phase logic.
 * Page just removes the boilerplate outer DOM + scroll hook wiring.
 *
 * Pages that need direct access to the scroll element (virtualizers, auto-scroll)
 * use `usePageScroll()` which returns `{ scrollRef, scrollTo }`.
 */
import { createContext, useContext, useEffect, useMemo, useRef, type ReactNode, type RefObject, type CSSProperties } from 'react';
import { createPortal } from 'react-dom';
import { useNavigationType } from 'react-router-dom';
import { Colors, ZIndex, MaxWidth, Layout, Position, Size, Spinner, Border, flexColumn, flexCenter, fixedFill, CssValue, Opacity, BorderStyle, PointerEvents, padding, SPINNER_FADE_MS } from '@festival/theme';
import { useIsMobileChrome } from '../hooks/ui/useIsMobile';
import { useScrollMask, type ScrollMaskOptions } from '../hooks/ui/useScrollMask';
import { useStaggerRush } from '../hooks/ui/useStaggerRush';
import { useScrollRestore } from '../hooks/ui/useScrollRestore';
import { useScrollContainer, useHeaderPortal } from '../contexts/ScrollContainerContext';
import { useRegisterFirstRun } from '../hooks/ui/useRegisterFirstRun';
import { useFirstRun } from '../hooks/ui/useFirstRun';
import FirstRunCarousel from '../components/firstRun/FirstRunCarousel';
import type { FirstRunSlideDef, FirstRunGateContext } from '../firstRun/types';
import { LoadPhase } from '@festival/core';
import ArcSpinner from '../components/common/ArcSpinner';

/** Page-level style objects — importable by SuspenseFallback, PlayerPage consumers, etc. */
export const pageCss = {
  page: { ...flexColumn, flex: 1, color: Colors.textPrimary } as CSSProperties,
  pageWithBg: { ...flexColumn, flex: 1, color: Colors.textPrimary, backgroundColor: Colors.backgroundApp, position: Position.relative } as CSSProperties,
  pageWithBgClip: { ...flexColumn, flex: 1, color: Colors.textPrimary, position: Position.relative } as CSSProperties,
  scrollArea: { flex: 1 } as CSSProperties,
  scrollAreaRelative: { flex: 1, position: Position.relative } as CSSProperties,
  scrollAreaRelativeZ: { flex: 1, position: Position.relative, zIndex: ZIndex.base } as CSSProperties,
  container: { maxWidth: MaxWidth.card, margin: CssValue.marginCenter, width: CssValue.full, padding: padding(0, Layout.paddingHorizontal) } as CSSProperties,
  containerZ: { maxWidth: MaxWidth.card, margin: CssValue.marginCenter, width: CssValue.full, padding: padding(0, Layout.paddingHorizontal), position: Position.relative, zIndex: ZIndex.base } as CSSProperties,
  bgImage: { ...fixedFill, backgroundSize: 'cover', backgroundPosition: 'center', opacity: Opacity.backgroundImage, pointerEvents: PointerEvents.none } as CSSProperties,
  bgDim: { ...fixedFill, backgroundColor: Colors.overlayDark, pointerEvents: PointerEvents.none } as CSSProperties,
  spinnerOverlay: { ...fixedFill, zIndex: ZIndex.dropdown, ...flexCenter } as CSSProperties,
  spinnerContainer: { ...flexCenter, minHeight: `calc(100vh - ${Layout.shellChromeHeight}px)` } as CSSProperties,
  arcSpinner: { width: Size.iconXl, height: Size.iconXl, borderStyle: BorderStyle.solid, borderWidth: Border.spinnerLg, borderColor: Spinner.trackColor, borderTopColor: Colors.accentPurple, borderRadius: CssValue.circle, animation: `spin ${Spinner.duration} linear infinite` } as CSSProperties,
  fabSpacer: { height: Layout.fabPaddingBottom, flexShrink: 0 } as CSSProperties,
};

/* ── Scroll area variant ── */
export type ScrollAreaVariant = 'default' | 'relative' | 'relativeZ';

function scrollAreaStyle(v: ScrollAreaVariant): CSSProperties {
  /* v8 ignore start */
  if (v === 'relativeZ') return pageCss.scrollAreaRelativeZ;
  if (v === 'relative') return pageCss.scrollAreaRelative;
  /* v8 ignore stop */
  return pageCss.scrollArea;
}

/* ── Container variant ── */
export type ContainerVariant = 'default' | 'z';

function containerBaseStyle(v: ContainerVariant): CSSProperties {
  /* v8 ignore start */
  if (v === 'z') return pageCss.containerZ;
  /* v8 ignore stop */
  return pageCss.container;
}

/* ── Page variant (outer wrapper) ── */
export type PageVariant = 'default' | 'withBg' | 'withBgClip';

function pageStyle(v: PageVariant): CSSProperties {
  /* v8 ignore start */
  if (v === 'withBgClip') return pageCss.pageWithBgClip;
  if (v === 'withBg') return pageCss.pageWithBg;
  /* v8 ignore stop */
  return pageCss.page;
}

/* ── Props ── */
export interface PageProps {
  /**
   * Optional external ref to the scroll container. When omitted, Page creates
   * its own ref internally. Use `usePageScroll()` for read access instead.
   */
  scrollRef?: RefObject<HTMLDivElement | null>;
  /** Extra scroll-mask dependency values (e.g. [items.length, phase]). */
  scrollDeps?: readonly unknown[];
  /** Scroll-mask options (fade size). */
  scrollMaskOptions?: ScrollMaskOptions;
  /** When set, Page auto-calls useScrollRestore with this cache key. */
  scrollRestoreKey?: string;
  /** Page outer wrapper variant. */
  variant?: PageVariant;
  /** Scroll area variant. */
  scrollVariant?: ScrollAreaVariant;
  /** Container variant. */
  containerVariant?: ContainerVariant;
  /** Extra className appended to the page wrapper. */
  className?: string;
  /** Extra className appended to the scroll area. */
  scrollClassName?: string;
  /** Inline styles merged onto the scroll area div. */
  scrollStyle?: React.CSSProperties;
  /** Extra className appended to the container. */
  containerClassName?: string;
  /** Inline styles merged onto the container div. */
  containerStyle?: React.CSSProperties;
  /** Content rendered before the scroll area (e.g. headers sitting outside scroll). */
  before?: ReactNode;
  /** Content rendered after the scroll area (e.g. modals, footers). */
  after?: ReactNode;
  /** Ref that receives the stagger-rush reset function. Call `ref.current()` to allow re-stagger. */
  staggerRushRef?: React.MutableRefObject<(() => void) | undefined>;
  /**
   * When provided, Page listens to scroll position and calls `onCollapse`
   * when scrollTop crosses the threshold (default 40px).
   * Set `disabled: true` on mobile to skip the listener entirely.
   */
  headerCollapse?: {
    disabled?: boolean;
    threshold?: number;
    onCollapse: (collapsed: boolean) => void;
  };
  /**
   * When provided, Page handles first-run registration, gate evaluation, and
   * carousel rendering automatically. The carousel is appended after `after`.
   * SettingsPage (replay hub) should NOT use this — it manages replays manually.
   */
  firstRun?: {
    key: string;
    label: string;
    slides: FirstRunSlideDef[];
    gateContext: FirstRunGateContext;
  };
  /**
   * When provided, Page auto-renders a centered spinner before the `before` slot
   * during Loading/SpinnerOut phases, and applies a fade-out animation during SpinnerOut.
   */
  loadPhase?: LoadPhase;
  /**
   * Controls how the FAB bottom spacer behaves on mobile chrome.
   * - `'end'` (default): spacer sits at the end of scrollable content — content can
   *   scroll under the FAB before reaching the spacer.
   * - `'fixed'`: shrinks the scroll viewport itself so content never scrolls behind
   *   the FAB/search bar.
   */
  fabSpacer?: 'end' | 'fixed';
  children: ReactNode;
}

export default function Page({
  scrollRef: externalScrollRef,
  scrollDeps,
  scrollMaskOptions,
  scrollRestoreKey,
  variant = 'default',
  scrollVariant = 'default',
  containerVariant = 'default',
  className,
  scrollClassName,
  scrollStyle,
  containerClassName,
  containerStyle,
  before,
  after,
  staggerRushRef,
  headerCollapse,
  firstRun: firstRunConfig,
  loadPhase,
  fabSpacer = 'end',
  children,
}: PageProps) {
  const internalRef = useRef<HTMLDivElement>(null);
  const scrollRef = externalScrollRef ?? internalRef;
  const scrollContainerRef = useScrollContainer();
  const portalTarget = useHeaderPortal();

  const stableScrollDeps = useMemo(() => scrollDeps ?? [], [scrollDeps]);
  useScrollMask(scrollRef, stableScrollDeps, scrollMaskOptions);
  const { resetRush } = useStaggerRush(scrollRef);
  if (staggerRushRef) staggerRushRef.current = resetRush;

  // Auto scroll-restore when key is provided
  const navType = useNavigationType();
  useScrollRestore(scrollRestoreKey ?? '', scrollRestoreKey ? navType : '');

  // Header collapse: listen to scrollContainer scrollTop and call onCollapse
  const lastCollapsedRef = useRef<boolean | null>(null);
  useEffect(() => {
    if (!headerCollapse || headerCollapse.disabled) return;
    const el = scrollContainerRef.current;
    if (!el) return;
    const threshold = headerCollapse.threshold ?? 40;
    const onScroll = () => {
      const collapsed = el.scrollTop > threshold;
      if (collapsed !== lastCollapsedRef.current) {
        lastCollapsedRef.current = collapsed;
        headerCollapse.onCollapse(collapsed);
      }
    };
    el.addEventListener('scroll', onScroll, { passive: true });
    return () => el.removeEventListener('scroll', onScroll);
  }); // intentionally no deps — re-subscribe on every render to capture latest headerCollapse

  const isMobileChrome = useIsMobileChrome();

  // 'fixed' mode: shrink the shell scroll container so content never scrolls behind the FAB
  useEffect(() => {
    if (!isMobileChrome || fabSpacer !== 'fixed') return;
    const el = scrollContainerRef.current;
    if (!el) return;
    el.style.marginBottom = `${Layout.fabPaddingBottom}px`;
    return () => { el.style.marginBottom = ''; };
  }, [isMobileChrome, fabSpacer, scrollContainerRef]);

  const pgStyle = pageStyle(variant);
  const saStyle = scrollAreaStyle(scrollVariant);
  const cStyle = containerBaseStyle(containerVariant);

  const pageScrollValue = useMemo(() => ({ scrollRef, resetRush }), [scrollRef, resetRush]);

  return (
    <PageScrollContext.Provider value={pageScrollValue}>
    {before && portalTarget && createPortal(before, portalTarget)}
    <div data-testid="page-root" ref={scrollRef} className={className} style={pgStyle}>
      {loadPhase != null && loadPhase !== LoadPhase.ContentIn && (
        <div style={loadPhase === LoadPhase.SpinnerOut
          ? { ...pageCss.spinnerOverlay, animation: `fadeOut ${SPINNER_FADE_MS}ms ease-out forwards` }
          : pageCss.spinnerOverlay}
        >
          <ArcSpinner />
        </div>
      )}
      <div data-testid="scroll-area" className={scrollClassName} style={{ ...saStyle, ...scrollStyle }}>
        <div className={containerClassName} style={{ ...cStyle, ...containerStyle }}>
          {children}
        </div>
        {isMobileChrome && fabSpacer === 'end' && <div style={pageCss.fabSpacer} />}
      </div>
      {after}
      {firstRunConfig && <PageFirstRun config={firstRunConfig} />}
    </div>
    </PageScrollContext.Provider>
  );
}

/** Separated component so hooks only run when firstRun config is provided. */
function PageFirstRun({ config }: { config: NonNullable<PageProps['firstRun']> }) {
  useRegisterFirstRun(config.key, config.label, config.slides);
  const firstRun = useFirstRun(config.key, config.gateContext);
  if (!firstRun.show) return null;
  return <FirstRunCarousel slides={firstRun.slides} onDismiss={firstRun.dismiss} onExitComplete={firstRun.onExitComplete} />;
}

import BackgroundImage from '../components/page/BackgroundImage';

/* ── Convenience sub-components for common patterns ── */

/** Album-art background with dim overlay that fades in once the image loads. */
export function PageBackground({ src }: { src: string | undefined }) {
  return <BackgroundImage src={src} />;
}

/* ── usePageScroll — access the active page's scroll element ── */

interface PageScrollContextValue {
  scrollRef: RefObject<HTMLDivElement | null>;
  /** Reset the stagger-rush flag so the next scroll triggers rush again. */
  resetRush: () => void;
}

const noop = () => {};
const PageScrollContext = createContext<PageScrollContextValue>({ scrollRef: { current: null }, resetRush: noop });

/**
 * Returns the active `<Page>`'s scroll-area ref and stagger-rush reset.
 *
 * Use this when a page needs direct scroll access (virtualizers, auto-scroll)
 * or needs to re-trigger stagger animations on data change.
 */
export function usePageScroll(): PageScrollContextValue {
  return useContext(PageScrollContext);
}

/**
 * @deprecated Use `usePageScroll()` instead. `<Page>` now owns its scroll ref internally.
 */
export function usePageScrollRef() {
  return useRef<HTMLDivElement>(null);
}
