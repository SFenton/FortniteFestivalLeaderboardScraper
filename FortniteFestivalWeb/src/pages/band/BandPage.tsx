/* eslint-disable react/forbid-dom-props -- page-level dynamic styles use inline style objects */
import { useCallback, useEffect, useMemo, useRef, useState, type AnimationEvent, type CSSProperties, type ReactNode } from 'react';
import { Link, useLocation, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { IoChevronForward, IoPeople } from 'react-icons/io5';
import type { BandRankingDto, BandType, PlayerBandEntry, PlayerBandMember, ServerInstrumentKey } from '@festival/core/api/serverTypes';
import { ACCURACY_SCALE, LoadPhase } from '@festival/core';
import { Colors, Font, Gap, IconSize, Layout, Radius, TRANSITION_MS, Weight, flexColumn, flexRow, frostedCard, transition, transitions } from '@festival/theme';
import { api } from '../../api/client';
import { queryKeys } from '../../api/queryKeys';
import EmptyState from '../../components/common/EmptyState';
import PageHeader from '../../components/common/PageHeader';
import { InstrumentIcon } from '../../components/display/InstrumentIcons';
import { SelectProfilePill } from '../../components/player/SelectProfilePill';
import StatBox from '../../components/player/StatBox';
import { useBandRankHistory } from '../../hooks/chart/useBandRankHistory';
import { usePageTransition } from '../../hooks/ui/usePageTransition';
import { useIsMobile } from '../../hooks/ui/useIsMobile';
import { useStagger } from '../../hooks/ui/useStagger';
import { useSelectedProfile } from '../../hooks/data/useSelectedProfile';
import { Routes } from '../../routes';
import Page from '../Page';
import PlayerSectionHeading from '../player/sections/PlayerSectionHeading';
import BandRankHistoryChart from './components/BandRankHistoryChart';
import BandSongsSection, { useBandSongs } from './components/BandSongsSection';

const VALID_BAND_TYPES: BandType[] = ['Band_Duets', 'Band_Trios', 'Band_Quad'];
const SELECT_BAND_PROFILE_ACTION_SLOT_MAX_WIDTH = 360;
const SELECT_BAND_PROFILE_ACTION_SLOT_STYLE: CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  justifyContent: 'flex-end',
  overflow: 'hidden',
  flexShrink: 0,
  minWidth: 0,
  transition: transitions(
    transition('max-width', TRANSITION_MS),
    transition('opacity', TRANSITION_MS),
  ),
};

