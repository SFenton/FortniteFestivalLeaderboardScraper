/**
 * Shared two-column grid / masonry layout styles.
 *
 * Used by `StatisticsScreen`, `SuggestionsScreen`, and `SongDetailsScreen`.
 * All three files had byte-identical definitions (just named differently).
 */
import {StyleSheet} from 'react-native';
import {Colors, Font, LineHeight, Gap, MaxWidth} from '../theme';

export const gridStyles = StyleSheet.create({
  /** Two-column grid container. */
  cardGrid: {
    flexDirection: 'row',
    alignItems: 'flex-start',
    justifyContent: 'center',
    gap: Gap.lg,
  },
  /** Left column — items align to right edge. */
  cardGridColumnLeft: {
    flex: 1,
    gap: Gap.lg,
    alignItems: 'flex-end',
  },
  /** Right column — items align to left edge. */
  cardGridColumnRight: {
    flex: 1,
    gap: Gap.lg,
    alignItems: 'flex-start',
  },

  // ── Section headers (used in StatisticsScreen) ────────────────────

  /** Section header container. */
  sectionHeader: {
    gap: Gap.sm,
    marginBottom: Gap.lg,
    maxWidth: MaxWidth.grid,
    width: '100%',
    alignSelf: 'center',
  },
  /** Section header title text. */
  sectionHeaderTitle: {
    color: Colors.textPrimary,
    fontSize: Font.xl,
    fontWeight: '800',
  },
  /** Section header description text. */
  sectionHeaderDescription: {
    color: Colors.textSecondary,
    fontSize: Font.md,
    lineHeight: LineHeight.lg,
  },
});
