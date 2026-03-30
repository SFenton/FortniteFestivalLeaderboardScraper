import { memo } from 'react';
import { IoHelpCircleOutline } from 'react-icons/io5';
import { modalStyles } from '../modals/modalStyles';

export interface RadioRowProps {
  label: string;
  hint?: string;
  selected: boolean;
  onSelect: () => void;
  onInfo?: () => void;
}

export const RadioRow = memo(function RadioRow({ label, hint, selected, onSelect, onInfo }: RadioRowProps) {
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
      {onInfo && (
        <span
          role="button"
          tabIndex={0}
          style={modalStyles.radioInfoBtn}
          onClick={e => { e.stopPropagation(); onInfo(); }}
          onKeyDown={e => { if (e.key === 'Enter') { e.stopPropagation(); onInfo(); } }}
        >
          <IoHelpCircleOutline size={18} />
        </span>
      )}
    </button>
  );
});
