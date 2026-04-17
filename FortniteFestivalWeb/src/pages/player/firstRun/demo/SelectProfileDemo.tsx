/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useMemo, type CSSProperties } from 'react';
import { Display, Justify, Align, PointerEvents } from '@festival/theme';
import FadeIn from '../../../../components/page/FadeIn';
import { SelectProfilePill } from '../../../../components/player/SelectProfilePill';
import { useIsMobileChrome } from '../../../../hooks/ui/useIsMobile';

/* v8 ignore start -- NOOP is passed as prop but never invoked in test (pointerEvents: none) */
const NOOP = () => {};
/* v8 ignore stop */

const wrapStyle: CSSProperties = {
  display: Display.flex,
  justifyContent: Justify.center,
  alignItems: Align.center,
  pointerEvents: PointerEvents.none,
};

export default function SelectProfileDemo() {
  const isMobile = useIsMobileChrome();

  return (
    <FadeIn style={wrapStyle}>
      <SelectProfilePill visible onClick={NOOP} isMobile={isMobile} />
    </FadeIn>
  );
}
