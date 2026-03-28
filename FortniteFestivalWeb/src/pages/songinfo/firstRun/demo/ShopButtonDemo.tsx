/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useMemo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { IoBagHandle } from 'react-icons/io5';
import { Gap, Colors, Font, Weight, Radius, Display, Align, Justify, Layout, IconSize, CssValue, Position, Isolation, padding } from '@festival/theme';
import { useSettings } from '../../../../contexts/SettingsContext';
import anim from '../../../../styles/animations.module.css';

/**
 * Demo component for the SongInfo FRE slide showing the Item Shop button.
 * Matches the production SongDetailHeader pill/circle exactly.
 * Pulse is only active when shop highlighting is enabled in settings.
 */
/* v8 ignore start -- demo component uses SettingsContext */
export default function ShopButtonDemo({ mobile }: { mobile?: boolean }) {
  const { t } = useTranslation();
  const { settings } = useSettings();
  const pulse = !settings.disableShopHighlighting && !settings.hideItemShop;
  const s = useStyles();

  if (mobile) {
    return (
      <div style={s.wrap}>
        <div className={pulse ? anim.shopCircleBreathe : undefined} style={pulse ? s.shopCirclePulse : s.shopCircle}>
          <IoBagHandle size={72} />
        </div>
      </div>
    );
  }

  return (
    <div style={s.wrap}>
      <div className={pulse ? anim.shopBreathe : undefined} style={pulse ? s.shopButtonPulse : s.shopButton}>
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
    shopButton: {
      display: Display.inlineFlex,
      alignItems: Align.center,
      justifyContent: Justify.center,
      padding: padding(0, Layout.pillButtonHeight, 0, Layout.shopButtonPaddingLeft),
      borderRadius: Radius.full,
      backgroundColor: Colors.accentBlue,
      color: Colors.textPrimary,
      fontSize: Font.display,
      fontWeight: Weight.semibold,
      flexShrink: 0,
      height: Layout.shopDesktopHeight,
    } as CSSProperties,
    shopCircle: {
      width: Layout.shopCircleSize,
      height: Layout.shopCircleSize,
      borderRadius: Radius.full,
      backgroundColor: Colors.accentBlue,
      display: Display.flex,
      alignItems: Align.center,
      justifyContent: Justify.center,
      color: Colors.textPrimary,
      flexShrink: 0,
    } as CSSProperties,
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
      backgroundColor: Colors.transparent,
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
      backgroundColor: Colors.transparent,
      isolation: Isolation.isolate,
    } as CSSProperties,
    iconMargin: { marginRight: Gap.lg } as CSSProperties,
  }), []);
}
