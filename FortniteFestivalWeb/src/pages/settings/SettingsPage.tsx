import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { useSettings } from '../../contexts/SettingsContext';
import { useIsMobile, useIsMobileChrome } from '../../hooks/ui/useIsMobile';
import { ToggleRow } from '../../components/common/ToggleRow';
import { ReorderList } from '../../components/sort/ReorderList';
import { METADATA_SORT_DISPLAY } from '../../utils/songSettings';
import ConfirmAlert from '../../components/modals/ConfirmAlert';
import { InstrumentIcon } from '../../components/display/InstrumentIcons';
import type { ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import { Colors, Font, Gap } from '@festival/theme';
import { useScrollMask } from '../../hooks/ui/useScrollMask';
import { useStaggerRush } from '../../hooks/ui/useStaggerRush';
import { api } from '../../api/client';
import css from './SettingsPage.module.css';

import { APP_VERSION, CORE_VERSION } from '../../hooks/data/useVersions';

/** Track whether settings page has rendered at least once to skip stagger on re-visit. */
let _hasRendered = false;

function FadeInDiv({ delay, children, style }: { delay?: number; children: React.ReactNode; style?: CSSProperties }) {
  const ref = useRef<HTMLDivElement>(null);
  /* v8 ignore start — animation cleanup */
  const handleEnd = useCallback(() => {
    const el = ref.current;
    if (!el) return;
    el.style.opacity = '';
    el.style.animation = '';
  }, []);
  /* v8 ignore stop */
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

/* -- Instrument toggle helpers -- */

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
  | 'metadataShowStars';

const METADATA_TOGGLES: { key: MetadataKey; label: string }[] = [
  { key: 'metadataShowScore', label: 'Score' },
  { key: 'metadataShowPercentage', label: 'Percentage' },
  { key: 'metadataShowPercentile', label: 'Percentile' },
  { key: 'metadataShowSeasonAchieved', label: 'Season Achieved' },
  { key: 'metadataShowDifficulty', label: 'Song Intensity' },
  { key: 'metadataShowStars', label: 'Stars' },
];

const LEEWAY_SLIDER_ID = 'fst-leeway-slider';
const leewaySliderCss = `
#${LEEWAY_SLIDER_ID} {
  -webkit-appearance: none;
  appearance: none;
  width: 100%;
  height: 6px;
  border-radius: 3px;
  outline: none;
  cursor: pointer;
}
#${LEEWAY_SLIDER_ID}::-webkit-slider-thumb {
  -webkit-appearance: none;
  appearance: none;
  width: 20px;
  height: 20px;
  border-radius: 50%;
  background: ${Colors.textPrimary};
  border: 2px solid ${Colors.accentBlue};
  cursor: pointer;
  box-shadow: 0 0 4px rgba(0,0,0,0.3);
}
#${LEEWAY_SLIDER_ID}::-moz-range-thumb {
  width: 20px;
  height: 20px;
  border-radius: 50%;
  background: ${Colors.textPrimary};
  border: 2px solid ${Colors.accentBlue};
  cursor: pointer;
  box-shadow: 0 0 4px rgba(0,0,0,0.3);
}
`;

function leewayTrackBackground(value: number): string {
  const pct = ((value - (-5)) / (5 - (-5))) * 100;
  return `linear-gradient(90deg, ${Colors.accentPurple} 0%, ${Colors.accentBlue} ${pct}%, ${Colors.surfaceMuted} ${pct}%)`;
}

function LeewaySlider({ value, onChange }: { value: number; onChange: (v: number) => void }) {
  const styleRef = useRef<HTMLStyleElement | null>(null);

  useEffect(() => {
    if (!styleRef.current) {
      const el = document.createElement('style');
      el.textContent = leewaySliderCss;
      document.head.appendChild(el);
      styleRef.current = el;
    }
    return () => {
      if (styleRef.current) {
        styleRef.current.remove();
        styleRef.current = null;
      }
    };
  }, []);

  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: Gap.md }}>
      <input
        id={LEEWAY_SLIDER_ID}
        type="range"
        min={-5}
        max={5}
        step={0.1}
        value={value}
        onChange={e => onChange(Math.round(parseFloat(e.target.value) * 10) / 10)}
        style={{ flex: 1, background: leewayTrackBackground(value) }}
      />
      <span style={{ minWidth: 48, textAlign: 'right', fontSize: Font.md, color: Colors.textSecondary, fontVariantNumeric: 'tabular-nums' }}>
        {value > 0 ? '+' : ''}{value.toFixed(1)}%
      </span>
    </div>
  );
}

