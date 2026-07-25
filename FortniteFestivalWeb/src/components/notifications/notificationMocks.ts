import type { MobileNotification } from './notificationTypes';

const ALBUM_ART_PREFIX = 'https://cdn2.unrealengine.com/';
const SFENTONX_ACCOUNT_ID = '195e93ef108143b2975ee46662d4d0e1';
const KAHNYRI_ACCOUNT_ID = '4c2a1300df4c49a9b9d2b352d704bdf0';
const THIRD_BAND_ACCOUNT_ID = 'db9342c9dd874c799b58f177ec899f5e';
const APPLE_SONG_ID = 'e90125a8-742a-4be9-baa0-4d93f5fba556';
const STAND_AND_FIGHT_REMIX_SONG_ID = '4e5b8da5-0891-4a5b-9386-85031fcdca08';
const GHOSTS_N_STUFF_SONG_ID = 'e60b07e6-065a-4059-a7a4-4a88fe268108';

export const mockMobileNotifications: MobileNotification[] = [
  {
    eventId: 1,
    notificationGuid: 'f2ddf535-f63e-4fd3-9c2c-9b7273fd0001',
    detectedAt: '2026-05-09T14:53:00Z',
    eventKind: 'player_score_pb',
    songId: APPLE_SONG_ID,
    instrument: 'Solo_Drums',
    title: 'Apple',
    songTitle: 'Apple',
    instrumentLabel: 'Pro Drums',
    context: 'SFentonX - Pro Drums',
    detectedLabel: 'Today 7:53 AM',
    media: { kind: 'song', albumArt: albumArt('tg9ervxpjbz6zww6-512x512-16b50aeec442.jpg'), alt: 'Apple album art' },
    navigation: { songId: APPLE_SONG_ID, instrument: 'Solo_Drums' },
    payload: {
      coalescedEventCount: 4,
      coalescedEventKinds: ['player_score_pb', 'player_gold_stars_achieved', 'player_stars_improved', 'player_song_rank_improved'],
      coalescedEvents: [
        { eventKind: 'player_score_pb', metric: 'score', oldNumeric: 127025, newNumeric: 137700 },
        { eventKind: 'player_gold_stars_achieved', metric: 'stars', oldNumeric: 5, newNumeric: 6 },
        { eventKind: 'player_stars_improved', metric: 'stars', oldNumeric: 5, newNumeric: 6 },
        { eventKind: 'player_song_rank_improved', metric: 'song_rank', oldRank: 1214, newRank: 982 },
      ],
    },
  },
  {
    eventId: 2,
    notificationGuid: 'f2ddf535-f63e-4fd3-9c2c-9b7273fd0002',
    detectedAt: '2026-05-09T14:52:00Z',
    eventKind: 'player_score_pb',
    songId: STAND_AND_FIGHT_REMIX_SONG_ID,
    instrument: 'Solo_Drums',
    title: 'Stand and Fight (Remix)',
    songTitle: 'Stand and Fight (Remix)',
    instrumentLabel: 'Drums',
    context: 'SFentonX - Drums',
    detectedLabel: 'Today 7:53 AM',
    media: { kind: 'song', albumArt: albumArt('9yu2qyo48olhpmev-512x512-ed189e21217f.jpg'), alt: 'Stand and Fight (Remix) album art' },
    navigation: { songId: STAND_AND_FIGHT_REMIX_SONG_ID, instrument: 'Solo_Drums' },
    payload: {
      coalescedEventCount: 3,
      coalescedEventKinds: ['player_score_pb', 'player_fc_achieved', 'player_song_rank_improved'],
      coalescedEvents: [
        { eventKind: 'player_score_pb', metric: 'score', oldNumeric: 126384, newNumeric: 126978 },
        { eventKind: 'player_fc_achieved', metric: 'full_combo' },
        { eventKind: 'player_song_rank_improved', metric: 'song_rank', oldRank: 442, newRank: 391 },
      ],
    },
  },
  {
    eventId: 3,
    notificationGuid: 'f2ddf535-f63e-4fd3-9c2c-9b7273fd0003',
    detectedAt: '2026-05-09T14:51:00Z',
    eventKind: 'player_first_score',
    songId: GHOSTS_N_STUFF_SONG_ID,
    instrument: 'Solo_Drums',
    title: "Ghosts 'n' Stuff",
    songTitle: "Ghosts 'n' Stuff",
    instrumentLabel: 'Pro Drums',
    context: 'SFentonX - Pro Drums',
    detectedLabel: 'Today 7:53 AM',
    media: { kind: 'song', albumArt: albumArt('brc3mquv0rvjdlhz-512x512-cfb9e6ab2c73.jpg'), alt: "Ghosts 'n' Stuff album art" },
    navigation: { songId: GHOSTS_N_STUFF_SONG_ID, instrument: 'Solo_Drums' },
    payload: {
      coalescedEventCount: 2,
      coalescedEventKinds: ['player_first_score', 'player_gold_stars_achieved'],
      coalescedEvents: [
        { eventKind: 'player_first_score', metric: 'score', newNumeric: 180005, newRank: 1288 },
        { eventKind: 'player_gold_stars_achieved', metric: 'stars', newNumeric: 6 },
      ],
    },
  },
  {
    eventId: 4,
    notificationGuid: 'f2ddf535-f63e-4fd3-9c2c-9b7273fd0004',
    detectedAt: '2026-05-09T14:50:00Z',
    eventKind: 'player_weighted_rank_improved',
    title: 'Solo Drums weighted percentile rank',
    instrumentLabel: 'Drums',
    context: 'SFentonX - Rankings',
    detectedLabel: 'Today 7:53 AM',
    media: { kind: 'soloInstrument', instrument: 'Solo_Drums', label: 'Drums' },
    navigation: { rankBy: 'weighted' },
    payload: {
      coalescedEventCount: 1,
      coalescedEventKinds: ['player_weighted_rank_improved'],
      coalescedEvents: [
        { eventKind: 'player_weighted_rank_improved', metric: 'weighted_rank', oldRank: 45, newRank: 42 },
      ],
    },
  },
  {
    eventId: 5,
    notificationGuid: 'f2ddf535-f63e-4fd3-9c2c-9b7273fd0005',
    detectedAt: '2026-05-09T14:49:00Z',
    eventKind: 'band_weighted_rank_improved',
    title: 'Band Duos weighted percentile rank',
    scopeLabel: 'Band Duos',
    context: 'SFentonX + kahnyri - Guitar/Drums',
    detectedLabel: 'Today 7:53 AM',
    media: { kind: 'instrumentCombo', instruments: ['Solo_Guitar', 'Solo_Drums'], label: 'Guitar/Drums' },
    navigation: { rankBy: 'weighted' },
    payload: {
      coalescedEventCount: 1,
      coalescedEventKinds: ['band_weighted_rank_improved'],
      coalescedEvents: [
        { eventKind: 'band_weighted_rank_improved', metric: 'weighted_rank', oldRank: 19, newRank: 16 },
      ],
    },
  },
  {
    eventId: 6,
    notificationGuid: 'f2ddf535-f63e-4fd3-9c2c-9b7273fd0006',
    detectedAt: '2026-05-09T14:48:00Z',
    eventKind: 'band_score_pb',
    songId: APPLE_SONG_ID,
    title: 'Apple',
    songTitle: 'Apple',
    rankingScope: 'overall',
    comboLabel: 'Bass/Bass/Drums',
    scopeLabel: 'Band Trios',
    context: 'SFentonX + kahnyri + db9342 - Bass/Bass/Drums',
    detectedLabel: 'Today 7:53 AM',
    media: {
      kind: 'instrumentCombo',
      instruments: ['Solo_Bass', 'Solo_Bass', 'Solo_Drums'],
      label: 'Bass/Bass/Drums',
      cycleAlbumArt: { albumArt: albumArt('tg9ervxpjbz6zww6-512x512-16b50aeec442.jpg'), alt: 'Apple band notification album art' },
    },
    navigation: {
      songId: APPLE_SONG_ID,
      band: {
        bandId: 'notification-band-trios-apple',
        bandType: 'Band_Trios',
        teamKey: `${SFENTONX_ACCOUNT_ID}:${KAHNYRI_ACCOUNT_ID}:${THIRD_BAND_ACCOUNT_ID}`,
        displayName: 'SFentonX + kahnyri + db9342',
        members: [
          { accountId: SFENTONX_ACCOUNT_ID, displayName: 'SFentonX' },
          { accountId: KAHNYRI_ACCOUNT_ID, displayName: 'kahnyri' },
          { accountId: THIRD_BAND_ACCOUNT_ID, displayName: 'db9342' },
        ],
      },
      bandFilter: {
        comboId: 'Solo_Bass+Solo_Bass+Solo_Drums',
        assignments: [
          { accountId: SFENTONX_ACCOUNT_ID, instrument: 'Solo_Bass' },
          { accountId: KAHNYRI_ACCOUNT_ID, instrument: 'Solo_Bass' },
          { accountId: THIRD_BAND_ACCOUNT_ID, instrument: 'Solo_Drums' },
        ],
      },
    },
    payload: {
      coalescedEventCount: 5,
      coalescedEventKinds: ['band_score_pb', 'band_fc_achieved', 'band_gold_stars_achieved', 'band_song_rank_improved', 'band_song_rank_improved'],
      coalescedEvents: [
        { eventKind: 'band_score_pb', metric: 'score', oldNumeric: 1210400, newNumeric: 1234567 },
        { eventKind: 'band_fc_achieved', metric: 'full_combo' },
        { eventKind: 'band_gold_stars_achieved', metric: 'stars', oldNumeric: 5, newNumeric: 6 },
        { eventKind: 'band_song_rank_improved', metric: 'song_rank', oldRank: 42, newRank: 31, rankingScope: 'overall', scopeLabel: 'Band Trios' },
        { eventKind: 'band_song_rank_improved', metric: 'song_rank', oldRank: 9, newRank: 6, rankingScope: 'combo', comboLabel: 'Bass/Bass/Drums' },
      ],
    },
  },
];

export const mockEmptyMobileNotifications: MobileNotification[] = [];

function albumArt(path: string) {
  return `${ALBUM_ART_PREFIX}${path}`;
}
