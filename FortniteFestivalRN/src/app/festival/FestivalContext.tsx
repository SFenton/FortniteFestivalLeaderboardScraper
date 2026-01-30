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
    const AsyncStorage = require('@react-native-async-storage/async-storage').default as {
      getItem: (k: string) => Promise<string | null>;
    };

    const raw = await AsyncStorage.getItem(SETTINGS_STORAGE_KEY);
    if (!raw) return defaultSettings();
    const parsed = JSON.parse(raw) as Partial<Settings>;
    return {...defaultSettings(), ...parsed};
  } catch {
    return defaultSettings();
  }
}

async function saveSettingsToStorage(settings: Settings): Promise<void> {
  if (process.env.JEST_WORKER_ID) return;
  try {
    // eslint-disable-next-line @typescript-eslint/no-var-requires
    const AsyncStorage = require('@react-native-async-storage/async-storage').default as {
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

  const ensureInitializedAsync = useCallback(
    async (opts?: {force?: boolean}) => {
      if (process.env.JEST_WORKER_ID) return;
      if (!opts?.force && initializedRef.current) return;
      if (initializingRef.current) return;

      initializingRef.current = true;
      setIsInitializing(true);
      try {
        logBufferRef.current.enqueue('Initializing service (syncing songs + images)...');
        await service.initialize();
        initializedRef.current = true;
        setSongs(service.songs);
        setScoresIndex({...service.scoresIndex});
        setMetrics(formatMetrics(service.getInstrumentation()));
        logBufferRef.current.enqueue(
          `Song sync complete. ${Object.keys(service.scoresIndex).length} cached scores; ${service.songs.length} songs loaded.`,
        );
      } finally {
        setIsInitializing(false);
        initializingRef.current = false;
      }
    },
    [service],
  );

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
          degreeOfParallelism: settings.degreeOfParallelism,
          settings,
          filteredSongIds: opts?.filteredSongIds,
        });

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
    }),
    [setSettingsPersisted, ensureInitializedAsync, startFetchAsync, clearLog, logUi, clearImageCache],
  );

  const state = useMemo(
    () => ({
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
    [isInitializing, isFetching, songs, scoresIndex, progressPct, progressLabel, metrics, logJoined, exchangeCode, settings],
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
