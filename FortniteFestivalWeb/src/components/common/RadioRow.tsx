import { memo } from 'react';
import { modalStyles } from '../modals/modalStyles';

export interface RadioRowProps {
  label: string;
  hint?: string;
  selected: boolean;
  onSelect: () => void;
}

export const RadioRow = memo(function RadioRow({ label, hint, selected, onSelect }: RadioRowProps) {
  return (
    <button
      style={selected ? modalStyles.radioRowSelected : modalStyles.radioRow}
      onClick={onSelect}
    >
      <span style={selected ? modalStyles.radioDotSelected : modalStyles.radioDot} />
      <span style={hint ? modalStyles.radioLabelGroup : undefined}>
        <span>{label}</span>
        {hint && <span style={modalStyles.radioRowHint}>{hint}</span>}
      </span>
    </button>
  );
});
