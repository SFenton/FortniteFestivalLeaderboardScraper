import React, {createContext, useCallback, useContext, useEffect, useMemo, useRef, useState} from 'react';
import {Platform} from 'react-native';

import type {
  BackfillStatusResult,
  LeaderboardData,
  ScoreHistoryEntry,
  Song,
} from '@festival/core';
import {FstServiceClient} from '@festival/core';

import {useAuth} from './AuthContext';
import {useFestival} from './FestivalContext';

// ── Types ───────────────────────────────────────────────────────────

export type SyncStatus =
  | 'idle'              // not connected to service
  | 'connecting'        // connecting WebSocket
  | 'waiting'           // waiting for backfill to complete
  | 'downloading'       // downloading personal DB
  | 'loading'           // loading downloaded data into app
  | 'ready'             // data loaded and displayed
  | 'error';            // something went wrong

type ServiceSyncState = {
  status: SyncStatus;
  error: string | null;
  backfillStatus: BackfillStatusResult | null;
  /** Whether we have ever successfully loaded data from the service. */
  hasData: boolean;
};

type ServiceSyncActions = {
  /** Manually trigger a re-download of the personal DB. */
  refreshData: () => Promise<void>;
  /** Check the current backfill status on the server. */
  checkBackfillStatus: () => Promise<void>;
};

type ServiceSyncContextValue = {
  sync: ServiceSyncState;
  syncActions: ServiceSyncActions;
};

const ServiceSyncContext = createContext<ServiceSyncContextValue | null>(null);

// ── Notification message type ───────────────────────────────────────

type NotificationMessage = {
  type: 'personal_db_ready' | 'backfill_complete' | 'history_recon_complete';
};

// ── Provider ────────────────────────────────────────────────────────

