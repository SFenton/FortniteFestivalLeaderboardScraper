import {
  createContext,
  useContext,
  useState,
  useEffect,
  useCallback,
  type ReactNode,
} from 'react';
import type { Song } from '../models';
import { api } from '../api/client';

type FestivalState = {
  songs: Song[];
  isLoading: boolean;
  error: string | null;
};

type FestivalActions = {
  refresh: () => Promise<void>;
};

type FestivalContextValue = {
  state: FestivalState;
  actions: FestivalActions;
};

const FestivalContext = createContext<FestivalContextValue | null>(null);

export function FestivalProvider({ children }: { children: ReactNode }) {
  const [songs, setSongs] = useState<Song[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    setIsLoading(true);
    setError(null);
    try {
      const res = await api.getSongs();
      setSongs(res.songs);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load songs');
    } finally {
      setIsLoading(false);
    }
  }, []);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  return (
    <FestivalContext.Provider
      value={{
        state: { songs, isLoading, error },
        actions: { refresh },
      }}
    >
      {children}
    </FestivalContext.Provider>
  );
}

export function useFestival(): FestivalContextValue {
  const ctx = useContext(FestivalContext);
  if (!ctx) {
    throw new Error('useFestival must be used within a FestivalProvider');
  }
  return ctx;
}
