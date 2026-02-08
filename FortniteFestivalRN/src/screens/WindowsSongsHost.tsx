import React from 'react';

import {useWindowsFlyoutUi} from '../navigation/windowsFlyoutUi';
import {SongsScreen} from './SongsScreen';
import {SongDetailsView} from './SongDetailsScreen';

export function WindowsSongsHost() {
  const [songId, setSongId] = React.useState<string | null>(null);
  const {setChromeHidden} = useWindowsFlyoutUi();

  React.useEffect(() => {
    setChromeHidden(songId != null);
    return () => setChromeHidden(false);
  }, [setChromeHidden, songId]);

  if (songId) {
    return <SongDetailsView songId={songId} showBack onBack={() => setSongId(null)} />;
  }

  return <SongsScreen onOpenSong={(id, _title) => setSongId(id)} />;
}
