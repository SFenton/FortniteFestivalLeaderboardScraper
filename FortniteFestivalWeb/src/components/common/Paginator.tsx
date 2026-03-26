import { memo, useEffect, useMemo, type ReactNode, type CSSProperties } from 'react';
import { IoChevronBack, IoChevronForward, IoPlayBack, IoPlayForward } from 'react-icons/io5';
import {
  Colors, Gap, IconSize, LineHeight, MetadataSize, Opacity, Border, Cursor,
  CssValue, CssProp, PointerEvents,
  flexCenter, flexRow, border, transition, transitions, scale, FAST_FADE_MS,
} from '@festival/theme';

/* ── Types ── */

export interface PaginatorProps {
  /** Handler for "previous" button. Omit to hide. */
  onPrev?: () => void;
  /** Handler for "next" button. Omit to hide. */
  onNext?: () => void;
  /** Handler for "skip to start" button. Omit to hide. */
  onSkipPrev?: () => void;
  /** Handler for "skip to end" button. Omit to hide. */
  onSkipNext?: () => void;
  /** Disable prev / skipPrev buttons. */
  prevDisabled?: boolean;
  /** Disable next / skipNext buttons. */
  nextDisabled?: boolean;
  /** Enable keyboard ArrowLeft/ArrowRight navigation. */
  keyboard?: boolean;
  /** Override className on the outer container. */
  className?: string;
  /** Override className on arrow buttons. */
  buttonClassName?: string;
  /** Override style on the outer container. */
  style?: CSSProperties;
  /** Content rendered between the arrow buttons (dots, badge, icon, etc.). */
  children?: ReactNode;
}

/* ── Paginator ── */

/**
 * Layout component for prev/next navigation.
 *
 * Renders: `[SkipPrev?] [Prev?] [children] [Next?] [SkipNext?]`
 *
 * Callers own what renders in the middle (dots, page badge, icon, nothing).
 * Paginator owns button layout, sizing, disabled states, and optional keyboard nav.
 */
function PaginatorInner({
  onPrev,
  onNext,
  onSkipPrev,
  onSkipNext,
  prevDisabled,
  nextDisabled,
  keyboard,
  className,
  buttonClassName,
  style,
  children,
}: PaginatorProps) {
  // Optional keyboard navigation
  useEffect(() => {
    if (!keyboard) return;
    const handler = (e: KeyboardEvent) => {
      if (e.key === 'ArrowLeft' && onPrev && !prevDisabled) onPrev();
      if (e.key === 'ArrowRight' && onNext && !nextDisabled) onNext();
    };
    window.addEventListener('keydown', handler);
    return () => window.removeEventListener('keydown', handler);
  }, [keyboard, onPrev, onNext, prevDisabled, nextDisabled]);

  const s = usePaginatorStyles(prevDisabled, nextDisabled);

  return (
    <div className={className} style={{ ...(className ? {} : s.container), ...style }}>
      {onSkipPrev && (
        <button className={buttonClassName} style={buttonClassName ? undefined : s.prevBtn} disabled={prevDisabled} onClick={onSkipPrev} aria-label="Skip to start">
          <IoPlayBack size={IconSize.chevron} />
        </button>
      )}
      {onPrev && (
        <button className={buttonClassName} style={buttonClassName ? undefined : s.prevBtn} disabled={prevDisabled} onClick={onPrev} aria-label="Previous">
          <IoChevronBack size={IconSize.default} />
        </button>
      )}
      {children && <div style={s.center}>{children}</div>}
      {onNext && (
        <button className={buttonClassName} style={buttonClassName ? undefined : s.nextBtn} disabled={nextDisabled} onClick={onNext} aria-label="Next">
          <IoChevronForward size={IconSize.default} />
        </button>
      )}
      {onSkipNext && (
        <button className={buttonClassName} style={buttonClassName ? undefined : s.nextBtn} disabled={nextDisabled} onClick={onSkipNext} aria-label="Skip to end">
          <IoPlayForward size={IconSize.chevron} />
        </button>
      )}
    </div>
  );
}

/* ── Dot sub-component ── */

export interface DotProps {
  /** Whether this dot represents the current item. */
  active?: boolean;
  /** Click handler (e.g. jump to this index). */
  onClick?: () => void;
  /** Accessible label. */
  label?: string;
}

const Dot = memo(function Dot({ active, onClick, label }: DotProps) {
  const s = useDotStyles(active);
  return (
    <button
      style={s.dot}
      onClick={onClick}
      aria-label={label}
      tabIndex={-1}
    />
  );
});

/* ── Compound export ── */

type PaginatorComponent = typeof PaginatorInner & {
  Dot: typeof Dot;
};

const Paginator = PaginatorInner as PaginatorComponent;
Paginator.Dot = Dot;

export default Paginator;

/* ── Styles ── */

function usePaginatorStyles(prevDisabled?: boolean, nextDisabled?: boolean) {
  return useMemo(() => {
    const arrowBtnBase: CSSProperties = {
      width: IconSize.lg,
      height: IconSize.lg,
      borderRadius: CssValue.circle,
      background: Colors.surfaceElevated,
      border: border(Border.thin, Colors.borderPrimary),
      color: Colors.textSecondary,
      ...flexCenter,
      flexShrink: 0,
      transition: transition(CssProp.opacity, FAST_FADE_MS),
      lineHeight: LineHeight.none,
      padding: Gap.none,
    };
    const withDisabled = (disabled?: boolean): CSSProperties => ({
      ...arrowBtnBase,
      opacity: disabled ? Opacity.dimmed : undefined,
      cursor: disabled ? Cursor.default : Cursor.pointer,
      pointerEvents: disabled ? PointerEvents.none : undefined,
    });
    return {
      container: { ...flexCenter, gap: Gap.md } as CSSProperties,
      prevBtn: withDisabled(prevDisabled),
      nextBtn: withDisabled(nextDisabled),
      center: { ...flexRow, gap: Gap.sm } as CSSProperties,
    };
  }, [prevDisabled, nextDisabled]);
}

function useDotStyles(active?: boolean) {
  return useMemo(() => ({
    dot: {
      width: MetadataSize.dotSize,
      height: MetadataSize.dotSize,
      borderRadius: CssValue.circle,
      backgroundColor: active ? Colors.accentBlue : Colors.surfaceMuted,
      transition: transitions(transition(CssProp.backgroundColor, FAST_FADE_MS), transition(CssProp.transform, FAST_FADE_MS)),
      transform: active ? scale(MetadataSize.dotActiveScale) : undefined,
      border: CssValue.none,
      padding: Gap.none,
      cursor: Cursor.pointer,
    } as CSSProperties,
  }), [active]);
}
