import type { CSSProperties } from 'react';
import type { PlayerBandEntry, PlayerBandGroup, PlayerBandMember, PlayerBandsResponse } from '@festival/core/api/serverTypes';
import { Colors, Font, Gap, InstrumentSize, Layout, Radius, Weight, frostedCard, flexColumn, flexRow } from '@festival/theme';
import { InstrumentIcon } from '../../../components/display/InstrumentIcons';
import type { PlayerItem } from '../helpers/playerPageTypes';
import PlayerSectionHeading from '../sections/PlayerSectionHeading';

type Translate = (key: string, opts?: Record<string, unknown>) => string;

export const EMPTY_PLAYER_BANDS: PlayerBandsResponse = {
  all: { totalCount: 0, entries: [] },
  duos: { totalCount: 0, entries: [] },
  trios: { totalCount: 0, entries: [] },
  quads: { totalCount: 0, entries: [] },
};

const BAND_GROUPS: Array<{ key: keyof PlayerBandsResponse; titleKey: string }> = [
  { key: 'all', titleKey: 'player.allBands' },
  { key: 'duos', titleKey: 'player.duos' },
  { key: 'trios', titleKey: 'player.trios' },
  { key: 'quads', titleKey: 'player.quads' },
];

export function buildPlayerBandsItems(
  t: Translate,
  displayName: string,
  bands: PlayerBandsResponse,
): PlayerItem[] {
  const items: PlayerItem[] = [
    {
      key: 'bands-heading',
      span: true,
      heightEstimate: 72,
      node: <PlayerSectionHeading title={t('player.bands', { name: displayName })} />,
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
          heightEstimate: estimateEntryHeight(entry),
          style: bandStyles.entryCard,
          node: <BandEntryCard entry={entry} testId={`player-bands-entry-${group.key}-${index}`} />,
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
        node: <BandViewAllCard label={t('player.viewAllBands', { count: value.totalCount.toLocaleString() })} />,
      });
    }
  }

  return items;
}

const BAND_GROUP_HEADER_HEIGHT = InstrumentSize.md + Gap.md;
const EMPTY_GROUP_HEIGHT = 150;
const VIEW_ALL_CARD_HEIGHT = Layout.entryRowHeight;
const MEMBER_ROW_HEIGHT = 32;
const ENTRY_CARD_BASE_HEIGHT = 32;
const BAND_MEMBER_ICON_SIZE = 32;

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

function BandEntryCard({ entry, testId }: { entry: PlayerBandEntry; testId: string }) {
  return (
    <div data-testid={testId} style={bandStyles.entryCardBody}>
      {entry.members.map((member) => <BandMemberRow key={`${entry.teamKey}:${member.accountId}`} member={member} />)}
    </div>
  );
}

function BandMemberRow({ member }: { member: PlayerBandMember }) {
  const displayName = member.displayName || member.accountId.slice(0, 8);
  const instruments = Array.from(new Set(member.instruments));

  return (
    <div style={bandStyles.memberRow}>
      <span style={bandStyles.memberName}>{displayName}</span>
      {instruments.length > 0 && (
        <div style={bandStyles.instrumentRow}>
          {instruments.map((instrument) => (
            <InstrumentIcon key={`${member.accountId}:${instrument}`} instrument={instrument} size={BAND_MEMBER_ICON_SIZE} />
          ))}
        </div>
      )}
    </div>
  );
}

function BandViewAllCard({ label }: { label: string }) {
  return (
    <div aria-disabled="true" style={bandStyles.viewAllCardBody}>
      {label}
    </div>
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

function estimateGroupHeight(group: PlayerBandGroup): number {
  const entryHeights = group.entries.reduce((total, entry) => total + estimateEntryHeight(entry), 0);
  const rowGapCount = Math.max(group.entries.length - 1, 0);
  const emptyHeight = group.entries.length === 0 ? EMPTY_GROUP_HEIGHT : 0;
  const buttonHeight = group.totalCount > group.entries.length ? VIEW_ALL_CARD_HEIGHT + Gap.md : 0;
  return BAND_GROUP_HEADER_HEIGHT + emptyHeight + entryHeights + rowGapCount * Gap.md + buttonHeight;
}

function estimateEntryHeight(entry: PlayerBandEntry): number {
  return ENTRY_CARD_BASE_HEIGHT + entry.members.length * MEMBER_ROW_HEIGHT;
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
  entryCard: {
    ...frostedCard,
    borderRadius: Radius.md,
    height: '100%',
  } as CSSProperties,
  entryCardBody: {
    ...flexColumn,
    gap: Gap.md,
    height: '100%',
    padding: Gap.md,
  } as CSSProperties,
  memberRow: {
    ...flexRow,
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: Gap.md,
    minHeight: 24,
  } as CSSProperties,
  memberName: {
    flex: 1,
    minWidth: 0,
    color: Colors.textPrimary,
    fontSize: Font.md,
    fontWeight: Weight.semibold,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  } as CSSProperties,
  instrumentRow: {
    ...flexRow,
    alignItems: 'center',
    gap: Gap.xs,
    flexShrink: 0,
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
    minHeight: VIEW_ALL_CARD_HEIGHT,
    color: Colors.textPrimary,
    fontSize: Font.md,
    fontWeight: Weight.semibold,
    height: '100%',
    cursor: 'default',
  } as CSSProperties,
};