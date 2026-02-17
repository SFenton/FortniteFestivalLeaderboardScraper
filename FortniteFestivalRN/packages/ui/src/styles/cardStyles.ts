/**
 * Shared card layout styles used by `SuggestionCard` and `StatisticsScreen`
 * (TopSongsCard).
 *
 * `StatisticsInstrumentCard` has a different visual treatment (larger radius,
 * border, different gaps) and keeps its own styles.
 */
import {StyleSheet} from 'react-native';
import {Colors, Radius, Font, LineHeight, Gap, Opacity, Size, MaxWidth} from '../theme';

export const cardStyles = StyleSheet.create({
  /** Card container — rounded, padded, full-width up to maxWidth. */
  card: {
    borderRadius: Radius.md,
    padding: Gap.xl,
    gap: Gap.md,
    maxWidth: MaxWidth.card,
    width: '100%',
  },
  /** Header row: title left, icon right. */
  cardHeaderRow: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
  },
  /** Left side of card header (title + subtitle). */
  cardHeaderLeft: {
    flex: 1,
    minWidth: 0,
    gap: Gap.sm,
  },
  /** Right side of card header (icon). */
  cardHeaderRight: {
    flexShrink: 0,
    paddingLeft: Gap.xl,
    justifyContent: 'center',
    alignItems: 'center',
  },
  /** Card title text. */
  cardTitle: {
    color: Colors.textPrimary,
    fontSize: Font.lg,
    fontWeight: '700',
  },
  /** Card subtitle / description text. */
  cardSubtitle: {
    color: Colors.textSecondary,
    fontSize: 13,
    lineHeight: LineHeight.md,
  },
  /** Instrument icon in the header. */
  cardHeaderIcon: {
    width: Size.iconMd,
    height: Size.iconMd,
    opacity: Opacity.icon,
  },
  /** List within a card (gap + slight top margin). */
  cardSongList: {
    gap: Gap.md,
    marginTop: Gap.sm,
  },
});
