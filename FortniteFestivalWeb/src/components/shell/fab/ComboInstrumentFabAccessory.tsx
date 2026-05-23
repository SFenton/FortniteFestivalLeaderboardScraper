import { Size } from '@festival/theme';
import { InstrumentIcon } from '../../display/InstrumentIcons';
import type { ServerInstrumentKey } from '@festival/core/api/serverTypes';

const ICON_SIZE = Size.iconSm;
const ROW_STYLE = {
  display: 'flex',
  alignItems: 'center',
  gap: 4,
  lineHeight: 0,
} as const;
const ICON_STYLE = {
  display: 'block',
  flexShrink: 0,
} as const;

/**
 * Right-side icon row attached to a FAB pill showing which instruments are
 * currently selected by the active band-combo filter. Returns null when no
 * instruments are selected (no accessory).
 */
export default function ComboInstrumentFabAccessory({ instruments }: { instruments: readonly ServerInstrumentKey[] }) {
  if (instruments.length === 0) return null;

  return (
    <span data-testid="fab-band-filter-instruments" aria-hidden="true" style={ROW_STYLE}>
      {instruments.map((instrument, index) => (
        <InstrumentIcon
          key={`${instrument}:${index}`}
          instrument={instrument}
          size={ICON_SIZE}
          style={ICON_STYLE}
        />
      ))}
    </span>
  );
}
