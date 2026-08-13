import { memo } from 'react';
import { IoHelpCircleOutline } from 'react-icons/io5';
import { modalStyles } from '../modals/modalStyles';
import PressableButton from './PressableButton';

export interface RadioRowProps {
  label: string;
  hint?: string;
  selected: boolean;
  onSelect: () => void;
  onInfo?: () => void;
  infoLabel?: string;
}

export const RadioRow = memo(function RadioRow({
  label,
  hint,
  selected,
  onSelect,
  onInfo,
  infoLabel,
}: RadioRowProps) {
  return (
    <div style={selected ? modalStyles.radioRowSelected : modalStyles.radioRow}>
      <PressableButton
        style={modalStyles.radioRowControl}
        onPress={onSelect}
        aria-pressed={selected}
      >
        <span style={selected ? modalStyles.radioDotSelected : modalStyles.radioDot} />
        <span style={hint ? modalStyles.radioLabelGroup : undefined}>
          <span>{label}</span>
          {hint && <span style={modalStyles.radioRowHint}>{hint}</span>}
        </span>
      </PressableButton>
      {onInfo && (
        <PressableButton
          style={modalStyles.radioInfoBtn}
          onPress={onInfo}
          aria-label={infoLabel}
        >
          <IoHelpCircleOutline size={18} />
        </PressableButton>
      )}
    </div>
  );
});
