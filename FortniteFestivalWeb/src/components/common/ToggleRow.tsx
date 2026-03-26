import { memo } from 'react';
import { modalStyles as ms } from '../modals/modalStyles';

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
  const rowStyle = {
    ...(large ? ms.toggleRowLarge : ms.toggleRow),
    ...(disabled ? ms.toggleRowDisabled : {}),
  };
  const trackStyle = large
    ? { ...(checked ? ms.toggleTrackLargeOn : ms.toggleTrackLarge), ...(disabled ? ms.toggleTrackDisabled : {}) }
    : { ...ms.toggleTrack, ...(checked ? ms.toggleTrackOn : {}), ...(disabled ? ms.toggleTrackDisabled : {}) };
  const thumbStyle = {
    ...(large ? ms.toggleThumbLarge : ms.toggleThumb),
    ...(checked ? (large ? ms.toggleThumbLargeOn : ms.toggleThumbOn) : {}),
  };

  return (
    <button
      style={rowStyle}
      onClick={disabled ? undefined : onToggle}
      disabled={disabled}
    >
      {icon && <div style={ms.toggleIcon}>{icon}</div>}
      <div style={ms.toggleContent}>
        <div style={large ? { ...ms.toggleLabel, ...ms.toggleLabelLarge } : ms.toggleLabel}>{label}</div>
        {description && <div style={large ? { ...ms.toggleDesc, ...ms.toggleDescLarge } : ms.toggleDesc}>{description}</div>}
      </div>
      <div style={trackStyle}>
        <div style={thumbStyle} />
      </div>
    </button>
  );
});
