/**
 * Generic host screen for Windows that manages the list → song‐details
 * drill-down pattern.  Replaces the per-screen `WindowsSongsHost`,
 * `WindowsStatisticsHost`, and `WindowsSuggestionsHost` files.
 *
 * When a song is selected the flyout chrome is hidden and a full-screen
 * `SongDetailsView` is shown.  Pressing back returns to the list.
 */
import React from 'react';

import {useWindowsFlyoutUi} from '../navigation/windowsFlyoutUi';
import {SongDetailsView} from './SongDetailsScreen';

export interface WindowsHostScreenProps {
  /** The list screen component to render (e.g. `SongsScreen`). */
  ListComponent: React.ComponentType<{onOpenSong: (songId: string, title: string) => void}>;
}

export function WindowsHostScreen({ListComponent}: WindowsHostScreenProps) {
  const [songId, setSongId] = React.useState<string | null>(null);
  const {setChromeHidden} = useWindowsFlyoutUi();

  React.useEffect(() => {
    setChromeHidden(songId != null);
    return () => setChromeHidden(false);
  }, [setChromeHidden, songId]);

  if (songId) {
    return <SongDetailsView songId={songId} showBack onBack={() => setSongId(null)} />;
  }

  return <ListComponent onOpenSong={(id, _title) => setSongId(id)} />;
}
