import type { TFunction } from 'i18next';
import { SERVER_INSTRUMENT_KEYS, serverInstrumentLabel, type ServerInstrumentKey } from '@festival/core/api/serverTypes';

export type NotificationTextEvent = {
  eventKind: string;
  instrument?: ServerInstrumentKey | null;
  instrumentLabel?: string | null;
  metric?: string | null;
  oldNumeric?: number | null;
  newNumeric?: number | null;
  oldRank?: number | null;
  newRank?: number | null;
  oldLabel?: string | null;
  newLabel?: string | null;
  oldFullCombo?: boolean | null;
  newFullCombo?: boolean | null;
  oldStars?: number | null;
  newStars?: number | null;
  comboLabel?: string | null;
  scopeLabel?: string | null;
  rankingScope?: string | null;
  scopeComboId?: string | null;
  comboId?: string | null;
};

export type NotificationTextPayload = {
  coalescedGroup?: string | null;
  coalescedEventKinds?: string[] | null;
  coalescedInstruments?: ServerInstrumentKey[] | null;
  coalescedEvents?: NotificationTextEvent[] | null;
  oldFullCombo?: boolean | null;
  newFullCombo?: boolean | null;
  oldStars?: number | null;
  newStars?: number | null;
};

export type NotificationTextInput = NotificationTextEvent & {
  title?: string | null;
  songTitle?: string | null;
  instrumentLabel?: string | null;
  comboLabel?: string | null;
  scopeLabel?: string | null;
  rankingScope?: string | null;
  payload?: NotificationTextPayload | null;
};

export type NotificationPresentation = {
  title: string;
  message: string;
  messageParts: NotificationMessagePart[];
  badges: string[];
  flags: NotificationFlag[];
  flagGroups?: NotificationFlagGroup[];
  accessibilityLabel: string;
};

export type NotificationMessagePart = {
  text: string;
  emphasis?: boolean;
};

export type NotificationFlagKind =
  | 'improvement'
  | 'firstPlay'
  | 'newHighScore'
  | 'fullCombo'
  | 'rankUp'
  | 'goldStars'
  | 'starsUp'
  | 'difficultyUp'
  | 'progress';

export type NotificationFlag = {
  kind: NotificationFlagKind;
  label: string;
};

export type NotificationFlagGroup = {
  instrument: ServerInstrumentKey;
  label: string;
  flags: NotificationFlag[];
};

type NotificationClause = {
  text: string;
  emphasisTerms: string[];
};

const EVENT_PRIORITY: Record<string, number> = {
  player_first_score: 10,
  band_first_score: 10,
  player_score_pb: 20,
  band_score_pb: 20,
  band_combo_score_pb: 20,
  player_fc_achieved: 30,
  band_fc_achieved: 30,
  player_gold_stars_achieved: 40,
  band_gold_stars_achieved: 40,
  player_stars_improved: 50,
  band_stars_improved: 50,
  player_song_rank_improved: 60,
  band_song_rank_improved: 60,
  player_difficulty_bumped: 70,
  band_member_difficulty_bumped: 70,
  player_total_score_improved: 75,
  player_fc_count_improved: 76,
  player_total_score_rank_improved: 80,
  band_total_score_rank_improved: 80,
  player_skill_rank_improved: 90,
  band_skill_rank_improved: 90,
  player_weighted_rank_improved: 100,
  band_weighted_rank_improved: 100,
  player_fc_rate_rank_improved: 110,
  band_fc_rate_rank_improved: 110,
  player_max_score_rank_improved: 120,
  band_max_score_rank_improved: 120,
};

export function formatNotificationPresentation(t: TFunction, input: NotificationTextInput): NotificationPresentation {
  const events = getDisplayEvents(input);
  const title = formatNotificationTitle(t, input, events);
  const statementStyleMessage = shouldUseStatementStyleMessage(events);
  const richClauses = formatNotificationClauses(t, input, events)
    .filter((clause) => clause.text.length > 0);
  const clauses = richClauses.map(clause => clause.text);
  const message = clauses.length > 0
    ? statementStyleMessage ? clauses.join('\n\n') : formatSentence(t, clauses)
    : translate(t, 'notifications.copy.unknown');
  const messageParts = richClauses.length > 0
    ? emphasizeText(message, richClauses.flatMap(clause => clause.emphasisTerms))
    : [{ text: message }];
  const flags = uniqueFlags(events.map(event => formatEventFlag(t, event.eventKind)));
  const flagGroups = formatNotificationFlagGroups(t, events);

  return {
    title,
    message,
    messageParts,
    badges: flags.map(flag => flag.label),
    flags,
    ...(flagGroups.length > 0 ? { flagGroups } : {}),
    accessibilityLabel: `${title}. ${message}`,
  };
}

