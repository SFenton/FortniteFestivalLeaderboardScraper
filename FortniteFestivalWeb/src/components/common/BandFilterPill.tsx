/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useMemo, type CSSProperties } from 'react';
import { IoFunnel } from 'react-icons/io5';
import { useTranslation } from 'react-i18next';
import type { PlayerBandType, ServerInstrumentKey } from '@festival/core/api/serverTypes';
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
import { bandTypeLabel } from '../../utils/bandTypes';

export interface BandFilterPillProps {
  label: string;
  selectedInstruments: readonly ServerInstrumentKey[];
  bandType?: PlayerBandType | null;
  onClick: () => void;
}

const activeButtonOverrides: CSSProperties = {
  background: Colors.accentBlue,
  backgroundImage: 'none',
  border: `1px solid ${Colors.transparent}`,
  boxShadow: 'none',
  color: '#FFFFFF',
};

const bandFilterPillTransition = transitions(
  transition('background-color', TRANSITION_MS),
  transition('border-color', TRANSITION_MS),
  transition('color', TRANSITION_MS),
  transition('box-shadow', TRANSITION_MS),
);

export default function BandFilterPill({ label, selectedInstruments, bandType, onClick }: BandFilterPillProps) {
  const { t } = useTranslation();
  const active = selectedInstruments.length > 0;
  const s = useStyles(active);
  const appliedBandTypeLabel = active && bandType ? bandTypeLabel(bandType, t) : null;

  return (
    <button
      type="button"
      style={s.button}
      onClick={onClick}
      title={label}
      aria-label={label}
      data-testid="band-filter-pill"
    >
      {active ? (
        <>
          <IoFunnel size={IconSize.action} data-testid="band-filter-pill-filter-icon" aria-hidden="true" />
          {appliedBandTypeLabel && <span style={s.bandTypeLabel}>{appliedBandTypeLabel}</span>}
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
      ...(iconOnly ? activeButtonOverrides : null),
    } as CSSProperties,
    bandTypeLabel: {
      color: 'inherit',
      lineHeight: 1,
      flexShrink: 0,
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
