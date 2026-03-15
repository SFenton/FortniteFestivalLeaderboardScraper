import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties } from 'react';
import { useSettings } from '../contexts/SettingsContext';
import { useIsMobile, useIsMobileChrome } from '../hooks/useIsMobile';
import { ToggleRow, ReorderList } from '../components/Modal';
import { METADATA_SORT_DISPLAY } from '../components/songSettings';
import ConfirmAlert from '../components/ConfirmAlert';
import { InstrumentIcon } from '../components/InstrumentIcons';
import type { InstrumentKey } from '../models';
import { Colors, Font, Gap, Layout, MaxWidth, Radius, frostedCard } from '../theme';
import { useScrollMask } from '../hooks/useScrollMask';
import { api } from '../api/client';

const APP_VERSION = '0.1.11';
const CORE_VERSION = '0.0.1';

/** Track whether settings page has rendered at least once to skip stagger on re-visit. */
let _hasRendered = false;

function FadeInDiv({ delay, children, style }: { delay?: number; children: React.ReactNode; style?: CSSProperties }) {
  const ref = useRef<HTMLDivElement>(null);
  const handleEnd = useCallback(() => {
    const el = ref.current;
    if (!el) return;
    el.style.opacity = '';
    el.style.animation = '';
  }, []);
  if (delay == null) return <div style={style}>{children}</div>;
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
  const isMobileChrome = useIsMobileChrome();
  const scrollRef = useRef<HTMLDivElement>(null);
  const updateScrollMask = useScrollMask(scrollRef, []);
  const handleScroll = useCallback(() => { updateScrollMask(); }, [updateScrollMask]);
  const [showResetConfirm, setShowResetConfirm] = useState(false);
  const [serviceVersion, setServiceVersion] = useState<string | null>(null);
  // Capture skip decision at mount time (ref avoids StrictMode double-mount issues)
  const skipAnimRef = useRef(_hasRendered);
  const skipAnim = skipAnimRef.current;
  _hasRendered = true;
  console.log(`[SettingsPage] _hasRendered=${_hasRendered} skipAnimRef.current=${skipAnimRef.current} skipAnim=${skipAnim}`);

  useEffect(() => {
    let cancelled = false;
    api.getVersion()
      .then(data => { if (!cancelled) setServiceVersion(data.version); })
      .catch(() => { /* service unreachable */ });
    return () => { cancelled = true; };
  }, []);

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
  const stagger = (idx: number) => skipAnim ? undefined : idx * 125;

  return (
    <div style={styles.page}>
      <div ref={scrollRef} onScroll={handleScroll} style={styles.scrollArea}>
      <div style={{ ...styles.container, ...(isMobile ? { paddingTop: Gap.md } : {}) }}>
        <div style={styles.cardColumn}>

          {/* ───── App Settings ───── */}
          <FadeInDiv delay={stagger(staggerIndex++)}>
          <div style={styles.sectionTitle}>App Settings</div>
          <div style={styles.sectionHint}>General Festival Score Tracker app settings.</div>
          <Card>
            <ToggleRow
              label="Show Instrument Icons"
              description="Display instrument icons on each song row showing which parts have leaderboard scores or FCs."
              checked={!settings.songsHideInstrumentIcons}
              onToggle={() => updateSettings({ songsHideInstrumentIcons: !settings.songsHideInstrumentIcons })}
              large={isMobile}
            />
            <ToggleRow
              label="Enable Independent Song Row Visual Order"
              description="When enabled, the metadata display order on song rows is controlled separately from sort priority. When disabled, metadata follows sort priority order."
              checked={settings.songRowVisualOrderEnabled}
              onToggle={() => updateSettings({ songRowVisualOrderEnabled: !settings.songRowVisualOrderEnabled })}
              large={isMobile}
            />
            {settings.songRowVisualOrderEnabled && (
              <div>
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
          <FadeInDiv delay={stagger(staggerIndex++)}>
          <div style={styles.sectionTitle}>Show Instruments</div>
          <div style={styles.sectionHint}>Choose which instruments to display throughout the app.</div>
          <Card>
            {INSTRUMENT_SHOW_MAP.map(inst => (
              <ToggleRow
                key={inst.showKey}
                label={inst.label}
                icon={<InstrumentIcon instrument={inst.key} size={isMobile ? 28 : 24} />}
                checked={settings[inst.showKey]}
                onToggle={() => toggleShow(inst.showKey)}
                disabled={settings[inst.showKey] && showActiveCount <= 1}
                large={isMobile}
              />
            ))}
          </Card>
          </FadeInDiv>

          {/* ───── Show Instrument Metadata ───── */}
          <FadeInDiv delay={stagger(staggerIndex++)}>
          <div style={styles.sectionTitle}>Show Instrument Metadata</div>
          <div style={styles.sectionHint}>
            When filtering songs down to one instrument in the song list, extra metadata for that song can appear. Choose what you'd like to see in the song row here.
          </div>
          <Card>
            {METADATA_TOGGLES.map(m => (
              <ToggleRow
                key={m.key}
                label={m.label}
                checked={settings[m.key]}
                onToggle={() => toggleMetadata(m.key)}
                large={isMobile}
              />
            ))}
          </Card>
          </FadeInDiv>

          {/* ───── Festival Score Tracker Version ───── */}
          <FadeInDiv delay={stagger(staggerIndex++)}>
          <div style={styles.sectionTitle}>Festival Score Tracker Version</div>
          <div style={styles.sectionHint}>Festival Score Tracker information to help with debugging.</div>
          <Card>
            <div style={styles.versionRow}>
              <span>App Version</span>
              <span style={styles.versionValue}>{APP_VERSION}</span>
            </div>
            <div style={styles.versionRow}>
              <span>Service Version</span>
              <span style={styles.versionValue}>{serviceVersion ?? 'Loading…'}</span>
            </div>
            <div style={styles.versionRow}>
              <span>@festival/core Version</span>
              <span style={styles.versionValue}>{CORE_VERSION}</span>
            </div>
          </Card>
          </FadeInDiv>

          {/* ───── Reset Settings ───── */}
          <FadeInDiv delay={stagger(staggerIndex++)}>
          <div style={{ ...styles.resetRow, ...(isMobile ? styles.resetRowMobile : {}) }}>
            <div>
              <div style={styles.sectionTitle}>Reset Settings</div>
              <div style={{ ...styles.sectionHint, marginBottom: 0 }}>Restore all settings to their default values.</div>
            </div>
            <button
              style={{ ...styles.resetButton, ...(isMobile ? styles.resetButtonMobile : {}) }}
              onClick={() => setShowResetConfirm(true)}
            >
              Reset All Settings
            </button>
          </div>
          </FadeInDiv>

        </div>
      </div>
      {isMobileChrome && <div style={styles.fabSpacer} />}
      </div>
      {showResetConfirm && (
        <ConfirmAlert
          title="Reset Settings"
          message="Are you sure you want to restore all settings to their default values?"
          onNo={() => setShowResetConfirm(false)}
          onYes={() => { setShowResetConfirm(false); resetSettings(); }}
        />
      )}
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
  },
  card: {
    ...frostedCard,
    borderRadius: Radius.md,
    padding: 16,
    display: 'flex',
    flexDirection: 'column' as const,
    gap: Gap.md,
  },
  sectionTitle: {
    fontSize: Font.xl,
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
    fontSize: Font.md,
    color: Colors.textSecondary,
    lineHeight: '1.5',
    marginBottom: Gap.md,
  },
  resetButton: {
    padding: `${Gap.md}px ${Gap.xl}px`,
    borderRadius: Radius.xs,
    border: `1px solid ${Colors.statusRed}`,
    backgroundColor: 'rgb(198,40,40)',
    color: Colors.textPrimary,
    fontSize: Font.sm,
    fontWeight: 600,
    cursor: 'pointer',
    flexShrink: 0,
  },
  resetRow: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: Gap.xl,
  },
  resetRowMobile: {
    flexDirection: 'column' as const,
    alignItems: 'stretch',
  },
  resetButtonMobile: {
    width: '100%',
    textAlign: 'center' as const,
    padding: `${Gap.xl}px ${Gap.xl}px`,
    fontSize: Font.md,
  },
  versionRow: {
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'center',
    padding: `${Gap.sm}px 0`,
    fontSize: Font.md,
  },
  versionValue: {
    color: Colors.textSecondary,
  },
  fabSpacer: {
    height: 72,
    flexShrink: 0,
  },
};
