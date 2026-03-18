/**
 * Shared types for the player page item-building pattern.
 */
import type { ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import type { SongSettings } from '../../../utils/songSettings';

/** A single item in the staggered grid. */
export type PlayerItem = {
  key: string;
  node: React.ReactNode;
  span: boolean;
  style?: React.CSSProperties;
  heightEstimate: number;
};

export type NavigateToSongs = (updater: (s: SongSettings) => SongSettings) => void;
export type NavigateToSongDetail = (songId: string, instrument: InstrumentKey, opts?: { autoScroll?: boolean }) => void;
