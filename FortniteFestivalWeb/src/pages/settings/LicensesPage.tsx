/* eslint-disable react/forbid-dom-props -- page uses inline theme style objects */
import { useCallback, useMemo, useRef, useState, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { useSetPageReady } from '../../contexts/PageReadyContext';
import { IoChevronForward } from 'react-icons/io5';
import { Colors, Font, Gap, Weight, Radius, Layout, Size, Display, Align, Overflow, CssValue, LineHeight, TextAlign, flexColumn, flexBetween, padding } from '@festival/theme';
import Page from '../Page';
import PageHeader from '../../components/common/PageHeader';
import { FrostedCard } from '../../components/common/FrostedCard';
import PressableButton from '../../components/common/PressableButton';
import ModalShell from '../../components/modals/components/ModalShell';
import { modalStyles } from '../../components/modals/modalStyles';
import { useScrollMask } from '../../hooks/ui/useScrollMask';
import { licenseManifest, type LicenseManifestEntry } from '../../generated/licenseManifest';

function formatEcosystem(ecosystem: LicenseManifestEntry['ecosystem']): string {
  switch (ecosystem) {
    case 'nuget':
      return 'NuGet';
    case 'npm':
      return 'npm';
    default:
      return 'Other';
  }
}

export default function LicensesPage() {
  useSetPageReady(true);
  const { t } = useTranslation();
  const styles = useStyles();
  const [selectedEntry, setSelectedEntry] = useState<LicenseManifestEntry | null>(null);
  const licenseScrollRef = useRef<HTMLDivElement>(null);
  const updateLicenseScrollMask = useScrollMask(licenseScrollRef, [selectedEntry?.id], { selfScroll: true });
  const handleLicenseScroll = useCallback(() => { updateLicenseScrollMask(); }, [updateLicenseScrollMask]);
  const modalTitle = selectedEntry ? `${selectedEntry.name} · ${selectedEntry.licenseType}` : '';
  const header = <PageHeader title={t('settings.licenses.title')} />;

  return (
    <Page
      scrollRestoreKey="settings-licenses"
      containerStyle={styles.container}
      before={header}
      after={selectedEntry && (
        <ModalShell
          visible={!!selectedEntry}
          title={modalTitle}
          onClose={() => setSelectedEntry(null)}
          panelTestId="licenses-package-modal"
        >
          <div ref={licenseScrollRef} onScroll={handleLicenseScroll} style={modalStyles.contentScroll}>
            <pre style={styles.licenseText}>{selectedEntry.licenseText}</pre>
          </div>
        </ModalShell>
      )}
    >
      <div style={styles.contentColumn}>
        <FrostedCard style={styles.card}>
          {licenseManifest.map(entry => (
            <PressableButton
              key={entry.id}
              style={styles.packageRow}
              onPress={() => setSelectedEntry(entry)}
              aria-label={`${entry.name} ${entry.version} ${entry.licenseType}`}
            >
              <span style={styles.packageTextGroup}>
                <span style={styles.packageName}>{entry.name}</span>
                <span style={styles.packageMeta}>{formatEcosystem(entry.ecosystem)} · {entry.version}</span>
              </span>
              <span style={styles.licenseBadge}>{entry.licenseType}</span>
              <IoChevronForward size={Size.iconSm} aria-hidden="true" style={styles.chevron} />
            </PressableButton>
          ))}
        </FrostedCard>
      </div>
    </Page>
  );
}

function useStyles() {
  return useMemo(() => ({
    container: {
      paddingBottom: Layout.paddingTop,
    } as CSSProperties,
    contentColumn: {
      ...flexColumn,
      gap: Gap.section,
    } as CSSProperties,
    card: {
      borderRadius: Radius.md,
      padding: Gap.none,
      overflow: Overflow.hidden,
      ...flexColumn,
    } as CSSProperties,
    packageRow: {
      ...flexBetween,
      width: CssValue.full,
      gap: Gap.md,
      padding: padding(Gap.lg, Layout.paddingTop),
      border: CssValue.none,
      background: CssValue.transparent,
      color: Colors.textPrimary,
      cursor: 'pointer',
      textAlign: TextAlign.left,
    } as CSSProperties,
    packageTextGroup: {
      ...flexColumn,
      minWidth: 0,
      flex: '1 1 auto',
      gap: Gap.xs,
    } as CSSProperties,
    packageName: {
      minWidth: 0,
      color: Colors.textPrimary,
      fontSize: Font.md,
      fontWeight: Weight.semibold,
      lineHeight: LineHeight.snug,
      overflow: Overflow.hidden,
      textOverflow: 'ellipsis',
      whiteSpace: 'nowrap',
    } as CSSProperties,
    packageMeta: {
      minWidth: 0,
      color: Colors.textMuted,
      fontSize: Font.sm,
      lineHeight: LineHeight.snug,
      overflow: Overflow.hidden,
      textOverflow: 'ellipsis',
      whiteSpace: 'nowrap',
    } as CSSProperties,
    licenseBadge: {
      flexShrink: 0,
      maxWidth: '38%',
      padding: padding(Gap.xs, Gap.md),
      borderRadius: Radius.xs,
      backgroundColor: Colors.surfaceMuted,
      color: Colors.textSecondary,
      fontSize: Font.sm,
      fontWeight: Weight.semibold,
      lineHeight: LineHeight.snug,
      overflow: Overflow.hidden,
      textOverflow: 'ellipsis',
      whiteSpace: 'nowrap',
    } as CSSProperties,
    chevron: {
      flexShrink: 0,
      color: Colors.textMuted,
    } as CSSProperties,
    licenseText: {
      margin: Gap.none,
      whiteSpace: 'pre-wrap',
      overflowWrap: 'anywhere',
      fontFamily: 'ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, "Liberation Mono", monospace',
      fontSize: Font.sm,
      lineHeight: LineHeight.relaxed,
      color: Colors.textSecondary,
    } as CSSProperties,
  }), []);
}
