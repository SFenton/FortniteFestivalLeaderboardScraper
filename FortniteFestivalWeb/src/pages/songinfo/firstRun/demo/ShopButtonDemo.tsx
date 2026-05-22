/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useMemo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { IoBagHandle } from 'react-icons/io5';
import { Gap, Colors, Font, Weight, Radius, Display, Align, Justify, Layout, IconSize, Position, Isolation, BoxSizing, Overflow, opaqueGlass, padding } from '@festival/theme';
import { useSettings } from '../../../../contexts/SettingsContext';
import anim from '../../../../styles/animations.module.css';

type ShopButtonDemoProps = {
  mobile?: boolean;
  tone?: 'default' | 'new';
};

/**
 * Demo component for the SongInfo FRE slide showing the Item Shop button.
 * Matches the production SongDetailHeader pill/circle exactly.
 * Pulse is only active when shop highlighting is enabled in settings.
 */
/* v8 ignore start -- demo component uses SettingsContext */
export default function ShopButtonDemo({ mobile, tone = 'default' }: ShopButtonDemoProps) {
  const { t } = useTranslation();
  const { settings } = useSettings();
  const pulse = !settings.disableShopHighlighting && !settings.hideItemShop;
  const s = useStyles();
  const pulseClass = tone === 'new' ? anim.shopBreatheGold : anim.shopBreathe;

  if (mobile) {
    return (
      <div style={s.wrap}>
        <div className={pulse ? pulseClass : undefined} style={pulse ? s.shopMobileButtonPulse : s.shopMobileButton}>
          <IoBagHandle size={IconSize.action} />
          <span style={s.mobileShopLabel}>{t('shop.itemShop')}</span>
        </div>
      </div>
    );
  }

  return (
    <div style={s.wrap}>
      <div className={pulse ? pulseClass : undefined} style={pulse ? s.shopButtonPulse : s.shopButton}>
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
      backgroundColor: Colors.statusGreenStroke,
      color: Colors.textPrimary,
      fontSize: Font.display,
      fontWeight: Weight.semibold,
      flexShrink: 0,
      height: Layout.shopDesktopHeight,
    } as CSSProperties,
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
    shopMobileButton: {
      display: Display.inlineFlex,
      alignItems: Align.center,
      justifyContent: Justify.center,
      gap: Gap.sm,
      minWidth: Layout.pillButtonHeight,
      maxWidth: 132,
      height: Layout.pillButtonHeight,
      borderRadius: Radius.full,
      padding: padding(0, Gap.lg),
      backgroundColor: Colors.statusGreenStroke,
      color: Colors.textPrimary,
      flexShrink: 0,
      boxSizing: BoxSizing.borderBox,
      whiteSpace: 'nowrap',
      overflow: Overflow.hidden,
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
    iconMargin: { marginRight: Gap.lg } as CSSProperties,
  }), []);
}
