import type { CSSProperties } from 'react';
import type { PlayerBandsResponse } from '@festival/core/api/serverTypes';
import { Link } from 'react-router-dom';
import { IoChevronForward } from 'react-icons/io5';
import { Colors, Font, Gap, InstrumentSize, Layout, Radius, Weight, frostedCard, flexColumn, flexRow } from '@festival/theme';
import type { PlayerItem } from '../helpers/playerPageTypes';
import PlayerSectionHeading from '../sections/PlayerSectionHeading';
import { Routes } from '../../../routes';
import PlayerBandCard, { estimatePlayerBandCardHeight } from './PlayerBandCard';

type Translate = (key: string, opts?: Record<string, unknown>) => string;

export const EMPTY_PLAYER_BANDS: PlayerBandsResponse = {
  all: { totalCount: 0, entries: [] },
  duos: { totalCount: 0, entries: [] },
  trios: { totalCount: 0, entries: [] },
  quads: { totalCount: 0, entries: [] },
};

const BAND_GROUPS: Array<{ key: keyof PlayerBandsResponse; titleKey: string }> = [
  { key: 'duos', titleKey: 'player.duos' },
  { key: 'trios', titleKey: 'player.trios' },
  { key: 'quads', titleKey: 'player.quads' },
];

export function buildPlayerBandsItems(
  t: Translate,
  displayName: string,
  bands: PlayerBandsResponse,
  sourceAccountId?: string,
): PlayerItem[] {
  const items: PlayerItem[] = [
    {
      key: 'bands-heading',
      span: true,
      heightEstimate: 72,
      node: (
        <PlayerSectionHeading
          title={t('player.bands', { name: displayName })}
          actionLabel={t('common.viewAll')}
          actionTo={sourceAccountId ? Routes.playerBands(sourceAccountId, 'all', 1, displayName) : undefined}
          actionTestId="player-bands-view-all"
        />
      ),
    },
  ];

  for (const group of BAND_GROUPS) {
    const value = bands[group.key];
    items.push({
      key: `bands-header-${group.key}`,
      span: true,
      heightEstimate: BAND_GROUP_HEADER_HEIGHT,
      node: <BandGroupHeader title={t(group.titleKey)} testId={`player-bands-header-${group.key}`} />,
    });

    if (value.entries.length > 0) {
      value.entries.forEach((entry, index) => {
        items.push({
          key: `bands-entry-${group.key}-${entry.teamKey}-${index}`,
          span: false,
          heightEstimate: estimatePlayerBandCardHeight(entry),
          node: <PlayerBandCard entry={entry} testId={`player-bands-entry-${group.key}-${index}`} sourceAccountId={sourceAccountId} />,
        });
      });
    } else {
      items.push({
        key: `bands-empty-${group.key}`,
        span: true,
        heightEstimate: EMPTY_GROUP_HEIGHT,
        style: bandStyles.emptyCard,
        node: <BandGroupEmptyState t={t} />,
      });
    }

    if (value.totalCount > value.entries.length) {
      items.push({
        key: `bands-view-all-${group.key}`,
        span: true,
        heightEstimate: VIEW_ALL_CARD_HEIGHT,
        style: bandStyles.viewAllCard,
        node: (
          <BandViewAllCard
            label={t('player.viewAllBands', { count: value.totalCount.toLocaleString() })}
            to={sourceAccountId ? Routes.playerBands(sourceAccountId, group.key, 1, displayName) : undefined}
          />
        ),
      });
    }
  }

  return items;
}

const BAND_GROUP_HEADER_HEIGHT = InstrumentSize.md + Gap.md;
const EMPTY_GROUP_HEIGHT = 150;
const VIEW_ALL_CARD_HEIGHT = Layout.entryRowHeight;

function BandGroupHeader({
  title,
  testId,
}: {
  title: string;
  testId: string;
}) {
  return (
    <div data-testid={testId} style={bandStyles.groupHeader}>
      <div style={bandStyles.groupHeaderText}>
        <span style={bandStyles.groupTitle}>{title}</span>
      </div>
    </div>
  );
}

function BandViewAllCard({ label, to }: { label: string; to?: string }) {
  const body = (
    <>
      <span>{label}</span>
      {to && <IoChevronForward aria-hidden="true" size={18} style={bandStyles.entryChevron} />}
    </>
  );

  if (!to) {
    return (
      <div aria-disabled="true" style={bandStyles.viewAllCardBody}>
        {body}
      </div>
    );
  }

  return (
    <Link to={to} style={bandStyles.viewAllCardLink}>
      {body}
    </Link>
  );
}

function BandGroupEmptyState({ t }: { t: Translate }) {
  return (
    <div style={bandStyles.emptyState}>
      <span style={bandStyles.emptyTitle}>{t('player.noBandsYet')}</span>
      <span style={bandStyles.emptySubtitle}>{t('player.noBandsYetSubtitle')}</span>
    </div>
  );
}

const bandStyles = {
  groupHeader: {
    ...flexRow,
    alignItems: 'center',
    gap: Gap.md,
    minHeight: InstrumentSize.md,
    paddingTop: Gap.md,
    paddingBottom: Gap.xs,
  } as CSSProperties,
  groupHeaderText: {
    ...flexColumn,
    flex: 1,
    minWidth: 0,
    justifyContent: 'center',
    minHeight: InstrumentSize.md,
  } as CSSProperties,
  groupTitle: {
    display: 'block',
    margin: 0,
    color: Colors.textPrimary,
    fontSize: Font.xl,
    fontWeight: Weight.bold,
  } as CSSProperties,
  entryChevron: {
    flexShrink: 0,
    color: Colors.textSubtle,
  } as CSSProperties,
  emptyCard: {
    ...frostedCard,
    borderRadius: Radius.md,
    padding: Gap.container,
  } as CSSProperties,
  emptyState: {
    ...flexColumn,
    alignItems: 'center',
    justifyContent: 'center',
    gap: Gap.sm,
    minHeight: 116,
    textAlign: 'center',
  } as CSSProperties,
  emptyTitle: {
    color: Colors.textPrimary,
    fontSize: Font.md,
    fontWeight: Weight.heavy,
  } as CSSProperties,
  emptySubtitle: {
    color: Colors.textSecondary,
    fontSize: Font.sm,
  } as CSSProperties,
  viewAllCard: {
    ...frostedCard,
    borderRadius: Radius.md,
    height: VIEW_ALL_CARD_HEIGHT,
  } as CSSProperties,
  viewAllCardBody: {
    ...flexRow,
    alignItems: 'center',
    justifyContent: 'center',
    gap: Gap.sm,
    minHeight: VIEW_ALL_CARD_HEIGHT,
    color: Colors.textPrimary,
    fontSize: Font.md,
    fontWeight: Weight.semibold,
    height: '100%',
    cursor: 'default',
  } as CSSProperties,
  viewAllCardLink: {
    ...flexRow,
    alignItems: 'center',
    justifyContent: 'center',
    gap: Gap.sm,
    minHeight: VIEW_ALL_CARD_HEIGHT,
    color: Colors.textPrimary,
    fontSize: Font.md,
    fontWeight: Weight.semibold,
    height: '100%',
    textDecoration: 'none',
    cursor: 'pointer',
  } as CSSProperties,
};