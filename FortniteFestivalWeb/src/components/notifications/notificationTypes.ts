import type { ServerInstrumentKey } from '@festival/core/api';
import type { NotificationNavigationContext } from './notificationDestination';
import type { NotificationTextEvent, NotificationTextInput } from './notificationText';

export type NotificationMedia =
  | { kind: 'song'; albumArt: string; alt: string }
  | { kind: 'songInstrumentGrid'; albumArt: string; alt: string; instruments: ServerInstrumentKey[]; label: string }
  | { kind: 'soloInstrument'; instrument: ServerInstrumentKey; label: string }
  | { kind: 'instrumentCombo'; instruments: ServerInstrumentKey[]; label: string; cycleAlbumArt?: { albumArt: string; alt: string } };

export type MobileNotification = NotificationTextInput & {
  eventId: number;
  notificationGuid: string;
  detectedAt: string;
  eventKind: string;
  songId?: string;
  instrument?: ServerInstrumentKey;
  title: string;
  artist?: string;
  context: string;
  detectedLabel: string;
  media: NotificationMedia;
  surfaceInstruments?: ServerInstrumentKey[];
  navigation?: NotificationNavigationContext | null;
  payload: {
    coalescedEventCount: number;
    coalescedEventKinds: string[];
    coalescedInstruments?: ServerInstrumentKey[];
    coalescedEvents: NotificationTextEvent[];
    oldFullCombo?: boolean | null;
    newFullCombo?: boolean | null;
    oldStars?: number | null;
    newStars?: number | null;
  };
};
