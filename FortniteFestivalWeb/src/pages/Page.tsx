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
import { useCallback, useMemo, useRef, type ReactNode, type RefObject } from 'react';
import { useScrollMask, type ScrollMaskOptions } from '../hooks/ui/useScrollMask';
import { useStaggerRush } from '../hooks/ui/useStaggerRush';
import css from './Page.module.css';

export { css as pageCss };

/* ── Scroll area variant ── */
export type ScrollAreaVariant = 'default' | 'relative' | 'relativeZ';

function scrollAreaCls(v: ScrollAreaVariant) {
  /* v8 ignore start */
  if (v === 'relativeZ') return css.scrollAreaRelativeZ;
  if (v === 'relative') return css.scrollAreaRelative;
  /* v8 ignore stop */
  return css.scrollArea;
}

/* ── Container variant ── */
export type ContainerVariant = 'default' | 'z';

function containerCls(v: ContainerVariant) {
  /* v8 ignore start */
  if (v === 'z') return css.containerZ;
  /* v8 ignore stop */
  return css.container;
}

/* ── Page variant (outer wrapper) ── */
export type PageVariant = 'default' | 'withBg' | 'withBgClip';

function pageCls(v: PageVariant) {
  /* v8 ignore start */
  if (v === 'withBgClip') return css.pageWithBgClip;
  if (v === 'withBg') return css.pageWithBg;
  /* v8 ignore stop */
  return css.page;
}

/* ── Props ── */
export interface PageProps {
  /** Ref to the scroll container. Consumers need this for scroll-restore, virtualizers, etc. */
  scrollRef: RefObject<HTMLDivElement | null>;
  /** Extra scroll-mask dependency values (e.g. [items.length, phase]). */
  scrollDeps?: readonly unknown[];
  /** Scroll-mask options (fade size). */
  scrollMaskOptions?: ScrollMaskOptions;
  /** Additional scroll handler called after mask + rush. */
  onScroll?: () => void;
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
  /** Extra className appended to the container. */
  containerClassName?: string;
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
  onScroll,
  variant = 'default',
  scrollVariant = 'default',
  containerVariant = 'default',
  className,
  scrollClassName,
  containerClassName,
  before,
  after,
  children,
}: PageProps) {
  const stableScrollDeps = useMemo(() => scrollDeps ?? [], [scrollDeps]);
  const updateScrollMask = useScrollMask(scrollRef, stableScrollDeps, scrollMaskOptions);
  const { rushOnScroll } = useStaggerRush(scrollRef);

  /* v8 ignore start — scroll handler */
  const handleScroll = useCallback(() => {
    updateScrollMask();
    rushOnScroll();
    onScroll?.();
    /* v8 ignore stop */
  }, [updateScrollMask, rushOnScroll, onScroll]);

  const pgClass = pageCls(variant);
  const pageClassName = className ? `${pgClass} ${className}` : pgClass;
  const saClass = scrollAreaCls(scrollVariant);
  const scrollClass = scrollClassName ? `${saClass} ${scrollClassName}` : saClass;
  const cClass = containerCls(containerVariant);
  const contClass = containerClassName ? `${cClass} ${containerClassName}` : cClass;

  return (
    <div className={pageClassName}>
      {before}
      <div ref={scrollRef} onScroll={handleScroll} className={scrollClass}>
        <div className={contClass}>
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
