/**
 * A generic frosted-glass card with a header row (title, description, optional
 * instrument icon) and a children slot for song lists or other content.
 *
 * Used by `SuggestionCard` and `TopSongsCard` to avoid duplicating the card
 * shell layout.
 */
import React from 'react';
import {Image, Text, View} from 'react-native';
import type {StyleProp, ViewStyle} from 'react-native';
import {FrostedSurface} from '../FrostedSurface';
import {getInstrumentIconSource} from '../instruments/instrumentVisuals';
import {cardStyles} from '../styles/cardStyles';
import type {InstrumentKey} from '@festival/core';

// ── Props ───────────────────────────────────────────────────────────

export interface CategoryCardProps {
  /** Card heading. */
  title: string;
  /** Subtitle / description shown below the title. */
  description: string;
  /** Max lines for the title. Default 1. */
  titleNumberOfLines?: number;
  /** Max lines for the description. Omit for unlimited. */
  descriptionNumberOfLines?: number;
  /** Instrument icon shown on the right side of the header. */
  instrumentKey?: InstrumentKey;
  /** Content rendered below the header (song rows, etc.). */
  children: React.ReactNode;
  /** Extra style applied to the outer FrostedSurface. */
  style?: StyleProp<ViewStyle>;
  /** Extra style applied to the header row (e.g. alignment overrides). */
  headerRowStyle?: StyleProp<ViewStyle>;
}

// ── Component ───────────────────────────────────────────────────────

export const CategoryCard = React.memo(function CategoryCard(props: CategoryCardProps) {
  const {
    title,
    description,
    titleNumberOfLines = 1,
    descriptionNumberOfLines,
    instrumentKey,
    children,
    style,
    headerRowStyle,
  } = props;

  return (
    <FrostedSurface style={[cardStyles.card, style]} tint="dark" intensity={18}>
      <View style={[cardStyles.cardHeaderRow, headerRowStyle]}>
        <View style={cardStyles.cardHeaderLeft}>
          <Text style={cardStyles.cardTitle} numberOfLines={titleNumberOfLines}>
            {title}
          </Text>
          <Text style={cardStyles.cardSubtitle} numberOfLines={descriptionNumberOfLines}>
            {description}
          </Text>
        </View>

        {instrumentKey ? (
          <View style={cardStyles.cardHeaderRight}>
            <Image
              source={getInstrumentIconSource(instrumentKey)}
              style={cardStyles.cardHeaderIcon}
              resizeMode="contain"
            />
          </View>
        ) : null}
      </View>

      <View style={cardStyles.cardSongList}>
        {children}
      </View>
    </FrostedSurface>
  );
});
