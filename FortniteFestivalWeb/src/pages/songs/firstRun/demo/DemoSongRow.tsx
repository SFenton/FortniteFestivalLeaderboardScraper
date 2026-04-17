/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
/**
 * Shared wrapper for demo song rows in the first-run carousel.
 * Handles the frostedCard container, fade-in animation, and fade-out/in
 * transition during the swap cycle — so individual demos only supply content.
 */
import type { ReactNode } from 'react';
import { FADE_DURATION, STAGGER_INTERVAL } from '@festival/theme';
import { songRow, songRowMobile } from '../../../../styles/songRowStyles';

const FADE_MS = FADE_DURATION;

export interface DemoSongRowProps {
  /** Row index (stable 0..n-1). */
  index: number;
  /** Whether the initial fade-in animation has completed. */
  initialDone: boolean;
  /** Set of row indices currently fading out. */
  fadingIdx: ReadonlySet<number>;
  /** Mobile layout (stacked). */
  mobile?: boolean;
  children: ReactNode;
}

export function DemoSongRow({ index, initialDone, fadingIdx, mobile, children }: DemoSongRowProps) {
  const baseStyle = mobile ? songRowMobile : songRow;
  return (
    <div
      style={initialDone
        ? { ...baseStyle, opacity: fadingIdx.has(index) ? 0 : 1, transition: `opacity ${FADE_MS}ms ease` }
        : { ...baseStyle, opacity: 0, animation: `fadeInUp ${FADE_MS}ms ease-out ${index * STAGGER_INTERVAL}ms forwards` }
      }
    >
      {children}
    </div>
  );
}
