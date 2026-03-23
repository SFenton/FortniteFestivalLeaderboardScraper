/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
/**
 * Reusable instrument icon + label header at predefined size scales.
 *
 * Usage:
 *   <InstrumentHeader instrument={inst} size={InstrumentHeaderSize.MD} />
 *   <InstrumentHeader instrument={inst} size={InstrumentHeaderSize.SM} label="Custom" />
 */
import { memo } from 'react';
import { InstrumentHeaderSize } from '@festival/core';
import { serverInstrumentLabel as instrumentLabel, type ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import { Size } from '@festival/theme';
import { InstrumentIcon } from './InstrumentIcons';
import css from './InstrumentHeader.module.css';

const ICON_SIZE: Record<InstrumentHeaderSize, number> = {
  [InstrumentHeaderSize.XS]: Size.iconTab,        // 20
  [InstrumentHeaderSize.SM]: Size.iconInstrumentSm, // 36
  [InstrumentHeaderSize.MD]: Size.iconInstrument,   // 48
  [InstrumentHeaderSize.LG]: Size.iconInstrumentLg, // 56
  [InstrumentHeaderSize.XL]: Size.iconInstrumentLg, // 56
};

const GAP_CLASS: Record<InstrumentHeaderSize, string> = {
  [InstrumentHeaderSize.XS]: css.xs!,
  [InstrumentHeaderSize.SM]: css.sm!,
  [InstrumentHeaderSize.MD]: css.md!,
  [InstrumentHeaderSize.LG]: css.lg!,
  [InstrumentHeaderSize.XL]: css.xl!,
};

const LABEL_CLASS: Record<InstrumentHeaderSize, string> = {
  [InstrumentHeaderSize.XS]: css.labelXs!,
  [InstrumentHeaderSize.SM]: css.labelSm!,
  [InstrumentHeaderSize.MD]: css.labelMd!,
  [InstrumentHeaderSize.LG]: css.labelLg!,
  [InstrumentHeaderSize.XL]: css.labelXl!,
};

export interface InstrumentHeaderProps {
  instrument: InstrumentKey;
  size: InstrumentHeaderSize;
  /** Override the label text. Defaults to the i18n instrument label. */
  label?: string;
  /** Extra className on the outer wrapper. */
  className?: string;
  /** Extra style on the outer wrapper. */
  style?: React.CSSProperties;
  /** Hide the text label, render icon only. */
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
  const wrapperClass = className
    ? `${css.header} ${GAP_CLASS[size]} ${className}`
    : `${css.header} ${GAP_CLASS[size]}`;

  return (
    <div className={wrapperClass} style={style}>
      <InstrumentIcon instrument={instrument} size={ICON_SIZE[size]} />
      {!iconOnly && (
        <span className={LABEL_CLASS[size]}>
          {label ?? instrumentLabel(instrument)}
        </span>
      )}
    </div>
  );
});

export default InstrumentHeader;
