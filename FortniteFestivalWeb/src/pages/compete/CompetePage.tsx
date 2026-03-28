/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useMemo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { IoChevronForward } from 'react-icons/io5';
import { api } from '../../api/client';
import { queryKeys } from '../../api/queryKeys';
import { useTrackedPlayer } from '../../hooks/data/useTrackedPlayer';
import { useSettings, visibleInstruments } from '../../contexts/SettingsContext';
import Page from '../Page';
import PageHeader from '../../components/common/PageHeader';
import { RankingEntry } from '../leaderboards/components/RankingEntry';
import { formatRating } from '../leaderboards/helpers/rankingHelpers';
import { Routes } from '../../routes';
import RivalRow from '../rivals/components/RivalRow';
import { usePageTransition } from '../../hooks/ui/usePageTransition';
import { useStagger } from '../../hooks/ui/useStagger';
import {
  Colors, Font, Weight, Gap, Radius, Layout,
  Display, Align, Justify, Cursor, CssValue, CssProp, WhiteSpace,
  frostedCard, flexColumn, flexRow, transition, padding, border, Border,
  FAST_FADE_MS, NAV_TRANSITION_MS,
} from '@festival/theme';
import fx from '../../styles/effects.module.css';

export default function CompetePage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { player } = useTrackedPlayer();
  const { settings } = useSettings();

  const accountId = player?.accountId ?? '';
  const previewInstrument = useMemo(() => visibleInstruments(settings)[0] ?? null, [settings]);

  // Composite rankings top 10
  const { data: compositeData, isLoading: compositeLoading } = useQuery({
    queryKey: queryKeys.compositeRankings(1, 10),
    queryFn: () => api.getCompositeRankings(1, 10),
  });

  // Player's composite ranking
  const { data: playerComposite } = useQuery({
    queryKey: queryKeys.playerCompositeRanking(accountId),
    queryFn: () => api.getPlayerCompositeRanking(accountId),
    enabled: !!accountId,
  });

  // Rivals — closest 3 above + 3 below for first visible instrument
  const { data: rivalsData } = useQuery({
    queryKey: queryKeys.rivalsList(accountId, previewInstrument ?? ''),
    queryFn: () => api.getRivalsList(accountId, previewInstrument!),
    enabled: !!accountId && !!previewInstrument,
  });

  const above = rivalsData?.above.slice(0, 3) ?? [];
  const below = rivalsData?.below.slice(0, 3) ?? [];
  const compositeEntries = compositeData?.entries ?? [];

  const playerInTop = !!(accountId && compositeEntries.some(e => e.accountId === accountId));

  // Wait for leaderboards; also wait for rivals if a player is tracked
  const rivalsReady = !accountId || !previewInstrument || !!rivalsData;
  const isReady = !compositeLoading && rivalsReady;
  const { phase, shouldStagger } = usePageTransition('compete', isReady, isReady);
  const { next: stagger, clearAnim } = useStagger(shouldStagger);
  const s = useCompeteStyles();

  return (
    <Page scrollRestoreKey="compete" loadPhase={phase} before={<PageHeader title={t('compete.title')} />}>
      {phase === 'contentIn' && (
      <div style={s.content}>
      {/* Leaderboards section */}
      <div style={s.section}>
        <div
          className={fx.sectionHeaderClickable}
          style={{ ...s.sectionHeaderClickable, ...stagger() }}
          onAnimationEnd={clearAnim}
          onClick={() => navigate(Routes.leaderboards)}
          role="button"
          tabIndex={0}
          onKeyDown={e => { if (e.key === 'Enter') navigate(Routes.leaderboards); }}
        >
          <div style={s.cardHeaderText}>
            <span style={s.cardTitle}>{t('compete.leaderboards')}</span>
          </div>
          <span style={s.seeAll}>{t('compete.seeAll')}</span>
          <IoChevronForward size={20} style={s.chevron} />
        </div>
        <div style={s.list}>
          {compositeEntries.map((e) => (
            <Link key={e.accountId} to={`/player/${e.accountId}`} style={{ ...(e.accountId === accountId ? s.playerRow : s.row), ...stagger() }} onAnimationEnd={clearAnim}>
              <RankingEntry
                rank={e.compositeRank}
                displayName={e.displayName ?? e.accountId.slice(0, 8)}
                ratingLabel={formatRating(e.compositeRating, 'adjusted')}
                isPlayer={e.accountId === accountId}
              />
            </Link>
          ))}
          {playerComposite && !playerInTop && (
            <Link to={`/player/${playerComposite.accountId}`} style={{ ...s.playerRow, ...stagger() }} onAnimationEnd={clearAnim}>
              <RankingEntry
                rank={playerComposite.compositeRank}
                displayName={playerComposite.displayName ?? playerComposite.accountId.slice(0, 8)}
                ratingLabel={formatRating(playerComposite.compositeRating, 'adjusted')}
                isPlayer
              />
            </Link>
          )}
        </div>
        <div style={{ ...s.viewAllButton, ...stagger() }} onAnimationEnd={clearAnim} onClick={() => navigate(Routes.leaderboards)}>
          {t('compete.viewFullLeaderboards')}
        </div>
      </div>

      {/* Rivals section */}
      <div style={s.section}>
        <div
          className={fx.sectionHeaderClickable}
          style={{ ...s.sectionHeaderClickable, ...stagger() }}
          onAnimationEnd={clearAnim}
          onClick={() => navigate(Routes.rivals)}
          role="button"
          tabIndex={0}
          onKeyDown={e => { if (e.key === 'Enter') navigate(Routes.rivals); }}
        >
          <div style={s.cardHeaderText}>
            <span style={s.cardTitle}>{t('compete.rivals')}</span>
          </div>
          <span style={s.seeAll}>{t('compete.seeAll')}</span>
          <IoChevronForward size={20} style={s.chevron} />
        </div>
        {above.length > 0 || below.length > 0 ? (
          <div style={s.rivalList}>
            {above.map((r) => (
              <RivalRow
                key={r.accountId}
                rival={r}
                direction="above"
                onClick={() => navigate(Routes.rivalDetail(r.accountId, r.displayName ?? undefined))}
                style={stagger()}
                onAnimationEnd={clearAnim}
              />
            ))}
            {below.map((r) => (
              <RivalRow
                key={r.accountId}
                rival={r}
                direction="below"
                onClick={() => navigate(Routes.rivalDetail(r.accountId, r.displayName ?? undefined))}
                style={stagger()}
                onAnimationEnd={clearAnim}
              />
            ))}
          </div>
        ) : (
          <div style={{ ...s.emptyHint, ...stagger() }} onAnimationEnd={clearAnim}>{t('compete.noRivals')}</div>
        )}
        <div style={{ ...s.viewAllButton, ...stagger() }} onAnimationEnd={clearAnim} onClick={() => navigate(Routes.rivals)}>
          {t('compete.viewAllRivals')}
        </div>
      </div>
      </div>
      )}
    </Page>
  );
}

