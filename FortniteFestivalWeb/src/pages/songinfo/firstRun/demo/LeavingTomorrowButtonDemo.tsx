/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useMemo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { IoBagHandle } from 'react-icons/io5';
import { Gap, Colors, Font, Weight, Radius, Display, Align, Justify, Layout, IconSize, CssValue, Position, Isolation, padding } from '@festival/theme';
import anim from '../../../../styles/animations.module.css';

/**
 * Demo component for the SongInfo FRE slide showing the red "leaving tomorrow" button.
 * Shows the Item Shop button with red pulsing to indicate urgency.
 */
/* v8 ignore start -- demo component */
export default function LeavingTomorrowButtonDemo({ mobile }: { mobile?: boolean }) {
  const { t } = useTranslation();
  const s = useStyles();

  if (mobile) {
    return (
      <div style={s.wrap}>
        <div className={anim.shopCircleBreatheRed} style={s.shopCirclePulse}>
          <IoBagHandle size={72} />
        </div>
      </div>
    );
  }

  return (
    <div style={s.wrap}>
      <div className={anim.shopBreatheRed} style={s.shopButtonPulse}>
        <IoBagHandle size={IconSize.md} style={s.iconMargin} />
        {t('shop.itemShop')}
      </div>
    </div>
  );
}
/* v8 ignore stop */

function useStyles() {
  return useMemo(() => ({
    wrap: { display: Display.flex, justifyContent: Justify.center, padding: Gap.sm } as CSSProperties,
    shopButtonPulse: {
      display: Display.inlineFlex,
      alignItems: Align.center,
      justifyContent: Justify.center,
      padding: padding(0, Layout.pillButtonHeight, 0, Layout.shopButtonPaddingLeft),
      borderRadius: Radius.full,
      color: Colors.textPrimary,
      fontSize: Font.display,
      fontWeight: Weight.semibold,
      flexShrink: 0,
      height: Layout.shopDesktopHeight,
      position: Position.relative,
      backgroundColor: CssValue.transparent,
      isolation: Isolation.isolate,
    } as CSSProperties,
    shopCirclePulse: {
      width: Layout.shopCircleSize,
      height: Layout.shopCircleSize,
      borderRadius: Radius.full,
      display: Display.flex,
      alignItems: Align.center,
      justifyContent: Justify.center,
      color: Colors.textPrimary,
      flexShrink: 0,
      position: Position.relative,
      backgroundColor: CssValue.transparent,
      isolation: Isolation.isolate,
    } as CSSProperties,
    iconMargin: { marginRight: Gap.md } as CSSProperties,
  }), []);
}
