import React, {useCallback, useMemo} from 'react';
import {FlatList, Image, Platform, StyleSheet, Text, View} from 'react-native';
import {FrostedSurface} from '../FrostedSurface';
import {getInstrumentIconSource} from '../instruments/instrumentVisuals';
import {SuggestionSongRow} from './SuggestionSongRow';
import type {LeaderboardData, Song, InstrumentShowSettings} from '@festival/core';
import type {SuggestionCategory, SuggestionSongItem} from '@festival/core';

export function SuggestionCard(props: {
  cat: SuggestionCategory;
  useCompactLayout: boolean;
  songById: ReadonlyMap<string, Song>;
  scoresIndex: Readonly<Record<string, LeaderboardData | undefined>>;
  instrumentQuerySettings: InstrumentShowSettings;
  /** When true, hide album art thumbnails on song rows. */
  hideArt?: boolean;
  onOpenSong: (songId: string, title: string) => void;
}) {
  const {cat} = props;

  const catInstrumentKey = useMemo(() => {
    let suffix: string | undefined;
    if (cat.key.startsWith('unplayed_')) {
      suffix = cat.key.slice('unplayed_'.length);
      if (suffix === 'any' || suffix.startsWith('any_')) return undefined;
    } else if (cat.key.startsWith('unfc_')) {
      suffix = cat.key.slice('unfc_'.length);
    } else if (cat.key.startsWith('almost_elite_')) {
      suffix = cat.key.slice('almost_elite_'.length);
    } else if (cat.key.startsWith('pct_push_')) {
      suffix = cat.key.slice('pct_push_'.length);
    }

    if (!suffix) return undefined;

    const known: Array<'pro_guitar' | 'pro_bass' | 'guitar' | 'bass' | 'drums' | 'vocals'> = ['pro_guitar', 'pro_bass', 'guitar', 'bass', 'drums', 'vocals'];
    for (const k of known) {
      if (suffix === k || suffix.startsWith(`${k}_`)) return k;
    }

    return undefined;
  }, [cat.key]);

  const useVirtualSongsList = cat.songs.length > 12;

  const renderSong = useCallback(({item}: {item: SuggestionSongItem}) => {
    return (
      <SuggestionSongRow
        categoryKey={cat.key}
        item={item}
        useCompactLayout={props.useCompactLayout}
        hideArt={props.hideArt}
        song={props.songById.get(item.songId)}
        leaderboardData={props.scoresIndex[item.songId]}
        settings={props.instrumentQuerySettings}
        onOpenSong={props.onOpenSong}
      />
    );
  }, [cat.key, props.instrumentQuerySettings, props.onOpenSong, props.scoresIndex, props.songById, props.useCompactLayout, props.hideArt]);

  const songSeparator = useCallback(() => <View style={styles.songSeparator} />, []);

  return (
    <FrostedSurface style={styles.card} tint="dark" intensity={18}>
      <View style={styles.cardHeaderRow}>
        <View style={styles.cardHeaderLeft}>
          <Text style={styles.cardTitle} numberOfLines={1}>
            {cat.title}
          </Text>
          <Text style={styles.cardSubtitle}>{cat.description}</Text>
        </View>

        {catInstrumentKey ? (
          <View style={styles.cardHeaderRight}>
            <Image source={getInstrumentIconSource(catInstrumentKey)} style={styles.cardHeaderIcon} resizeMode="contain" />
          </View>
        ) : null}
      </View>

      <View style={styles.songList}>
        {useVirtualSongsList ? (
          <FlatList
            data={cat.songs}
            keyExtractor={s => `${cat.key}:${s.songId}`}
            renderItem={renderSong}
            ItemSeparatorComponent={songSeparator}
            scrollEnabled={false}
            keyboardShouldPersistTaps="handled"
            removeClippedSubviews={Platform.OS === 'android'}
            initialNumToRender={8}
            maxToRenderPerBatch={6}
            updateCellsBatchingPeriod={24}
            windowSize={5}
          />
        ) : (
          cat.songs.map(s => (
            <SuggestionSongRow
              key={`${cat.key}:${s.songId}`}
              categoryKey={cat.key}
              item={s}
              useCompactLayout={props.useCompactLayout}
              hideArt={props.hideArt}
              song={props.songById.get(s.songId)}
              leaderboardData={props.scoresIndex[s.songId]}
              settings={props.instrumentQuerySettings}
              onOpenSong={props.onOpenSong}
            />
          ))
        )}
      </View>
    </FrostedSurface>
  );
}

const styles = StyleSheet.create({
  card: {
    borderRadius: 12,
    padding: 12,
    gap: 8,
    maxWidth: 1080,
    width: '100%',
  },
  cardHeaderRow: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
  },
  cardHeaderLeft: {
    flex: 1,
    minWidth: 0,
    gap: 4,
  },
  cardHeaderRight: {
    flexShrink: 0,
    paddingLeft: 12,
    justifyContent: 'center',
    alignItems: 'center',
  },
  cardTitle: {
    color: '#FFFFFF',
    fontSize: 16,
    fontWeight: '700',
  },
  cardHeaderIcon: {
    width: 28,
    height: 28,
    opacity: 0.92,
  },
  cardSubtitle: {
    color: '#D7DEE8',
    fontSize: 13,
    lineHeight: 18,
  },
  songList: {
    gap: 8,
    marginTop: 4,
  },
  songSeparator: {
    height: 8,
  },
});