export function ServiceSyncProvider({children}: {children: React.ReactNode}) {
  const {auth, authActions} = useAuth();
  const {service, actions: festivalActions} = useFestival();

  const [status, setStatus] = useState<SyncStatus>('idle');
  const [error, setError] = useState<string | null>(null);
  const [backfillStatus, setBackfillStatus] = useState<BackfillStatusResult | null>(null);
  const [hasData, setHasData] = useState(false);

  const wsRef = useRef<WebSocket | null>(null);
  const clientRef = useRef<FstServiceClient | null>(null);
  const reconnectTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const mountedRef = useRef(true);

  // Build FstServiceClient when authenticated
  useEffect(() => {
    if (auth.status === 'authenticated' && auth.session && auth.serviceEndpoint) {
      clientRef.current = new FstServiceClient(
        auth.serviceEndpoint,
        auth.session.accessToken,
      );
    } else {
      clientRef.current = null;
    }
  }, [auth.status, auth.session, auth.session?.accessToken, auth.serviceEndpoint]);

  // ── Download + load personal DB ───────────────────────────────

  const downloadAndLoadData = useCallback(async () => {
    const client = clientRef.current;
    if (!client) return;

    try {
      setStatus('downloading');
      setError(null);

      // Check if personal DB is available
      const versionInfo = await client.getSyncVersion();
      if (!versionInfo.available) {
        // DB not ready yet — server is still building it
        setStatus('waiting');
        return;
      }

      // Download the personal DB file and load it into the app.
      // Works on all platforms: RNFS for file I/O, openDatabase() for SQLite.

      if (Platform.OS === 'ios' || Platform.OS === 'android') {
        await downloadAndLoadSqlite(client);
      } else if (Platform.OS === 'windows') {
        await downloadAndLoadWindows(client);
      } else {
        console.log('[ServiceSync] Unsupported platform for DB sync');
        setStatus('waiting');
        return;
      }

      setStatus('ready');
      setHasData(true);
    } catch (err: any) {
      console.error('[ServiceSync] Download failed:', err);
      setError(err?.message ?? 'Failed to download data');
      setStatus('error');
    }
  }, []);

  const downloadAndLoadSqlite = async (client: FstServiceClient) => {
    setStatus('downloading');

    // Use RNFS to download the SQLite file directly to disk (iOS/Android)
    // eslint-disable-next-line @typescript-eslint/no-var-requires
    const RNFS = require('react-native-fs') as typeof import('react-native-fs');

    const dbDir = RNFS.DocumentDirectoryPath;
    const dbPath = `${dbDir}/fst-personal.sqlite`;

    const endpoint = clientRef.current
      ? (clientRef.current as any).baseUrl
      : auth.serviceEndpoint?.replace(/\/+$/, '');

    const downloadResult = await RNFS.downloadFile({
      fromUrl: `${endpoint}/api/me/sync`,
      toFile: dbPath,
      headers: {
        Authorization: `Bearer ${auth.session?.accessToken ?? ''}`,
      },
    }).promise;

    if (downloadResult.statusCode !== 200) {
      throw new Error(`Download failed with status ${downloadResult.statusCode}`);
    }

    console.log(`[ServiceSync] Downloaded personal DB: ${downloadResult.bytesWritten} bytes`);

    setStatus('loading');

    // Extract directory and filename for the openDatabase call
    const lastSlash = dbPath.lastIndexOf('/');
    const dir = dbPath.substring(0, lastSlash);
    const filename = dbPath.substring(lastSlash + 1);

    await loadDataFromDownloadedDb(filename, dir);
  };

  const downloadAndLoadWindows = async (_client: FstServiceClient) => {
    setStatus('downloading');

    const endpoint = clientRef.current
      ? (clientRef.current as any).baseUrl
      : auth.serviceEndpoint?.replace(/\/+$/, '');

    const headers = {
      Authorization: `Bearer ${auth.session?.accessToken ?? ''}`,
    };

    // Helper: fetch all pages for a given data type.
    const fetchAllPages = async (type: string): Promise<any[]> => {
      const allItems: any[] = [];
      let page = 0;
      let totalPages = 1;

      while (page < totalPages) {
        const res = await fetch(
          `${endpoint}/api/me/sync/json/${type}?page=${page}&pageSize=1000`,
          {headers},
        );
        if (!res.ok) {
          throw new Error(`Download ${type} page ${page} failed: ${res.status}`);
        }
        const data = await res.json();
        totalPages = data.totalPages ?? 1;
        allItems.push(...(data.items ?? []));
        console.log(
          `[ServiceSync] ${type} page ${page + 1}/${totalPages}: ${data.items?.length ?? 0} items`,
        );
        page++;
      }

      return allItems;
    };

    // Fetch each data type, paging through all results
    const songRows = await fetchAllPages('songs');
    const scoreRows = await fetchAllPages('scores');
    const historyRows = await fetchAllPages('history');

    console.log(
      `[ServiceSync] Downloaded JSON: ${songRows.length} songs, ${scoreRows.length} scores, ${historyRows.length} history`,
    );

    setStatus('loading');

    // Map the JSON rows using the same mappers as the SQLite path
    const songs: Song[] = songRows.map(mapDbRowToSong);
    const scores: LeaderboardData[] = scoreRows.map(mapDbRowToScore);
    const history: ScoreHistoryEntry[] = historyRows.map(mapDbRowToHistoryEntry);

    // Save data into the app's persistence layer
    const persistence = service.persistence;
    if (persistence) {
      if (songs.length > 0) await persistence.saveSongs(songs);
      if (scores.length > 0) await persistence.saveScores(scores);
      if (history.length > 0) await persistence.saveScoreHistory(history);
    }

    // Trigger FestivalProvider to reload from persistence
    await festivalActions.ensureInitializedAsync({force: true});
  };

  const loadDataFromDownloadedDb = async (name: string, location: string) => {
    try {
      // Open the downloaded personal DB via nitro-sqlite (iOS/Android only).
      // eslint-disable-next-line @typescript-eslint/no-var-requires
      const {openDatabase} = require('./adapters/sqlite/openDatabase') as typeof import('./adapters/sqlite/openDatabase');

      const personalDb = openDatabase({name, location});

      try {
        // Read songs from the downloaded DB
        const songsResult = await personalDb.executeSql(
          'SELECT SongId, Title, Artist, ActiveDate, LastModified, ImagePath, LeadDiff, BassDiff, VocalsDiff, DrumsDiff, ProLeadDiff, ProBassDiff, ReleaseYear, Tempo FROM Songs',
        );
        const songs: Song[] = [];
        for (let i = 0; i < songsResult.rows.length; i++) {
          songs.push(mapDbRowToSong(songsResult.rows.item(i)));
        }

        // Read scores
        const scoresResult = await personalDb.executeSql(
          `SELECT sc.SongId, s.Title, s.Artist,
            sc.GuitarScore, sc.GuitarDiff, sc.GuitarStars, sc.GuitarFC, sc.GuitarPct, sc.GuitarSeason, sc.GuitarRank, sc.GuitarGameDiff,
            sc.DrumsScore, sc.DrumsDiff, sc.DrumsStars, sc.DrumsFC, sc.DrumsPct, sc.DrumsSeason, sc.DrumsRank, sc.DrumsGameDiff,
            sc.BassScore, sc.BassDiff, sc.BassStars, sc.BassFC, sc.BassPct, sc.BassSeason, sc.BassRank, sc.BassGameDiff,
            sc.VocalsScore, sc.VocalsDiff, sc.VocalsStars, sc.VocalsFC, sc.VocalsPct, sc.VocalsSeason, sc.VocalsRank, sc.VocalsGameDiff,
            sc.ProGuitarScore, sc.ProGuitarDiff, sc.ProGuitarStars, sc.ProGuitarFC, sc.ProGuitarPct, sc.ProGuitarSeason, sc.ProGuitarRank, sc.ProGuitarGameDiff,
            sc.ProBassScore, sc.ProBassDiff, sc.ProBassStars, sc.ProBassFC, sc.ProBassPct, sc.ProBassSeason, sc.ProBassRank, sc.ProBassGameDiff,
            sc.GuitarTotal, sc.DrumsTotal, sc.BassTotal, sc.VocalsTotal, sc.ProGuitarTotal, sc.ProBassTotal,
            sc.GuitarRawPct, sc.DrumsRawPct, sc.BassRawPct, sc.VocalsRawPct, sc.ProGuitarRawPct, sc.ProBassRawPct,
            sc.GuitarCalcTotal, sc.DrumsCalcTotal, sc.BassCalcTotal, sc.VocalsCalcTotal, sc.ProGuitarCalcTotal, sc.ProBassCalcTotal
          FROM Scores sc LEFT JOIN Songs s ON s.SongId = sc.SongId`,
        );
        const scores: LeaderboardData[] = [];
        for (let i = 0; i < scoresResult.rows.length; i++) {
          scores.push(mapDbRowToScore(scoresResult.rows.item(i)));
        }

        // Read score history
        const historyResult = await personalDb.executeSql(
          'SELECT SongId, Instrument, OldScore, NewScore, OldRank, NewRank, Accuracy, IsFullCombo, Stars, Percentile, Season, ScoreAchievedAt, SeasonRank, AllTimeRank, ChangedAt FROM ScoreHistory ORDER BY ChangedAt ASC',
        );
        const history: ScoreHistoryEntry[] = [];
        for (let i = 0; i < historyResult.rows.length; i++) {
          history.push(mapDbRowToHistoryEntry(historyResult.rows.item(i)));
        }

        console.log(
          `[ServiceSync] Loaded from personal DB: ${songs.length} songs, ${scores.length} scores, ${history.length} history entries`,
        );

        // Save the data into the app's persistence layer
        const persistence = service.persistence;
        if (persistence) {
          if (songs.length > 0) await persistence.saveSongs(songs);
          if (scores.length > 0) await persistence.saveScores(scores);
          if (history.length > 0) await persistence.saveScoreHistory(history);
        }

        // Trigger FestivalProvider to reload from persistence
        await festivalActions.ensureInitializedAsync({force: true});
      } finally {
        // Close the personal DB connection
        try {
          personalDb.close?.();
        } catch {
          // ignore
        }
      }
    } catch (err) {
      console.error('[ServiceSync] Failed to load from downloaded DB:', err);
      throw err;
    }
  };

  // ── Check backfill status ─────────────────────────────────────

  const checkBackfillStatus = useCallback(async () => {
    const client = clientRef.current;
    if (!client) return;

    try {
      const result = await client.getBackfillStatus();
      if (mountedRef.current) {
        setBackfillStatus(result);
      }
    } catch (err) {
      console.warn('[ServiceSync] Failed to check backfill status:', err);
    }
  }, []);

  // ── WebSocket connection ──────────────────────────────────────

  const connectWebSocket = useCallback(async () => {
    if (auth.status !== 'authenticated' || !auth.session) return;

    // Don't reconnect if already connected
    if (wsRef.current && wsRef.current.readyState === WebSocket.OPEN) return;

    // Check if the access token is expired and refresh before connecting
    const expiresAt = new Date(auth.session.expiresAt);
    if (expiresAt <= new Date()) {
      console.log('[ServiceSync] Access token expired, refreshing before WS connect...');
      try {
        const newToken = await authActions.refreshAccessToken();
        // Update the service client with the fresh token
        if (clientRef.current) {
          clientRef.current.setAccessToken(newToken);
        }
      } catch (err) {
        console.error('[ServiceSync] Token refresh failed, cannot reconnect WS:', err);
        // Token refresh failed — the user may need to re-login.
        // Don't retry endlessly; let AuthContext handle the expired session.
        return;
      }
    }

    const client = clientRef.current;
    if (!client) return;

    const wsUrl = client.getWebSocketUrl();
    console.log('[ServiceSync] Connecting WebSocket...');
    setStatus('connecting');

    try {
      const ws = new WebSocket(wsUrl);

      ws.onopen = () => {
        console.log('[ServiceSync] WebSocket connected');
        if (mountedRef.current) {
          setStatus('waiting');
          // Check backfill status immediately on connect
          void checkBackfillStatus();
        }
      };

      ws.onmessage = (event: any) => {
        try {
          const msg = JSON.parse(
            typeof event.data === 'string' ? event.data : '',
          ) as NotificationMessage;

          console.log('[ServiceSync] Received notification:', msg.type);

          if (msg.type === 'personal_db_ready') {
            // Personal DB was rebuilt — download and load it
            void downloadAndLoadData();
          } else if (msg.type === 'backfill_complete') {
            void checkBackfillStatus();
          } else if (msg.type === 'history_recon_complete') {
            void checkBackfillStatus();
          }
        } catch (err) {
          console.warn('[ServiceSync] Failed to parse WS message:', err);
        }
      };

      ws.onerror = (event: any) => {
        console.warn('[ServiceSync] WebSocket error:', event?.message);
      };

      ws.onclose = () => {
        console.log('[ServiceSync] WebSocket closed');
        wsRef.current = null;

        // Reconnect after a delay (unless component is unmounted)
        if (mountedRef.current && auth.status === 'authenticated') {
          reconnectTimerRef.current = setTimeout(() => {
            if (mountedRef.current) void connectWebSocket();
          }, 5000);
        }
      };

      wsRef.current = ws;
    } catch (err) {
      console.error('[ServiceSync] WebSocket connection failed:', err);
      // Retry after delay
      reconnectTimerRef.current = setTimeout(() => {
        if (mountedRef.current) void connectWebSocket();
      }, 5000);
    }
  }, [auth.status, auth.session, authActions, checkBackfillStatus, downloadAndLoadData]);

  // ── Lifecycle: connect WebSocket when authenticated ───────────

  useEffect(() => {
    if (auth.status !== 'authenticated' || auth.mode !== 'service') {
      // Clean up if not authenticated
      if (wsRef.current) {
        wsRef.current.close();
        wsRef.current = null;
      }
      if (reconnectTimerRef.current) {
        clearTimeout(reconnectTimerRef.current);
        reconnectTimerRef.current = null;
      }
      return;
    }

    // Try to download existing personal DB immediately on mount
    void downloadAndLoadData();

    // Connect WebSocket for notifications
    void connectWebSocket();

    return () => {
      if (wsRef.current) {
        wsRef.current.close();
        wsRef.current = null;
      }
      if (reconnectTimerRef.current) {
        clearTimeout(reconnectTimerRef.current);
        reconnectTimerRef.current = null;
      }
    };
  }, [auth.status, auth.mode, connectWebSocket, downloadAndLoadData]);

  // Cleanup on unmount
  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
    };
  }, []);

  // ── Public actions ────────────────────────────────────────────

  const refreshData = useCallback(async () => {
    await downloadAndLoadData();
  }, [downloadAndLoadData]);

  // ── Context value ─────────────────────────────────────────────

  const state = useMemo<ServiceSyncState>(
    () => ({status, error, backfillStatus, hasData}),
    [status, error, backfillStatus, hasData],
  );

  const syncActions = useMemo<ServiceSyncActions>(
    () => ({refreshData, checkBackfillStatus}),
    [refreshData, checkBackfillStatus],
  );

  const value = useMemo<ServiceSyncContextValue>(
    () => ({sync: state, syncActions}),
    [state, syncActions],
  );

  return (
    <ServiceSyncContext.Provider value={value}>
      {children}
    </ServiceSyncContext.Provider>
  );
}