function formatNotificationTitle(t: TFunction, input: NotificationTextInput, events: NotificationTextEvent[]) {
  const baseTitle = input.songTitle ?? input.title;
  const instrumentLabel = input.instrumentLabel?.trim();
  if (baseTitle && isMultiInstrumentPlayerSongNotification(events)) {
    return baseTitle;
  }

  if (baseTitle && instrumentLabel && events.some(event => PLAYER_SONG_TEXT_EVENT_KINDS.has(event.eventKind))) {
    return `${baseTitle} · ${instrumentLabel}`;
  }

  const bandScopeLabel = input.scopeLabel?.trim();
  if (baseTitle && bandScopeLabel && events.some(event => BAND_SONG_TEXT_EVENT_KINDS.has(event.eventKind))) {
    return `${baseTitle} · ${bandScopeLabel}`;
  }

  const instrumentAggregateTitle = formatInstrumentAggregateTitle(t, input, instrumentLabel, events);
  if (instrumentAggregateTitle) return instrumentAggregateTitle;

  const aggregateRankTitle = formatAggregateRankTitle(t, input, instrumentLabel, events);
  if (aggregateRankTitle) return aggregateRankTitle;

  const progressTitle = formatProgressTitle(t, events);
  if (progressTitle) return progressTitle;

  return input.title ?? input.songTitle ?? translate(t, 'notifications.values.notification');
}

function formatAggregateRankTitle(t: TFunction, input: NotificationTextInput, instrumentLabel: string | undefined, events: NotificationTextEvent[]) {
  const rankEvents = events.filter(event => AGGREGATE_RANK_TITLE_KEYS[event.eventKind]);
  if (rankEvents.length > 1) {
    const scope = instrumentLabel ?? input.comboLabel ?? input.scopeLabel ?? null;
    return scope
      ? translate(t, 'notifications.titles.rankUpdatesWithScope', { scope })
      : translate(t, 'notifications.titles.rankUpdates');
  }

  const rankEvent = rankEvents[0];
  if (!rankEvent) return null;
  return translate(t, 'notifications.titles.rankImproved', {
    rank: translate(t, AGGREGATE_RANK_TITLE_KEYS[rankEvent.eventKind]),
  });
}

function formatInstrumentAggregateTitle(t: TFunction, input: NotificationTextInput, instrumentLabel: string | undefined, events: NotificationTextEvent[]) {
  if (!isPlayerInstrumentAggregateNotification(events)) return null;
  const scope = instrumentLabel ?? input.scopeLabel ?? null;
  return scope
    ? translate(t, 'notifications.titles.instrumentUpdatesWithScope', { scope })
    : translate(t, 'notifications.titles.instrumentUpdates');
}

function formatProgressTitle(t: TFunction, events: NotificationTextEvent[]) {
  const progressEvent = events.find(event => PROGRESS_TITLE_KEYS[event.eventKind]);
  return progressEvent ? translate(t, PROGRESS_TITLE_KEYS[progressEvent.eventKind]) : null;
}

function getDisplayEvents(input: NotificationTextInput): NotificationTextEvent[] {
  const payloadEvents = input.payload?.coalescedEvents?.filter(event => event.eventKind) ?? [];
  const events = payloadEvents.length > 0
    ? payloadEvents
    : [{
        eventKind: input.eventKind,
        instrument: input.instrument,
        instrumentLabel: input.instrumentLabel,
        metric: input.metric,
        oldNumeric: input.oldNumeric,
        newNumeric: input.newNumeric,
        oldRank: input.oldRank,
        newRank: input.newRank,
        oldLabel: input.oldLabel,
        newLabel: input.newLabel,
        oldFullCombo: input.oldFullCombo,
        newFullCombo: input.newFullCombo,
        oldStars: input.oldStars,
        newStars: input.newStars,
      }];

  return removeRedundantStarEvents(withDerivedScoreResultEvents(input, events))
    .sort((left, right) => priority(left.eventKind) - priority(right.eventKind));
}

function withDerivedScoreResultEvents(input: NotificationTextInput, events: NotificationTextEvent[]): NotificationTextEvent[] {
  const explicitFullComboKeys = new Set(events
    .filter(event => event.eventKind === 'player_fc_achieved' || event.eventKind === 'band_fc_achieved')
    .map(event => statusEventKey(input, event)));
  const explicitGoldStarKeys = new Set(events
    .filter(event => event.eventKind === 'player_gold_stars_achieved' || event.eventKind === 'band_gold_stars_achieved')
    .map(event => statusEventKey(input, event)));
  const derivedEvents: NotificationTextEvent[] = [];
  const multiInstrumentPlayerSong = isMultiInstrumentPlayerSongNotification(events);

  for (const event of events) {
    if (!SCORE_RESULT_EVENT_KINDS.has(event.eventKind)) continue;
    const scoreResult = scoreResultState(input, events, event, multiInstrumentPlayerSong);
    if (!scoreResult) continue;

    const key = statusEventKey(input, event);
    const bandEvent = event.eventKind.startsWith('band_');
    if (scoreResult.newFullCombo === true && !explicitFullComboKeys.has(key)) {
      derivedEvents.push(derivedScoreResultEvent(input, event, bandEvent ? 'band_fc_achieved' : 'player_fc_achieved', 'full_combo', scoreResult));
      explicitFullComboKeys.add(key);
    }
    if (scoreResult.newStars != null && scoreResult.newStars >= 6 && !explicitGoldStarKeys.has(key)) {
      derivedEvents.push(derivedScoreResultEvent(input, event, bandEvent ? 'band_gold_stars_achieved' : 'player_gold_stars_achieved', 'stars', scoreResult));
      explicitGoldStarKeys.add(key);
    }
  }

  return derivedEvents.length > 0 ? [...events, ...derivedEvents] : events;
}

