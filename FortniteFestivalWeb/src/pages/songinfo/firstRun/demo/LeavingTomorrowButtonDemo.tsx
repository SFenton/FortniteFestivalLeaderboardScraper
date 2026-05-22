/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useMemo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { IoBagHandle } from 'react-icons/io5';
import { Gap, Colors, Font, Weight, Radius, Display, Align, Justify, Layout, IconSize, Position, Isolation, BoxSizing, Overflow, opaqueGlass, padding } from '@festival/theme';
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
        <div className={anim.shopBreatheRed} style={s.shopMobileButtonPulse}>
          <IoBagHandle size={IconSize.action} />
          <span style={s.mobileShopLabel}>{t('shop.itemShop')}</span>
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
      ...opaqueGlass,
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
      isolation: Isolation.isolate,
    } as CSSProperties,
    shopMobileButtonPulse: {
      ...opaqueGlass,
      display: Display.inlineFlex,
      alignItems: Align.center,
      justifyContent: Justify.center,
      gap: Gap.sm,
      minWidth: Layout.pillButtonHeight,
      maxWidth: 132,
      height: Layout.pillButtonHeight,
      borderRadius: Radius.full,
      padding: padding(0, Gap.lg),
      color: Colors.textPrimary,
      flexShrink: 0,
      position: Position.relative,
      isolation: Isolation.isolate,
      boxSizing: BoxSizing.borderBox,
      whiteSpace: 'nowrap',
      overflow: Overflow.hidden,
    } as CSSProperties,
    mobileShopLabel: {
      minWidth: 0,
      overflow: Overflow.hidden,
      textOverflow: 'ellipsis',
      whiteSpace: 'nowrap',
      fontSize: Font.sm,
      fontWeight: Weight.semibold,
      lineHeight: 1,
    } as CSSProperties,
    iconMargin: { marginRight: Gap.md } as CSSProperties,
  }), []);
}
