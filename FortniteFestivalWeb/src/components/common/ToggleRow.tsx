import { memo, useCallback, type KeyboardEvent } from 'react';
import { IoHelpCircleOutline } from 'react-icons/io5';
import { modalStyles as ms } from '../modals/modalStyles';
import { usePressAction } from '../../hooks/ui/usePressAction';
import PressableButton from './PressableButton';

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
  const handleInfoPress = useCallback(() => {
    onInfo?.();
  }, [onInfo]);
  const infoPressHandlers = usePressAction<HTMLSpanElement>({ onPress: handleInfoPress, disabled: !onInfo, stopPropagation: true });
  const handleInfoKeyDown = useCallback((e: KeyboardEvent<HTMLSpanElement>) => {
    if (!onInfo || (e.key !== 'Enter' && e.key !== ' ')) return;
    e.preventDefault();
    e.stopPropagation();
    onInfo();
  }, [onInfo]);

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
    <PressableButton
      style={rowStyle}
      onPress={onToggle}
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
          {...infoPressHandlers}
          onKeyDown={handleInfoKeyDown}
        >
          <IoHelpCircleOutline size={18} />
        </span>
      )}
      <div style={trackStyle}>
        <div style={thumbStyle} />
      </div>
    </PressableButton>
  );
});