type ScoreResultState = {
  oldFullCombo?: boolean | null;
  newFullCombo?: boolean | null;
  oldStars?: number | null;
  newStars?: number | null;
};

function scoreResultState(
  input: NotificationTextInput,
  events: NotificationTextEvent[],
  event: NotificationTextEvent,
  multiInstrumentPlayerSong: boolean,
): ScoreResultState | null {
  if (hasScoreResultState(event)) return event;
  if (multiInstrumentPlayerSong && !eventMatchesTopLevelScoreResult(input, event)) return null;
  if (!topLevelPayloadBelongsToEvent(input, events, event)) return null;
  const state = input.payload;
  return state && hasScoreResultState(state) ? state : null;
}

function topLevelPayloadBelongsToEvent(input: NotificationTextInput, events: NotificationTextEvent[], event: NotificationTextEvent) {
  const scoreEvents = events.filter(candidate => SCORE_RESULT_EVENT_KINDS.has(candidate.eventKind));
  if (scoreEvents.length !== 1 && !eventMatchesTopLevelScoreResult(input, event)) return false;
  const eventInstrument = event.instrument ?? null;
  const inputInstrument = input.instrument ?? null;
  return !eventInstrument || !inputInstrument || eventInstrument === inputInstrument;
}

function eventMatchesTopLevelScoreResult(input: NotificationTextInput, event: NotificationTextEvent) {
  const inputInstrument = input.instrument ?? null;
  const eventInstrument = event.instrument ?? null;
  return event.eventKind === input.eventKind
    && inputInstrument != null
    && eventInstrument != null
    && inputInstrument === eventInstrument
    && valueMatches(input.metric, event.metric)
    && valueMatches(input.oldNumeric, event.oldNumeric)
    && valueMatches(input.newNumeric, event.newNumeric)
    && valueMatches(input.oldRank, event.oldRank)
    && valueMatches(input.newRank, event.newRank);
}

function valueMatches<T>(inputValue: T | null | undefined, eventValue: T | null | undefined) {
  return inputValue == null || eventValue == null || inputValue === eventValue;
}

function hasScoreResultState(state: ScoreResultState) {
  return state.newFullCombo != null || state.newStars != null;
}

function derivedScoreResultEvent(
  input: NotificationTextInput,
  source: NotificationTextEvent,
  eventKind: string,
  metric: string,
  state: ScoreResultState,
): NotificationTextEvent {
  return {
    eventKind,
    instrument: source.instrument ?? input.instrument,
    instrumentLabel: source.instrumentLabel ?? input.instrumentLabel,
    metric,
    oldNumeric: metric === 'stars' ? state.oldStars : null,
    newNumeric: metric === 'stars' ? state.newStars : null,
    rankingScope: source.rankingScope ?? input.rankingScope,
    comboLabel: source.comboLabel ?? input.comboLabel,
    scopeLabel: source.scopeLabel ?? input.scopeLabel,
    scopeComboId: source.scopeComboId,
    comboId: source.comboId ?? input.comboId,
  };
}

function statusEventKey(input: NotificationTextInput, event: NotificationTextEvent) {
  return [
    event.eventKind.startsWith('band_') ? 'band' : 'player',
    event.instrument ?? input.instrument ?? '',
    event.rankingScope ?? input.rankingScope ?? '',
    event.comboId ?? event.scopeComboId ?? input.comboId ?? '',
  ].join('|');
}

function formatNotificationClauses(t: TFunction, input: NotificationTextInput, events: NotificationTextEvent[]): NotificationClause[] {
  if (isPlayerInstrumentAggregateNotification(events)) {
    return formatPlayerInstrumentAggregateClauses(t, events);
  }

  if (isMultiAggregateRankNotification(events)) {
    return formatAggregateRankUpdateClauses(t, events);
  }

  if (isMultiInstrumentPlayerSongNotification(events)) {
    return formatMultiInstrumentSongClauses(t, input, events);
  }

  return events.flatMap((event, index) => formatEventClauses(t, input, event, index === 0));
}

function shouldUseStatementStyleMessage(events: NotificationTextEvent[]) {
  return isPlayerInstrumentAggregateNotification(events) || isMultiAggregateRankNotification(events) || isMultiInstrumentPlayerSongNotification(events);
}

function isPlayerInstrumentAggregateNotification(events: NotificationTextEvent[]) {
  const aggregateEvents = events.filter(event => PLAYER_INSTRUMENT_AGGREGATE_EVENT_KINDS.has(event.eventKind));
  return aggregateEvents.length > 1
    && aggregateEvents.length === events.length
    && aggregateEvents.some(event => PLAYER_INSTRUMENT_AGGREGATE_PROGRESS_EVENT_KINDS.has(event.eventKind));
}

