import { useEffect, useMemo, useRef } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import type { QueryClient } from '@tanstack/react-query';
import type { AccountNameRefreshResponse } from '@festival/core/api/serverTypes';
import { api } from '../../api/client';
import { queryKeys } from '../../api/queryKeys';
import {
  readSelectedProfile,
  writeSelectedProfile,
  type SelectedBandMemberProfile,
  type SelectedProfile,
} from '../../state/selectedProfile';

const REFRESH_DEBOUNCE_MS = 200;

export function getSelectedProfileRefreshAccountIds(profile: SelectedProfile | null): string[] {
  if (!profile) return [];
  const ids = profile.type === 'player'
    ? [profile.accountId]
    : profile.members.map(member => member.accountId);
  return Array.from(new Set(ids.map(id => id.trim()).filter(Boolean)));
}

export function getSelectedProfileRefreshKey(profile: SelectedProfile | null): string {
  if (!profile) return '';
  const ids = getSelectedProfileRefreshAccountIds(profile).sort((a, b) => a.localeCompare(b));
  if (profile.type === 'player') return `player:${ids.join(',')}`;
  return `band:${profile.bandId}:${profile.bandType}:${profile.teamKey}:${ids.join(',')}`;
}

export function useSelectedProfileNameRefresh(profile: SelectedProfile | null): void {
  const queryClient = useQueryClient();
  const refreshKey = useMemo(() => getSelectedProfileRefreshKey(profile), [profile]);
  const requestSeqRef = useRef(0);
  const profileRef = useRef<SelectedProfile | null>(profile);

  useEffect(() => {
    profileRef.current = profile;
  }, [profile]);

  useEffect(() => {
    const requestSeq = ++requestSeqRef.current;
    const requestProfile = profileRef.current;
    if (!requestProfile || !refreshKey) return;

    const accountIds = getSelectedProfileRefreshAccountIds(requestProfile);
    if (accountIds.length === 0) return;

    const timer = window.setTimeout(() => {
      void api.refreshAccountNames(accountIds)
        .then(response => {
          if (requestSeq !== requestSeqRef.current || response.changed <= 0) return;
          applyAccountNameRefreshResult(queryClient, requestProfile, refreshKey, response);
        })
        .catch(() => {
          // Silent best-effort refresh: keep the currently displayed names.
        });
    }, REFRESH_DEBOUNCE_MS);

    return () => {
      window.clearTimeout(timer);
    };
  }, [queryClient, refreshKey]);
}

export function applyAccountNameRefreshResult(
  queryClient: QueryClient,
  profile: SelectedProfile,
  requestKey: string,
  response: AccountNameRefreshResponse,
): boolean {
  if (response.changed <= 0) return false;
  const current = readSelectedProfile();
  if (!current || getSelectedProfileRefreshKey(current) !== requestKey) return false;

  const selectedProfileChanged = patchSelectedProfileNames(response.names, requestKey);
  patchAccountNameQueryData(queryClient, response.names);
  invalidateAccountNameQueries(queryClient, response.changedAccountIds, profile);
  return selectedProfileChanged;
}

export function patchSelectedProfileNames(names: Record<string, string>, requestKey: string): boolean {
  const current = readSelectedProfile();
  if (!current || getSelectedProfileRefreshKey(current) !== requestKey) return false;

  if (current.type === 'player') {
    const displayName = findName(names, current.accountId);
    if (!displayName || current.displayName === displayName) return false;
    writeSelectedProfile({ ...current, displayName });
    return true;
  }

  const previousDerivedName = formatSelectedBandMembers(current.members);
  let changed = false;
  const members = current.members.map(member => {
    const displayName = findName(names, member.accountId);
    if (!displayName || member.displayName === displayName) return member;
    changed = true;
    return { ...member, displayName };
  });

  if (!changed) return false;

  const nextDisplayName = current.displayName === previousDerivedName
    ? formatSelectedBandMembers(members)
    : current.displayName;
  writeSelectedProfile({ ...current, displayName: nextDisplayName, members });
  return true;
}

export function patchAccountNameQueryData(queryClient: QueryClient, names: Record<string, string>): void {
  if (Object.keys(names).length === 0) return;
  for (const query of queryClient.getQueryCache().findAll()) {
    queryClient.setQueryData(query.queryKey, oldData => patchDisplayNamesInValue(oldData, names));
  }
}

