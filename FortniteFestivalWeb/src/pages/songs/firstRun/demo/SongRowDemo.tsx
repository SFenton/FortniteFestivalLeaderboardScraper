import SongInfo from '../../../../components/songs/metadata/SongInfo';
import { useIsMobile } from '../../../../hooks/ui/useIsMobile';
import { Layout } from '@festival/theme';
import { useDemoSongs } from '../../../../hooks/data/useDemoSongs';
import { DemoSongRow } from './DemoSongRow';
import css from './SongRowDemo.module.css';

const ROW_HEIGHT_DESKTOP = Layout.demoRowHeight;
const ROW_HEIGHT_MOBILE = Layout.demoRowMobileHeight;

export default function SongRowDemo() {
  const isMobile = useIsMobile();
  const { rows, fadingIdx, initialDone } = useDemoSongs({
    rowHeight: ROW_HEIGHT_DESKTOP,
    mobileRowHeight: ROW_HEIGHT_MOBILE,
    isMobile,
  });

  return (
    <div className={css.list}>
      {rows.map((song, i) => (
        <DemoSongRow key={i} index={i} initialDone={initialDone} fadingIdx={fadingIdx} mobile={isMobile}>
          {isMobile ? (
            <div className={css.mobileTopRow}>
              <SongInfo albumArt={song.albumArt} title={song.title} artist={song.artist} year={song.year} />
            </div>
          ) : (
            <SongInfo albumArt={song.albumArt} title={song.title} artist={song.artist} year={song.year} />
          )}
        </DemoSongRow>
      ))}
    </div>
  );
}