export default function BandPage() {
  const { t } = useTranslation();
  const { bandId } = useParams<{ bandId?: string }>();
  const [searchParams] = useSearchParams();
  const location = useLocation();
  const navigate = useNavigate();
  const { profile, selectBand } = useSelectedProfile();

  const lookupAccountId = searchParams.get('accountId') ?? undefined;
  const lookupTeamKey = searchParams.get('teamKey') ?? undefined;
  const lookupBandTypeRaw = searchParams.get('bandType') ?? undefined;
  const lookupBandType = isBandType(lookupBandTypeRaw) ? lookupBandTypeRaw : undefined;
  const routeNames = searchParams.get('names')?.trim() || undefined;
  const hasTeamContext = !!lookupBandType && !!lookupTeamKey;
  const hasAccountLookupContext = !!lookupAccountId && hasTeamContext;

  const lookupQuery = useQuery({
    queryKey: queryKeys.bandLookup(lookupAccountId ?? '', lookupBandType ?? '', lookupTeamKey ?? ''),
    queryFn: async () => {
      const response = await api.getPlayerBandsByType(lookupAccountId!, lookupBandType!);
      const match = response.entries.find(entry => entry.bandType === lookupBandType && entry.teamKey === lookupTeamKey);
      if (!match) throw new Error(t('band.lookupFailed'));
      return match;
    },
    enabled: hasAccountLookupContext,
    staleTime: 5 * 60_000,
  });

  const rankingQuery = useQuery({
    queryKey: queryKeys.bandRanking(lookupBandType ?? '', lookupTeamKey ?? ''),
    queryFn: () => api.getBandRanking(lookupBandType!, lookupTeamKey!),
    enabled: hasTeamContext,
    staleTime: 5 * 60_000,
  });

  const rankedContextBand = useMemo(
    () => rankingQuery.data ? buildBandEntryFromRanking(rankingQuery.data, routeNames) : null,
    [rankingQuery.data, routeNames],
  );
  const contextBand = lookupQuery.data ?? rankedContextBand;
  const effectiveBandId = bandId ?? contextBand?.bandId ?? rankingQuery.data?.bandId ?? null;
  const bandRouteContext = useMemo(() => {
    if (lookupBandType && lookupTeamKey) {
      return { accountId: lookupAccountId, bandType: lookupBandType, teamKey: lookupTeamKey, names: routeNames };
    }
    return routeNames ? { names: routeNames } : undefined;
  }, [lookupAccountId, lookupBandType, lookupTeamKey, routeNames]);

  useEffect(() => {
    if (!bandId && contextBand?.bandId) {
      navigate(Routes.band(contextBand.bandId, bandRouteContext), { replace: true });
    }
  }, [bandId, bandRouteContext, contextBand?.bandId, navigate]);

  const detailQuery = useQuery({
    queryKey: queryKeys.bandDetail(effectiveBandId ?? ''),
    queryFn: () => api.getBandDetail(effectiveBandId!),
    enabled: !!effectiveBandId && !hasTeamContext && !contextBand,
    staleTime: 5 * 60_000,
  });

  const missingLookupParams = !bandId && !hasTeamContext;
  const loading = (hasTeamContext && rankingQuery.isLoading && !contextBand)
    || (hasAccountLookupContext && lookupQuery.isLoading && !rankingQuery.data)
    || (!!effectiveBandId && !hasTeamContext && !contextBand && detailQuery.isLoading);
  const error = missingLookupParams
    ? new Error(t('band.missingId'))
    : (!contextBand ? (lookupQuery.error ?? rankingQuery.error ?? detailQuery.error ?? null) : null);
  const payload = contextBand ? { band: contextBand, ranking: rankingQuery.data ?? null } : (detailQuery.data ?? null);
  const genericBandTitle = t('band.title');
  const unknownMemberName = t('common.unknownUser');
  const resolvedTitle = payload ? formatBandTitle(payload.band, unknownMemberName, genericBandTitle) : undefined;

  useEffect(() => {
    if (!payload || routeNames || (!bandId && contextBand?.bandId)) return;
    if (!resolvedTitle || resolvedTitle === genericBandTitle) return;
    const next = new URLSearchParams(searchParams);
    next.set('names', resolvedTitle);
    navigate(`${location.pathname}?${next.toString()}`, { replace: true, state: location.state });
  }, [bandId, contextBand?.bandId, genericBandTitle, location.pathname, location.state, navigate, payload, resolvedTitle, routeNames, searchParams]);

  const pageKey = effectiveBandId ?? `${lookupAccountId ?? 'missing'}:${lookupBandType ?? 'missing'}:${lookupTeamKey ?? 'missing'}`;
  const bandRankHistory = useBandRankHistory(payload?.band.bandType, payload?.band.teamKey, 'adjusted', 30);
  const bandSongsQuery = useBandSongs(payload?.band.bandType, payload?.band.teamKey);
  const secondaryLoading = !!payload && (bandRankHistory.loading || bandSongsQuery.isLoading);
  const hasCachedData = !!payload && bandRankHistory.hasData && bandSongsQuery.data != null;
  const { phase, shouldStagger } = usePageTransition(`band:${pageKey}`, !loading && !secondaryLoading, hasCachedData);
  const { forIndex: stagger, clearAnim } = useStagger(shouldStagger);
  const styles = useStyles();

  const title = resolvedTitle ?? routeNames ?? genericBandTitle;
  const subtitle = payload
    ? t('band.subtitle', {
        type: formatBandType(payload.band.bandType),
        count: (payload.band.appearanceCount ?? 0).toLocaleString(),
      })
    : undefined;

  const isCurrentBandSelected = !!payload && profile?.type === 'band'
    && profile.bandId === payload.band.bandId
    && profile.teamKey === payload.band.teamKey;
  const selectBandProfileVisible = !!payload && !isCurrentBandSelected;
  const [selectBandProfileMounted, setSelectBandProfileMounted] = useState(selectBandProfileVisible);
  const selectBandProfileExitTimerRef = useRef<number | null>(null);

  const clearSelectBandProfileExitTimer = useCallback(() => {
    if (selectBandProfileExitTimerRef.current === null) return;
    window.clearTimeout(selectBandProfileExitTimerRef.current);
    selectBandProfileExitTimerRef.current = null;
  }, []);

  useEffect(() => () => {
    clearSelectBandProfileExitTimer();
  }, [clearSelectBandProfileExitTimer]);

  useEffect(() => {
    if (selectBandProfileVisible) {
      clearSelectBandProfileExitTimer();
      setSelectBandProfileMounted(true);
      return;
    }

    if (!selectBandProfileMounted) return;

    clearSelectBandProfileExitTimer();
    selectBandProfileExitTimerRef.current = window.setTimeout(() => {
      selectBandProfileExitTimerRef.current = null;
      setSelectBandProfileMounted(false);
    }, TRANSITION_MS);
  }, [clearSelectBandProfileExitTimer, selectBandProfileMounted, selectBandProfileVisible]);

  const handleBandProfileClick = useCallback(() => {
    if (!payload) return;

    selectBand({
      bandId: payload.band.bandId,
      bandType: payload.band.bandType,
      teamKey: payload.band.teamKey,
      displayName: title,
      members: payload.band.members.map(member => ({
        accountId: member.accountId,
        displayName: formatMemberName(member, unknownMemberName),
      })),
    });
  }, [payload, selectBand, title, unknownMemberName]);

  const bandProfileAction = selectBandProfileMounted ? (
    <div
      data-testid="band-select-profile-slot"
      aria-hidden={!selectBandProfileVisible}
      style={{
        ...SELECT_BAND_PROFILE_ACTION_SLOT_STYLE,
        maxWidth: selectBandProfileVisible ? SELECT_BAND_PROFILE_ACTION_SLOT_MAX_WIDTH : 0,
        opacity: selectBandProfileVisible ? 1 : 0,
      }}
    >
      <SelectProfilePill
        visible={selectBandProfileVisible}
        label={t('band.selectProfile')}
        ariaLabel={t('band.selectProfile')}
        icon={<IoPeople size={IconSize.action} />}
        onClick={handleBandProfileClick}
      />
    </div>
  ) : undefined;

  return (
    <Page
      scrollRestoreKey={`band:${pageKey}`}
      scrollDeps={[phase, effectiveBandId]}
      loadPhase={phase}
      containerStyle={styles.container}
      before={<PageHeader title={title} subtitle={subtitle} reserveSubtitleSpace={loading} actions={bandProfileAction} />}
    >
      {phase === LoadPhase.ContentIn && error && (
        <EmptyState
          fullPage
          title={t('band.notFound')}
          subtitle={error instanceof Error ? error.message : t('band.notFoundSubtitle')}
          style={stagger(0)}
          onAnimationEnd={clearAnim}
        />
      )}

      {phase === LoadPhase.ContentIn && !error && payload && (
        <div style={styles.content}>
          <MembersSection band={payload.band} style={stagger(0)} onAnimationEnd={clearAnim} />
          <BandSummarySection band={payload.band} style={stagger(1)} onAnimationEnd={clearAnim} />
          <BandStatisticsSection ranking={payload.ranking ?? null} style={stagger(2)} onAnimationEnd={clearAnim} />
          <BandRankHistorySection band={payload.band} ranking={payload.ranking ?? null} style={stagger(3)} onAnimationEnd={clearAnim} />
          <BandSongsSection bandType={payload.band.bandType} teamKey={payload.band.teamKey} displayName={title} style={stagger(4)} onAnimationEnd={clearAnim} />
        </div>
      )}
    </Page>
  );
}

