/**
 * Shared pill styles for percentile, percent, season, difficulty, and FC badges.
 *
 * These are used by `SongRow`, `SuggestionSongRow`, and `StatisticsScreen`.
 * When a component needs to override a property (e.g. SongRow uses a larger
 * fontSize or adds minWidth), spread the base and apply the override locally.
 */
import {StyleSheet} from 'react-native';
import {Colors, Radius, Font, Gap, Size} from '../theme';

export const pillStyles = StyleSheet.create({
  /** Base percentile pill (blue background). */
  percentilePill: {
    backgroundColor: Colors.badgeBlueBg,
    borderRadius: Radius.sm,
    paddingHorizontal: Gap.md,
    paddingVertical: 3,
    borderWidth: 2,
    borderColor: Colors.transparent,
    alignItems: 'center',
    justifyContent: 'center',
  },
  /** Gold modifier — spread over `percentilePill` for top-5% scores. */
  percentilePillGold: {
    backgroundColor: Colors.goldBg,
    borderColor: Colors.gold,
  },
  /** Text inside a percentile pill. */
  percentilePillText: {
    color: Colors.textPrimary,
    fontWeight: '800',
    fontSize: Font.sm,
  },
  /** Gold text modifier for percentile pill. */
  percentilePillTextGold: {
    color: Colors.gold,
  },
  /** FC badge (gold border + gold bg). */
  fcBadge: {
    backgroundColor: Colors.goldBg,
    borderColor: Colors.gold,
    borderWidth: 2,
    borderRadius: Radius.xs,
    paddingHorizontal: Gap.md,
    minWidth: Size.pillMinWidth,
    alignItems: 'center',
    justifyContent: 'center',
  },
  /** FC badge text. */
  fcBadgeText: {
    color: Colors.gold,
    fontSize: Font.md,
    fontWeight: '800',
  },
});
