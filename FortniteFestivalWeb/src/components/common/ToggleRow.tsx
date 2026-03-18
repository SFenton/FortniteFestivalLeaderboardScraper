import { memo } from 'react';
import css from '../modals/Modal.module.css';

export interface ToggleRowProps {
  label: React.ReactNode;
  description?: string;
  checked: boolean;
  onToggle: () => void;
  disabled?: boolean;
  icon?: React.ReactNode;
  large?: boolean;
}

export const ToggleRow = memo(function ToggleRow({ label, description, checked, onToggle, disabled, icon, large }: ToggleRowProps) {
  const rowClass = `${large ? css.toggleRowLarge : css.toggleRow} ${disabled ? css.toggleRowDisabled : ''}`;
  const trackClass = `${large ? css.toggleTrackLarge : css.toggleTrack} ${checked ? css.toggleTrackOn : ''} ${disabled ? css.toggleTrackDisabled : ''}`;
  const thumbClass = `${large ? css.toggleThumbLarge : css.toggleThumb} ${checked ? (large ? css.toggleThumbLargeOn : css.toggleThumbOn) : ''}`;

  return (
    <button
      className={rowClass}
      onClick={disabled ? undefined : onToggle}
      disabled={disabled}
    >
      {icon && <div className={css.toggleIcon}>{icon}</div>}
      <div className={css.toggleContent}>
        <div className={`${css.toggleLabel} ${large ? css.toggleLabelLarge : ''}`}>{label}</div>
        {description && <div className={`${css.toggleDesc} ${large ? css.toggleDescLarge : ''}`}>{description}</div>}
      </div>
      <div className={trackClass}>
        <div className={thumbClass} />
      </div>
    </button>
  );
});
