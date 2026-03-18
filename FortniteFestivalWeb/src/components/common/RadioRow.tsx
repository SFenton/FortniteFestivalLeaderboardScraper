import { memo } from 'react';
import css from '../modals/Modal.module.css';

export interface RadioRowProps {
  label: string;
  selected: boolean;
  onSelect: () => void;
}

export const RadioRow = memo(function RadioRow({ label, selected, onSelect }: RadioRowProps) {
  return (
    <button
      className={selected ? css.radioRowSelected : css.radioRow}
      onClick={onSelect}
    >
      <span className={selected ? css.radioDotSelected : css.radioDot} />
      <span>{label}</span>
    </button>
  );
});
