import {
  expandWirePlayerResponse,
  expandWireSongsResponse,
  expandWireStatsResponse,
  getServerSongInstrumentDifficulty,
  isChartedServerDifficulty,
  serverInstrumentLabel,
  serverSongSupportsInstrument,
  soloFamilyScopeLabel,
} from '../api/serverTypes';
import type {
  ServiceInfoResponse,
  ServerInstrumentKey,
  ServerSong,
  SoloFamilyScopeId,
} from '../api/serverTypes';

describe('server API runtime helpers', () => {
  test('types additive service-info durable progress contract', () => {
    const response: ServiceInfoResponse = {
      contractVersion: 2,
      phasePlan: {
        version: 'fst.scrape-plan.v2',
        phases: [{
          id: 'post.band_maintenance',
          label: 'Maintaining band projections',
          legacyPhase: 'BandMaintenance',
          ordinal: 300,
          defaultUnitsKind: 'scopes',
        }],
      },
      lastCompletedUpdate: null,
      currentUpdate: {
        status: 'updating',
        startedAt: '2026-08-13T00:00:00Z',
        phase: 'PostScrapeEnrichment',
        subOperation: 'BandMaintenance',
        contractVersion: 2,
        operationId: 'scrape.update',
        phaseId: 'post.band_maintenance',
        subphaseId: 'current_projection_refresh',
        phasePlanVersion: 'fst.scrape-plan.v2',
        unitsKind: 'scopes',
        unitsCompleted: 5,
        unitsTotal: 10,
        unitsTotalFinal: true,
        phasePercent: 50,
        overallPercentKind: 'indeterminate',
        heartbeatAt: '2026-08-13T00:00:05Z',
        lastProgressAt: '2026-08-13T00:00:04Z',
      },
      workerStatus: null,
      nextScheduledUpdateAt: null,
    };

    expect(response.currentUpdate.phasePercent).toBe(50);
    expect(response.phasePlan?.phases[0].id).toBe('post.band_maintenance');
  });

  test('formats known labels and preserves unknown values', () => {
    expect(serverInstrumentLabel('Solo_Guitar')).toBe('Lead');
    expect(serverInstrumentLabel('Unknown' as ServerInstrumentKey)).toBe('Unknown');
    expect(soloFamilyScopeLabel('pro_strings')).toBe('Pro Strings');
    expect(soloFamilyScopeLabel('unknown' as SoloFamilyScopeId)).toBe('unknown');
  });

  test('detects charted song difficulties', () => {
    expect(isChartedServerDifficulty(0)).toBe(true);
    expect(isChartedServerDifficulty(6)).toBe(true);
    expect(isChartedServerDifficulty(99)).toBe(false);
    expect(isChartedServerDifficulty(-1)).toBe(false);
    expect(isChartedServerDifficulty(Number.NaN)).toBe(false);
    expect(isChartedServerDifficulty(null)).toBe(false);

    const song: ServerSong = {
      songId: 'song',
      title: 'Song',
      artist: 'Artist',
      difficulty: {guitar: 4, bass: 99, drums: -1},
    };
    expect(getServerSongInstrumentDifficulty(song, 'Solo_Guitar')).toBe(4);
    expect(getServerSongInstrumentDifficulty(song, 'Solo_Bass')).toBeUndefined();
    expect(getServerSongInstrumentDifficulty(song, 'Solo_Drums')).toBeUndefined();
    expect(getServerSongInstrumentDifficulty(song, null)).toBeUndefined();
    expect(serverSongSupportsInstrument(song, 'Solo_Guitar')).toBe(true);
    expect(serverSongSupportsInstrument(song, 'Solo_Bass')).toBe(false);
  });

  test('expands compact player scores and valid-score variants', () => {
    const wire: Parameters<typeof expandWirePlayerResponse>[0] = {
      accountId: 'account',
      displayName: 'Player',
      totalScores: 2,
      status: 'syncing',
      notYetPublished: true,
      scores: [
        {
          si: 'song-a',
          ins: '02',
          sc: 123,
          acc: 987,
          fc: true,
          st: 6,
          dif: 5,
          sn: 3,
          pct: 0.01,
          rk: 10,
          lrk: 4,
          et: '2026-01-01T00:00:00Z',
          te: 100,
          lp: '2026-01-02T00:00:00Z',
          vlp: '2026-01-03T00:00:00Z',
          ml: 2,
          vs: [
            {sc: 120, acc: 900, fc: false, st: 5, ml: 3, rt: [{l: 1, r: 2}]},
            {sc: 110, acc: null, fc: null, st: null, ml: 4, rt: null},
          ],
          isValid: true,
          validScore: 120,
          validAccuracy: 900,
          validIsFullCombo: false,
          validStars: 5,
          validRank: 12,
          validTotalEntries: 100,
        },
        {
          si: 'song-b',
          ins: '00',
          sc: 50,
          acc: 500,
          fc: false,
          st: 2,
          dif: 1,
          sn: 1,
          pct: 0.5,
          rk: 50,
          lrk: 0,
          te: 100,
          validAccuracy: null,
        },
      ],
    };

    const expanded = expandWirePlayerResponse(wire);
    expect(expanded.scores[0]).toMatchObject({
      instrument: 'Solo_Bass',
      accuracy: 987_000,
      localRank: 4,
      validAccuracy: 900_000,
    });
    expect(expanded.scores[0].validScores?.[0]).toEqual({
      score: 120,
      accuracy: 900_000,
      fc: false,
      stars: 5,
      minLeeway: 3,
      rankTiers: [{leeway: 1, rank: 2}],
    });
    expect(expanded.scores[0].validScores?.[1].accuracy).toBeNull();
    expect(expanded.scores[1].instrument).toBe('Solo_Guitar');
    expect(expanded.scores[1].localRank).toBeUndefined();
    expect(expanded.scores[1].validScores).toBeUndefined();
  });

  test('expands compact song population tiers', () => {
    const wire: Parameters<typeof expandWireSongsResponse>[0] = {
      count: 2,
      songs: [
        {songId: 'plain', title: 'Plain', artist: 'Artist'},
        {
          songId: 'tiered',
          title: 'Tiered',
          artist: 'Artist',
          populationTiers: {
            Solo_Guitar: {bc: 10, t: [{l: 1, t: 20}]},
            Solo_Bass: null,
          },
        },
      ],
    };

    const expanded = expandWireSongsResponse(wire);
    expect(expanded.songs[0].populationTiers).toBeUndefined();
    expect(expanded.songs[1].populationTiers?.Solo_Guitar).toEqual({
      baseCount: 10,
      tiers: [{leeway: 1, total: 20}],
    });
    expect(expanded.songs[1].populationTiers?.Solo_Bass).toBeUndefined();
  });

  test('expands compact stats, inheritance, and malformed grouped songs', () => {
    const firstTier = {
      ml: 1,
      sp: 2,
      otc: 3,
      fcc: 4,
      fcp: 5,
      s6: 6,
      s5: 7,
      s4: 8,
      s3: 9,
      s2: 10,
      s1: 11,
      aa: 987,
      ba: 999,
      ast: 5.5,
      as: 123,
      tsc: 456,
      cp: 78,
      br: 9,
      brs: 'best',
      pd: [4, 3],
      ap: 0,
      op: 99,
      ts: JSON.stringify([{p: 1, s: ['top-a', 'top-b']}]),
      bs: 'already-expanded',
      bri: '02',
    };
    const inheritedTier = {
      ...firstTier,
      pd: [],
      ap: null,
      op: undefined,
      ts: null,
      bs: null,
      bri: null,
    };
    const wire: Parameters<typeof expandWireStatsResponse>[0] = {
      accountId: 'account',
      totalSongs: 12,
      instruments: [
        {ins: '00', tiers: [firstTier, inheritedTier]},
        {ins: '04', tiers: [firstTier]},
        {ins: '01', tiers: [{...inheritedTier, ts: '', bs: ''}]},
      ],
      instrumentRanks: [{
        ins: 'Solo_Guitar',
        totalRanked: 10,
        base: {adjusted: 1, weighted: 2, fcRate: 3, totalScore: 4, maxScore: 5},
        tiers: [],
      }],
    };

    const expanded = expandWireStatsResponse(wire);
    expect(expanded.instruments[0].instrument).toBe('Overall');
    expect(expanded.instruments[1].instrument).toBe('Solo_Drums');
    expect(expanded.instruments[0].tiers[0]).toMatchObject({
      avgAccuracy: 987_000,
      bestAccuracy: 999_000,
      percentileDist: JSON.stringify({'1': 4, '2': 3}),
      avgPercentile: 'Top 1%',
      overallPercentile: 'Top 100%',
      bestRankInstrument: 'Solo_Bass',
    });
    expect(expanded.instruments[0].tiers[0].topSongs)
      .toBe(JSON.stringify([
        {songId: 'top-a', percentile: 1},
        {songId: 'top-b', percentile: 1},
      ]));
    expect(expanded.instruments[0].tiers[0].bottomSongs).toBe('already-expanded');
    expect(expanded.instruments[0].tiers[1].topSongs)
      .toBe(expanded.instruments[0].tiers[0].topSongs);
    expect(expanded.instruments[0].tiers[1].bottomSongs).toBe('already-expanded');
    expect(expanded.instruments[0].tiers[1].percentileDist).toBeNull();
    expect(expanded.instruments[2].tiers[0].topSongs).toBeNull();
    expect(expanded.instruments[2].tiers[0].bottomSongs).toBeNull();
    expect(expanded.instrumentRanks?.[0].tiers).toEqual([]);
    expect(expanded.compositeRanks).toBeNull();
    expect(expanded.familyRanks).toBeNull();
    expect(expanded.bands).toBeNull();

    const empty = expandWireStatsResponse({
      accountId: 'empty',
      totalSongs: 0,
      instruments: [],
    });
    expect(empty.instrumentRanks).toBeNull();

    const incompleteWire = {
      accountId: 'incomplete',
      totalSongs: undefined,
      instruments: undefined,
    } as unknown as Parameters<typeof expandWireStatsResponse>[0];
    expect(expandWireStatsResponse(incompleteWire)).toMatchObject({
      totalSongs: 0,
      instruments: [],
    });
  });
});
