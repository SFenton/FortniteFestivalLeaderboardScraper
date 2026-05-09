import { describe, expect, it } from 'vitest';
import i18next from 'i18next';
import { formatNotificationPresentation, type NotificationTextInput } from '../../../src/components/notifications/notificationText';

const base: NotificationTextInput = {
  eventKind: 'player_score_pb',
  title: 'Apple',
  songTitle: 'Apple',
  instrumentLabel: 'Drums',
  scopeLabel: 'Band Trios',
  comboLabel: 'Bass/Bass/Drums',
  rankingScope: 'overall',
};

function format(input: NotificationTextInput) {
  return present(input).message;
}

function present(input: NotificationTextInput) {
  return formatNotificationPresentation(i18next.t, { ...base, ...input });
}

function emphasizedText(input: ReturnType<typeof formatNotificationPresentation>) {
  return input.messageParts.filter(part => part.emphasis).map(part => part.text);
}

describe('notificationText', () => {
  it.each([
    ['player_first_score', { newNumeric: 180005, newRank: 1288 }, 'Your first Drums play on Apple scored 180,005 and started at #1,288.'],
    ['player_score_pb', { oldNumeric: 127025, newNumeric: 137700 }, 'You set a new personal best on Drums for Apple with 137,700.'],
    ['player_song_rank_improved', { oldRank: 1214, newRank: 982 }, 'You climbed from #1,214 to #982 on Drums for Apple.'],
    ['player_stars_improved', { oldNumeric: 5, newNumeric: 6 }, 'You improved from 5 to 6 stars on Drums for Apple.'],
    ['player_gold_stars_achieved', { newNumeric: 6 }, 'You earned gold stars on Drums for Apple.'],
    ['player_fc_achieved', {}, 'You got a Full Combo on Drums for Apple.'],
    ['player_difficulty_bumped', { oldLabel: 'Hard', newLabel: 'Expert' }, 'You improved your difficulty on Drums for Apple from Hard to Expert.'],
    ['player_weighted_rank_improved', { oldRank: 45, newRank: 42 }, 'You moved up from #45 to #42 in Drums weighted rankings.'],
    ['player_skill_rank_improved', { oldRank: 45, newRank: 42 }, 'You moved up from #45 to #42 in Drums skill rankings.'],
    ['player_total_score_rank_improved', { oldRank: 45, newRank: 42 }, 'You moved up from #45 to #42 in Drums total score rankings.'],
    ['player_fc_rate_rank_improved', { oldRank: 45, newRank: 42 }, 'You moved up from #45 to #42 in Drums Full Combo rankings.'],
    ['player_total_score_improved', { oldNumeric: 100000, newNumeric: 123456 }, 'Your Drums total score increased to 123,456.'],
    ['player_fc_count_improved', { oldNumeric: 31, newNumeric: 32 }, 'Your Drums Full Combo count increased to 32.'],
    ['band_first_score', { newNumeric: 180005, newRank: 1288 }, "Your band's first play on Apple scored 180,005 and started at #1,288."],
    ['band_score_pb', { oldNumeric: 127025, newNumeric: 137700 }, 'Your band set a new best score on Apple with 137,700.'],
    ['band_combo_score_pb', { oldNumeric: 1210400, newNumeric: 1234567, rankingScope: 'combo' }, 'Your band set a new best score on Apple for Bass/Bass/Drums with 1,234,567.'],
    ['band_song_rank_improved', { oldRank: 1214, newRank: 982 }, 'Your band climbed from #1,214 to #982 on Apple.'],
    ['band_stars_improved', { oldNumeric: 5, newNumeric: 6 }, 'Your band improved from 5 to 6 stars on Apple.'],
    ['band_gold_stars_achieved', { newNumeric: 6 }, 'Your band earned gold stars on Apple.'],
    ['band_fc_achieved', {}, 'Your band got a Full Combo on Apple.'],
    ['band_member_difficulty_bumped', { oldLabel: 'Hard', newLabel: 'Expert' }, 'Your band improved difficulty on Apple from Hard to Expert.'],
    ['band_weighted_rank_improved', { oldRank: 19, newRank: 16 }, 'Your band moved up from #19 to #16 in Band Trios weighted rankings.'],
    ['band_total_score_rank_improved', { oldRank: 19, newRank: 16 }, 'Your band moved up from #19 to #16 in Band Trios total score rankings.'],
    ['band_fc_rate_rank_improved', { oldRank: 19, newRank: 16 }, 'Your band moved up from #19 to #16 in Band Trios Full Combo rankings.'],
    ['band_total_score_improved', { oldNumeric: 2000000, newNumeric: 2100000 }, "Your band's Band Trios total score increased to 2,100,000."],
    ['band_fc_count_improved', { oldNumeric: 18, newNumeric: 19 }, "Your band's Band Trios Full Combo count increased to 19."],
  ])('formats %s', (eventKind, values, expected) => {
    expect(format({ eventKind, ...(values as Partial<NotificationTextInput>) })).toBe(expected);
  });

  it('adds the friendly instrument to solo song notification titles', () => {
    expect(present({ eventKind: 'player_score_pb' }).title).toBe('Apple · Drums');
    expect(present({ eventKind: 'player_first_score', title: "Ghosts 'n' Stuff", songTitle: "Ghosts 'n' Stuff", instrumentLabel: 'Pro Drums' }).title).toBe("Ghosts 'n' Stuff · Pro Drums");
    expect(present({ eventKind: 'band_score_pb', title: 'Apple' }).title).toBe('Apple · Band Trios');
    expect(present({ eventKind: 'band_combo_score_pb', title: 'Apple', scopeLabel: 'Band Trios' }).title).toBe('Apple · Band Trios');
  });

  it('formats aggregate rank notification titles with friendly rank and scope names', () => {
    expect(present({ eventKind: 'player_weighted_rank_improved', title: 'Solo Drums weighted rank' }).title).toBe('Weighted Rank · Drums');
    expect(present({ eventKind: 'player_skill_rank_improved', title: 'Solo Drums skill rank' }).title).toBe('Skill Rank · Drums');
    expect(present({ eventKind: 'player_total_score_rank_improved', title: 'Solo Drums total score rank' }).title).toBe('Total Score Rank · Drums');
    expect(present({ eventKind: 'player_fc_rate_rank_improved', title: 'Solo Drums Full Combo rank' }).title).toBe('Full Combo Rank · Drums');
    expect(present({ eventKind: 'band_weighted_rank_improved', title: 'Band Duos weighted rank', scopeLabel: 'Band Duos' }).title).toBe('Weighted Rank · Band Duos');
    expect(present({ eventKind: 'band_total_score_rank_improved', title: 'Band Duos total score rank', scopeLabel: 'Band Duos' }).title).toBe('Total Score Rank · Band Duos');
    expect(present({ eventKind: 'band_fc_rate_rank_improved', title: 'Band Duos Full Combo rank', scopeLabel: 'Band Duos' }).title).toBe('Full Combo Rank · Band Duos');
    expect(present({ eventKind: 'player_total_score_improved', title: 'Solo Drums total score' }).title).toBe('Solo Drums total score');
  });

  it('combines player PB, Full Combo, gold stars, and rank into one friendly sentence', () => {
    const presentation = formatNotificationPresentation(i18next.t, {
      ...base,
      eventKind: 'player_score_pb',
      payload: {
        coalescedEvents: [
          { eventKind: 'player_score_pb', metric: 'score', oldNumeric: 127025, newNumeric: 137700 },
          { eventKind: 'player_fc_achieved', metric: 'full_combo' },
          { eventKind: 'player_gold_stars_achieved', metric: 'stars', oldNumeric: 5, newNumeric: 6 },
          { eventKind: 'player_song_rank_improved', metric: 'song_rank', oldRank: 1214, newRank: 982 },
        ],
      },
    });

    expect(presentation.title).toBe('Apple · Drums');
    expect(presentation.message).toBe('You set a new personal best on Drums for Apple with 137,700, got a Full Combo, earned gold stars, and climbed from #1,214 to #982.');
    expect(presentation.messageParts.map(part => part.text).join('')).toBe(presentation.message);
    expect(emphasizedText(presentation)).toEqual(['Drums', 'Apple', '137,700', 'Full Combo', 'gold stars', '#1,214', '#982']);
  expect(presentation.badges).toEqual(['New High Score', 'Full Combo', 'Gold Stars', 'Rank Up']);
  expect(presentation.flags.map(flag => flag.kind)).toEqual(['newHighScore', 'fullCombo', 'goldStars', 'rankUp']);
  });

  it('does not repeat stars improved when gold stars is also present', () => {
    const presentation = formatNotificationPresentation(i18next.t, {
      ...base,
      eventKind: 'player_score_pb',
      payload: {
        coalescedEvents: [
          { eventKind: 'player_score_pb', metric: 'score', oldNumeric: 127025, newNumeric: 137700 },
          { eventKind: 'player_gold_stars_achieved', metric: 'stars', oldNumeric: 5, newNumeric: 6 },
          { eventKind: 'player_stars_improved', metric: 'stars', oldNumeric: 5, newNumeric: 6 },
        ],
      },
    });

    expect(presentation.message).toBe('You set a new personal best on Drums for Apple with 137,700 and earned gold stars.');
  expect(presentation.badges).toEqual(['New High Score', 'Gold Stars']);
  });

  it('formats first play with rank and gold stars as a clean list', () => {
    const presentation = formatNotificationPresentation(i18next.t, {
      ...base,
      eventKind: 'player_first_score',
      title: "Ghosts 'n' Stuff",
      songTitle: "Ghosts 'n' Stuff",
      payload: {
        coalescedEvents: [
          { eventKind: 'player_first_score', metric: 'score', newNumeric: 180005, newRank: 1288 },
          { eventKind: 'player_gold_stars_achieved', metric: 'stars', newNumeric: 6 },
        ],
      },
    });

    expect(presentation.title).toBe("Ghosts 'n' Stuff · Drums");
    expect(presentation.message).toBe("Your first Drums play on Ghosts 'n' Stuff scored 180,005, started at #1,288, and earned gold stars.");
    expect(presentation.message).not.toContain('and started at #1,288 and');
    expect(emphasizedText(presentation)).toEqual(['Drums', "Ghosts 'n' Stuff", '180,005', '#1,288', 'gold stars']);
  });

  it('combines band combo PB, Full Combo, gold stars, and rank into one friendly sentence', () => {
    const presentation = formatNotificationPresentation(i18next.t, {
      ...base,
      eventKind: 'band_combo_score_pb',
      rankingScope: 'combo',
      payload: {
        coalescedEvents: [
          { eventKind: 'band_combo_score_pb', metric: 'score', oldNumeric: 1210400, newNumeric: 1234567 },
          { eventKind: 'band_fc_achieved', metric: 'full_combo' },
          { eventKind: 'band_gold_stars_achieved', metric: 'stars', oldNumeric: 5, newNumeric: 6 },
          { eventKind: 'band_song_rank_improved', metric: 'song_rank', oldRank: 42, newRank: 31 },
        ],
      },
    });

    expect(presentation.message).toBe('Your band set a new best score on Apple for Bass/Bass/Drums with 1,234,567, got a Full Combo, earned gold stars, and climbed from #42 to #31.');
  expect(presentation.badges).toEqual(['New High Score', 'Full Combo', 'Gold Stars', 'Rank Up']);
  });

  it('keeps overall and combo band song rank clauses in one friendly sentence', () => {
    const presentation = formatNotificationPresentation(i18next.t, {
      ...base,
      eventKind: 'band_score_pb',
      rankingScope: 'overall',
      payload: {
        coalescedEvents: [
          { eventKind: 'band_score_pb', metric: 'score', oldNumeric: 1210400, newNumeric: 1234567 },
          { eventKind: 'band_song_rank_improved', metric: 'song_rank', oldRank: 42, newRank: 31, rankingScope: 'overall', scopeLabel: 'Band Trios' },
          { eventKind: 'band_song_rank_improved', metric: 'song_rank', oldRank: 9, newRank: 6, rankingScope: 'combo', comboLabel: 'Bass/Bass/Drums' },
        ],
      },
    });

    expect(presentation.message).toBe('Your band set a new best score on Apple with 1,234,567, climbed from #42 to #31 in Band Trios, and climbed from #9 to #6 for Bass/Bass/Drums.');
    expect(emphasizedText(presentation)).toEqual(['Apple', '1,234,567', '#42', '#31', 'Band Trios', '#9', '#6', 'Bass/Bass/Drums']);
  expect(presentation.badges).toEqual(['New High Score', 'Rank Up']);
  });

  it('falls back for unknown event kinds', () => {
    const presentation = formatNotificationPresentation(i18next.t, { ...base, eventKind: 'future_event_kind' });

    expect(presentation.message).toBe('New improvement detected.');
    expect(presentation.badges).toEqual(['Improvement']);
  });
});
