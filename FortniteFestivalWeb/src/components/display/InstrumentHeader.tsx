/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { memo, useMemo } from 'react';
import { InstrumentHeaderSize } from '@festival/core';
import { serverInstrumentLabel as instrumentLabel, type ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import { InstrumentSize, IconSize, Gap, Font, Weight, Colors, Display, Align, flexColumn } from '@festival/theme';
import { InstrumentIcon } from './InstrumentIcons';

export interface InstrumentHeaderProps {
  instrument: InstrumentKey;
  size: InstrumentHeaderSize;
  label?: string;
  subtitle?: string;
  className?: string;
  style?: React.CSSProperties;
  iconOnly?: boolean;
  sig?: string;
}

const InstrumentHeader = memo(function InstrumentHeader({
  instrument,
  size,
  label,
  subtitle,
  className,
  style,
  iconOnly,
  sig,
}: InstrumentHeaderProps) {
  const s = useStyles(size);
  return (
    <div className={className} style={{ ...s.header, ...style }}>
      <InstrumentIcon instrument={instrument} sig={sig} size={s.iconSize} />
      {!iconOnly && (
        subtitle ? (
          <div style={s.titleCol}>
            <span style={s.label}>{label ?? instrumentLabel(instrument)}</span>
            <span style={s.subtitle}>{subtitle}</span>
          </div>
        ) : (
          <span style={s.label}>
            {label ?? instrumentLabel(instrument)}
          </span>
        )
      )}
    </div>
  );
});

export default InstrumentHeader;

function useStyles(size: InstrumentHeaderSize) {
  return useMemo(() => {
    const config = {
      [InstrumentHeaderSize.XS]: { icon: IconSize.tab,       gap: Gap.xs, fontSize: Font.xs, fontWeight: Weight.semibold, color: Colors.textSecondary },
      [InstrumentHeaderSize.SM]: { icon: InstrumentSize.sm,   gap: Gap.md, fontSize: Font.md, fontWeight: Weight.bold,     color: Colors.textPrimary },
      [InstrumentHeaderSize.MD]: { icon: InstrumentSize.md,   gap: Gap.lg, fontSize: Font.xl, fontWeight: Weight.bold,     color: Colors.textPrimary },
      [InstrumentHeaderSize.LG]: { icon: InstrumentSize.lg,   gap: Gap.lg, fontSize: Font.lg, fontWeight: Weight.bold,     color: Colors.textSecondary },
      [InstrumentHeaderSize.XL]: { icon: InstrumentSize.lg,   gap: Gap.xl, fontSize: Font.xl, fontWeight: Weight.bold,     color: Colors.textPrimary },
    }[size];
    return {
      iconSize: config.icon,
      header: {
        display: Display.flex,
        alignItems: Align.center,
        gap: config.gap,
      },
      titleCol: {
        ...flexColumn,
        justifyContent: Align.center,
        height: config.icon,
      } as React.CSSProperties,
      label: {
        fontSize: config.fontSize,
        fontWeight: config.fontWeight,
        color: config.color,
      },
      subtitle: {
        fontSize: Font.sm,
        color: Colors.textSecondary,
      },
    };
  }, [size]);
}