export function patchDisplayNamesInValue<T>(value: T, names: Record<string, string>): T {
  const namesByAccountId = new Map(Object.entries(names).map(([accountId, displayName]) => [accountId.toLowerCase(), displayName]));

  function patch(current: unknown): { value: unknown; changed: boolean } {
    if (Array.isArray(current)) {
      let changed = false;
      const next = current.map(item => {
        const patched = patch(item);
        changed ||= patched.changed;
        return patched.value;
      });
      return changed ? { value: next, changed } : { value: current, changed: false };
    }

    if (!current || typeof current !== 'object') return { value: current, changed: false };

    const record = current as Record<string, unknown>;
    let next: Record<string, unknown> | null = null;
    let changed = false;
    const accountId = typeof record.accountId === 'string' ? record.accountId : null;
    const displayName = accountId ? namesByAccountId.get(accountId.toLowerCase()) : undefined;

    if (displayName && record.displayName !== displayName) {
      next = { ...record, displayName };
      changed = true;
    }

    for (const [key, item] of Object.entries(record)) {
      const patched = patch(item);
      if (!patched.changed) continue;
      next ??= { ...record };
      next[key] = patched.value;
      changed = true;
    }

    return changed ? { value: next, changed: true } : { value: current, changed: false };
  }

  return patch(value).value as T;
}

function invalidateAccountNameQueries(queryClient: QueryClient, accountIds: readonly string[], profile: SelectedProfile): void {
  for (const accountId of accountIds) {
    void queryClient.invalidateQueries({ queryKey: ['player', accountId] });
    void queryClient.invalidateQueries({ queryKey: ['playerStats', accountId] });
    void queryClient.invalidateQueries({ queryKey: ['playerHistory', accountId] });
    void queryClient.invalidateQueries({ queryKey: ['playerRanking'] });
    void queryClient.invalidateQueries({ queryKey: ['playerCompositeRanking', accountId] });
    void queryClient.invalidateQueries({ queryKey: ['playerSoloFamilyRanking', accountId] });
    void queryClient.invalidateQueries({ queryKey: ['playerComboRanking', accountId] });
    void queryClient.invalidateQueries({ queryKey: ['leaderboardNeighborhood'] });
    void queryClient.invalidateQueries({ queryKey: ['compositeNeighborhood', accountId] });
    void queryClient.invalidateQueries({ queryKey: ['rivalsOverview', accountId] });
    void queryClient.invalidateQueries({ queryKey: ['rivalsList', accountId] });
    void queryClient.invalidateQueries({ queryKey: ['rivalDetail', accountId] });
    void queryClient.invalidateQueries({ queryKey: ['playerBandsList', accountId] });
  }

  void queryClient.invalidateQueries({ queryKey: ['selectedMemberRankings'] });
  void queryClient.invalidateQueries({ queryKey: ['selectedMemberSongScores'] });
  void queryClient.invalidateQueries({ queryKey: ['memberScoreFilter'] });

  if (profile.type === 'band') {
    void queryClient.invalidateQueries({ queryKey: queryKeys.bandDetail(profile.bandId) });
    void queryClient.invalidateQueries({ queryKey: ['bandLookup'] });
    void queryClient.invalidateQueries({ queryKey: ['bandRanking', profile.bandType, profile.teamKey] });
    void queryClient.invalidateQueries({ queryKey: ['bandRankings'] });
    void queryClient.invalidateQueries({ queryKey: ['bandSongs', profile.bandType, profile.teamKey] });
    void queryClient.invalidateQueries({ queryKey: ['bandSongRows', profile.bandType, profile.teamKey] });
    void queryClient.invalidateQueries({ queryKey: ['songBandLeaderboard'] });
    void queryClient.invalidateQueries({ queryKey: ['allSongBandLeaderboards'] });
  }
}

function findName(names: Record<string, string>, accountId: string): string | undefined {
  const direct = names[accountId];
  if (direct) return direct;
  const match = Object.entries(names).find(([key]) => key.toLowerCase() === accountId.toLowerCase());
  return match?.[1];
}

function formatSelectedBandMembers(members: readonly SelectedBandMemberProfile[]): string {
  return members.map(member => member.displayName.trim()).filter(Boolean).join(' + ');
}