// ── Hook ────────────────────────────────────────────────────────────

export function useServiceSync(): ServiceSyncContextValue {
  const ctx = useContext(ServiceSyncContext);
  if (!ctx) {
    throw new Error('useServiceSync must be used within <ServiceSyncProvider>');
  }
  return ctx;
}

// ── DB row → model helpers ──────────────────────────────────────────

function safeInt(v: unknown): number {
  return typeof v === 'number' && Number.isFinite(v) ? Math.trunc(v) : 0;
}

function safeStr(v: unknown): string {
  return typeof v === 'string' ? v : '';
}

function mapDbRowToSong(r: any): Song {
  return {
    track: {
      su: safeStr(r.SongId),
      tt: r.Title ?? undefined,
      an: r.Artist ?? undefined,
      ry: safeInt(r.ReleaseYear),
      mt: safeInt(r.Tempo),
      in: {
        gr: safeInt(r.LeadDiff),
        ba: safeInt(r.BassDiff),
        vl: safeInt(r.VocalsDiff),
        ds: safeInt(r.DrumsDiff),
        pg: safeInt(r.ProLeadDiff),
        pb: safeInt(r.ProBassDiff),
      },
    },
    _activeDate: r.ActiveDate ?? undefined,
    lastModified: r.LastModified ?? undefined,
    imagePath: r.ImagePath ?? undefined,
  };
}

