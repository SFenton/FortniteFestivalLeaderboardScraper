import type { CSSProperties } from 'react';
import type { PlayerBandEntry, PlayerBandGroup, PlayerBandMember, PlayerBandsResponse } from '@festival/core/api/serverTypes';
import { Colors, Font, Gap, Radius, Weight, frostedCard, flexColumn, flexRow } from '@festival/theme';
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
      key: `bands-${group.key}`,
      span: false,
      heightEstimate: estimateGroupHeight(value),
      node: <BandGroupCard t={t} title={t(group.titleKey)} group={value} testId={`player-bands-${group.key}`} />,
    });
  }

  return items;
}

function BandGroupCard({
  group,
  title,
  t,
  testId,
}: {
  group: PlayerBandGroup;
  title: string;
  t: Translate;
  testId: string;
}) {
  const viewAllLabel = t('player.viewAllBands', { count: group.totalCount.toLocaleString() });

  return (
    <section data-testid={testId} style={bandStyles.groupCard}>
      <h3 style={bandStyles.groupTitle}>{title}</h3>
      <div style={bandStyles.groupBody}>
        {group.entries.length > 0
          ? group.entries.map((entry) => <BandEntryRow key={`${entry.bandType}:${entry.teamKey}`} entry={entry} />)
          : <BandGroupEmptyState t={t} />}
      </div>
      {group.totalCount > group.entries.length && (
        <div aria-disabled="true" style={bandStyles.viewAllButton}>
          {viewAllLabel}
        </div>
      )}
    </section>
  );
}

function BandEntryRow({ entry }: { entry: PlayerBandEntry }) {
  return (
    <div style={bandStyles.entryRow}>
      {entry.members.map((member, index) => (
        <div key={`${entry.teamKey}:${member.accountId}`} style={bandStyles.memberBlock}>
          <BandMember member={member} />
          {index < entry.members.length - 1 && <span style={bandStyles.memberSeparator}>•</span>}
        </div>
      ))}
    </div>
  );
}

function BandMember({ member }: { member: PlayerBandMember }) {
  return (
    <div style={bandStyles.memberChip}>
      <span style={bandStyles.memberName}>{member.displayName || member.accountId.slice(0, 8)}</span>
      {member.instruments.length > 0 && (
        <div style={bandStyles.instrumentRow}>
          {member.instruments.map((instrument) => (
            <InstrumentIcon key={`${member.accountId}:${instrument}`} instrument={instrument} size={16} />
          ))}
        </div>
      )}
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
  const rowCount = Math.max(group.entries.length, 1);
  const buttonHeight = group.totalCount > group.entries.length ? 48 : 0;
  return 120 + rowCount * 82 + buttonHeight;
}

const bandStyles = {
  groupCard: {
    ...frostedCard,
    ...flexColumn,
    gap: Gap.md,
    borderRadius: Radius.md,
    padding: Gap.container,
    height: '100%',
  } as CSSProperties,
  groupTitle: {
    margin: 0,
    color: Colors.textPrimary,
    fontSize: Font.lg,
    fontWeight: Weight.heavy,
  } as CSSProperties,
  groupBody: {
    ...flexColumn,
    gap: Gap.sm,
    flex: 1,
  } as CSSProperties,
  entryRow: {
    ...frostedCard,
    ...flexRow,
    alignItems: 'center',
    flexWrap: 'wrap',
    gap: Gap.sm,
    borderRadius: Radius.md,
    padding: Gap.md,
  } as CSSProperties,
  memberBlock: {
    ...flexRow,
    alignItems: 'center',
    gap: Gap.sm,
    flexWrap: 'wrap',
  } as CSSProperties,
  memberChip: {
    ...flexRow,
    alignItems: 'center',
    gap: Gap.sm,
    flexWrap: 'wrap',
  } as CSSProperties,
  memberName: {
    color: Colors.textPrimary,
    fontSize: Font.md,
    fontWeight: Weight.semibold,
  } as CSSProperties,
  memberSeparator: {
    color: Colors.textMuted,
    fontSize: Font.md,
    fontWeight: Weight.heavy,
  } as CSSProperties,
  instrumentRow: {
    ...flexRow,
    alignItems: 'center',
    gap: Gap.xs,
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
  viewAllButton: {
    ...flexRow,
    alignItems: 'center',
    justifyContent: 'center',
    minHeight: 40,
    borderRadius: Radius.md,
    color: Colors.textSecondary,
    fontSize: Font.sm,
    fontWeight: Weight.semibold,
    opacity: 0.75,
    cursor: 'default',
  } as CSSProperties,
};