function isMultiAggregateRankNotification(events: NotificationTextEvent[]) {
  return events.filter(event => AGGREGATE_RANK_TITLE_KEYS[event.eventKind]).length > 1;
}

function isMultiInstrumentPlayerSongNotification(events: NotificationTextEvent[]) {
  const instruments = new Set(
    events
      .filter(event => PLAYER_SONG_TEXT_EVENT_KINDS.has(event.eventKind))
      .map(event => eventInstrumentLabel(event))
      .filter((instrument): instrument is string => Boolean(instrument)),
  );
  return instruments.size > 1;
}

function formatAggregateRankUpdateClauses(t: TFunction, events: NotificationTextEvent[]): NotificationClause[] {
  return events
    .filter(event => AGGREGATE_RANK_TITLE_KEYS[event.eventKind])
    .map((event) => {
      const rank = rankName(t, event.eventKind);
      const oldRank = formatRank(t, event.oldRank);
      const newRank = formatRank(t, event.newRank);
      return {
        text: translate(t, 'notifications.copy.rankUpdateStatement', { rank, oldRank, newRank }),
        emphasisTerms: filterEmphasisTerms([rank, oldRank, newRank]),
      };
    });
}

function formatPlayerInstrumentAggregateClauses(t: TFunction, events: NotificationTextEvent[]): NotificationClause[] {
  const eventByKind = new Map(events.map(event => [event.eventKind, event]));
  const clauses = filterClauses([
    formatTotalScoreAggregateClause(t, eventByKind),
    formatFullComboAggregateClause(t, eventByKind),
    formatInstrumentAggregateRankClause(t, eventByKind.get('player_skill_rank_improved'), 'notifications.copy.instrumentAggregate.skillRank'),
    formatInstrumentAggregateRankClause(t, eventByKind.get('player_weighted_rank_improved'), 'notifications.copy.instrumentAggregate.weightedRank'),
    formatInstrumentAggregateRankClause(t, eventByKind.get('player_max_score_rank_improved'), 'notifications.copy.instrumentAggregate.maxScoreRank'),
  ]);

  if (clauses.length > 0) return clauses;
  return events.flatMap((event, index) => formatEventClauses(t, { eventKind: event.eventKind }, event, index === 0));
}

function formatTotalScoreAggregateClause(t: TFunction, eventByKind: ReadonlyMap<string, NotificationTextEvent>): NotificationClause | null {
  const valueEvent = eventByKind.get('player_total_score_improved');
  const rankEvent = eventByKind.get('player_total_score_rank_improved');
  const newScore = formatNumberValue(t, valueEvent?.newNumeric, 'notifications.values.score');
  const oldRank = formatRank(t, rankEvent?.oldRank);
  const newRank = formatRank(t, rankEvent?.newRank);

  if (valueEvent && rankEvent) {
    return {
      text: translate(t, 'notifications.copy.instrumentAggregate.totalScoreValueAndRank', { newScore, oldRank, newRank }),
      emphasisTerms: filterEmphasisTerms([newScore, 'total score rank', oldRank, newRank]),
    };
  }

  if (valueEvent) {
    return {
      text: translate(t, 'notifications.copy.instrumentAggregate.totalScoreValue', { newScore }),
      emphasisTerms: filterEmphasisTerms([newScore]),
    };
  }

  return formatInstrumentAggregateRankClause(t, rankEvent, 'notifications.copy.instrumentAggregate.totalScoreRank');
}

function formatFullComboAggregateClause(t: TFunction, eventByKind: ReadonlyMap<string, NotificationTextEvent>): NotificationClause | null {
  const countEvent = eventByKind.get('player_fc_count_improved');
  const rankEvent = eventByKind.get('player_fc_rate_rank_improved');
  const newCount = formatNumberValue(t, countEvent?.newNumeric, 'notifications.values.count');
  const oldRank = formatRank(t, rankEvent?.oldRank);
  const newRank = formatRank(t, rankEvent?.newRank);

  if (countEvent && rankEvent) {
    return {
      text: translate(t, 'notifications.copy.instrumentAggregate.fullComboCountAndRank', { newCount, oldRank, newRank }),
      emphasisTerms: filterEmphasisTerms([newCount, 'Full Combo percentage rank', oldRank, newRank]),
    };
  }

  if (countEvent) {
    return {
      text: translate(t, 'notifications.copy.instrumentAggregate.fullComboCount', { newCount }),
      emphasisTerms: filterEmphasisTerms([newCount]),
    };
  }

  return formatInstrumentAggregateRankClause(t, rankEvent, 'notifications.copy.instrumentAggregate.fullComboRank');
}

function formatInstrumentAggregateRankClause(t: TFunction, event: NotificationTextEvent | undefined, key: string): NotificationClause | null {
  if (!event) return null;
  const oldRank = formatRank(t, event.oldRank);
  const newRank = formatRank(t, event.newRank);
  return {
    text: translate(t, key, { oldRank, newRank }),
    emphasisTerms: filterEmphasisTerms([...instrumentAggregateRankEmphasisTerms(key), oldRank, newRank]),
  };
}

