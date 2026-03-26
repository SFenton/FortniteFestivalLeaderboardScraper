import { memo, useContext, useMemo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import {
  Colors, Font, Weight, Gap, Radius, Border, IconSize,
  Display, TextAlign,
  border, padding,
} from '@festival/theme';
import { FestivalContext } from '../../../contexts/FestivalContext';

export default memo(function SeasonPill({ season, current }: { season: number; current?: boolean }) {
  const { t } = useTranslation();
  const ctx = useContext(FestivalContext);
  const currentSeason = ctx?.state.currentSeason ?? 0;
  const isCurrent = current ?? (currentSeason > 0 && season === currentSeason);
  const s = useStyles(isCurrent);
  return <span style={s.pill}>{t('format.seasonShort', { season })}</span>;
});

function useStyles(isCurrent: boolean) {
  return useMemo(() => ({
    pill: {
      flexShrink: 0,
      width: IconSize.xl,
      textAlign: TextAlign.center,
      padding: padding(Gap.xs, Gap.sm),
      borderRadius: Radius.xs,
      backgroundColor: isCurrent ? Colors.textSecondary : Colors.surfaceSubtle,
      color: isCurrent ? Colors.surfaceSubtle : Colors.textSecondary,
      fontSize: Font.lg,
      fontWeight: Weight.semibold,
      border: border(Border.thick, isCurrent ? Colors.surfaceSubtle : Colors.borderSubtle),
      display: Display.inlineBlock,
    } as CSSProperties,
  }), [isCurrent]);
}
