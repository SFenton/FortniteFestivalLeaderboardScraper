import { memo } from 'react';
import { modalStyles } from '../modals/modalStyles';

export interface RadioRowProps {
  label: string;
  selected: boolean;
  onSelect: () => void;
}

export const RadioRow = memo(function RadioRow({ label, selected, onSelect }: RadioRowProps) {
  return (
    <button
      style={selected ? modalStyles.radioRowSelected : modalStyles.radioRow}
      onClick={onSelect}
    >
      <span style={selected ? modalStyles.radioDotSelected : modalStyles.radioDot} />
      <span>{label}</span>
    </button>
  );
});
