import { useCallback, useMemo, useRef, type CSSProperties } from 'react';
import { useSettings } from '../contexts/SettingsContext';
import { useIsMobile } from '../hooks/useIsMobile';
import { ToggleRow, ReorderList } from '../components/Modal';
import { METADATA_SORT_DISPLAY } from '../components/songSettings';
import { InstrumentIcon } from '../components/InstrumentIcons';
import type { InstrumentKey } from '../models';
import { Colors, Font, Gap, Layout, MaxWidth, Radius } from '../theme';

function FadeInDiv({ delay, children, style }: { delay: number; children: React.ReactNode; style?: CSSProperties }) {
  const ref = useRef<HTMLDivElement>(null);
  const handleEnd = useCallback(() => {
    const el = ref.current;
    if (!el) return;
    el.style.opacity = '';
    el.style.animation = '';
  }, []);
  return (
    <div
      ref={ref}
      style={{ opacity: 0, animation: `fadeInUp 400ms ease-out ${delay}ms forwards`, ...style }}
      onAnimationEnd={handleEnd}
    >
      {children}
    </div>
  );
}

/* ── Instrument toggle helpers ── */

type ShowKey = 'showLead' | 'showBass' | 'showDrums' | 'showVocals' | 'showProLead' | 'showProBass';

const INSTRUMENT_SHOW_MAP: { key: InstrumentKey; showKey: ShowKey; label: string }[] = [
  { key: 'Solo_Guitar', showKey: 'showLead', label: 'Lead' },
  { key: 'Solo_Bass', showKey: 'showBass', label: 'Bass' },
  { key: 'Solo_Drums', showKey: 'showDrums', label: 'Drums' },
  { key: 'Solo_Vocals', showKey: 'showVocals', label: 'Vocals' },
  { key: 'Solo_PeripheralGuitar', showKey: 'showProLead', label: 'Pro Lead' },
  { key: 'Solo_PeripheralBass', showKey: 'showProBass', label: 'Pro Bass' },
];

type MetadataKey =
  | 'metadataShowScore'
  | 'metadataShowPercentage'
  | 'metadataShowPercentile'
  | 'metadataShowSeasonAchieved'
  | 'metadataShowDifficulty'
  | 'metadataShowIsFC'
  | 'metadataShowStars';

const METADATA_TOGGLES: { key: MetadataKey; label: string }[] = [
  { key: 'metadataShowScore', label: 'Score' },
  { key: 'metadataShowPercentage', label: 'Percentage' },
  { key: 'metadataShowPercentile', label: 'Percentile' },
  { key: 'metadataShowSeasonAchieved', label: 'Season Achieved' },
  { key: 'metadataShowDifficulty', label: 'Song Intensity' },
  { key: 'metadataShowIsFC', label: 'Is FC' },
  { key: 'metadataShowStars', label: 'Stars' },
];

