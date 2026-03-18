/**
 * Centered page-level message for empty states and errors.
 * Replaces the repeated `<div className={s.center}>` / `<div className={s.centerError}>` pattern.
 */
import { type ReactNode } from 'react';
import css from './PageMessage.module.css';

export interface PageMessageProps {
  children: ReactNode;
  /** Render in error style (red text). */
  error?: boolean;
}

export function PageMessage({ children, error }: PageMessageProps) {
  return <div className={error ? css.error : css.message}>{children}</div>;
}
