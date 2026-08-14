import { useSyncExternalStore } from 'react';
import { useMediaQuery } from './useMediaQuery';

type SaveDataConnection = EventTarget & {
  readonly saveData?: boolean;
};

type NavigatorWithConnection = Navigator & {
  readonly connection?: SaveDataConnection;
};

function getConnection(): SaveDataConnection | undefined {
  return (navigator as NavigatorWithConnection).connection;
}

function subscribeSaveData(callback: () => void): () => void {
  const connection = getConnection();
  connection?.addEventListener('change', callback);
  return () => connection?.removeEventListener('change', callback);
}

function readSaveData(): boolean {
  return getConnection()?.saveData === true;
}

function subscribeVisibility(callback: () => void): () => void {
  document.addEventListener('visibilitychange', callback);
  return () => document.removeEventListener('visibilitychange', callback);
}

export function useVisualPreferences(): {
  reducedMotion: boolean;
  saveData: boolean;
  isDocumentVisible: boolean;
} {
  const reducedMotion = useMediaQuery('(prefers-reduced-motion: reduce)');
  const saveData = useSyncExternalStore(subscribeSaveData, readSaveData, () => false);
  const isDocumentVisible = useSyncExternalStore(
    subscribeVisibility,
    () => !document.hidden,
    () => true,
  );
  return { reducedMotion, saveData, isDocumentVisible };
}