export default function SettingsPage() {
  const { settings, updateSettings, resetSettings } = useSettings();
  const isMobile = useIsMobile();

  const showActiveCount = INSTRUMENT_SHOW_MAP.filter(i => settings[i.showKey]).length;

  const toggleShow = useCallback(
    (showKey: ShowKey) => {
      updateSettings({ [showKey]: !settings[showKey] });
    },
    [settings, updateSettings],
  );

  const toggleMetadata = useCallback(
    (key: MetadataKey) => {
      updateSettings({ [key]: !settings[key] });
    },
    [settings, updateSettings],
  );

  const visualOrderItems = useMemo(
    () =>
      settings.songRowVisualOrder.map(k => ({
        key: k,
        label: METADATA_SORT_DISPLAY[k] ?? k,
      })),
    [settings.songRowVisualOrder],
  );

  let staggerIndex = 0;

  return (
    <div style={styles.page}>
      {isMobile && (
        <div style={styles.header}>
          <div style={styles.container}>
            <FadeInDiv delay={staggerIndex++ * 125}>
              <h1 style={styles.heading}>Settings</h1>
            </FadeInDiv>
          </div>
        </div>
      )}
      <div style={styles.scrollArea}>
      <div style={styles.container}>
        <div style={styles.cardColumn}>

          {/* ───── App Settings ───── */}
          <FadeInDiv delay={staggerIndex++ * 125}>
          <Card>
            <div style={styles.sectionTitle}>App Settings</div>
            <div style={styles.sectionHint}>General Festival Score Tracker app settings.</div>
            <ToggleRow
              label="Show Instrument Icons"
              description="Display instrument icons on each song row showing which parts have leaderboard scores or FCs."
              checked={!settings.songsHideInstrumentIcons}
              onToggle={() => updateSettings({ songsHideInstrumentIcons: !settings.songsHideInstrumentIcons })}
            />
            <div style={{ marginTop: Gap.md }}>
              <ToggleRow
                label="Enable Independent Song Row Visual Order"
                description="When enabled, the metadata display order on song rows is controlled separately from sort priority. When disabled, metadata follows sort priority order."
                checked={settings.songRowVisualOrderEnabled}
                onToggle={() => updateSettings({ songRowVisualOrderEnabled: !settings.songRowVisualOrderEnabled })}
              />
            </div>
            {settings.songRowVisualOrderEnabled && (
              <div style={{ marginTop: Gap.md }}>
                <div style={styles.innerSectionTitle}>Song Row Visual Order</div>
                <div style={styles.sectionHint}>
                  When filtering to a single instrument in the song list, extra metadata is displayed. Choose the order it appears in on the bottom row.
                </div>
                <div style={{ marginTop: Gap.md }}>
                  <ReorderList
                    items={visualOrderItems}
                    onReorder={items => updateSettings({ songRowVisualOrder: items.map(i => i.key) })}
                  />
                </div>
              </div>
            )}
          </Card>
          </FadeInDiv>

          {/* ───── Show Instruments ───── */}
          <FadeInDiv delay={staggerIndex++ * 125}>
          <Card>
            <div style={styles.sectionTitle}>Show Instruments</div>
            <div style={styles.sectionHint}>Choose which instruments to display throughout the app.</div>
            {INSTRUMENT_SHOW_MAP.map(inst => (
              <ToggleRow
                key={inst.showKey}
                label={inst.label}
                icon={<InstrumentIcon instrument={inst.key} size={20} />}
                checked={settings[inst.showKey]}
                onToggle={() => toggleShow(inst.showKey)}
                disabled={settings[inst.showKey] && showActiveCount <= 1}
              />
            ))}
          </Card>
          </FadeInDiv>

          {/* ───── Show Instrument Metadata ───── */}
          <FadeInDiv delay={staggerIndex++ * 125}>
          <Card>
            <div style={styles.sectionTitle}>Show Instrument Metadata</div>
            <div style={styles.sectionHint}>
              When filtering songs down to one instrument in the song list, extra metadata for that song can appear. Choose what you'd like to see in the song row here.
            </div>
            {METADATA_TOGGLES.map(m => (
              <ToggleRow
                key={m.key}
                label={m.label}
                checked={settings[m.key]}
                onToggle={() => toggleMetadata(m.key)}
              />
            ))}
          </Card>
          </FadeInDiv>

          {/* ───── Reset Settings ───── */}
          <FadeInDiv delay={staggerIndex++ * 125}>
          <Card>
            <div style={styles.sectionTitle}>Reset Settings</div>
            <div style={styles.sectionHint}>Restore all settings to their default values.</div>
            <button
              style={styles.resetButton}
              onClick={resetSettings}
            >
              Reset All Settings
            </button>
          </Card>
          </FadeInDiv>

        </div>
      </div>
      </div>
    </div>
  );
}

function Card({ children }: { children: React.ReactNode }) {
  return <div style={styles.card}>{children}</div>;
}

const styles: Record<string, React.CSSProperties> = {
  page: {
    height: '100%',
    display: 'flex',
    flexDirection: 'column' as const,
    color: Colors.textPrimary,
    fontFamily:
      "-apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif",
  },
  header: {
    flexShrink: 0,
    zIndex: 10,
  },
  scrollArea: {
    flex: 1,
    overflowY: 'auto' as const,
  },
  container: {
    maxWidth: MaxWidth.card,
    margin: '0 auto',
    padding: `${Layout.paddingTop}px ${Layout.paddingHorizontal}px`,
  },
  heading: {
    fontSize: Font.title,
    fontWeight: 700,
    margin: 0,
  },
  cardColumn: {
    display: 'flex',
    flexDirection: 'column' as const,
    gap: Gap.section,
    paddingBottom: Gap.section * 2,
  },
  card: {
    backgroundColor: Colors.glassCard,
    backdropFilter: 'blur(20px)',
    WebkitBackdropFilter: 'blur(20px)',
    border: `1px solid ${Colors.glassBorder}`,
    borderRadius: Radius.md,
    padding: 16,
    display: 'flex',
    flexDirection: 'column' as const,
    gap: Gap.md,
  },
  sectionTitle: {
    fontSize: Font.lg,
    fontWeight: 700,
    color: Colors.textPrimary,
  },
  innerSectionTitle: {
    fontSize: Font.md,
    fontWeight: 700,
    color: Colors.textPrimary,
    marginBottom: Gap.sm,
  },
  sectionHint: {
    fontSize: Font.sm,
    color: Colors.textSecondary,
    lineHeight: '1.5',
  },
  resetButton: {
    padding: `${Gap.md}px ${Gap.xl}px`,
    borderRadius: Radius.xs,
    border: `1px solid ${Colors.statusRed}`,
    backgroundColor: Colors.dangerBg,
    color: Colors.textPrimary,
    fontSize: Font.sm,
    fontWeight: 600,
    cursor: 'pointer',
    alignSelf: 'flex-start',
  },
};
