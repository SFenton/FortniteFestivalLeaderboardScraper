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
    ['player_first_score', { newNumeric: 180005, newRank: 1288 }, 'First Drums score on Apple: 180,005 and started at #1,288.'],
    ['player_score_pb', { oldNumeric: 127025, newNumeric: 137700 }, 'Drums score improved from 127,025 to 137,700.'],
    ['player_song_rank_improved', { oldRank: 1214, newRank: 982 }, 'Drums rank improved from #1,214 to #982.'],
    ['player_stars_improved', { oldNumeric: 5, newNumeric: 6 }, 'Drums stars improved from 5 to 6.'],
    ['player_gold_stars_achieved', { newNumeric: 6 }, 'Drums earned Gold Stars.'],
    ['player_fc_achieved', {}, 'Drums earned a Full Combo.'],
    ['player_difficulty_bumped', { oldLabel: 'Hard', newLabel: 'Expert' }, 'Drums difficulty improved from Hard to Expert.'],
    ['player_weighted_rank_improved', { oldRank: 45, newRank: 42 }, 'Moved from #45 to #42.'],
    ['player_skill_rank_improved', { oldRank: 45, newRank: 42 }, 'Moved from #45 to #42.'],
    ['player_total_score_rank_improved', { oldRank: 45, newRank: 42 }, 'Moved from #45 to #42.'],
    ['player_fc_rate_rank_improved', { oldRank: 45, newRank: 42 }, 'Moved from #45 to #42.'],
    ['player_total_score_improved', { oldNumeric: 100000, newNumeric: 123456 }, 'Improved from 100,000 to 123,456.'],
    ['player_fc_count_improved', { oldNumeric: 31, newNumeric: 32 }, 'Improved from 31 to 32.'],
    ['band_first_score', { newNumeric: 180005, newRank: 1288 }, 'First band score on Apple: 180,005 and started at #1,288.'],
    ['band_score_pb', { oldNumeric: 127025, newNumeric: 137700 }, 'Score improved from 127,025 to 137,700.'],
    ['band_combo_score_pb', { oldNumeric: 1210400, newNumeric: 1234567, rankingScope: 'combo' }, 'Bass/Bass/Drums score improved from 1,210,400 to 1,234,567.'],
    ['band_song_rank_improved', { oldRank: 1214, newRank: 982 }, 'Rank improved from #1,214 to #982.'],
    ['band_stars_improved', { oldNumeric: 5, newNumeric: 6 }, 'Stars improved from 5 to 6.'],
    ['band_gold_stars_achieved', { newNumeric: 6 }, 'Earned Gold Stars.'],
    ['band_fc_achieved', {}, 'Earned a Full Combo.'],
    ['band_member_difficulty_bumped', { oldLabel: 'Hard', newLabel: 'Expert' }, 'Difficulty improved from Hard to Expert.'],
    ['band_weighted_rank_improved', { oldRank: 19, newRank: 16 }, 'Moved from #19 to #16.'],
    ['band_total_score_rank_improved', { oldRank: 19, newRank: 16 }, 'Moved from #19 to #16.'],
    ['band_fc_rate_rank_improved', { oldRank: 19, newRank: 16 }, 'Moved from #19 to #16.'],
    ['band_total_score_improved', { oldNumeric: 2000000, newNumeric: 2100000 }, 'Improved from 2,000,000 to 2,100,000.'],
    ['band_fc_count_improved', { oldNumeric: 18, newNumeric: 19 }, 'Improved from 18 to 19.'],
  ])('formats %s', (eventKind, values, expected) => {
    expect(format({ eventKind, ...(values as Partial<NotificationTextInput>) })).toBe(expected);
  });

  it('adds the friendly instrument to solo song notification titles', () => {
    expect(present({ eventKind: 'player_score_pb' }).title).toBe('Apple · Drums');
    expect(present({ eventKind: 'player_first_score', title: "Ghosts 'n' Stuff", songTitle: "Ghosts 'n' Stuff", instrumentLabel: 'Pro Drums' }).title).toBe("Ghosts 'n' Stuff · Pro Drums");
    expect(present({ eventKind: 'band_score_pb', title: 'Apple' }).title).toBe('Apple · Band Trios');
    expect(present({ eventKind: 'band_combo_score_pb', title: 'Apple', scopeLabel: 'Band Trios' }).title).toBe('Apple · Band Trios');
  });

  it('formats aggregate and progress notification titles as event headlines', () => {
    expect(present({ eventKind: 'player_weighted_rank_improved', title: 'Solo Drums weighted rank' }).title).toBe('Weighted Rank Improved');
    expect(present({ eventKind: 'player_skill_rank_improved', title: 'Solo Drums skill rank' }).title).toBe('Skill Rank Improved');
    expect(present({ eventKind: 'player_total_score_rank_improved', title: 'Solo Drums total score rank' }).title).toBe('Total Score Rank Improved');
    expect(present({ eventKind: 'player_fc_rate_rank_improved', title: 'Solo Drums Full Combo rank' }).title).toBe('Full Combo Rank Improved');
    expect(present({ eventKind: 'band_weighted_rank_improved', title: 'Band Duos weighted rank', scopeLabel: 'Band Duos' }).title).toBe('Weighted Rank Improved');
    expect(present({ eventKind: 'band_total_score_rank_improved', title: 'Band Duos total score rank', scopeLabel: 'Band Duos' }).title).toBe('Total Score Rank Improved');
    expect(present({ eventKind: 'band_fc_rate_rank_improved', title: 'Band Duos Full Combo rank', scopeLabel: 'Band Duos' }).title).toBe('Full Combo Rank Improved');
    expect(present({ eventKind: 'player_total_score_improved', title: 'Solo Drums total score' }).title).toBe('Total Score Improved');
    expect(present({ eventKind: 'band_fc_count_improved', title: 'Band Duos Full Combo count' }).title).toBe('Full Combo Count Improved');
  });

  it('does not repeat selected band names in aggregate rank notifications', () => {
    const presentation = present({
      eventKind: 'band_fc_rate_rank_improved',
      title: 'SFentonX + kahnyri + Phankie.ToT',
      scopeLabel: 'SFentonX + kahnyri + Phankie.ToT',
      oldRank: 7557,
      newRank: 7554,
    });

    expect(presentation.title).toBe('Full Combo Rank Improved');
    expect(presentation.message).toBe('Moved from #7,557 to #7,554.');
    expect(presentation.message).not.toContain('SFentonX');
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
    expect(presentation.message).toBe('Drums score improved from 127,025 to 137,700, earned a Full Combo, earned Gold Stars, and rank improved from #1,214 to #982.');
    expect(presentation.messageParts.map(part => part.text).join('')).toBe(presentation.message);
    expect(emphasizedText(presentation)).toEqual(['Drums', '127,025', '137,700', 'Full Combo', 'Gold Stars', '#1,214', '#982']);
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

    expect(presentation.message).toBe('Drums score improved from 127,025 to 137,700 and earned Gold Stars.');
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
    expect(presentation.message).toBe("First Drums score on Ghosts 'n' Stuff: 180,005, started at #1,288, and earned Gold Stars.");
    expect(presentation.message).not.toContain('and started at #1,288 and');
    expect(emphasizedText(presentation)).toEqual(['Drums', "Ghosts 'n' Stuff", '180,005', '#1,288', 'Gold Stars']);
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

    expect(presentation.message).toBe('Bass/Bass/Drums score improved from 1,210,400 to 1,234,567, earned a Full Combo, earned Gold Stars, and rank improved from #42 to #31.');
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

    expect(presentation.message).toBe('Score improved from 1,210,400 to 1,234,567, Band Trios rank improved from #42 to #31, and Bass/Bass/Drums rank improved from #9 to #6.');
    expect(emphasizedText(presentation)).toEqual(['1,210,400', '1,234,567', 'Band Trios', '#42', '#31', 'Bass/Bass/Drums', '#9', '#6']);
    expect(presentation.badges).toEqual(['New High Score', 'Rank Up']);
  });

  it('falls back for unknown event kinds', () => {
    const presentation = formatNotificationPresentation(i18next.t, { ...base, eventKind: 'future_event_kind' });

    expect(presentation.message).toBe('New improvement detected.');
    expect(presentation.badges).toEqual(['Improvement']);
  });
});