function buildBandEntryFromRanking(ranking: BandRankingDto, routeNames?: string): PlayerBandEntry {
  const routeNameFallbacks = splitRouteNames(routeNames);
  const memberNameByAccountId = new Map(ranking.teamMembers.map(member => [member.accountId, member.displayName?.trim() || undefined]));
  const members: PlayerBandMember[] = (ranking.members?.length ? ranking.members : ranking.teamMembers.map(member => ({
    accountId: member.accountId,
    displayName: member.displayName,
    instruments: [] as ServerInstrumentKey[],
  }))).map((member, index) => ({
    accountId: member.accountId,
    displayName: member.displayName?.trim() || memberNameByAccountId.get(member.accountId) || routeNameFallbacks[index] || null,
    instruments: member.instruments ?? [],
  }));

  return {
    bandId: ranking.bandId,
    bandType: ranking.bandType,
    teamKey: ranking.teamKey,
    appearanceCount: ranking.songsPlayed,
    members,
  };
}

function splitRouteNames(routeNames?: string): string[] {
  if (!routeNames) return [];
  return routeNames
    .split(/\s+\+\s+|\s*,\s*/)
    .map(name => name.trim())
    .filter(Boolean);
}

function MembersSection({ band, style, onAnimationEnd }: { band: PlayerBandEntry; style?: CSSProperties; onAnimationEnd: (e: AnimationEvent<HTMLElement>) => void }) {
  const { t } = useTranslation();
  const styles = useStyles();
  return (
    <section data-testid="band-section-members" style={{ ...styles.section, ...style }} onAnimationEnd={onAnimationEnd} aria-label={t('band.members')}>
      <PlayerSectionHeading title={t('band.members')} />
      <div style={styles.memberGrid}>
        {band.members.map(member => <BandMemberCard key={member.accountId} member={member} fallbackName={t('common.unknownUser')} />)}
      </div>
    </section>
  );
}

