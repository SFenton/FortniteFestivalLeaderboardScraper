/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useMemo } from 'react';
import { Link, useLocation, useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { IoChevronBack } from 'react-icons/io5';
import {
  Colors, Font, Weight, Gap, MaxWidth, Layout, ZIndex, IconSize,
  Display, Position, Align, Justify, BoxSizing, CssValue,
  padding, flexRow,
  TRANSITION_MS,
} from '@festival/theme';

export default function BackLink({ fallback, animate = true }: { fallback: string; animate?: boolean }) {
  const location = useLocation();
  const navigate = useNavigate();
  const { t } = useTranslation();
  const backTo = (location.state as { backTo?: string } | null)?.backTo ?? fallback;
  const s = useStyles(animate);

  // Always use history.back() so the destination sees a POP navigation
  // and can restore from cache. The <Link> href is a fallback.
  const handleClick = (e: React.MouseEvent) => {
    e.preventDefault();
    navigate(-1);
  };

  return (
    <div className="sa-top" style={s.wrapper}>
      <Link to={backTo} onClick={handleClick} style={s.backLink}>
        <span style={s.iconSlot}><IoChevronBack size={IconSize.back} /></span>
        {t('common.back')}
      </Link>
    </div>
  );
}

function useStyles(animate: boolean) {
  return useMemo(() => ({
    wrapper: {
      ...flexRow,
      padding: padding(Layout.paddingTop + Gap.md, Layout.paddingHorizontal, Gap.md),
      maxWidth: MaxWidth.card,
      margin: CssValue.marginCenter,
      width: CssValue.full,
      boxSizing: BoxSizing.borderBox,
      position: Position.relative,
      zIndex: ZIndex.popover,
      ...(animate ? { animation: `fadeIn ${TRANSITION_MS}ms ease-out` } : {}),
    } as const,
    backLink: {
      display: Display.inlineFlex,
      alignItems: Align.center,
      gap: Gap.sm,
      color: Colors.textPrimary,
      textDecoration: 'none',
      fontSize: Font.title,
      fontWeight: Weight.bold,
      lineHeight: 1,
      marginLeft: Layout.headerIconNudge,
    } as const,
    iconSlot: {
      display: Display.inlineFlex,
      alignItems: Align.center,
      justifyContent: Justify.center,
      width: Layout.headerIconSlot,
      height: Layout.headerIconSlot,
      flexShrink: 0,
    } as const,
  }), [animate]);
}
