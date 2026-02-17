/**
 * Shared song-row layout styles used by `SuggestionSongRow`, `StatisticsScreen`
 * (TopSongRow), and partially by `SongRow`.
 *
 * SongRow's layout differs enough that it keeps its own variant of several
 * properties (different padding, opacity, flex shorthand).  The text styles
 * (`songTitle`, `songMeta`) and thumbnail styles (`thumb`) are shared.
 */
import {StyleSheet} from 'react-native';
import {Colors, Radius, Font, Gap, Opacity, Size} from '../theme';

export const songRowStyles = StyleSheet.create({
  /** Outer pressable wrapper (no-op — just an anchor for the style name). */
  songRowPressable: {},
  /** Inner row: horizontal flex with centered items. */
  songRowInner: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: Gap.lg,
    paddingVertical: Gap.lg,
    paddingHorizontal: Gap.lg,
  },
  /** Pressed opacity. */
  songRowInnerPressed: {
    opacity: Opacity.pressed,
  },
  /** Left side: thumbnail + text. */
  songLeft: {
    flexGrow: 1,
    flexShrink: 1,
    minWidth: 0,
    flexDirection: 'row',
    alignItems: 'center',
    gap: Gap.lg,
  },
  /** Right side: score / pills. */
  songRight: {
    flexShrink: 0,
    alignItems: 'flex-end',
    justifyContent: 'center',
    gap: Gap.sm,
  },
  /** Text column between thumbnail and right content. */
  songRowText: {
    flexGrow: 1,
    flexShrink: 1,
    minWidth: 0,
    gap: Gap.xs,
  },
  /** Thumbnail container — 44×44 rounded square. */
  thumbWrap: {
    width: Size.thumb,
    height: Size.thumb,
    borderRadius: Radius.sm,
    overflow: 'hidden',
    backgroundColor: Colors.backgroundCardAlt2,
  },
  /** Thumbnail image — fills the wrap. */
  thumb: {
    width: '100%',
    height: '100%',
  },
  /** Placeholder shown when no image is available. */
  thumbPlaceholder: {
    width: '100%',
    height: '100%',
    backgroundColor: Colors.backgroundCardAlt,
  },
  /** Song title text. */
  songTitle: {
    color: Colors.textPrimary,
    fontWeight: '700',
    fontSize: Font.md,
  },
  /** Song meta / artist text. */
  songMeta: {
    color: Colors.textTertiary,
    fontSize: Font.sm,
  },
  /** Secondary detail text (right-side score, etc). */
  songRightText: {
    color: Colors.textSecondary,
    fontSize: Font.sm,
    fontWeight: '700',
  },
});