function BandSummarySection({ band, style, onAnimationEnd }: { band: PlayerBandEntry; style?: CSSProperties; onAnimationEnd: (e: AnimationEvent<HTMLElement>) => void }) {
  const { t } = useTranslation();
  const styles = useStyles();
  return (
    <section data-testid="band-section-summary" style={{ ...styles.section, ...style }} onAnimationEnd={onAnimationEnd} aria-label={t('band.summary')}>
      <PlayerSectionHeading title={t('band.summary')} />
      <div style={styles.statsGrid}>
        <StatCard label={t('band.type')} value={formatBandType(band.bandType)} />
        <StatCard label={t('band.appearances')} value={(band.appearanceCount ?? 0).toLocaleString()} />
        <StatCard label={t('band.members')} value={band.members.length.toLocaleString()} />
      </div>
    </section>
  );
}

function BandMemberCard({ member, fallbackName }: { member: PlayerBandMember; fallbackName: string }) {
  const styles = useStyles();
  const displayName = formatMemberName(member, fallbackName);
  const instruments = Array.from(new Set(member.instruments));

  return (
    <Link data-testid="band-member-card" to={Routes.player(member.accountId)} aria-label={`View ${displayName}`} style={styles.memberCard}>
      <span style={styles.memberContent}>
        <span style={styles.memberName}>{displayName}</span>
        {instruments.length > 0 && (
          <span style={styles.instrumentRow}>
            {instruments.map(instrument => (
              <InstrumentIcon key={`${member.accountId}:${instrument}`} instrument={instrument as ServerInstrumentKey} size={32} />
            ))}
          </span>
        )}
      </span>
      <IoChevronForward data-testid="band-member-chevron" aria-hidden="true" size={18} style={styles.memberChevron} />
    </Link>
  );
}

function BandStatisticsSection({ ranking, style, onAnimationEnd }: { ranking: BandRankingDto | null; style?: CSSProperties; onAnimationEnd: (e: AnimationEvent<HTMLElement>) => void }) {
  const { t } = useTranslation();
  const styles = useStyles();

  return (
    <section data-testid="band-section-statistics" style={{ ...styles.section, ...style }} onAnimationEnd={onAnimationEnd} aria-label={t('band.statistics')}>
      <PlayerSectionHeading title={t('band.statistics')} />
      {!ranking ? (
        <div style={styles.emptyCard}><span style={styles.emptyText}>{t('band.noRanking')}</span></div>
      ) : (
        <div style={styles.statsGrid}>
          <StatCard label={t('band.adjustedRank')} value={formatRank(ranking.adjustedSkillRank)} />
          <StatCard label={t('band.weightedRank')} value={formatRank(ranking.weightedRank)} />
          <StatCard label={t('band.fcRateRank')} value={formatRank(ranking.fcRateRank)} />
          <StatCard label={t('band.totalScoreRank')} value={formatRank(ranking.totalScoreRank)} />
          <StatCard label={t('band.songsPlayed')} value={`${ranking.songsPlayed.toLocaleString()} / ${ranking.totalChartedSongs.toLocaleString()}`} />
          <StatCard label={t('band.totalScore')} value={ranking.totalScore.toLocaleString()} />
          <StatCard label={t('band.fcRate')} value={`${(ranking.fcRate * 100).toFixed(1)}%`} />
          <StatCard label={t('band.avgAccuracy')} value={formatAccuracy(ranking.avgAccuracy)} />
        </div>
      )}
    </section>
  );
}

