import { lazyWithPreload } from '../../utils/lazyWithPreload';

const searchModal = lazyWithPreload(() => import('../search/SearchModal'));
const notificationsModal = lazyWithPreload(() => import('../notifications/MobileNotificationsModal'));
const bandInstrumentFilterModal = lazyWithPreload(() => import('../../pages/band/modals/BandInstrumentFilterModal'));
const songsSortModal = lazyWithPreload(() => import('../../pages/songs/modals/SortModal'));
const songsFilterModal = lazyWithPreload(() => import('../../pages/songs/modals/FilterModal'));

export const LazySearchModal = searchModal.Component;
export const preloadSearchModal = searchModal.preload;

export const LazyMobileNotificationsModal = notificationsModal.Component;
export const preloadMobileNotificationsModal = notificationsModal.preload;

export const LazyBandInstrumentFilterModal = bandInstrumentFilterModal.Component;
export const preloadBandInstrumentFilterModal = bandInstrumentFilterModal.preload;

export const LazySongsSortModal = songsSortModal.Component;
export const preloadSongsSortModal = songsSortModal.preload;

export const LazySongsFilterModal = songsFilterModal.Component;
export const preloadSongsFilterModal = songsFilterModal.preload;
