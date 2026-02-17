/**
 * Shared structural layout for song rows that use the `songRowStyles` pattern.
 *
 * Renders: Pressable > thumbnail + title/artist + rightContent
 *          optional bottomContent below the main row.
 *
 * Used by `SuggestionSongRow` and `TopSongRow`. (The main `SongRow` uses a
 * different visual treatment with `FrostedSurface` and its own local styles.)
 */
import React from 'react';
import {Image, Pressable, Text, View} from 'react-native';
import type {StyleProp, ViewStyle} from 'react-native';
import {MarqueeText} from '../MarqueeText';
import {songRowStyles} from '../styles/songRowStyles';

// ── Props ───────────────────────────────────────────────────────────

export interface SongRowShellProps {
  /** Song title. */
  title: string;
  /** Artist name. */
  artist: string;
  /** Album art URI. When undefined/empty a placeholder is rendered. */
  imageUri?: string;
  /** Hide the thumbnail entirely (e.g. compact suggestions layout). */
  hideArt?: boolean;
  /**
   * Use `MarqueeText` (auto-scrolling) for title & artist.
   * Default `false` → plain `Text numberOfLines={1}`.
   */
  useMarquee?: boolean;
  /** Meta line below the title. Defaults to `artist` (or `artist • year`). */
  metaText?: string;
  /** Called when the row is tapped. */
  onPress?: () => void;
  /** Accessibility label override. */
  accessibilityLabel?: string;
  /** Content rendered to the right of the title/artist. */
  rightContent?: React.ReactNode;
  /** Content rendered below the main row (full width). */
  bottomContent?: React.ReactNode;
  /** Extra style applied to the outermost wrapper. */
  style?: StyleProp<ViewStyle>;
}

// ── Component ───────────────────────────────────────────────────────

export const SongRowShell = React.memo(function SongRowShell(props: SongRowShellProps) {
  const {
    title,
    artist,
    imageUri,
    hideArt,
    useMarquee,
    metaText,
    onPress,
    accessibilityLabel,
    rightContent,
    bottomContent,
    style,
  } = props;

  const meta = metaText ?? artist;

  const content = (pressed: boolean) => (
    <View style={[songRowStyles.songRowPressable, pressed && songRowStyles.songRowInnerPressed]}>
      <View style={songRowStyles.songRowInner}>
        {/* ── Left: thumbnail + text ── */}
        <View style={songRowStyles.songLeft}>
          {!hideArt && (
            <View style={songRowStyles.thumbWrap}>
              {imageUri ? (
                <Image source={{uri: imageUri}} style={songRowStyles.thumb} resizeMode="cover" />
              ) : (
                <View style={songRowStyles.thumbPlaceholder} />
              )}
            </View>
          )}

          <View style={songRowStyles.songRowText}>
            {useMarquee ? (
              <>
                <MarqueeText text={title} textStyle={songRowStyles.songTitle} />
                <MarqueeText text={meta} textStyle={songRowStyles.songMeta} />
              </>
            ) : (
              <>
                <Text numberOfLines={1} style={songRowStyles.songTitle}>
                  {title || '(unknown)'}
                </Text>
                <Text numberOfLines={1} style={songRowStyles.songMeta}>
                  {meta || '(unknown)'}
                </Text>
              </>
            )}
          </View>
        </View>

        {/* ── Right: caller-supplied content ── */}
        {rightContent}
      </View>

      {/* ── Bottom: caller-supplied content (full width) ── */}
      {bottomContent}
    </View>
  );

  if (!onPress) {
    return <View style={style}>{content(false)}</View>;
  }

  return (
    <Pressable
      onPress={onPress}
      style={[songRowStyles.songRowPressable, style]}
      accessibilityRole="button"
      accessibilityLabel={accessibilityLabel ?? `Open ${title}`}
    >
      {({pressed}) => content(pressed)}
    </Pressable>
  );
});