function instrumentAggregateRankEmphasisTerms(key: string) {
  switch (key) {
    case 'notifications.copy.instrumentAggregate.totalScoreRank':
      return ['total score rank'];
    case 'notifications.copy.instrumentAggregate.fullComboRank':
      return ['Full Combo percentage rank'];
    case 'notifications.copy.instrumentAggregate.skillRank':
      return ['adjusted percentile rank'];
    case 'notifications.copy.instrumentAggregate.weightedRank':
      return ['percentile rank, weighted by number of entries'];
    case 'notifications.copy.instrumentAggregate.maxScoreRank':
      return ['max score rank'];
    default:
      return [];
  }
}

function filterClauses(clauses: Array<NotificationClause | null>): NotificationClause[] {
  return clauses.filter((clause): clause is NotificationClause => Boolean(clause));
}

function formatMultiInstrumentSongClauses(t: TFunction, input: NotificationTextInput, events: NotificationTextEvent[]): NotificationClause[] {
  return groupedInstrumentEvents(events).map(({ label, events: groupEvents }) => {
    const scopedInput = { ...input, instrumentLabel: label };
    const detailClauses = groupEvents.flatMap((event) => {
      return formatEventClauses(t, scopedInput, event, false);
    });
    const updateText = formatClauseFragment(t, detailClauses.map(clause => clause.text));
    return {
      text: translate(t, 'notifications.copy.instrumentUpdateStatement', { instrument: label, updates: updateText }),
      emphasisTerms: filterEmphasisTerms([label, ...detailClauses.flatMap(clause => clause.emphasisTerms)]),
    };
  });
}

function groupedInstrumentEvents(events: NotificationTextEvent[]) {
  const groups = new Map<string, NotificationTextEvent[]>();
  for (const event of events.filter(event => PLAYER_SONG_TEXT_EVENT_KINDS.has(event.eventKind))) {
    const label = eventInstrumentLabel(event);
    if (!label) continue;
    const group = groups.get(label) ?? [];
    group.push(event);
    groups.set(label, group);
  }

  return Array.from(groups.entries())
    .map(([label, groupEvents]) => ({ label, events: groupEvents.sort((left, right) => priority(left.eventKind) - priority(right.eventKind)) }))
    .sort((left, right) => instrumentLabelOrder(left.label) - instrumentLabelOrder(right.label));
}

function formatNotificationFlagGroups(t: TFunction, events: NotificationTextEvent[]): NotificationFlagGroup[] {
  if (!isMultiInstrumentPlayerSongNotification(events)) return [];
  return groupedInstrumentEvents(events).flatMap(({ label, events: groupEvents }) => {
    const instrument = groupEvents.find(event => event.instrument)?.instrument ?? instrumentForLabel(label);
    if (!instrument) return [];
    const flags = uniqueFlags(groupEvents.map(event => formatEventFlag(t, event.eventKind)));
    return flags.length > 0 ? [{ instrument, label, flags }] : [];
  });
}

function removeRedundantStarEvents(events: NotificationTextEvent[]) {
  const goldStarKeys = new Set(events
    .filter(event => event.eventKind === 'player_gold_stars_achieved' || event.eventKind === 'band_gold_stars_achieved')
    .map(starEventKey));
  return events.filter(event => {
    if (event.eventKind === 'player_stars_improved' && goldStarKeys.has(starEventKey(event))) return false;
    if (event.eventKind === 'band_stars_improved' && goldStarKeys.has(starEventKey(event))) return false;
    return true;
  });
}

function starEventKey(event: NotificationTextEvent) {
  return [event.eventKind.startsWith('band_') ? 'band' : 'player', event.instrument ?? '', event.rankingScope ?? '', event.comboId ?? event.scopeComboId ?? ''].join('|');
}

function formatEventClauses(t: TFunction, input: NotificationTextInput, event: NotificationTextEvent, primary: boolean): NotificationClause[] {
  const values = buildValues(t, input, event);
  const combo = rankingScope(input, event) === 'combo' && Boolean(comboLabel(input, event));
  const key = primary
    ? primaryKey(event.eventKind, combo, shouldUseBandRankNoScopeCopy(input, event), hasInstrumentContext(input, event))
    : detailKey(event);
  if (!key) return [];
  const clause = translate(t, key, values);
  const emphasisTerms = emphasisTermsForEvent(event, values);
  if (primary && FIRST_SCORE_EVENT_KINDS.has(event.eventKind)) {
    return [
      { text: clause, emphasisTerms },
      { text: translate(t, 'notifications.copy.detail.first_score_rank', values), emphasisTerms: filterEmphasisTerms([values.newRank]) },
    ];
  }
  return [{ text: clause, emphasisTerms }];
}

function primaryKey(eventKind: string, combo: boolean, noScopeBandRank = false, instrumentContext = false): string | null {
  if (noScopeBandRank) return `notifications.copy.primaryBandNoScope.${eventKind}`;
  if (combo && eventKind.startsWith('band_')) {
    if (instrumentContext && COMBO_INSTRUMENT_PRIMARY_EVENT_KINDS.has(eventKind)) return `notifications.copy.primaryComboInstrument.${eventKind}`;
    const comboKey = `notifications.copy.primaryCombo.${eventKind}`;
    if (COMBO_PRIMARY_EVENT_KINDS.has(eventKind)) return comboKey;
  }
  if (PRIMARY_EVENT_KINDS.has(eventKind)) return `notifications.copy.primary.${eventKind}`;
  return null;
}

