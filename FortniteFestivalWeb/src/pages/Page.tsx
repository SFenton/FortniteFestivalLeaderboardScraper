/**
 * Shared page shell that provides the standard layout, scroll infrastructure,
 * scroll-mask fade, and stagger-rush behaviour used by every page.
 *
 * Usage:
 *   <Page scrollRef={scrollRef} scrollDeps={[items.length]} onScroll={onScroll}>
 *     {content}
 *   </Page>
 *
 * Pages still own their own state, data fetching, and load-phase logic.
 * Page just removes the boilerplate outer DOM + scroll hook wiring.
 */
import { useMemo, useRef, type ReactNode, type RefObject, type CSSProperties } from 'react';
import { Colors, ZIndex, MaxWidth, Layout, Overflow, Position, Size, Spinner, Border, flexColumn, flexCenter, fixedFill, CssValue, Opacity, BorderStyle, PointerEvents, padding } from '@festival/theme';
import { useScrollMask, type ScrollMaskOptions } from '../hooks/ui/useScrollMask';
import { useStaggerRush } from '../hooks/ui/useStaggerRush';

/** Page-level style objects — importable by SuspenseFallback, PlayerPage consumers, etc. */
export const pageCss = {
  page: { ...flexColumn, height: CssValue.full, color: Colors.textPrimary } as CSSProperties,
  pageWithBg: { ...flexColumn, height: CssValue.full, color: Colors.textPrimary, backgroundColor: Colors.backgroundApp, position: Position.relative } as CSSProperties,
  pageWithBgClip: { ...flexColumn, height: CssValue.full, color: Colors.textPrimary, backgroundColor: Colors.backgroundApp, position: Position.relative, overflow: Overflow.hidden } as CSSProperties,
  scrollArea: { flex: 1, minHeight: 0 } as CSSProperties,
  scrollAreaRelative: { flex: 1, minHeight: 0, position: Position.relative } as CSSProperties,
  scrollAreaRelativeZ: { flex: 1, minHeight: 0, position: Position.relative, zIndex: ZIndex.base } as CSSProperties,
  container: { maxWidth: MaxWidth.card, margin: CssValue.marginCenter, width: CssValue.full, padding: padding(0, Layout.paddingHorizontal) } as CSSProperties,
  containerZ: { maxWidth: MaxWidth.card, margin: CssValue.marginCenter, width: CssValue.full, padding: padding(0, Layout.paddingHorizontal), position: Position.relative, zIndex: ZIndex.base } as CSSProperties,
  bgImage: { ...fixedFill, backgroundSize: 'cover', backgroundPosition: 'center', opacity: Opacity.backgroundImage, pointerEvents: PointerEvents.none } as CSSProperties,
  bgDim: { ...fixedFill, backgroundColor: Colors.overlayDark, pointerEvents: PointerEvents.none } as CSSProperties,
  spinnerOverlay: { ...fixedFill, zIndex: ZIndex.dropdown, ...flexCenter } as CSSProperties,
  spinnerContainer: { ...flexCenter, minHeight: `calc(100vh - ${Layout.shellChromeHeight}px)` } as CSSProperties,
  arcSpinner: { width: Size.iconXl, height: Size.iconXl, borderStyle: BorderStyle.solid, borderWidth: Border.spinnerLg, borderColor: Spinner.trackColor, borderTopColor: Colors.accentPurple, borderRadius: CssValue.circle, animation: `spin ${Spinner.duration} linear infinite` } as CSSProperties,
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
  /** Ref to the scroll container. Consumers need this for scroll-restore, virtualizers, etc. */
  scrollRef: RefObject<HTMLDivElement | null>;
  /** Extra scroll-mask dependency values (e.g. [items.length, phase]). */
  scrollDeps?: readonly unknown[];
  /** Scroll-mask options (fade size). */
  scrollMaskOptions?: ScrollMaskOptions;
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
  /** Content rendered before the scroll area (e.g. sticky headers sitting outside scroll). */
  before?: ReactNode;
  /** Content rendered after the scroll area (e.g. modals, footers). */
  after?: ReactNode;
  children: ReactNode;
}

export default function Page({
  scrollRef,
  scrollDeps,
  scrollMaskOptions,
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
  children,
}: PageProps) {
  const stableScrollDeps = useMemo(() => scrollDeps ?? [], [scrollDeps]);
  // Scroll mask and stagger rush now listen to window scroll internally —
  // no need to wire them into an onScroll handler on a container div.
  useScrollMask(scrollRef, stableScrollDeps, scrollMaskOptions);
  useStaggerRush(scrollRef);

  const pgStyle = pageStyle(variant);
  const saStyle = scrollAreaStyle(scrollVariant);
  const cStyle = containerBaseStyle(containerVariant);

  return (
    <div data-testid="page-root" className={className} style={pgStyle}>
      {before}
      <div data-testid="scroll-area" ref={scrollRef} className={scrollClassName} style={{ ...saStyle, ...scrollStyle }}>
        <div className={containerClassName} style={{ ...cStyle, ...containerStyle }}>
          {children}
        </div>
      </div>
      {after}
    </div>
  );
}

import BackgroundImage from '../components/page/BackgroundImage';

/* ── Convenience sub-components for common patterns ── */

/** Album-art background with dim overlay that fades in once the image loads. */
export function PageBackground({ src }: { src: string | undefined }) {
  return <BackgroundImage src={src} />;
}

/**
 * Standard hook to get the scroll ref that pages pass to <Page>.
 * Just a typed useRef — exists so pages don't need to import useRef + type.
 */
export function usePageScrollRef() {
  return useRef<HTMLDivElement>(null);
}
