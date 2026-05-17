import { memo, useCallback, type KeyboardEvent } from 'react';
import { IoHelpCircleOutline } from 'react-icons/io5';
import { modalStyles } from '../modals/modalStyles';
import { usePressAction } from '../../hooks/ui/usePressAction';
import PressableButton from './PressableButton';

export interface RadioRowProps {
  label: string;
  hint?: string;
  selected: boolean;
  onSelect: () => void;
  onInfo?: () => void;
}

export const RadioRow = memo(function RadioRow({ label, hint, selected, onSelect, onInfo }: RadioRowProps) {
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

  return (
    <PressableButton
      style={selected ? modalStyles.radioRowSelected : modalStyles.radioRow}
      onPress={onSelect}
    >
      <span style={selected ? modalStyles.radioDotSelected : modalStyles.radioDot} />
      <span style={hint ? modalStyles.radioLabelGroup : undefined}>
        <span>{label}</span>
        {hint && <span style={modalStyles.radioRowHint}>{hint}</span>}
      </span>
      {onInfo && (
        <span
          role="button"
          tabIndex={0}
          style={modalStyles.radioInfoBtn}
          {...infoPressHandlers}
          onKeyDown={handleInfoKeyDown}
        >
          <IoHelpCircleOutline size={18} />
        </span>
      )}
    </PressableButton>
  );
});
