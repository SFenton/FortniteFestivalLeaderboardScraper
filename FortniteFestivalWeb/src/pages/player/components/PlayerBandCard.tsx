import { useRef, type AnimationEventHandler, type CSSProperties, type ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { IoChevronForward } from 'react-icons/io5';
import type { PlayerBandEntry, PlayerBandMember } from '@festival/core/api/serverTypes';
import { Colors, CssProp, FAST_FADE_MS, Font, Gap, Radius, Weight, flexColumn, flexRow, frostedCard, transition } from '@festival/theme';
import { InstrumentIcon } from '../../../components/display/InstrumentIcons';
import { Routes } from '../../../routes';
import { useContainerWidth } from '../../../hooks/ui/useContainerWidth';
import { getInstrumentRowWidth, splitInstrumentRows } from '../../songs/layoutMode';

const MEMBER_ROW_HEIGHT = 32;
const STACKED_MEMBER_NAME_ROW_HEIGHT = 28;
const ENTRY_CARD_BASE_HEIGHT = 32;
const BAND_MEMBER_ICON_SIZE: number = 32;
const BAND_MEMBER_ICON_GAP = Gap.xs;
const BAND_MEMBER_INLINE_GAP = Gap.md;
const APPEARANCE_FOOTER_HEIGHT = 34;
const MIN_MEMBER_NAME_INLINE_WIDTH = 96;
const MAX_MEMBER_NAME_INLINE_WIDTH = 180;
const ESTIMATED_NAME_CHARACTER_WIDTH = 8;
const ESTIMATED_NAME_INLINE_PADDING = 16;
const STACKED_MEMBER_NAME_TOP_PADDING = Gap.md;
const STACKED_INSTRUMENT_VERTICAL_PADDING = Gap.sm;

export type BandMemberInstrumentLayout = {
  stacked: boolean;
  instrumentRowCount: 1 | 2;
};

export function estimateMemberNameInlineWidth(displayName: string): number {
  const estimated = displayName.length * ESTIMATED_NAME_CHARACTER_WIDTH + ESTIMATED_NAME_INLINE_PADDING;
  return Math.min(MAX_MEMBER_NAME_INLINE_WIDTH, Math.max(MIN_MEMBER_NAME_INLINE_WIDTH, estimated));
}

export function resolveBandMemberInstrumentLayout(
  displayName: string,
  instrumentCount: number,
  contentWidth?: number,
): BandMemberInstrumentLayout {
  if (instrumentCount <= 0 || !contentWidth || contentWidth <= 0) {
    return { stacked: false, instrumentRowCount: 1 };
  }

  const fullIconRowWidth = getInstrumentRowWidth(instrumentCount, BAND_MEMBER_ICON_SIZE, BAND_MEMBER_ICON_GAP);
  const minimumInlineWidth = estimateMemberNameInlineWidth(displayName) + BAND_MEMBER_INLINE_GAP + fullIconRowWidth;
  if (minimumInlineWidth <= contentWidth) {
    return { stacked: false, instrumentRowCount: 1 };
  }

  return {
    stacked: true,
    instrumentRowCount: fullIconRowWidth <= contentWidth ? 1 : 2,
  };
}

function getUniqueMemberInstruments(member: PlayerBandMember): PlayerBandMember['instruments'] {
  return Array.from(new Set(member.instruments));
}

function resolveStackedInstrumentRowCount(instrumentCount: number, contentWidth?: number): 1 | 2 {
  if (instrumentCount <= 0) return 1;
  if (!contentWidth || contentWidth <= 0) return instrumentCount > 4 ? 2 : 1;
  const fullIconRowWidth = getInstrumentRowWidth(instrumentCount, BAND_MEMBER_ICON_SIZE, BAND_MEMBER_ICON_GAP);
  return fullIconRowWidth <= contentWidth ? 1 : 2;
}

export function resolveBandCardMemberLayouts(
  members: readonly PlayerBandMember[],
  contentWidth?: number,
): BandMemberInstrumentLayout[] {
  const baseLayouts = members.map((member) => {
    const displayName = member.displayName || member.accountId.slice(0, 8);
    return resolveBandMemberInstrumentLayout(displayName, getUniqueMemberInstruments(member).length, contentWidth);
  });
  const isCardStacked = baseLayouts.some(layout => layout.stacked);
  if (!isCardStacked) return baseLayouts;

  return members.map((member) => ({
    stacked: true,
    instrumentRowCount: resolveStackedInstrumentRowCount(getUniqueMemberInstruments(member).length, contentWidth),
  }));
}

function estimateBandMemberRowHeight(member: PlayerBandMember, layout: BandMemberInstrumentLayout): number {
  const instruments = getUniqueMemberInstruments(member);

  if (!layout.stacked || instruments.length === 0) {
    return layout.stacked ? Math.max(MEMBER_ROW_HEIGHT, STACKED_MEMBER_NAME_ROW_HEIGHT) : MEMBER_ROW_HEIGHT;
  }

  return STACKED_MEMBER_NAME_ROW_HEIGHT
    + Gap.sm
    + STACKED_INSTRUMENT_VERTICAL_PADDING * 2
    + layout.instrumentRowCount * BAND_MEMBER_ICON_SIZE
    + Math.max(layout.instrumentRowCount - 1, 0) * BAND_MEMBER_ICON_GAP;
}

function estimateBandMemberRowsHeight(members: readonly PlayerBandMember[], contentWidth?: number): number {
  const memberLayouts = resolveBandCardMemberLayouts(members, contentWidth);
  return members.reduce((total, member, index) => total + estimateBandMemberRowHeight(member, memberLayouts[index] ?? { stacked: false, instrumentRowCount: 1 }), 0);
}

export function estimatePlayerBandCardHeight(entry: PlayerBandEntry, showAppearanceCount = false, contentWidth?: number): number {
  const membersHeight = estimateBandMemberRowsHeight(entry.members, contentWidth);
  return ENTRY_CARD_BASE_HEIGHT + membersHeight + (showAppearanceCount ? APPEARANCE_FOOTER_HEIGHT : 0);
}

export function formatPlayerBandNames(entry: PlayerBandEntry): string | undefined {
  const seen = new Set<string>();
  const names = entry.members
    .filter((member) => {
      if (seen.has(member.accountId)) return false;
      seen.add(member.accountId);
      return true;
    })
    .map(member => member.displayName || member.accountId.slice(0, 8));
  return names.length > 0 ? names.join(' + ') : undefined;
}

export function getPlayerBandRoute(entry: PlayerBandEntry, sourceAccountId?: string): string | null {
  const names = formatPlayerBandNames(entry);
  if (entry.bandId) {
    return Routes.band(entry.bandId, sourceAccountId
      ? { accountId: sourceAccountId, bandType: entry.bandType, teamKey: entry.teamKey, names }
      : { bandType: entry.bandType, teamKey: entry.teamKey, names });
  }
  if (sourceAccountId) return Routes.bandLookup(sourceAccountId, entry.bandType, entry.teamKey, names);
  return null;
}

type PlayerBandCardProps = {
  entry: PlayerBandEntry;
  sourceAccountId?: string;
  rank?: number;
  rankWidth?: number;
  testId?: string;
  style?: CSSProperties;
  ariaLabel?: string;
  appearanceLabel?: string;
  renderMemberMetadata?: (member: PlayerBandMember) => ReactNode;
  scoreFooter?: ReactNode;
  scoreFooterAriaLabel?: string;
  onAnimationEnd?: AnimationEventHandler<HTMLElement>;
};

export default function PlayerBandCard({
  entry,
  sourceAccountId,
  rank,
  rankWidth,
  testId,
  style,
  ariaLabel,
  appearanceLabel,
  renderMemberMetadata,
  scoreFooter,
  scoreFooterAriaLabel,
  onAnimationEnd,
}: PlayerBandCardProps) {
  const route = getPlayerBandRoute(entry, sourceAccountId);
  const appearanceCount = entry.appearanceCount ?? 0;
  const contentRef = useRef<HTMLDivElement>(null);
  const contentWidth = useContainerWidth(contentRef);
  const memberLayouts = resolveBandCardMemberLayouts(entry.members, contentWidth);
  const rankRail = typeof rank === 'number' ? (
    <div data-testid="band-rank-rail" style={{ ...bandCardStyles.rankRail, ...(rankWidth ? { width: rankWidth, minWidth: rankWidth } : undefined) }} aria-label={`Rank ${rank.toLocaleString()}`}>#{rank.toLocaleString()}</div>
  ) : null;
  const memberContent = (
    <div ref={contentRef} style={bandCardStyles.entryCardContent}>
      <div style={bandCardStyles.memberList}>
        {entry.members.map((member, index) => <BandMemberRow key={`${entry.teamKey}:${member.accountId}:${index}`} member={member} layout={memberLayouts[index] ?? { stacked: false, instrumentRowCount: 1 }} metadata={renderMemberMetadata?.(member)} />)}
      </div>
    </div>
  );
  const chevron = route ? <IoChevronForward aria-hidden="true" size={18} style={bandCardStyles.entryChevron} /> : null;
  const footer = appearanceLabel ? (
    <div style={bandCardStyles.appearanceFooter} aria-label={`${appearanceCount.toLocaleString()} ${appearanceLabel}`}>
      <span style={bandCardStyles.appearanceCount}>{appearanceCount.toLocaleString()}</span>
      <span style={bandCardStyles.appearanceLabel}>{appearanceLabel}</span>
    </div>
  ) : scoreFooter ? (
    <div style={bandCardStyles.scoreFooter} aria-label={scoreFooterAriaLabel}>
      {scoreFooter}
    </div>
  ) : null;
  const hasFooter = !!footer;
  const content = hasFooter ? (
    <>
      <div data-testid="band-card-member-content" style={bandCardStyles.entryCardMetaContent}>{memberContent}</div>
      {footer}
    </>
  ) : memberContent;
  const body = rankRail ? (
    <div data-testid="band-ranked-card-content" style={bandCardStyles.rankedCardContent}>
      {rankRail}
      <div style={hasFooter ? bandCardStyles.rankedMetaStack : bandCardStyles.rankedMemberOnly}>{content}</div>
    </div>
  ) : content;

  if (!route) {
    return (
      <div data-testid={testId} style={{ ...bandCardStyles.entryCard, ...style }} onAnimationEnd={onAnimationEnd}>
        <div style={hasFooter ? bandCardStyles.entryCardMetaBody : bandCardStyles.entryCardBody}>{body}{chevron}</div>
      </div>
    );
  }

  return (
    <Link data-testid={testId} to={route} aria-label={ariaLabel ?? `View band ${entry.teamKey}`} style={{ ...bandCardStyles.entryCard, ...(hasFooter ? bandCardStyles.entryCardMetaLink : bandCardStyles.entryCardLink), ...style }} onAnimationEnd={onAnimationEnd}>
      {body}
      {chevron}
    </Link>
  );
}

function BandMemberRow({ member, layout, metadata }: { member: PlayerBandMember; layout: BandMemberInstrumentLayout; metadata?: ReactNode }) {
  const displayName = member.displayName || member.accountId.slice(0, 8);
  const instruments = getUniqueMemberInstruments(member);
  const instrumentRows = layout.stacked && layout.instrumentRowCount > 1
    ? splitInstrumentRows(instruments, 2)
    : [instruments];

  return (
    <div data-testid="band-member-row" data-layout={layout.stacked ? 'stacked' : 'inline'} style={layout.stacked ? bandCardStyles.memberRowStacked : bandCardStyles.memberRow}>
      <span data-testid="band-member-name" style={layout.stacked ? bandCardStyles.memberNameStacked : bandCardStyles.memberName}>{displayName}</span>
      {metadata ? (
        <div data-testid="band-member-trailing" style={layout.stacked ? bandCardStyles.memberTrailingStacked : bandCardStyles.memberTrailingInline}>
          <span data-testid="band-member-metadata-slot" style={bandCardStyles.memberMetadataSlot}>{metadata}</span>
          {instruments.length > 0 && (
            <div data-testid="band-member-instrument-rows" style={layout.stacked ? bandCardStyles.instrumentRowsWrapper : bandCardStyles.instrumentRowsInlineWrapper}>
              {instrumentRows.map((row, rowIndex) => (
                <div key={`${member.accountId}:instrument-row:${rowIndex}`} data-testid="band-member-instrument-row" style={layout.stacked ? bandCardStyles.instrumentRowCentered : bandCardStyles.instrumentRow}>
                  {row.map((instrument) => (
                    <InstrumentIcon key={`${member.accountId}:${instrument}`} instrument={instrument} size={BAND_MEMBER_ICON_SIZE} />
                  ))}
                </div>
              ))}
            </div>
          )}
        </div>
      ) : instruments.length > 0 && (
        <div data-testid="band-member-instrument-rows" style={layout.stacked ? bandCardStyles.instrumentRowsWrapper : bandCardStyles.instrumentRowsInlineWrapper}>
          {instrumentRows.map((row, rowIndex) => (
            <div key={`${member.accountId}:instrument-row:${rowIndex}`} data-testid="band-member-instrument-row" style={layout.stacked ? bandCardStyles.instrumentRowCentered : bandCardStyles.instrumentRow}>
              {row.map((instrument) => (
                <InstrumentIcon key={`${member.accountId}:${instrument}`} instrument={instrument} size={BAND_MEMBER_ICON_SIZE} />
              ))}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

const bandCardStyles = {
  entryCard: {
    ...frostedCard,
    position: 'relative',
    borderRadius: Radius.md,
    height: '100%',
    overflow: 'hidden',
  } as CSSProperties,
  entryCardBody: {
    ...flexRow,
    alignItems: 'center',
    gap: Gap.sm,
    height: '100%',
    padding: `${Gap.md}px ${Gap.xl}px`,
    boxSizing: 'border-box',
  } as CSSProperties,
  entryCardLink: {
    ...flexRow,
    alignItems: 'center',
    gap: Gap.sm,
    padding: `${Gap.md}px ${Gap.xl}px`,
    paddingRight: Gap.xl + 18,
    color: Colors.textPrimary,
    textDecoration: 'none',
    boxSizing: 'border-box',
    cursor: 'pointer',
  } as CSSProperties,
  entryCardMetaBody: {
    ...flexColumn,
    alignItems: 'stretch',
    gap: Gap.md,
    height: '100%',
    padding: `${Gap.md}px ${Gap.xl}px`,
    boxSizing: 'border-box',
  } as CSSProperties,
  entryCardMetaLink: {
    ...flexColumn,
    alignItems: 'stretch',
    gap: Gap.md,
    padding: `${Gap.md}px ${Gap.xl}px`,
    color: Colors.textPrimary,
    textDecoration: 'none',
    boxSizing: 'border-box',
    cursor: 'pointer',
  } as CSSProperties,
  entryCardMetaContent: {
    ...flexRow,
    alignItems: 'center',
    gap: Gap.sm,
    flex: 1,
    minWidth: 0,
    paddingRight: Gap.xl + 18,
  } as CSSProperties,
  entryCardContent: {
    ...flexRow,
    alignItems: 'center',
    gap: Gap.sm,
    flex: 1,
    minWidth: 0,
  } as CSSProperties,
  rankedCardContent: {
    ...flexRow,
    alignItems: 'center',
    gap: Gap.md,
    flex: 1,
    minWidth: 0,
  } as CSSProperties,
  rankedMetaStack: {
    ...flexColumn,
    alignItems: 'stretch',
    gap: Gap.md,
    flex: 1,
    minWidth: 0,
  } as CSSProperties,
  rankedMemberOnly: {
    ...flexRow,
    alignItems: 'center',
    flex: 1,
    minWidth: 0,
  } as CSSProperties,
  rankRail: {
    ...flexRow,
    alignItems: 'center',
    justifyContent: 'center',
    alignSelf: 'stretch',
    width: 44,
    minWidth: 44,
    color: Colors.textSecondary,
    fontSize: Font.lg,
    fontWeight: Weight.bold,
    lineHeight: 1,
    fontVariantNumeric: 'tabular-nums',
    transition: transition(CssProp.width, FAST_FADE_MS),
  } as CSSProperties,
  memberList: {
    ...flexColumn,
    gap: Gap.md,
    flex: 1,
    minWidth: 0,
  } as CSSProperties,
  memberTrailingInline: {
    ...flexRow,
    alignItems: 'center',
    justifyContent: 'flex-end',
    gap: Gap.md,
    flexShrink: 0,
    minWidth: 0,
  } as CSSProperties,
  memberTrailingStacked: {
    ...flexRow,
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: Gap.md,
    width: '100%',
    minWidth: 0,
    flexWrap: 'wrap',
  } as CSSProperties,
  memberMetadataSlot: {
    ...flexRow,
    alignItems: 'center',
    flexShrink: 0,
  } as CSSProperties,
  entryChevron: {
    position: 'absolute',
    top: '50%',
    right: Gap.xl,
    transform: 'translateY(-50%)',
    flexShrink: 0,
    color: Colors.textPrimary,
    pointerEvents: 'none',
  } as CSSProperties,
  memberRow: {
    ...flexRow,
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: Gap.md,
    minHeight: 24,
  } as CSSProperties,
  memberRowStacked: {
    ...flexColumn,
    alignItems: 'stretch',
    justifyContent: 'center',
    gap: Gap.sm,
    minHeight: STACKED_MEMBER_NAME_ROW_HEIGHT,
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
  memberNameStacked: {
    minWidth: 0,
    paddingTop: STACKED_MEMBER_NAME_TOP_PADDING,
    color: Colors.textPrimary,
    fontSize: Font.lg,
    fontWeight: Weight.bold,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  } as CSSProperties,
  instrumentRowsInlineWrapper: {
    ...flexRow,
    alignItems: 'center',
    flexShrink: 0,
  } as CSSProperties,
  instrumentRowsWrapper: {
    ...flexColumn,
    alignItems: 'stretch',
    justifyContent: 'flex-start',
    gap: BAND_MEMBER_ICON_GAP,
    width: '100%',
    minWidth: 0,
    paddingTop: STACKED_INSTRUMENT_VERTICAL_PADDING,
    paddingBottom: STACKED_INSTRUMENT_VERTICAL_PADDING,
  } as CSSProperties,
  instrumentRow: {
    ...flexRow,
    alignItems: 'center',
    justifyContent: 'flex-end',
    gap: BAND_MEMBER_ICON_GAP,
    flexShrink: 0,
  } as CSSProperties,
  instrumentRowCentered: {
    ...flexRow,
    alignItems: 'center',
    justifyContent: 'flex-start',
    gap: BAND_MEMBER_ICON_GAP,
    maxWidth: '100%',
    flexShrink: 0,
  } as CSSProperties,
  appearanceFooter: {
    ...flexRow,
    alignItems: 'center',
    justifyContent: 'center',
    alignSelf: 'center',
    gap: Gap.xs,
    width: '100%',
    flexShrink: 0,
  } as CSSProperties,
  appearanceCount: {
    color: Colors.textPrimary,
    fontSize: Font.md,
    fontWeight: Weight.semibold,
    lineHeight: 1.2,
  } as CSSProperties,
  appearanceLabel: {
    color: Colors.textPrimary,
    fontSize: Font.md,
    fontWeight: Weight.semibold,
    lineHeight: 1.2,
  } as CSSProperties,
  scoreFooter: {
    ...flexRow,
    alignItems: 'center',
    justifyContent: 'center',
    alignSelf: 'center',
    flexWrap: 'wrap',
    gap: Gap.md,
    width: '100%',
    padding: `0 ${Gap.md}px`,
    boxSizing: 'border-box',
    flexShrink: 0,
  } as CSSProperties,
};
