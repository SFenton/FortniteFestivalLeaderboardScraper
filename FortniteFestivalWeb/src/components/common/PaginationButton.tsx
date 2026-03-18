/**
 * A single frosted-glass pagination button.
 * Consumers own the layout container; this is just the button.
 */
import { memo, type ReactNode } from 'react';
import css from './PaginationButton.module.css';

export interface PaginationButtonProps {
  children: ReactNode;
  disabled?: boolean;
  onClick: () => void;
}

export const PaginationButton = memo(function PaginationButton({
  children,
  disabled,
  onClick,
}: PaginationButtonProps) {
  return (
    <button
      className={disabled ? css.disabled : css.button}
      disabled={disabled}
      onClick={onClick}
    >
      {children}
    </button>
  );
});
