/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { forwardRef, type CSSProperties, type ReactNode } from 'react';
import { frostedCard } from '@festival/theme';

export interface FrostedCardProps {
  children: ReactNode;
  className?: string;
  style?: CSSProperties;
}

/**
 * Shared frosted-glass surface component.
 *
 * Provides the `frostedCard` surface styling (background, noise, border,
 * inset shadows).  The `--frosted-card` CSS custom property baked into the
 * style mixin automatically opts the element into the proximity light-trail
 * effect driven by `useProximityGlow` in Page.
 *
 * **Does NOT own layout** — consumers control `borderRadius`, `padding`,
 * `gap`, `display`, etc. via the `style` prop.
 */
export const FrostedCard = forwardRef<HTMLDivElement, FrostedCardProps>(
  function FrostedCard({ children, className, style }, ref) {
    return (
      <div ref={ref} className={className} style={{ ...frostedCard, ...style }}>
        {children}
      </div>
    );
  },
);
