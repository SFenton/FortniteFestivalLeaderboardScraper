/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
/**
 * Reusable pill button with icon + label.
 * Used for sort/filter actions in toolbar headers.
 */
import type { CSSProperties } from 'react';
import { Colors, Gap, Radius, Font, Weight, Layout, frostedCard, flexCenter, transitions, transition, TRANSITION_MS } from '@festival/theme';

const PILL_TRANSITION = transitions(
  transition('background-color', TRANSITION_MS),
  transition('border-color', TRANSITION_MS),
  transition('color', TRANSITION_MS),
  transition('box-shadow', TRANSITION_MS),
);

const pillStyle: CSSProperties = {
  ...flexCenter,
  position: 'relative',
  width: 'auto',
  gap: Gap.md,
  height: Layout.pillButtonHeight,
  borderRadius: Radius.full,
  cursor: 'pointer',
  fontWeight: Weight.semibold,
  whiteSpace: 'nowrap',
  paddingLeft: Gap.xl,
  paddingRight: Gap.xl,
  fontSize: Font.sm,
  ...frostedCard,
  color: Colors.textPrimary,
  transition: PILL_TRANSITION,
};

const pillActiveOverrides: CSSProperties = {
  backgroundColor: Colors.accentBlue,
  backgroundImage: 'none',
  border: '1px solid transparent',
  boxShadow: 'none',
  color: '#FFFFFF',
};

const dotStyle: CSSProperties = {
  width: 6,
  height: 6,
  borderRadius: '50%',
  backgroundColor: Colors.accentBlueBright,
};

export interface ActionPillProps {
  icon: React.ReactNode;
  label: string;
  onClick: () => void;
  /** Highlight the pill as active (e.g. filters applied). */
  active?: boolean;
  /** Show a small dot indicator. */
  dot?: boolean;
  /** Extra className on the button. */
  className?: string;
  /** Extra style (e.g. for stagger animations). */
  style?: React.CSSProperties;
}

export function ActionPill({ icon, label, onClick, active, dot, className, style }: ActionPillProps) {
  const merged = active ? { ...pillStyle, ...pillActiveOverrides, ...style } : { ...pillStyle, ...style };

  return (
    <button
      className={className}
      style={merged}
      onClick={onClick}
      title={label}
      aria-label={label}
    >
      {icon}
      <span>{label}</span>
      {dot && <span style={dotStyle} />}
    </button>
  );
}
