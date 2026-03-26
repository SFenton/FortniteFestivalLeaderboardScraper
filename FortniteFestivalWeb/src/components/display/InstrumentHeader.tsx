/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { memo, useMemo } from 'react';
import { InstrumentHeaderSize } from '@festival/core';
import { serverInstrumentLabel as instrumentLabel, type ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import { InstrumentSize, IconSize, Gap, Font, Weight, Colors, Display, Align } from '@festival/theme';
import { InstrumentIcon } from './InstrumentIcons';

export interface InstrumentHeaderProps {
  instrument: InstrumentKey;
  size: InstrumentHeaderSize;
  label?: string;
  className?: string;
  style?: React.CSSProperties;
  iconOnly?: boolean;
}

const InstrumentHeader = memo(function InstrumentHeader({
  instrument,
  size,
  label,
  className,
  style,
  iconOnly,
}: InstrumentHeaderProps) {
  const s = useStyles(size);
  return (
    <div className={className} style={{ ...s.header, ...style }}>
      <InstrumentIcon instrument={instrument} size={s.iconSize} />
      {!iconOnly && (
        <span style={s.label}>
          {label ?? instrumentLabel(instrument)}
        </span>
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
      label: {
        fontSize: config.fontSize,
        fontWeight: config.fontWeight,
        color: config.color,
      },
    };
  }, [size]);
}
