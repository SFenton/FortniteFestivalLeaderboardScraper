const RELOAD_MARKER_KEY = 'fst:stale-chunk-reload-at';
const RELOAD_GUARD_MS = 60_000;

type StaleChunkRecoveryDependencies = {
  storage: Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>;
  reload: () => void;
  now: () => number;
};

export function recoverFromStaleChunk(
  event: Event,
  dependencies: StaleChunkRecoveryDependencies,
): boolean {
  const now = dependencies.now();
  const marker = dependencies.storage.getItem(RELOAD_MARKER_KEY);
  if (marker !== null) {
    const previousReloadAt = Number(marker);
    if (Number.isFinite(previousReloadAt) && now - previousReloadAt < RELOAD_GUARD_MS) {
      return false;
    }
  }

  event.preventDefault();
  dependencies.storage.setItem(RELOAD_MARKER_KEY, String(now));
  dependencies.reload();
  return true;
}

export function installStaleChunkRecovery(windowRef: Window = window): () => void {
  const dependencies: StaleChunkRecoveryDependencies = {
    storage: windowRef.sessionStorage,
    reload: () => windowRef.location.reload(),
    now: Date.now,
  };
  const handlePreloadError = (event: Event) => {
    recoverFromStaleChunk(event, dependencies);
  };

  windowRef.addEventListener('vite:preloadError', handlePreloadError);
  const clearMarkerTimer = windowRef.setTimeout(
    () => dependencies.storage.removeItem(RELOAD_MARKER_KEY),
    RELOAD_GUARD_MS,
  );

  return () => {
    windowRef.removeEventListener('vite:preloadError', handlePreloadError);
    windowRef.clearTimeout(clearMarkerTimer);
  };
}
