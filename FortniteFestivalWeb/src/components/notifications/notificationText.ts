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
    ? statementStyleMessage ? clauses.join(' ') : formatSentence(t, clauses)
    : translate(t, 'notifications.copy.unknown');
  const messageParts = richClauses.length > 0
    ? emphasizeText(message, richClauses.flatMap(clause => clause.emphasisTerms))
    : [{ text: message }];
  const flags = uniqueFlags(events.map(event => formatEventFlag(t, event.eventKind)));

  return {
    title,
    message,
    messageParts,
    badges: flags.map(flag => flag.label),
    flags,
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
      }];

  return removeRedundantStarEvents(events)
    .sort((left, right) => priority(left.eventKind) - priority(right.eventKind));
}

function formatNotificationClauses(t: TFunction, input: NotificationTextInput, events: NotificationTextEvent[]): NotificationClause[] {
  if (isMultiAggregateRankNotification(events)) {
    return formatAggregateRankUpdateClauses(t, events);
  }

  if (isMultiInstrumentPlayerSongNotification(events)) {
    return formatMultiInstrumentSongClauses(t, input, events);
  }

  return events.flatMap((event, index) => formatEventClauses(t, input, event, index === 0));
}

function shouldUseStatementStyleMessage(events: NotificationTextEvent[]) {
  return isMultiAggregateRankNotification(events) || isMultiInstrumentPlayerSongNotification(events);
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

function formatMultiInstrumentSongClauses(t: TFunction, input: NotificationTextInput, events: NotificationTextEvent[]): NotificationClause[] {
  return groupedInstrumentEvents(events).map(({ label, events: groupEvents }) => {
    const scopedInput = { ...input, instrumentLabel: label };
    const detailClauses = groupEvents.flatMap((event) => {
      const clauses = formatEventClauses(t, scopedInput, event, false);
      if (FIRST_SCORE_EVENT_KINDS.has(event.eventKind) && event.newRank != null) {
        const values = buildValues(t, scopedInput, event);
        return [
          ...clauses,
          { text: translate(t, 'notifications.copy.detail.first_score_rank', values), emphasisTerms: filterEmphasisTerms([values.newRank]) },
        ];
      }
      return clauses;
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
    ? primaryKey(event.eventKind, combo, shouldUseBandRankNoScopeCopy(input, event))
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

function primaryKey(eventKind: string, combo: boolean, noScopeBandRank = false): string | null {
  if (noScopeBandRank) return `notifications.copy.primaryBandNoScope.${eventKind}`;
  if (combo && eventKind.startsWith('band_')) {
    const comboKey = `notifications.copy.primaryCombo.${eventKind}`;
    if (COMBO_PRIMARY_EVENT_KINDS.has(eventKind)) return comboKey;
  }
  if (PRIMARY_EVENT_KINDS.has(eventKind)) return `notifications.copy.primary.${eventKind}`;
  return null;
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

const FIRST_SCORE_EVENT_KINDS = new Set([
  'player_first_score',
  'band_first_score',
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