import {ScoreTracker} from '@festival/core';

function readTrackerFromRow(row: any, prefix: string): ScoreTracker | undefined {
  const score = row[`${prefix}Score`];
  if (score == null) return undefined;

  const tracker = new ScoreTracker();
  tracker.maxScore = safeInt(score);
  tracker.difficulty = safeInt(row[`${prefix}Diff`]);
  tracker.numStars = safeInt(row[`${prefix}Stars`]);
  tracker.isFullCombo = safeInt(row[`${prefix}FC`]) === 1;
  tracker.percentHit = safeInt(row[`${prefix}Pct`]);
  tracker.seasonAchieved = safeInt(row[`${prefix}Season`]);
  tracker.rank = safeInt(row[`${prefix}Rank`]);
  tracker.initialized = tracker.maxScore > 0;
  tracker.totalEntries = safeInt(row[`${prefix}Total`]);
  tracker.rawPercentile =
    typeof row[`${prefix}RawPct`] === 'number' ? row[`${prefix}RawPct`] : 0;
  tracker.calculatedNumEntries = safeInt(row[`${prefix}CalcTotal`]);

  const rawGameDiff = safeInt(row[`${prefix}GameDiff`]);
  tracker.gameDifficulty = (rawGameDiff >= 0 && rawGameDiff <= 3 ? rawGameDiff : -1) as any;
  tracker.refreshDerived();
  return tracker;
}

