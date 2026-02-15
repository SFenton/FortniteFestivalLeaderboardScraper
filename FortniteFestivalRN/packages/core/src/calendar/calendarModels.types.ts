// Type-only port of FortniteFestival.Core.Models.CalendarModels

export type ActiveEvent = {
  eventType?: string;
  activeUntil?: string;
  activeSince?: string;
  instanceId?: string;
  devName?: string;
  eventName?: string;
  eventStart?: string;
  eventEnd?: string;
};

export type StateData = {
  electionId?: string;
  candidates?: unknown[];
  electionEnds?: string;
  numWinners?: number;
  activeStorefronts?: unknown[];
  eventNamedWeights?: unknown;
  activeEvents?: ActiveEvent[];
  seasonNumber?: number;
  seasonTemplateId?: string;
  matchXpBonusPoints?: number;
  eventPunchCardTemplateId?: string;
  seasonBegin?: string;
  seasonEnd?: string;
  seasonDisplayedEnd?: string;
  weeklyStoreEnd?: string;
  stwEventStoreEnd?: string;
  stwWeeklyStoreEnd?: string;
  sectionStoreEnds?: unknown;
  rmtPromotion?: string;
  dailyStoreEnd?: string;
  activePurchaseLimitingEventIds?: unknown[];
  storefront?: unknown;
  rmtPromotionConfig?: unknown[];
  storeEnd?: string;
  region?: unknown;
  k?: string[];
  islandCodes?: unknown[];
  playlistCuratedContent?: unknown;
  playlistCuratedHub?: unknown;
  islandTemplates?: unknown[];
};

export type State = {
  validFrom?: string;
  activeEvents?: unknown[];
  state?: StateData;
};

export type Channels = Record<string, {states?: State[]; cacheExpire?: string} | undefined>;

export type CalendarResponse = {
  channels?: Channels;
  cacheIntervalMins?: number;
  currentTime?: string;
};
