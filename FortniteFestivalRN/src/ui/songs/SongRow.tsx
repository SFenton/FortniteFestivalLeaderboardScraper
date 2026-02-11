import React from 'react';
import {Image, Pressable, StyleSheet, Text, View} from 'react-native';
import {FrostedSurface} from '../FrostedSurface';
import {getInstrumentIconSource} from '../instruments/instrumentVisuals';
import type {InstrumentKey} from '../../core/instruments';

// ── Public types ────────────────────────────────────────────────────

/** Colour pair for an instrument chip (circle behind the icon). */
export type InstrumentChipVisual = {
  instrumentKey: InstrumentKey;
  fill: string;
  stroke: string;
};

/** Everything the row needs to render – no domain models required. */
export type SongRowDisplayData = {
  title: string;
  artist: string;
  year?: number;
  imageUri?: string;
  /** When omitted the instrument chips are hidden. */
  instruments?: InstrumentChipVisual[];
};

// ── Component ───────────────────────────────────────────────────────

export const SongRow = React.memo(function SongRow(props: {
  data: SongRowDisplayData;
  /** Use stacked (narrow) layout. Default: false. */
  compact?: boolean;
  /** Called when the row is tapped. Omit to make the row non-interactive. */
  onPress?: () => void;
}) {
  const {data, compact, onPress} = props;
  const {title, artist, year, imageUri, instruments} = data;
  const hasArt = !!imageUri;

  const inner = (pressed: boolean) => (
    <FrostedSurface style={[styles.rowSurface, pressed && styles.rowSurfacePressed]} tint="dark" intensity={12}>
      {compact ? (
        <View style={styles.rowInnerCompact}>
          <View style={[styles.compactTopRow, !hasArt && styles.compactTopRowNoArt]}>
            {hasArt && (
              <View style={styles.thumbWrap}>
                <Image source={{uri: imageUri}} style={styles.thumb} resizeMode="cover" />
              </View>
            )}

            <View style={[styles.rowText, !hasArt && styles.rowTextCentered]}>
              <Text numberOfLines={1} style={styles.songTitle}>{title}</Text>
              <Text numberOfLines={1} style={styles.songMeta}>
                {artist}{artist && year ? ' • ' : ''}{year ?? ''}
              </Text>
            </View>
          </View>

          {instruments && instruments.length > 0 && (
            <View style={styles.instrumentRowCompact}>
              {instruments.map(s => (
                <View
                  key={s.instrumentKey}
                  style={[styles.instrumentChipCompact, {backgroundColor: s.fill, borderColor: s.stroke}]}
                >
                  <Image source={getInstrumentIconSource(s.instrumentKey)} style={styles.instrumentIconCompact} resizeMode="contain" />
                </View>
              ))}
            </View>
          )}
        </View>
      ) : (
        <View style={styles.rowInner}>
          <View style={styles.left}>
            {hasArt && (
              <View style={styles.thumbWrap}>
                <Image source={{uri: imageUri}} style={styles.thumb} resizeMode="cover" />
              </View>
            )}

            <View style={[styles.rowText, !hasArt && styles.rowTextCentered]}>
              <Text numberOfLines={1} style={styles.songTitle}>{title}</Text>
              <Text numberOfLines={1} style={styles.songMeta}>
                {artist}{artist && year ? ' • ' : ''}{year ?? ''}
              </Text>
            </View>
          </View>

          {instruments && instruments.length > 0 && (
            <View style={styles.instrumentRow}>
              {instruments.map(s => (
                <View
                  key={s.instrumentKey}
                  style={[styles.instrumentChip, {backgroundColor: s.fill, borderColor: s.stroke}]}
                >
                  <Image source={getInstrumentIconSource(s.instrumentKey)} style={styles.instrumentIcon} resizeMode="contain" />
                </View>
              ))}
            </View>
          )}
        </View>
      )}
    </FrostedSurface>
  );

  if (onPress) {
    return (
      <Pressable onPress={onPress} style={styles.rowPressable} accessibilityRole="button" accessibilityLabel={`Open ${title}`}>
        {({pressed}) => inner(pressed)}
      </Pressable>
    );
  }

  // Non-interactive – just render the surface.
  return <View style={styles.rowPressable}>{inner(false)}</View>;
});

// ── Styles ──────────────────────────────────────────────────────────

const styles = StyleSheet.create({
  rowPressable: {
    marginBottom: 8,
    maxWidth: 600,
    width: '100%',
    alignSelf: 'center',
  },
  rowSurface: {
    borderRadius: 12,
  },
  rowSurfacePressed: {
    opacity: 0.92,
  },
  rowInner: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    paddingHorizontal: 12,
    paddingVertical: 10,
  },
  rowInnerCompact: {
    paddingHorizontal: 12,
    paddingVertical: 10,
    gap: 10,
  },
  compactTopRow: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 10,
    minWidth: 0,
  },
  compactTopRowNoArt: {
    justifyContent: 'center',
  },
  left: {
    flex: 1,
    flexDirection: 'row',
    alignItems: 'center',
    gap: 10,
    minWidth: 0,
  },
  rowText: {
    flex: 1,
    minWidth: 0,
  },
  rowTextCentered: {
    flex: undefined,
    alignItems: 'center',
  },
  instrumentRow: {
    flexDirection: 'row',
    gap: 6,
    alignItems: 'center',
    flexShrink: 0,
    marginLeft: 10,
  },
  instrumentRowCompact: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: 8,
    alignItems: 'center',
    justifyContent: 'center',
    alignSelf: 'center',
  },
  instrumentChip: {
    width: 40,
    height: 40,
    borderRadius: 20,
    borderWidth: 2,
    alignItems: 'center',
    justifyContent: 'center',
  },
  instrumentChipCompact: {
    width: 34,
    height: 34,
    borderRadius: 17,
    borderWidth: 2,
    alignItems: 'center',
    justifyContent: 'center',
  },
  instrumentIcon: {
    width: 32,
    height: 32,
  },
  instrumentIconCompact: {
    width: 24,
    height: 24,
  },
  thumbWrap: {
    width: 44,
    height: 44,
    borderRadius: 10,
    overflow: 'hidden',
    backgroundColor: '#0B1220',
    borderWidth: 1,
    borderColor: '#263244',
  },
  thumb: {
    width: '100%',
    height: '100%',
  },
  thumbPlaceholder: {
    flex: 1,
    backgroundColor: '#0B1220',
  },
  songTitle: {
    color: '#FFFFFF',
    fontSize: 14,
    fontWeight: '700',
  },
  songMeta: {
    color: '#B8C0CC',
    fontSize: 12,
    marginTop: 2,
  },
});
