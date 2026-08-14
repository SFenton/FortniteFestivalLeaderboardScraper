import type { ReactNode } from 'react';
import { Navigate } from 'react-router-dom';
import { Routes } from '../../routes';

export function RedirectToSongs() {
  return <Navigate to={Routes.songs} replace />;
}

export function RequirePlayer({
  hasPlayer,
  children,
}: {
  hasPlayer: boolean;
  children: ReactNode;
}) {
  return hasPlayer ? children : <RedirectToSongs />;
}

export function RequireSelection({
  hasSelection,
  children,
}: {
  hasSelection: boolean;
  children: ReactNode;
}) {
  return hasSelection ? children : <RedirectToSongs />;
}
