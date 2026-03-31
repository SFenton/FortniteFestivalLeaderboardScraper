import { useCallback } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { Routes } from '../../routes';

/**
 * Returns a callback that navigates to the song detail page (`/songs/:songId`).
 *
 * If the previous page in the history stack is that same song detail page
 * (detected via `location.state.backTo`), uses `navigate(-1)` so the browser
 * performs a POP — preserving scroll position and page cache.
 * Otherwise pushes a new entry onto the history stack.
 */
export function useNavigateToSongDetail(songId: string | undefined): () => void {
  const location = useLocation();
  const navigate = useNavigate();
  const backTo = (location.state as { backTo?: string } | null)?.backTo;
  const target = songId ? Routes.songDetail(songId) : undefined;

  return useCallback(() => {
    if (!target) return;
    if (backTo === target) {
      navigate(-1);
    } else {
      navigate(target);
    }
  }, [backTo, target, navigate]);
}
