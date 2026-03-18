/**
 * Self-contained arc spinner with size variants.
 * Uses a single CSS module — no cross-file composes — to avoid
 * CSS chunk ordering issues with lazy-loaded pages.
 */
import { memo } from 'react';
import css from './ArcSpinner.module.css';

export type SpinnerSize = 'sm' | 'md' | 'lg';

const SIZE_CLASS = {
  sm: css.sm!,
  md: css.md!,
  lg: css.lg!,
} as const;

interface ArcSpinnerProps {
  /** sm = 24px, md = 36px, lg = 48px. Default: lg. */
  size?: SpinnerSize;
  className?: string;
}

const ArcSpinner = memo(function ArcSpinner({ size = 'lg', className }: ArcSpinnerProps) {
  const cls = className ? `${SIZE_CLASS[size]} ${className}` : SIZE_CLASS[size];
  return <div className={cls} />;
});

export default ArcSpinner;
