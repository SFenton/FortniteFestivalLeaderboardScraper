/**
 * Shared instrument-picker styles used by FilterModal, SuggestionsFilterModal,
 * and InstrumentSelector. Replaces FilterModal.module.css.
 */
import type { CSSProperties } from 'react';
import { Colors, Gap, Layout, ZIndex, Display, Align, Justify, Position, Overflow, Cursor, TRANSITION_MS, FADE_DURATION, transition } from '@festival/theme';

export const filterStyles = {
  instrumentRow: { display: Display.flex, gap: Gap.md, flexWrap: 'wrap', justifyContent: Justify.center } as CSSProperties,
  instrumentBtn: { width: Layout.demoInstrumentBtn, height: Layout.demoInstrumentBtn, display: Display.flex, alignItems: Align.center, justifyContent: Justify.center, borderRadius: '50%', border: 'none', backgroundColor: 'transparent', cursor: Cursor.pointer, position: Position.relative, overflow: Overflow.hidden } as CSSProperties,
  instrumentCircle: { position: Position.absolute, inset: 0, borderRadius: '50%', backgroundColor: Colors.statusGreen, transform: 'scale(0)', transition: transition('transform', TRANSITION_MS) } as CSSProperties,
  instrumentCircleActive: { position: Position.absolute, inset: 0, borderRadius: '50%', backgroundColor: Colors.statusGreen, transform: 'scale(1)', transition: transition('transform', TRANSITION_MS) } as CSSProperties,
  instrumentIconWrap: { position: Position.relative, zIndex: ZIndex.base, display: Display.flex, alignItems: Align.center, justifyContent: Justify.center } as CSSProperties,
  instrumentFiltersWrap: { display: Display.grid, transition: transition('grid-template-rows', FADE_DURATION) } as CSSProperties,
  instrumentFiltersInner: { overflow: Overflow.hidden, minHeight: 0 } as CSSProperties,
  instrumentLabel: { display: Display.inlineFlex, alignItems: Align.center, gap: Gap.md } as CSSProperties,
} as const;
