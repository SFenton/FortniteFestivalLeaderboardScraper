import React, {createContext, useCallback, useContext, useEffect, useMemo, useRef, useState} from 'react';

import type {Settings} from '../../core/settings';
import {defaultSettings} from '../../core/settings';
import type {LeaderboardData, Song} from '../../core/models';
import {FestivalService} from '../../core/services/festivalService';

import {BatchedLogBuffer} from '../process/logBuffer';
import {computeProgressState} from '../process/progress';
import {createFestivalPersistence} from '../../platform/festivalPersistence';
import {createFetchHttpClient} from '../../platform/httpClient';
import {createNativeImageCache} from '../../platform/imageCache';

type FestivalState = {
  // Boot-time gate so screens can avoid flashing empty UI before initialization.
  isReady: boolean;
  isInitializing: boolean;
  isFetching: boolean;
  songs: Song[];
  scoresIndex: Readonly<Record<string, LeaderboardData>>;
  progressPct: number;
  progressLabel: string;
  metrics: string;
  logJoined: string;
  exchangeCode: string;
  settings: Settings;
};

type FestivalActions = {
  setExchangeCode: (v: string) => void;
  setSettings: (s: Settings) => void;
  ensureInitializedAsync: (opts?: {force?: boolean}) => Promise<void>;
  startFetchAsync: (opts?: {filteredSongIds?: string[]}) => Promise<boolean>;
  clearLog: () => void;
  logUi: (line: string) => void;
  clearImageCache: () => Promise<void>;
  deleteAllScores: () => Promise<void>;
  clearEverything: () => Promise<void>;
};

type FestivalContextValue = {
  service: FestivalService;
  state: FestivalState;
  actions: FestivalActions;
};

const FestivalContext = createContext<FestivalContextValue | null>(null);

const formatMetrics = (inst: {requests: number; improved: number; empty: number; errors: number; bytes: number; elapsedSec: number}) =>
  `Req:${inst.requests} Improved:${inst.improved} Empty:${inst.empty} Errors:${inst.errors} Bytes:${inst.bytes} Elapsed:${inst.elapsedSec.toFixed(1)}s`;

const SETTINGS_STORAGE_KEY = 'fnfestival:settings';

async function loadSettingsFromStorage(): Promise<Settings> {
  if (process.env.JEST_WORKER_ID) return defaultSettings();
  try {
    // eslint-disable-next-line @typescript-eslint/no-var-requires
    const AsyncStorageModule = require('@react-native-async-storage/async-storage') as {
      default?: unknown;
      getItem?: (k: string) => Promise<string | null>;
    };
    const AsyncStorage = ((AsyncStorageModule as any).default ?? AsyncStorageModule) as {
      getItem: (k: string) => Promise<string | null>;
    };

    const raw = await AsyncStorage.getItem(SETTINGS_STORAGE_KEY);
    if (!raw) return defaultSettings();
    const parsed = JSON.parse(raw) as Partial<Settings>;

    // Drop deprecated keys so they don't get re-saved forever via object spread.
    // eslint-disable-next-line @typescript-eslint/no-unused-vars
    const {songsHideTrackMetadata: _deprecatedSongsHideTrackMetadata, degreeOfParallelism: _deprecatedDop, ...rest} = parsed as any;

    return {...defaultSettings(), ...rest};
  } catch {
    return defaultSettings();
  }
}

async function saveSettingsToStorage(settings: Settings): Promise<void> {
  if (process.env.JEST_WORKER_ID) return;
  try {
    // eslint-disable-next-line @typescript-eslint/no-var-requires
    const AsyncStorageModule = require('@react-native-async-storage/async-storage') as {
      default?: unknown;
      setItem?: (k: string, v: string) => Promise<void>;
    };
    const AsyncStorage = ((AsyncStorageModule as any).default ?? AsyncStorageModule) as {
      setItem: (k: string, v: string) => Promise<void>;
    };
    await AsyncStorage.setItem(SETTINGS_STORAGE_KEY, JSON.stringify(settings));
  } catch {
    // ignore
  }
}

