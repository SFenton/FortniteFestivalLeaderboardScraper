import React from 'react';

import {useWindowsFlyoutUi} from '../navigation/windowsFlyoutUi';
import {StatisticsScreen} from './StatisticsScreen';
import {SongDetailsView} from './SongDetailsScreen';

export function WindowsStatisticsHost() {
  const [songId, setSongId] = React.useState<string | null>(null);
  const {setChromeHidden} = useWindowsFlyoutUi();

  React.useEffect(() => {
    setChromeHidden(songId != null);
    return () => setChromeHidden(false);
  }, [setChromeHidden, songId]);

  if (songId) {
    return <SongDetailsView songId={songId} showBack onBack={() => setSongId(null)} />;
  }

  return <StatisticsScreen onOpenSong={(id, _title) => setSongId(id)} />;
}
