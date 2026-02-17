import React, {useCallback, useMemo} from 'react';
import {FlatList, Platform, StyleSheet, View} from 'react-native';
import {Gap} from '../theme';
import {CategoryCard} from '../cards/CategoryCard';
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
    <CategoryCard
      title={cat.title}
      description={cat.description}
      instrumentKey={catInstrumentKey}>
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
    </CategoryCard>
  );
}

const styles = StyleSheet.create({
  songSeparator: {
    height: Gap.md,
  },
});
