/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { memo, type CSSProperties } from 'react';
import css from './StatBox.module.css';

interface StatBoxProps {
  label: string;
  value: React.ReactNode;
  color?: string;
  onClick?: () => void;
}

const StatBox = memo(function StatBox({ label, value, color, onClick }: StatBoxProps) {
  const inner = (
    <div className={css.box}>
      <span className={css.value} style={color ? { color } as CSSProperties : undefined}>{value}</span>
      <span className={css.label}>{label}</span>
    </div>
  );
  if (onClick) {
    return (
    <div className={css.clickable} onClick={onClick}>
      {inner}
      <svg className={css.chevron} width="8" height="14" viewBox="0 0 8 14" fill="none">
        <path d="M1.5 1.5L6.5 7L1.5 12.5" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/>
      </svg>
    </div>
  );
  }
  return inner;
});

export default StatBox;