function hasInstrumentContext(input: NotificationTextInput, event: NotificationTextEvent) {
  return Boolean(eventInstrumentLabel(event) ?? input.instrumentLabel?.trim());
}

function shouldUseBandRankNoScopeCopy(input: NotificationTextInput, event: NotificationTextEvent) {
  const scope = (event.scopeLabel ?? input.scopeLabel)?.trim();
  const title = input.title?.trim();
  return Boolean(
    scope
    && title
    && scope === title
    && event.eventKind.startsWith('band_')
    && AGGREGATE_RANK_TITLE_KEYS[event.eventKind],
  );
}

function detailKey(event: NotificationTextEvent): string | null {
  if (event.eventKind === 'band_song_rank_improved' && event.rankingScope === 'combo' && event.comboLabel) {
    return 'notifications.copy.detailCombo.band_song_rank_improved';
  }
  if (event.eventKind === 'band_song_rank_improved' && event.rankingScope === 'overall' && event.scopeLabel) {
    return 'notifications.copy.detailScope.band_song_rank_improved';
  }
  const eventKind = event.eventKind;
  if (DETAIL_EVENT_KINDS.has(eventKind)) return `notifications.copy.detail.${eventKind}`;
  return null;
}

function buildValues(t: TFunction, input: NotificationTextInput, event: NotificationTextEvent) {
  return {
    song: input.songTitle ?? input.title ?? translate(t, 'notifications.values.song'),
    newScore: formatNumberValue(t, event.newNumeric, 'notifications.values.score'),
    oldScore: formatNumberValue(t, event.oldNumeric, 'notifications.values.score'),
    oldRank: formatRank(t, event.oldRank),
    newRank: formatRank(t, event.newRank),
    oldStars: formatNumberValue(t, event.oldNumeric, 'notifications.values.stars'),
    newStars: formatNumberValue(t, event.newNumeric, 'notifications.values.stars'),
    oldDifficulty: event.oldLabel ?? formatNumberValue(t, event.oldNumeric, 'notifications.values.difficulty'),
    newDifficulty: event.newLabel ?? formatNumberValue(t, event.newNumeric, 'notifications.values.difficulty'),
    oldCount: formatNumberValue(t, event.oldNumeric, 'notifications.values.count'),
    newCount: formatNumberValue(t, event.newNumeric, 'notifications.values.count'),
    instrument: eventInstrumentLabel(event) ?? input.instrumentLabel ?? translate(t, 'notifications.values.instrument'),
    combo: comboLabel(input, event) ?? translate(t, 'notifications.values.combo'),
    scope: scopeLabel(t, input, event),
  };
}

function rankingScope(input: NotificationTextInput, event: NotificationTextEvent) {
  return event.rankingScope ?? input.rankingScope;
}

function comboLabel(input: NotificationTextInput, event: NotificationTextEvent) {
  return event.comboLabel ?? input.comboLabel;
}

function scopeLabel(t: TFunction, input: NotificationTextInput, event: NotificationTextEvent) {
  if (rankingScope(input, event) === 'combo' && comboLabel(input, event)) return comboLabel(input, event);
  return event.scopeLabel ?? input.scopeLabel ?? input.instrumentLabel ?? translate(t, 'notifications.values.rankings');
}

function formatSentence(t: TFunction, clauses: string[]) {
  if (clauses.length === 1) return translate(t, 'notifications.copy.join.one', { clause: clauses[0] });
  if (clauses.length === 2) return translate(t, 'notifications.copy.join.two', { first: clauses[0], second: clauses[1] });
  return translate(t, 'notifications.copy.join.many', {
    head: clauses.slice(0, -1).join(', '),
    last: clauses[clauses.length - 1],
  });
}

function formatClauseFragment(t: TFunction, clauses: string[]) {
  if (clauses.length === 0) return translate(t, 'notifications.copy.unknown');
  if (clauses.length === 1) return translate(t, 'notifications.copy.joinFragment.one', { clause: clauses[0] });
  if (clauses.length === 2) return translate(t, 'notifications.copy.joinFragment.two', { first: clauses[0], second: clauses[1] });
  return translate(t, 'notifications.copy.joinFragment.many', {
    head: clauses.slice(0, -1).join(', '),
    last: clauses[clauses.length - 1],
  });
}

function eventInstrumentLabel(event: NotificationTextEvent) {
  if (event.instrumentLabel?.trim()) return event.instrumentLabel.trim();
  return event.instrument ? serverInstrumentLabel(event.instrument) : null;
}

function instrumentLabelOrder(label: string) {
  const index = SERVER_INSTRUMENT_KEYS.findIndex(instrument => serverInstrumentLabel(instrument) === label);
  return index >= 0 ? index : 1000;
}

function instrumentForLabel(label: string): ServerInstrumentKey | null {
  return SERVER_INSTRUMENT_KEYS.find(instrument => serverInstrumentLabel(instrument) === label) ?? null;
}