function BandRankHistorySection({ band, ranking, style, onAnimationEnd }: { band: PlayerBandEntry; ranking: BandRankingDto | null; style?: CSSProperties; onAnimationEnd: (e: AnimationEvent<HTMLElement>) => void }) {
  const styles = useStyles();
  return (
    <section data-testid="band-section-rank-history" style={{ ...styles.section, ...style }} onAnimationEnd={onAnimationEnd}>
      <BandRankHistoryChart bandType={band.bandType} teamKey={band.teamKey} totalRankedTeams={ranking?.totalRankedTeams} />
    </section>
  );
}

function StatCard({ label, value }: { label: string; value: ReactNode }) {
  const styles = useStyles();
  return (
    <div data-testid="band-stat-card" style={styles.statCard}>
      <StatBox label={label} value={value} />
    </div>
  );
}

function isBandType(value: string | undefined): value is BandType {
  return !!value && VALID_BAND_TYPES.includes(value as BandType);
}

function formatBandType(bandType: BandType): string {
  switch (bandType) {
    case 'Band_Duets': return 'Duos';
    case 'Band_Trios': return 'Trios';
    case 'Band_Quad': return 'Quads';
  }
}

function formatBandTitle(band: PlayerBandEntry, fallbackName: string, fallbackTitle: string): string {
  const names = band.members.map(member => formatMemberName(member, fallbackName));
  return names.length > 0 ? names.join(' + ') : fallbackTitle;
}

function formatMemberName(member: PlayerBandMember, fallbackName: string): string {
  return member.displayName?.trim() || fallbackName;
}

function formatRank(rank: number): string {
  return rank > 0 ? `#${rank.toLocaleString()}` : '—';
}

function formatAccuracy(accuracy: number): string {
  return accuracy > 0 ? `${(accuracy / ACCURACY_SCALE).toFixed(1)}%` : '—';
}

function useStyles() {
  const isMobile = useIsMobile();
  return useMemo(() => ({
    container: {
      paddingBottom: Layout.fabPaddingBottom,
    } as CSSProperties,
    content: {
      ...flexColumn,
      gap: Gap.md,
    } as CSSProperties,
    section: {
      ...flexColumn,
      gap: Gap.md,
    } as CSSProperties,
    statsGrid: {
      display: 'grid',
      gridTemplateColumns: isMobile ? '1fr' : 'repeat(2, minmax(0, 1fr))',
      gap: Gap.md,
    } as CSSProperties,
    statCard: {
      ...frostedCard,
      minWidth: 0,
      borderRadius: Radius.md,
      height: '100%',
      overflow: 'hidden',
    } as CSSProperties,
    memberGrid: {
      display: 'grid',
      gridTemplateColumns: isMobile ? '1fr' : 'repeat(2, minmax(0, 1fr))',
      gap: Gap.md,
    } as CSSProperties,
    memberCard: {
      ...frostedCard,
      ...flexRow,
      alignItems: 'center',
      gap: Gap.sm,
      minWidth: 0,
      padding: Gap.md,
      borderRadius: Radius.md,
      color: Colors.textPrimary,
      textDecoration: 'none',
      height: '100%',
      boxSizing: 'border-box',
    } as CSSProperties,
    memberContent: {
      ...flexColumn,
      gap: Gap.sm,
      minWidth: 0,
      flex: 1,
    } as CSSProperties,
    memberName: {
      color: Colors.textPrimary,
      fontSize: Font.lg,
      fontWeight: Weight.bold,
      overflow: 'hidden',
      textOverflow: 'ellipsis',
      whiteSpace: 'nowrap',
    } as CSSProperties,
    instrumentRow: {
      ...flexRow,
      alignItems: 'center',
      gap: Gap.xs,
      flexWrap: 'wrap',
    } as CSSProperties,
    memberChevron: {
      flexShrink: 0,
      color: Colors.textSubtle,
    } as CSSProperties,
    emptyCard: {
      ...frostedCard,
      borderRadius: Radius.md,
      padding: Gap.container,
    } as CSSProperties,
    emptyText: {
      color: Colors.textSecondary,
      fontSize: Font.md,
    } as CSSProperties,
  }), [isMobile]);
}