function mapDbRowToScore(row: any): LeaderboardData {
  const ld: LeaderboardData = {
    songId: safeStr(row.SongId),
    title: row.Title ?? undefined,
    artist: row.Artist ?? undefined,
  };

  ld.guitar = readTrackerFromRow(row, 'Guitar');
  ld.drums = readTrackerFromRow(row, 'Drums');
  ld.bass = readTrackerFromRow(row, 'Bass');
  ld.vocals = readTrackerFromRow(row, 'Vocals');
  ld.pro_guitar = readTrackerFromRow(row, 'ProGuitar');
  ld.pro_bass = readTrackerFromRow(row, 'ProBass');
  ld.dirty = false;

  return ld;
}

function mapDbRowToHistoryEntry(row: any): ScoreHistoryEntry {
  return {
    songId: safeStr(row.SongId),
    instrument: safeStr(row.Instrument),
    oldScore: row.OldScore ?? undefined,
    newScore: row.NewScore ?? undefined,
    oldRank: row.OldRank ?? undefined,
    newRank: row.NewRank ?? undefined,
    accuracy: row.Accuracy ?? undefined,
    isFullCombo: row.IsFullCombo != null ? row.IsFullCombo === 1 : undefined,
    stars: row.Stars ?? undefined,
    percentile: row.Percentile ?? undefined,
    season: row.Season ?? undefined,
    scoreAchievedAt: row.ScoreAchievedAt ?? undefined,
    seasonRank: row.SeasonRank ?? undefined,
    allTimeRank: row.AllTimeRank ?? undefined,
    changedAt: safeStr(row.ChangedAt),
  };
}
