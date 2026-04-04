import { memo } from 'react';
import { IoHelpCircleOutline } from 'react-icons/io5';
import { modalStyles as ms } from '../modals/modalStyles';

export interface ToggleRowProps {
  label: React.ReactNode;
  description?: string;
  checked: boolean;
  onToggle: () => void;
  disabled?: boolean;
  icon?: React.ReactNode;
  large?: boolean;
  onInfo?: () => void;
}

export const ToggleRow = memo(function ToggleRow({ label, description, checked, onToggle, disabled, icon, large, onInfo }: ToggleRowProps) {
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
      {onInfo && (
        <span
          role="button"
          tabIndex={0}
          style={ms.toggleInfoBtn}
          onClick={e => { e.stopPropagation(); onInfo(); }}
          onKeyDown={e => { if (e.key === 'Enter') { e.stopPropagation(); onInfo(); } }}
        >
          <IoHelpCircleOutline size={18} />
        </span>
      )}
      <div style={trackStyle}>
        <div style={thumbStyle} />
      </div>
    </button>
  );
});
