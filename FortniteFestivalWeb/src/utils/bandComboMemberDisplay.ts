import type { BandConfiguration, PlayerBandMember, ServerInstrumentKey } from '@festival/core/api/serverTypes';

export function resolveBandComboDisplayedMembers(
  members: readonly PlayerBandMember[],
  activeFilterInstruments?: readonly ServerInstrumentKey[],
  activeFilterComboId?: string,
  configurations?: readonly BandConfiguration[],
): PlayerBandMember[] {
  if (!activeFilterInstruments?.length) return [...members];

  const configuredMembers = buildConfiguredMembers(members, configurations, activeFilterInstruments, activeFilterComboId);
  return configuredMembers ?? constrainMemberInstrumentsToCombo(filterMemberInstruments(members, activeFilterInstruments), activeFilterInstruments);
}

function buildConfiguredMembers(
  members: readonly PlayerBandMember[],
  configurations: readonly BandConfiguration[] | undefined,
  activeFilterInstruments: readonly ServerInstrumentKey[],
  activeFilterComboId?: string,
): PlayerBandMember[] | null {
  if (!members.length || !configurations?.length) return null;

  const matchingConfigurations = activeFilterComboId
    ? configurations.filter(configuration => configuration.comboId === activeFilterComboId)
    : configurations;
  const memberInstrumentSets = members.map(() => new Set<ServerInstrumentKey>());
  const seenAssignments = new Set<string>();

  for (const configuration of matchingConfigurations) {
    const assignedInstruments = members.map(member => configuration.memberInstruments[member.accountId]);
    if (assignedInstruments.some(instrument => !instrument)) continue;

    const assignmentKey = assignedInstruments.map((instrument, index) => `${members[index]!.accountId}:${instrument}`).join('|');
    if (seenAssignments.has(assignmentKey)) continue;

    seenAssignments.add(assignmentKey);
    assignedInstruments.forEach((instrument, index) => memberInstrumentSets[index]!.add(instrument!));
  }

  if (memberInstrumentSets.some(instruments => instruments.size === 0)) return null;

  return constrainMemberInstrumentsToCombo(members.map((member, index) => ({
    ...member,
    instruments: orderFilteredInstruments(memberInstrumentSets[index]!, activeFilterInstruments),
  })), activeFilterInstruments);
}

function filterMemberInstruments(members: readonly PlayerBandMember[], activeFilterInstruments: readonly ServerInstrumentKey[]): PlayerBandMember[] {
  const allowed = new Set(activeFilterInstruments);
  return members.map(member => ({
    ...member,
    instruments: orderFilteredInstruments(member.instruments.filter(instrument => allowed.has(instrument)), activeFilterInstruments),
  }));
}

function constrainMemberInstrumentsToCombo(members: readonly PlayerBandMember[], activeFilterInstruments: readonly ServerInstrumentKey[]): PlayerBandMember[] {
  const comboCounts = toInstrumentCounts(activeFilterInstruments);
  const memberInstrumentSets = members.map(member => new Set(member.instruments));

  let changed = true;
  while (changed) {
    changed = false;
    for (const [instrument, allowedCount] of comboCounts) {
      const fixedCount = memberInstrumentSets.filter(instruments => instruments.size === 1 && instruments.has(instrument)).length;
      if (fixedCount < allowedCount) continue;

      for (const instruments of memberInstrumentSets) {
        if (instruments.size <= 1 || !instruments.has(instrument)) continue;
        instruments.delete(instrument);
        changed = true;
      }
    }
  }

  return members.map((member, index) => ({
    ...member,
    instruments: orderFilteredInstruments(memberInstrumentSets[index]!, activeFilterInstruments),
  }));
}

function orderFilteredInstruments(instruments: Iterable<ServerInstrumentKey>, activeFilterInstruments: readonly ServerInstrumentKey[]): ServerInstrumentKey[] {
  const instrumentSet = new Set(instruments);
  const orderedActiveInstruments = Array.from(new Set(activeFilterInstruments));
  return [
    ...orderedActiveInstruments.filter(instrument => instrumentSet.has(instrument)),
    ...Array.from(instrumentSet).filter(instrument => !orderedActiveInstruments.includes(instrument)),
  ];
}

function toInstrumentCounts(instruments: readonly ServerInstrumentKey[]) {
  const counts = new Map<ServerInstrumentKey, number>();
  for (const instrument of instruments) {
    counts.set(instrument, (counts.get(instrument) ?? 0) + 1);
  }
  return counts;
}