export function FestivalProvider(props: {children: React.ReactNode}) {
  console.log('[FestivalProvider] Rendering FestivalProvider');
  const logBufferRef = useRef(new BatchedLogBuffer());
  const logCounterRef = useRef(0);
  const initializedRef = useRef(false);
  const initializingRef = useRef(false);

  const [exchangeCode, setExchangeCode] = useState('');
  const [settings, setSettings] = useState<Settings>(defaultSettings());
  const settingsLoadedRef = useRef(false);
  const [settingsReady, setSettingsReady] = useState(false);

  const [isReady, setIsReady] = useState(false);

  const [isInitializing, setIsInitializing] = useState(false);
  const [isFetching, setIsFetching] = useState(false);
  const [songs, setSongs] = useState<Song[]>([]);
  const [scoresIndex, setScoresIndex] = useState<Readonly<Record<string, LeaderboardData>>>({});
  const [progressPct, setProgressPct] = useState(0);
  const [progressLabel, setProgressLabel] = useState('0%');
  const [metrics, setMetrics] = useState('');
  const [logJoined, setLogJoined] = useState('');

  // Load persisted settings once.
  useEffect(() => {
    if (settingsLoadedRef.current) return;
    settingsLoadedRef.current = true;
    void (async () => {
      const loaded = await loadSettingsFromStorage();
      setSettings(loaded);
      setSettingsReady(true);
    })();
  }, []);

  const service = useMemo(() => {
    console.log('[FestivalProvider] Creating service...');
    const persistence = createFestivalPersistence();
    const http = createFetchHttpClient();
    const imageCache = createNativeImageCache();

    const svc = new FestivalService({
      http,
      persistence,
      imageCache,
      events: {
        log: line => {
          logBufferRef.current.enqueue(line);
        },
        songProgress: (current, total, title, started) => {
          const next = computeProgressState({current, total, started, logCounter: logCounterRef.current});
          setProgressPct(next.progressPct);
          setProgressLabel(next.progressLabel);
          logCounterRef.current = next.nextLogCounter;

          if (next.shouldLog) {
            logBufferRef.current.enqueue(started ? `Started: ${title}` : `Finished: ${title}`);
          }
        },
        scoreUpdated: board => {
          // Keep a fresh snapshot for simple UIs.
          setScoresIndex(cur => ({...cur, [board.songId]: board}));
        },
      },
    });

    return svc;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Throttled log flushing (keeps UI snappy).
  useEffect(() => {
    const timer = setInterval(() => {
      const flushed = logBufferRef.current.flush('\n');
      setLogJoined(flushed.joined);
    }, 250);
    return () => clearInterval(timer);
  }, []);

  const clearLog = useCallback(() => {
    logBufferRef.current = new BatchedLogBuffer();
    setLogJoined('');
  }, []);

  const logUi = useCallback((line: string) => {
    if (process.env.JEST_WORKER_ID) return;
    const ts = new Date().toISOString().slice(11, 19);
    logBufferRef.current.enqueue(`${ts} ${line}`);
  }, []);

  const clearImageCache = useCallback(async () => {
    if (process.env.JEST_WORKER_ID) return;
    await service.clearImageCache();
    setSongs([...service.songs]); // Trigger re-render to reflect cleared imagePath
    logBufferRef.current.enqueue('Image cache cleared. Re-sync to download images again.');
  }, [service]);

  const deleteAllScores = useCallback(async () => {
    if (process.env.JEST_WORKER_ID) return;
    await service.deleteAllScores();
    setScoresIndex({});
    setSettings(cur => {
      const next = {...cur, hasEverSyncedScores: false};
      void saveSettingsToStorage(next);
      return next;
    });
    logBufferRef.current.enqueue('All local scores deleted.');
  }, [service]);

  const ensureInitializedAsync = useCallback(
    async (opts?: {force?: boolean}) => {
      if (process.env.JEST_WORKER_ID) return;
      if (!opts?.force && initializedRef.current) return;
      if (initializingRef.current) return;

      initializingRef.current = true;
      setIsInitializing(true);
      if (opts?.force) {
        setIsReady(false);
      }
      try {
        // Coarse-grained progress for boot UI.
        setProgressPct(5);
        setProgressLabel('Starting…');
        logBufferRef.current.enqueue('Initializing service (syncing songs + images)...');

        // Periodically push the service's in-memory songs to React state so
        // the UI (e.g. SlidingRowsBackground) sees imagePaths as they arrive
        // during the image-sync phase of initialize().
        console.log('[FestivalProvider] Starting image refresh timer');
        const imageRefreshTimer = setInterval(() => {
          const snap = [...service.songs];
          const withImages = snap.filter(s => !!s.imagePath).length;
          console.log(`[FestivalProvider] Refresh tick: ${snap.length} songs, ${withImages} with imagePath`);
          setSongs(snap);
        }, 2000);

        try {
          await service.initialize();
        } finally {
          clearInterval(imageRefreshTimer);
        }

        // Final snapshot with all data populated.
        initializedRef.current = true;
        const finalSongs = [...service.songs];
        const finalWithImages = finalSongs.filter(s => !!s.imagePath).length;
        console.log(`[FestivalProvider] Init complete: ${finalSongs.length} songs, ${finalWithImages} with imagePath`);
        setSongs(finalSongs);
        setScoresIndex({...service.scoresIndex});
        setMetrics(formatMetrics(service.getInstrumentation()));
        logBufferRef.current.enqueue(
          `Song sync complete. ${Object.keys(service.scoresIndex).length} cached scores; ${service.songs.length} songs loaded.`,
        );

        // Mark “ever synced” flags based on reality after init.
        setSettings(cur => {
          const hasAnyCachedScores = Object.keys(service.scoresIndex ?? {}).length > 0;
          const next: Settings = {
            ...cur,
            hasEverSyncedSongs: true,
            hasEverSyncedScores: cur.hasEverSyncedScores || hasAnyCachedScores,
          };
          void saveSettingsToStorage(next);
          return next;
        });

        setProgressPct(100);
        setProgressLabel('Complete');
      } finally {
        setIsInitializing(false);
        initializingRef.current = false;
        setIsReady(true);
      }
    },
    [service],
  );

  const clearEverything = useCallback(async () => {
    if (process.env.JEST_WORKER_ID) return;
    // 1. Delete all scores
    await service.deleteAllScores();
    setScoresIndex({});
    // 2. Clear image cache
    await service.clearImageCache();
    setSongs([...service.songs]);
    // 3. Reset settings to defaults
    const freshSettings = defaultSettings();
    setSettings(freshSettings);
    void saveSettingsToStorage(freshSettings);
    // 4. Clear exchange code
    setExchangeCode('');
    logBufferRef.current.enqueue('All app data cleared. Re-syncing...');
    // 5. Re-kick initialization (this sets isReady=false, then back to true after sync)
    initializedRef.current = false;
    await ensureInitializedAsync({force: true});
  }, [service, ensureInitializedAsync]);

  // Without a dedicated Sync screen, initialize on app start so the Songs tab
  // can show data immediately.
  useEffect(() => {
    if (!settingsReady) return;
    void ensureInitializedAsync();
  }, [ensureInitializedAsync, settingsReady]);

  const startFetchAsync = useCallback(
    async (opts?: {filteredSongIds?: string[]}) => {
      if (process.env.JEST_WORKER_ID) return false;
      if (isFetching) return false;
      if (!exchangeCode.trim()) return false;

      setIsFetching(true);
      try {
        setProgressPct(0);
        setProgressLabel('0%');
        logBufferRef.current.enqueue('Starting score fetch...');

        const ok = await service.fetchScores({
          exchangeCode: exchangeCode.trim(),
          degreeOfParallelism: 16,
          settings,
          filteredSongIds: opts?.filteredSongIds,
        });

        // If we got any scores at all, treat that as “synced” even if the fetch
        // returned ok=false due to partial failures.
        const hasAnyScores = Object.keys(service.scoresIndex ?? {}).length > 0;
        if (ok || hasAnyScores) {
          setSettings(cur => {
            if (cur.hasEverSyncedScores) return cur;
            const next = {...cur, hasEverSyncedScores: true};
            void saveSettingsToStorage(next);
            return next;
          });
        }

        setSongs(service.songs);
        setScoresIndex({...service.scoresIndex});
        setMetrics(formatMetrics(service.getInstrumentation()));
        logBufferRef.current.enqueue(ok ? 'Score fetch complete.' : 'Score fetch failed.');
        return ok;
      } finally {
        setIsFetching(false);
      }
    },
    [exchangeCode, isFetching, service, settings],
  );

  const setSettingsPersisted = useCallback((s: Settings) => {
    setSettings(s);
    void saveSettingsToStorage(s);
  }, []);

  const actions = useMemo(
    () => ({
      setExchangeCode,
      setSettings: setSettingsPersisted,
      ensureInitializedAsync,
      startFetchAsync,
      clearLog,
      logUi,
      clearImageCache,
      deleteAllScores,
      clearEverything,
    }),
    [setSettingsPersisted, ensureInitializedAsync, startFetchAsync, clearLog, logUi, clearImageCache, deleteAllScores, clearEverything],
  );

  const state = useMemo(
    () => ({
      isReady,
      isInitializing,
      isFetching,
      songs,
      scoresIndex,
      progressPct,
      progressLabel,
      metrics,
      logJoined,
      exchangeCode,
      settings,
    }),
    [isReady, isInitializing, isFetching, songs, scoresIndex, progressPct, progressLabel, metrics, logJoined, exchangeCode, settings],
  );

  const value: FestivalContextValue = useMemo(
    () => ({
      service,
      state,
      actions,
    }),
    [service, state, actions],
  );

  return <FestivalContext.Provider value={value}>{props.children}</FestivalContext.Provider>;
}

export function useFestival(): FestivalContextValue {
  const ctx = useContext(FestivalContext);
  if (!ctx) throw new Error('useFestival must be used within FestivalProvider');
  return ctx;
}