export default function SettingsPage() {
  const { t } = useTranslation();
  const { settings, updateSettings, resetSettings } = useSettings();
  const isMobile = useIsMobile();
  const isMobileChrome = useIsMobileChrome();
  const scrollRef = useRef<HTMLDivElement>(null);
  const updateScrollMask = useScrollMask(scrollRef, []);
  const rushOnScroll = useStaggerRush(scrollRef);
  const handleScroll = useCallback(() => { updateScrollMask(); rushOnScroll(); }, [updateScrollMask, rushOnScroll]);
  const [showResetConfirm, setShowResetConfirm] = useState(false);
  const [serviceVersion, setServiceVersion] = useState<string | null>(null);
  // Capture skip decision at mount time (ref avoids StrictMode double-mount issues)
  const skipAnimRef = useRef(_hasRendered);
  const skipAnim = skipAnimRef.current;
  _hasRendered = true;

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
    <div className={css.page}>
      <div ref={scrollRef} onScroll={handleScroll} className={css.scrollArea}>
      <div className={css.container} style={isMobile ? { paddingTop: Gap.md } : undefined}>
        <div className={css.cardColumn}>

          {/* ── App Settings ── */}
          <FadeInDiv delay={stagger(staggerIndex++)}>
          <div className={css.sectionTitle}>{t('settings.appSettings')}</div>
          <div className={css.sectionHint}>{t('settings.appSettingsHint')}</div>
          <Card>
            <ToggleRow
              label={t('settings.showInstrumentIcons')}
              description={t('settings.showInstrumentIconsDesc')}
              checked={!settings.songsHideInstrumentIcons}
              onToggle={() => updateSettings({ songsHideInstrumentIcons: !settings.songsHideInstrumentIcons })}
              large={isMobile}
            />
            <ToggleRow
              label={t('settings.enableVisualOrder')}
              description={t('settings.enableVisualOrderDesc')}
              checked={settings.songRowVisualOrderEnabled}
              onToggle={() => updateSettings({ songRowVisualOrderEnabled: !settings.songRowVisualOrderEnabled })}
              large={isMobile}
            />
            {settings.songRowVisualOrderEnabled && (
              <div>
                <div className={css.innerSectionTitle}>Song Row Visual Order</div>
                <div className={css.sectionHint}>
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
            <ToggleRow
              label={t('settings.filterInvalidScores')}
              description="When enabled, the app will attempt to filter out invalid leaderboard values based on the maximum score derived from the CHOpt path."
              checked={settings.filterInvalidScores}
              onToggle={() => updateSettings({ filterInvalidScores: !settings.filterInvalidScores })}
              large={isMobile}
            />
            <div style={{ display: 'grid', gridTemplateRows: settings.filterInvalidScores ? '1fr' : '0fr', transition: 'grid-template-rows 0.2s ease' }}>
              <div style={{ overflow: 'hidden', minHeight: 0 }}>
                <div style={{ paddingLeft: Gap.xl, paddingRight: 36 + Gap.xl, paddingBottom: Gap.md }}>
                  <div className={css.innerSectionTitle}>Maximum Score Leeway</div>
                  <div style={{ fontSize: isMobile ? Font.md : Font.sm, color: Colors.textMuted, lineHeight: '1.5', marginBottom: Gap.md }}>
                    This slider controls a percentage value that allows for some expanded range of scores to still be
                    valid. For example, a CHOpt path with a max score of 100k and {settings.filterInvalidScoresLeeway}% leeway will allow the app to
                    accept scores up to {(100000 * (1 + settings.filterInvalidScoresLeeway / 100)).toLocaleString()}  as &ldquo;valid&rdquo;.
                  </div>
                  <LeewaySlider
                    value={settings.filterInvalidScoresLeeway}
                    onChange={v => updateSettings({ filterInvalidScoresLeeway: v })}
                  />
                </div>
              </div>
            </div>
          </Card>
          </FadeInDiv>

          {/* ── Instruments ── */}
          <FadeInDiv delay={stagger(staggerIndex++)}>
          <div className={css.sectionTitle}>{t('settings.showInstruments')}</div>
          <div className={css.sectionHint}>{t('settings.showInstrumentsHint')}</div>
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

          {/* ── Instrument Metadata ── */}
          <FadeInDiv delay={stagger(staggerIndex++)}>
          <div className={css.sectionTitle}>{t('settings.showMetadata')}</div>
          <div className={css.sectionHint}>
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

          {/* ── Version ── */}
          <FadeInDiv delay={stagger(staggerIndex++)}>
          <div className={css.sectionTitle}>{t('settings.versionTitle')}</div>
          <div className={css.sectionHint}>{t('settings.versionHint')}</div>
          <Card>
            <div className={css.versionRow}>
              <span>{t('settings.appVersion')}</span>
              <span className={css.versionValue}>{APP_VERSION}</span>
            </div>
            <div className={css.versionRow}>
              <span>{t('settings.serviceVersion')}</span>
              <span className={css.versionValue}>{serviceVersion ?? t('common.loading')}</span>
            </div>
            <div className={css.versionRow}>
              <span>{t('settings.coreVersion')}</span>
              <span className={css.versionValue}>{CORE_VERSION}</span>
            </div>
          </Card>
          </FadeInDiv>

          {/* ── Reset ── */}
          <FadeInDiv delay={stagger(staggerIndex++)}>
          <div className={isMobile ? css.resetRowMobile : css.resetRow}>
            <div>
              <div className={css.sectionTitle}>{t('settings.resetSection')}</div>
              <div className={css.sectionHint} style={{ marginBottom: 0 }}>{t('settings.resetDescription')}</div>
            </div>
            <button
              className={isMobile ? css.resetButtonMobile : css.resetButton}
              onClick={() => setShowResetConfirm(true)}
            >
              {t('settings.resetAll')}
            </button>
          </div>
          </FadeInDiv>

        </div>
      </div>
      {isMobileChrome && <div className={css.fabSpacer} />}
      </div>
      {showResetConfirm && (
        <ConfirmAlert
          title={t('settings.resetConfirmTitle')}
          message={t('settings.resetConfirmMessage')}
          onNo={() => setShowResetConfirm(false)}
          onYes={() => { setShowResetConfirm(false); resetSettings(); }}
        />
      )}
    </div>
  );
}

function Card({ children }: { children: React.ReactNode }) {
  return <div className={css.card}>{children}</div>;
}

