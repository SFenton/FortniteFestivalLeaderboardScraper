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
  it('formats new item shop song service notifications without badges', () => {
    const presentation = present({
      eventKind: 'service_new_shop_song',
      title: 'Folded',
      songTitle: 'Folded',
      artist: 'Kehlani',
    });

    expect(presentation.title).toBe('New Song · Folded - Kehlani');
    expect(presentation.message).toBe('Folded by Kehlani has been added to the Item Shop.');
    expect(presentation.messageParts).toEqual([
      { text: 'Folded', emphasis: true },
      { text: ' by ' },
      { text: 'Kehlani', emphasis: true },
      { text: ' has been added to the Item Shop.' },
    ]);
    expect(presentation.badges).toEqual([]);
    expect(presentation.flags).toEqual([]);
  });

  it.each([
    ['player_first_score', { newNumeric: 180005, newRank: 1288 }, 'Your first Drums play on Apple scored 180,005 points and started at #1,288.'],
    ['player_score_pb', { oldNumeric: 127025, newNumeric: 137700 }, 'You set a new personal best on Drums for Apple with 137,700 points.'],
    ['player_song_rank_improved', { oldRank: 1214, newRank: 982 }, 'You climbed from #1,214 to #982 on Drums for Apple.'],
    ['player_stars_improved', { oldNumeric: 5, newNumeric: 6 }, 'You improved from 5 to 6 stars on Drums for Apple.'],
    ['player_gold_stars_achieved', { newNumeric: 6 }, 'You earned gold stars on Drums for Apple.'],
    ['player_fc_achieved', {}, 'You got a Full Combo on Drums for Apple.'],
    ['player_difficulty_bumped', { oldLabel: 'Hard', newLabel: 'Expert' }, 'You improved your difficulty on Drums for Apple from Hard to Expert.'],
    ['player_weighted_rank_improved', { oldRank: 45, newRank: 42 }, 'You moved up from #45 to #42 in Drums percentile rankings, weighted by number of entries.'],
    ['player_skill_rank_improved', { oldRank: 45, newRank: 42 }, 'You moved up from #45 to #42 in Drums adjusted percentile rankings.'],
    ['player_total_score_rank_improved', { oldRank: 45, newRank: 42 }, 'You moved up from #45 to #42 in Drums total score rankings.'],
    ['player_fc_rate_rank_improved', { oldRank: 45, newRank: 42 }, 'You moved up from #45 to #42 in Drums Full Combo rankings.'],
    ['player_total_score_improved', { oldNumeric: 100000, newNumeric: 123456 }, 'Your Drums total score increased to 123,456 points.'],
    ['player_fc_count_improved', { oldNumeric: 31, newNumeric: 32 }, 'Your Drums Full Combo count increased to 32.'],
    ['band_first_score', { newNumeric: 180005, newRank: 1288 }, "Your band's first play on Apple scored 180,005 points and started at #1,288."],
    ['band_score_pb', { oldNumeric: 127025, newNumeric: 137700 }, 'Your band set a new best score on Apple with 137,700 points.'],
    ['band_combo_score_pb', { oldNumeric: 1210400, newNumeric: 1234567, rankingScope: 'combo' }, 'Your Drums play set a new best score with 1,234,567 points.'],
    ['band_song_rank_improved', { oldRank: 1214, newRank: 982 }, 'Your band climbed from #1,214 to #982 on Apple.'],
    ['band_stars_improved', { oldNumeric: 5, newNumeric: 6 }, 'Your band improved from 5 to 6 stars on Apple.'],
    ['band_gold_stars_achieved', { newNumeric: 6 }, 'Your band earned gold stars on Apple.'],
    ['band_fc_achieved', {}, 'Your band got a Full Combo on Apple.'],
    ['band_member_difficulty_bumped', { oldLabel: 'Hard', newLabel: 'Expert' }, 'Your band improved difficulty on Apple from Hard to Expert.'],
    ['band_weighted_rank_improved', { oldRank: 19, newRank: 16 }, 'Your band moved up from #19 to #16 in Band Trios percentile rankings, weighted by number of entries.'],
    ['band_total_score_rank_improved', { oldRank: 19, newRank: 16 }, 'Your band moved up from #19 to #16 in Band Trios total score rankings.'],
    ['band_fc_rate_rank_improved', { oldRank: 19, newRank: 16 }, 'Your band moved up from #19 to #16 in Band Trios Full Combo rankings.'],
    ['band_total_score_improved', { oldNumeric: 2000000, newNumeric: 2100000 }, "Your band's Band Trios total score increased to 2,100,000 points."],
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

  it('formats aggregate and progress notification titles as event headlines', () => {
    expect(present({ eventKind: 'player_weighted_rank_improved', title: 'Solo Drums weighted percentile rank' }).title).toBe('Weighted Percentile Rank Improved');
    expect(present({ eventKind: 'player_skill_rank_improved', title: 'Solo Drums adjusted percentile rank' }).title).toBe('Adjusted Percentile Rank Improved');
    expect(present({ eventKind: 'player_total_score_rank_improved', title: 'Solo Drums total score rank' }).title).toBe('Total Score Rank Improved');
    expect(present({ eventKind: 'player_fc_rate_rank_improved', title: 'Solo Drums Full Combo rank' }).title).toBe('Full Combo Rank Improved');
    expect(present({ eventKind: 'band_weighted_rank_improved', title: 'Band Duos weighted percentile rank', scopeLabel: 'Band Duos' }).title).toBe('Weighted Percentile Rank Improved');
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
    expect(presentation.message).toBe('Your band moved up from #7,557 to #7,554 in Full Combo rankings.');
    expect(presentation.message).not.toContain('SFentonX');
  });

  it('formats combo song score notifications with the player instrument when available', () => {
    expect(format({
      eventKind: 'band_first_score',
      rankingScope: 'combo',
      instrumentLabel: 'Pro Drums',
      newNumeric: 180005,
      newRank: 1288,
    })).toBe('Your first Pro Drums play scored 180,005 points and started at #1,288.');

    expect(format({
      eventKind: 'band_combo_score_pb',
      rankingScope: 'combo',
      instrumentLabel: 'Pro Drums',
      oldNumeric: 1210400,
      newNumeric: 1234567,
    })).toBe('Your Pro Drums play set a new best score with 1,234,567 points.');
  });

  it('keeps band combo score copy when no instrument context is available', () => {
    expect(format({
      eventKind: 'band_combo_score_pb',
      rankingScope: 'combo',
      instrumentLabel: null,
      oldNumeric: 1210400,
      newNumeric: 1234567,
    })).toBe('Your band set a new best score on Apple for Bass/Bass/Drums with 1,234,567 points.');
  });

  it('combines player PB, Full Combo, gold stars, and rank into one friendly sentence', () => {
    const presentation = formatNotificationPresentation(i18next.t, {
      ...base,
      eventKind: 'player_score_pb',
      payload: {
        oldFullCombo: true,
        newFullCombo: true,
        oldStars: 6,
        newStars: 6,
        coalescedEvents: [
          { eventKind: 'player_score_pb', metric: 'score', oldNumeric: 127025, newNumeric: 137700 },
          { eventKind: 'player_fc_achieved', metric: 'full_combo' },
          { eventKind: 'player_gold_stars_achieved', metric: 'stars', oldNumeric: 5, newNumeric: 6 },
          { eventKind: 'player_song_rank_improved', metric: 'song_rank', oldRank: 1214, newRank: 982 },
        ],
      },
    });

    expect(presentation.title).toBe('Apple · Drums');
    expect(presentation.message).toBe('You set a new personal best on Drums for Apple with 137,700 points, got a Full Combo, earned gold stars, and climbed from #1,214 to #982.');
    expect(presentation.messageParts.map(part => part.text).join('')).toBe(presentation.message);
    expect(emphasizedText(presentation)).toEqual(['Drums', 'Apple', '137,700', 'Full Combo', 'gold stars', '#1,214', '#982']);
    expect(presentation.badges).toEqual(['New High Score', 'Full Combo', 'Gold Stars', 'Rank Up']);
    expect(presentation.flags.map(flag => flag.kind)).toEqual(['newHighScore', 'fullCombo', 'goldStars', 'rankUp']);
    expect(presentation.message.match(/Full Combo/g) ?? []).toHaveLength(1);
    expect(presentation.message.match(/gold stars/g) ?? []).toHaveLength(1);
    expect(presentation.flagGroups).toBeUndefined();
  });

  it('surfaces Full Combo and gold stars for PB scores that already had those statuses', () => {
    const presentation = formatNotificationPresentation(i18next.t, {
      ...base,
      eventKind: 'player_score_pb',
      payload: {
        oldFullCombo: true,
        newFullCombo: true,
        oldStars: 6,
        newStars: 6,
        coalescedEvents: [
          { eventKind: 'player_score_pb', metric: 'score', oldNumeric: 127025, newNumeric: 137700 },
          { eventKind: 'player_song_rank_improved', metric: 'song_rank', oldRank: 1214, newRank: 982 },
        ],
      },
    });

    expect(presentation.message).toBe('You set a new personal best on Drums for Apple with 137,700 points, got a Full Combo, earned gold stars, and climbed from #1,214 to #982.');
    expect(presentation.badges).toEqual(['New High Score', 'Full Combo', 'Gold Stars', 'Rank Up']);
  });

  it('formats multiple aggregate rank updates as rank update statements', () => {
    const presentation = formatNotificationPresentation(i18next.t, {
      ...base,
      eventKind: 'player_total_score_rank_improved',
      instrumentLabel: 'Drums',
      payload: {
        coalescedEvents: [
          { eventKind: 'player_total_score_rank_improved', metric: 'total_score_rank', oldRank: 263, newRank: 189 },
          { eventKind: 'player_weighted_rank_improved', metric: 'weighted_rank', oldRank: 201, newRank: 163 },
        ],
      },
    });

    expect(presentation.title).toBe('Rank Updates · Drums');
    expect(presentation.message).toBe('For Total Score Rank, moved from #263 to #189.\n\nFor Weighted Percentile Rank, moved from #201 to #163.');
    expect(presentation.badges).toEqual(['Rank Up']);
  });

  it('formats player instrument aggregate updates as paired stat and rank lines', () => {
    const presentation = formatNotificationPresentation(i18next.t, {
      ...base,
      eventKind: 'player_total_score_improved',
      instrumentLabel: 'Tap Vocals',
      payload: {
        coalescedEvents: [
          { eventKind: 'player_total_score_improved', metric: 'total_score', oldNumeric: 91257683, newNumeric: 91743538 },
          { eventKind: 'player_total_score_rank_improved', metric: 'total_score_rank', oldRank: 12, newRank: 10 },
          { eventKind: 'player_fc_count_improved', metric: 'full_combo_count', oldNumeric: 649, newNumeric: 655 },
          { eventKind: 'player_fc_rate_rank_improved', metric: 'fc_rate_rank', oldRank: 4, newRank: 1 },
          { eventKind: 'player_weighted_rank_improved', metric: 'weighted_rank', oldRank: 37, newRank: 35 },
        ],
      },
    });

    expect(presentation.title).toBe('Tap Vocals · Improvements');
    expect(presentation.message).toBe([
      'Your total score increased to 91,743,538 points and your total score rank moved up from #12 to #10.',
      'Your Full Combo count increased to 655 and your Full Combo percentage rank moved up from #4 to #1.',
      'Your percentile rank, weighted by number of entries, moved up from #37 to #35.',
    ].join('\n\n'));
    expect(emphasizedText(presentation)).toEqual(['91,743,538', 'total score rank', '#12', '#10', '655', 'Full Combo percentage rank', '#4', '#1', 'percentile rank, weighted by number of entries', '#37', '#35']);
    expect(presentation.badges).toEqual(['Progress', 'Rank Up']);
  });

  it('keeps player aggregate progress visible when paired experimental ranks are filtered out upstream', () => {
    const presentation = formatNotificationPresentation(i18next.t, {
      ...base,
      eventKind: 'player_total_score_improved',
      instrumentLabel: 'Tap Vocals',
      payload: {
        coalescedEvents: [
          { eventKind: 'player_total_score_improved', metric: 'total_score', oldNumeric: 91257683, newNumeric: 91743538 },
          { eventKind: 'player_total_score_rank_improved', metric: 'total_score_rank', oldRank: 12, newRank: 10 },
          { eventKind: 'player_fc_count_improved', metric: 'full_combo_count', oldNumeric: 649, newNumeric: 655 },
        ],
      },
    });

    expect(presentation.title).toBe('Tap Vocals · Improvements');
    expect(presentation.message).toBe([
      'Your total score increased to 91,743,538 points and your total score rank moved up from #12 to #10.',
      'Your Full Combo count increased to 655.',
    ].join('\n\n'));
  });

  it('formats same-song multi-instrument updates with instrument statements', () => {
    const presentation = formatNotificationPresentation(i18next.t, {
      ...base,
      eventKind: 'player_score_pb',
      songTitle: 'Taxes',
      title: 'Taxes',
      payload: {
        oldFullCombo: true,
        newFullCombo: true,
        oldStars: 6,
        newStars: 6,
        coalescedEvents: [
          { eventKind: 'player_score_pb', instrument: 'Solo_Guitar', instrumentLabel: 'Lead', metric: 'score', oldNumeric: 220000, newNumeric: 230891 },
          { eventKind: 'player_song_rank_improved', instrument: 'Solo_Guitar', instrumentLabel: 'Lead', metric: 'song_rank', oldRank: 180, newRank: 160 },
          { eventKind: 'player_fc_achieved', instrument: 'Solo_Drums', instrumentLabel: 'Drums', metric: 'full_combo' },
          { eventKind: 'player_gold_stars_achieved', instrument: 'Solo_Drums', instrumentLabel: 'Drums', metric: 'stars', oldNumeric: 5, newNumeric: 6 },
        ],
      },
    });

    expect(presentation.title).toBe('Taxes');
    expect(presentation.message).toBe('For Lead, your play set a new personal best with 230,891 points and climbed from #180 to #160.\n\nFor Drums, got a Full Combo and earned gold stars.');
    expect(presentation.badges).toEqual(['New High Score', 'Full Combo', 'Gold Stars', 'Rank Up']);
    expect(presentation.flagGroups?.map(group => ({ instrument: group.instrument, label: group.label, flags: group.flags.map(flag => flag.label) }))).toEqual([
      { instrument: 'Solo_Guitar', label: 'Lead', flags: ['New High Score', 'Rank Up'] },
      { instrument: 'Solo_Drums', label: 'Drums', flags: ['Full Combo', 'Gold Stars'] },
    ]);
  });

  it('uses child score result state for multi-instrument PB status when available', () => {
    const presentation = formatNotificationPresentation(i18next.t, {
      ...base,
      eventKind: 'player_score_pb',
      songTitle: 'Taxes',
      title: 'Taxes',
      payload: {
        coalescedEvents: [
          { eventKind: 'player_score_pb', instrument: 'Solo_Guitar', instrumentLabel: 'Lead', metric: 'score', oldNumeric: 220000, newNumeric: 230891, oldFullCombo: true, newFullCombo: true, oldStars: 6, newStars: 6 },
          { eventKind: 'player_score_pb', instrument: 'Solo_Drums', instrumentLabel: 'Drums', metric: 'score', oldNumeric: 200000, newNumeric: 210000 },
        ],
      },
    });

    expect(presentation.message).toBe('For Lead, your play set a new personal best with 230,891 points, got a Full Combo, and earned gold stars.\n\nFor Drums, your play set a new personal best with 210,000 points.');
    expect(presentation.flagGroups?.map(group => ({ instrument: group.instrument, flags: group.flags.map(flag => flag.label) }))).toEqual([
      { instrument: 'Solo_Guitar', flags: ['New High Score', 'Full Combo', 'Gold Stars'] },
      { instrument: 'Solo_Drums', flags: ['New High Score'] },
    ]);
  });

  it('uses top-level score result state only for the matching primary multi-instrument child', () => {
    const presentation = formatNotificationPresentation(i18next.t, {
      ...base,
      eventKind: 'player_score_pb',
      songTitle: 'Inferno Island',
      title: 'Inferno Island',
      instrument: 'Solo_Drums',
      instrumentLabel: 'Drums',
      metric: 'score',
      oldNumeric: 203946,
      newNumeric: 214668,
      payload: {
        oldFullCombo: true,
        newFullCombo: true,
        oldStars: 6,
        newStars: 6,
        coalescedEvents: [
          { eventKind: 'player_score_pb', instrument: 'Solo_Drums', instrumentLabel: 'Drums', metric: 'score', oldNumeric: 203946, newNumeric: 214668 },
          { eventKind: 'player_song_rank_improved', instrument: 'Solo_Drums', instrumentLabel: 'Drums', metric: 'song_rank', oldRank: 342, newRank: 56 },
          { eventKind: 'player_first_score', instrument: 'Solo_PeripheralCymbals', instrumentLabel: 'Pro Drums + Cymbals', metric: 'score', newNumeric: 201919, newRank: 6 },
        ],
      },
    });

    expect(presentation.title).toBe('Inferno Island');
    expect(presentation.message).toBe('For Drums, your play set a new personal best with 214,668 points, got a Full Combo, earned gold stars, and climbed from #342 to #56.\n\nFor Pro Drums + Cymbals, your first play scored 201,919 points and started at #6.');
    expect(presentation.flagGroups?.map(group => ({ instrument: group.instrument, flags: group.flags.map(flag => flag.label) }))).toEqual([
      { instrument: 'Solo_Drums', flags: ['New High Score', 'Full Combo', 'Gold Stars', 'Rank Up'] },
      { instrument: 'Solo_PeripheralCymbals', flags: ['First Play'] },
    ]);
  });

  it('does not duplicate first-play ranks in same-song multi-instrument updates', () => {
    const presentation = formatNotificationPresentation(i18next.t, {
      ...base,
      eventKind: 'player_first_score',
      songTitle: 'Taxes',
      title: 'Taxes',
      payload: {
        coalescedEvents: [
          { eventKind: 'player_first_score', instrument: 'Solo_Guitar', instrumentLabel: 'Lead', metric: 'score', newNumeric: 180005, newRank: 1288 },
          { eventKind: 'player_fc_achieved', instrument: 'Solo_Drums', instrumentLabel: 'Drums', metric: 'full_combo' },
        ],
      },
    });

    expect(presentation.message).toBe('For Lead, your first play scored 180,005 points and started at #1,288.\n\nFor Drums, got a Full Combo.');
    expect(presentation.message.match(/#1,288/g) ?? []).toHaveLength(1);
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

    expect(presentation.message).toBe('You set a new personal best on Drums for Apple with 137,700 points and earned gold stars.');
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
    expect(presentation.message).toBe("Your first Drums play on Ghosts 'n' Stuff scored 180,005 points, started at #1,288, and earned gold stars.");
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

    expect(presentation.message).toBe('Your Drums play set a new best score with 1,234,567 points, got a Full Combo, earned gold stars, and climbed from #42 to #31.');
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

    expect(presentation.message).toBe('Your band set a new best score on Apple with 1,234,567 points, climbed from #42 to #31 in Band Trios, and climbed from #9 to #6 for Bass/Bass/Drums.');
    expect(emphasizedText(presentation)).toEqual(['Apple', '1,234,567', '#42', '#31', 'Band Trios', '#9', '#6', 'Bass/Bass/Drums']);
    expect(presentation.badges).toEqual(['New High Score', 'Rank Up']);
  });

  it('falls back for unknown event kinds', () => {
    const presentation = formatNotificationPresentation(i18next.t, { ...base, eventKind: 'future_event_kind' });

    expect(presentation.message).toBe('New improvement detected.');
    expect(presentation.badges).toEqual(['Improvement']);
  });
});