function emphasizeText(text: string, terms: string[]): NotificationMessagePart[] {
  const candidates = filterEmphasisTerms(terms).filter(term => text.includes(term));
  if (candidates.length === 0) return [{ text }];

  const parts: NotificationMessagePart[] = [];
  let index = 0;
  while (index < text.length) {
    const term = candidates.find(candidate => text.startsWith(candidate, index));
    if (term) {
      appendMessagePart(parts, term, true);
      index += term.length;
      continue;
    }

    appendMessagePart(parts, text[index], false);
    index += 1;
  }

  return parts;
}

function appendMessagePart(parts: NotificationMessagePart[], text: string, emphasis: boolean) {
  const last = parts[parts.length - 1];
  if (last && Boolean(last.emphasis) === emphasis) {
    last.text += text;
    return;
  }
  parts.push(emphasis ? { text, emphasis: true } : { text });
}

function emphasisTermsForEvent(event: NotificationTextEvent, values: ReturnType<typeof buildValues>) {
  const terms = [
    values.newScore,
    values.oldScore,
    values.oldRank,
    values.newRank,
    values.oldDifficulty,
    values.newDifficulty,
    values.oldCount,
    values.newCount,
    values.instrument,
    values.combo,
    values.scope,
  ];

  if (SONG_TEXT_EVENT_KINDS.has(event.eventKind)) {
    terms.push(values.song);
  }

  if (event.eventKind === 'player_gold_stars_achieved' || event.eventKind === 'band_gold_stars_achieved') {
    terms.push('Gold Stars');
    terms.push('gold stars');
  }
  if (event.eventKind === 'player_fc_achieved' || event.eventKind === 'band_fc_achieved') {
    terms.push('Full Combo');
  }
  if (event.eventKind === 'player_stars_improved' || event.eventKind === 'band_stars_improved') {
    terms.push(`${values.oldStars} to ${values.newStars} stars`);
  }

  return filterEmphasisTerms(terms);
}

function filterEmphasisTerms(terms: Array<string | null | undefined>) {
  return Array.from(new Set(
    terms
      .map(term => term?.trim())
      .filter((term): term is string => Boolean(term) && !FALLBACK_EMPHASIS_TERMS.has(term)),
  )).sort((left, right) => right.length - left.length);
}

function formatNumberValue(t: TFunction, value: number | null | undefined, fallbackKey: string) {
  return value == null ? translate(t, fallbackKey) : value.toLocaleString();
}

function formatRank(t: TFunction, rank: number | null | undefined) {
  return rank == null ? translate(t, 'notifications.values.rank') : `#${rank.toLocaleString()}`;
}

function formatEventFlag(t: TFunction, eventKind: string): NotificationFlag {
  const kind = flagKind(eventKind);
  return {
    kind,
    label: translate(t, `notifications.flags.${kind}`, { defaultValue: translate(t, 'notifications.flags.improvement') }),
  };
}

function translate(t: TFunction, key: string, options?: Record<string, unknown>) {
  return t(key, options) as string;
}

function priority(eventKind: string) {
  return EVENT_PRIORITY[eventKind] ?? 1000;
}

function rankName(t: TFunction, eventKind: string) {
  const key = AGGREGATE_RANK_TITLE_KEYS[eventKind];
  return key ? translate(t, key) : translate(t, 'notifications.values.rank');
}

function flagKind(eventKind: string): NotificationFlagKind {
  if (eventKind === 'player_first_score' || eventKind === 'band_first_score') return 'firstPlay';
  if (eventKind === 'player_score_pb' || eventKind === 'band_score_pb' || eventKind === 'band_combo_score_pb') return 'newHighScore';
  if (eventKind === 'player_fc_achieved' || eventKind === 'band_fc_achieved') return 'fullCombo';
  if (eventKind.includes('rank_improved')) return 'rankUp';
  if (eventKind === 'player_gold_stars_achieved' || eventKind === 'band_gold_stars_achieved') return 'goldStars';
  if (eventKind === 'player_stars_improved' || eventKind === 'band_stars_improved') return 'starsUp';
  if (eventKind === 'player_difficulty_bumped' || eventKind === 'band_member_difficulty_bumped') return 'difficultyUp';
  if (eventKind === 'player_total_score_improved' || eventKind === 'player_fc_count_improved' || eventKind === 'band_total_score_improved' || eventKind === 'band_fc_count_improved') return 'progress';
  return 'improvement';
}

function uniqueFlags(flags: NotificationFlag[]) {
  const seen = new Set<NotificationFlagKind>();
  return flags.filter((flag) => {
    if (seen.has(flag.kind)) return false;
    seen.add(flag.kind);
    return true;
  });
}