function useCompeteStyles() {
  return useMemo(() => {
    const rowBase: CSSProperties = {
      ...frostedCard,
      display: Display.flex,
      alignItems: Align.center,
      gap: Gap.xl,
      padding: padding(0, Gap.xl),
      height: Layout.entryRowHeight,
      borderRadius: Radius.md,
      textDecoration: CssValue.none,
      color: CssValue.inherit,
      transition: transition(CssProp.backgroundColor, FAST_FADE_MS),
      fontSize: Font.md,
    };
    return {
      content: {
        ...flexColumn,
        gap: Gap.section,
      } as CSSProperties,
      section: {
        ...flexColumn,
        gap: Gap.sm,
      } as CSSProperties,
      sectionHeaderClickable: {
        ...flexRow,
        gap: Gap.md,
        paddingBottom: Gap.md,
        cursor: Cursor.pointer,
        borderRadius: Radius.sm,
        transition: transition(CssProp.opacity, NAV_TRANSITION_MS),
      } as CSSProperties,
      cardHeaderText: {
        flex: 1,
        minWidth: 0,
      } as CSSProperties,
      cardTitle: {
        display: Display.block,
        fontSize: Font.lg,
        fontWeight: Weight.bold,
        color: Colors.textPrimary,
      } as CSSProperties,
      seeAll: {
        fontSize: Font.lg,
        fontWeight: Weight.bold,
        color: Colors.textPrimary,
        flexShrink: 0,
        whiteSpace: WhiteSpace.nowrap,
      } as CSSProperties,
      chevron: {
        color: Colors.textPrimary,
        flexShrink: 0,
      } as CSSProperties,
      list: {
        ...flexColumn,
        gap: Gap.sm,
      } as CSSProperties,
      rivalList: {
        ...flexColumn,
        gap: 2,
        containerType: 'inline-size',
      } as CSSProperties,
      row: { ...rowBase } as CSSProperties,
      playerRow: {
        ...rowBase,
        backgroundColor: Colors.purpleHighlight,
        border: border(Border.thin, Colors.purpleHighlightBorder),
      } as CSSProperties,
      emptyHint: {
        fontSize: Font.sm,
        color: Colors.textSecondary,
        padding: padding(Gap.md, 0),
      } as CSSProperties,
      viewAllButton: {
        ...frostedCard,
        display: Display.flex,
        alignItems: Align.center,
        justifyContent: Justify.center,
        height: Layout.entryRowHeight,
        borderRadius: Radius.md,
        color: Colors.textPrimary,
        fontSize: Font.md,
        fontWeight: Weight.semibold,
        cursor: Cursor.pointer,
        transition: transition(CssProp.backgroundColor, FAST_FADE_MS),
      } as CSSProperties,
    };
  }, []);
}
