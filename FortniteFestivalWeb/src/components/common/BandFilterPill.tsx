/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useMemo, type CSSProperties } from 'react';
import { IoFunnel } from 'react-icons/io5';
import type { ServerInstrumentKey } from '@festival/core/api/serverTypes';
import {
  Align,
  Colors,
  Font,
  Gap,
  IconSize,
  InstrumentSize,
  Layout,
  Radius,
  Weight,
  frostedCard,
  flexCenter,
  transitions,
  transition,
  TRANSITION_MS,
} from '@festival/theme';
import { InstrumentIcon } from '../display/InstrumentIcons';

export interface BandFilterPillProps {
  label: string;
  selectedInstruments: readonly ServerInstrumentKey[];
  onClick: () => void;
}

const bandFilterPillTransition = transitions(
  transition('background-color', TRANSITION_MS),
  transition('border-color', TRANSITION_MS),
  transition('color', TRANSITION_MS),
  transition('box-shadow', TRANSITION_MS),
);

export default function BandFilterPill({ label, selectedInstruments, onClick }: BandFilterPillProps) {
  const s = useStyles(selectedInstruments.length > 0);

  return (
    <button
      type="button"
      style={s.button}
      onClick={onClick}
      title={label}
      aria-label={label}
      data-testid="band-filter-pill"
    >
      {selectedInstruments.length > 0 ? (
        <>
          <IoFunnel size={IconSize.action} data-testid="band-filter-pill-filter-icon" aria-hidden="true" />
          <span style={s.iconGroup} aria-hidden="true">
            {selectedInstruments.map((instrument, index) => (
              <InstrumentIcon
                key={`${instrument}-${index}`}
                instrument={instrument}
                size={InstrumentSize.sm}
                style={s.instrumentIcon}
              />
            ))}
          </span>
        </>
      ) : (
        <>
          <IoFunnel size={IconSize.action} data-testid="band-filter-pill-filter-icon" />
          <span>{label}</span>
        </>
      )}
    </button>
  );
}

function useStyles(iconOnly: boolean) {
  return useMemo(() => ({
    button: {
      ...flexCenter,
      position: 'relative',
      width: 'auto',
      minWidth: iconOnly ? Layout.pillButtonHeight : undefined,
      gap: iconOnly ? Gap.lg : Gap.md,
      height: Layout.pillButtonHeight,
      borderRadius: Radius.full,
      cursor: 'pointer',
      fontWeight: Weight.semibold,
      whiteSpace: 'nowrap',
      paddingLeft: iconOnly ? Gap.lg : Gap.xl,
      paddingRight: iconOnly ? Gap.lg : Gap.xl,
      fontSize: Font.sm,
      ...frostedCard,
      color: Colors.textPrimary,
      transition: bandFilterPillTransition,
    } as CSSProperties,
    iconGroup: {
      display: 'flex',
      alignItems: Align.center,
      gap: Gap.sm,
      lineHeight: 0,
    } as CSSProperties,
    instrumentIcon: {
      display: 'block',
      flexShrink: 0,
    } as CSSProperties,
  }), [iconOnly]);
}