const PRIMARY_EVENT_KINDS = new Set([
  'player_first_score',
  'player_score_pb',
  'player_song_rank_improved',
  'player_stars_improved',
  'player_gold_stars_achieved',
  'player_fc_achieved',
  'player_difficulty_bumped',
  'player_weighted_rank_improved',
  'player_skill_rank_improved',
  'player_total_score_rank_improved',
  'player_fc_rate_rank_improved',
  'player_max_score_rank_improved',
  'player_total_score_improved',
  'player_fc_count_improved',
  'band_first_score',
  'band_score_pb',
  'band_combo_score_pb',
  'band_song_rank_improved',
  'band_stars_improved',
  'band_gold_stars_achieved',
  'band_fc_achieved',
  'band_member_difficulty_bumped',
  'band_weighted_rank_improved',
  'band_skill_rank_improved',
  'band_total_score_rank_improved',
  'band_fc_rate_rank_improved',
  'band_max_score_rank_improved',
  'band_total_score_improved',
  'band_fc_count_improved',
]);

const COMBO_PRIMARY_EVENT_KINDS = new Set([
  'band_first_score',
  'band_combo_score_pb',
  'band_song_rank_improved',
  'band_stars_improved',
  'band_gold_stars_achieved',
  'band_fc_achieved',
  'band_member_difficulty_bumped',
]);

const COMBO_INSTRUMENT_PRIMARY_EVENT_KINDS = new Set([
  'band_first_score',
  'band_score_pb',
  'band_combo_score_pb',
]);

const FIRST_SCORE_EVENT_KINDS = new Set([
  'player_first_score',
  'band_first_score',
]);

const SCORE_RESULT_EVENT_KINDS = new Set([
  'player_first_score',
  'player_score_pb',
  'band_first_score',
  'band_score_pb',
  'band_combo_score_pb',
]);

const FALLBACK_EMPHASIS_TERMS = new Set([
  'this song',
  'a new score',
  'your new rank',
  'more',
  'a higher difficulty',
  'this instrument',
  'this combo',
  'these rankings',
]);

const SONG_TEXT_EVENT_KINDS = new Set([
  'player_first_score',
  'player_score_pb',
  'player_song_rank_improved',
  'player_stars_improved',
  'player_gold_stars_achieved',
  'player_fc_achieved',
  'player_difficulty_bumped',
  'band_first_score',
  'band_score_pb',
  'band_combo_score_pb',
  'band_song_rank_improved',
  'band_stars_improved',
  'band_gold_stars_achieved',
  'band_fc_achieved',
  'band_member_difficulty_bumped',
]);

const PLAYER_SONG_TEXT_EVENT_KINDS = new Set([
  'player_first_score',
  'player_score_pb',
  'player_song_rank_improved',
  'player_stars_improved',
  'player_gold_stars_achieved',
  'player_fc_achieved',
  'player_difficulty_bumped',
]);

const BAND_SONG_TEXT_EVENT_KINDS = new Set([
  'band_first_score',
  'band_score_pb',
  'band_combo_score_pb',
  'band_song_rank_improved',
  'band_stars_improved',
  'band_gold_stars_achieved',
  'band_fc_achieved',
  'band_member_difficulty_bumped',
]);

const PLAYER_INSTRUMENT_AGGREGATE_PROGRESS_EVENT_KINDS = new Set([
  'player_total_score_improved',
  'player_fc_count_improved',
]);

const PLAYER_INSTRUMENT_AGGREGATE_EVENT_KINDS = new Set([
  'player_total_score_improved',
  'player_total_score_rank_improved',
  'player_fc_count_improved',
  'player_fc_rate_rank_improved',
  'player_skill_rank_improved',
  'player_weighted_rank_improved',
  'player_max_score_rank_improved',
]);

const AGGREGATE_RANK_TITLE_KEYS: Record<string, string> = {
  player_weighted_rank_improved: 'notifications.rankNames.weightedRank',
  player_skill_rank_improved: 'notifications.rankNames.skillRank',
  player_total_score_rank_improved: 'notifications.rankNames.totalScoreRank',
  player_fc_rate_rank_improved: 'notifications.rankNames.fullComboRank',
  player_max_score_rank_improved: 'notifications.rankNames.maxScoreRank',
  band_weighted_rank_improved: 'notifications.rankNames.weightedRank',
  band_skill_rank_improved: 'notifications.rankNames.skillRank',
  band_total_score_rank_improved: 'notifications.rankNames.totalScoreRank',
  band_fc_rate_rank_improved: 'notifications.rankNames.fullComboRank',
  band_max_score_rank_improved: 'notifications.rankNames.maxScoreRank',
};

const PROGRESS_TITLE_KEYS: Record<string, string> = {
  player_total_score_improved: 'notifications.titles.totalScoreImproved',
  band_total_score_improved: 'notifications.titles.totalScoreImproved',
  player_fc_count_improved: 'notifications.titles.fullComboCountImproved',
  band_fc_count_improved: 'notifications.titles.fullComboCountImproved',
};

const DETAIL_EVENT_KINDS = new Set([
  'player_first_score',
  'player_score_pb',
  'player_song_rank_improved',
  'player_stars_improved',
  'player_gold_stars_achieved',
  'player_fc_achieved',
  'player_difficulty_bumped',
  'band_first_score',
  'band_score_pb',
  'band_combo_score_pb',
  'band_song_rank_improved',
  'band_stars_improved',
  'band_gold_stars_achieved',
  'band_fc_achieved',
  'band_member_difficulty_bumped',
]);
