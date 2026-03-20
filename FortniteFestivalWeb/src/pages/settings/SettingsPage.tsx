import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigationType } from 'react-router-dom';
import { useSettings } from '../../contexts/SettingsContext';
import { useIsMobile, useIsMobileChrome } from '../../hooks/ui/useIsMobile';
import { ToggleRow } from '../../components/common/ToggleRow';
import SectionHeader from '../../components/common/SectionHeader';
import { ReorderList } from '../../components/sort/ReorderList';
import { METADATA_SORT_DISPLAY } from '../../utils/songSettings';
import ConfirmAlert from '../../components/modals/ConfirmAlert';
import { InstrumentIcon } from '../../components/display/InstrumentIcons';
import type { ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import { Colors, Font, Gap } from '@festival/theme';
import { useScrollMask } from '../../hooks/ui/useScrollMask';
import { useStaggerRush } from '../../hooks/ui/useStaggerRush';
import { useScrollRestore } from '../../hooks/ui/useScrollRestore';
import { api } from '../../api/client';
import css from './SettingsPage.module.css';

import { APP_VERSION, CORE_VERSION, THEME_VERSION } from '../../hooks/data/useVersions';

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

const INSTRUMENT_SHOW_MAP: { key: InstrumentKey; showKey: ShowKey; i18nKey: string }[] = [
  { key: 'Solo_Guitar', showKey: 'showLead', i18nKey: 'instruments.lead' },
  { key: 'Solo_Bass', showKey: 'showBass', i18nKey: 'instruments.bass' },
  { key: 'Solo_Drums', showKey: 'showDrums', i18nKey: 'instruments.drums' },
  { key: 'Solo_Vocals', showKey: 'showVocals', i18nKey: 'instruments.vocals' },
  { key: 'Solo_PeripheralGuitar', showKey: 'showProLead', i18nKey: 'instruments.proLead' },
  { key: 'Solo_PeripheralBass', showKey: 'showProBass', i18nKey: 'instruments.proBass' },
];

type MetadataKey =
  | 'metadataShowScore'
  | 'metadataShowPercentage'
  | 'metadataShowPercentile'
  | 'metadataShowSeasonAchieved'
  | 'metadataShowDifficulty'
  | 'metadataShowStars';

const METADATA_TOGGLES: { key: MetadataKey; i18nKey: string }[] = [
  { key: 'metadataShowScore', i18nKey: 'metadata.score' },
  { key: 'metadataShowPercentage', i18nKey: 'metadata.percentage' },
  { key: 'metadataShowPercentile', i18nKey: 'metadata.percentile' },
  { key: 'metadataShowSeasonAchieved', i18nKey: 'metadata.seasonAchieved' },
  { key: 'metadataShowDifficulty', i18nKey: 'metadata.intensity' },
  { key: 'metadataShowStars', i18nKey: 'metadata.stars' },
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

/* v8 ignore start — LeewaySlider: DOM style injection not testable in jsdom */
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
/* v8 ignore stop */

export default function SettingsPage() {
  const { t } = useTranslation();
  const navType = useNavigationType();
  const { settings, updateSettings, resetSettings } = useSettings();
  const isMobile = useIsMobile();
  const isMobileChrome = useIsMobileChrome();
  const scrollRef = useRef<HTMLDivElement>(null);
  const saveScroll = useScrollRestore(scrollRef, 'settings', navType);
  const updateScrollMask = useScrollMask(scrollRef, []);
  const rushOnScroll = useStaggerRush(scrollRef);
  /* v8 ignore start — scroll handler */
  const handleScroll = useCallback(() => { saveScroll(); updateScrollMask(); rushOnScroll(); }, [saveScroll, updateScrollMask, rushOnScroll]);
  /* v8 ignore stop */
  const [showResetConfirm, setShowResetConfirm] = useState(false);
  const [serviceVersion, setServiceVersion] = useState<string | null>(null);
  // Capture skip decision at mount time (ref avoids StrictMode double-mount issues)
  const skipAnimRef = useRef(_hasRendered);
  const skipAnim = skipAnimRef.current;
  _hasRendered = true;

  /* v8 ignore start — version fetch + settings callbacks */
  useEffect(() => {
    let cancelled = false;
    api.getVersion()
      .then(data => { if (!cancelled) setServiceVersion(data.version); })
      .catch(() => { /* service unreachable */ });
    return () => { cancelled = true; };
  }, []);
  /* v8 ignore stop */

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

  /* v8 ignore start — presentation-only metadata display mapping */
  const visualOrderItems = useMemo(
    () =>
      settings.songRowVisualOrder.map(k => ({
        key: k,
        label: METADATA_SORT_DISPLAY[k] ?? k,
      })),
    [settings.songRowVisualOrder],
  );
  /* v8 ignore stop */

  let staggerIndex = 0;
  const stagger = (idx: number) => skipAnim ? undefined : idx * 125;

  return (
    <div className={css.page}>
      <div ref={scrollRef} onScroll={handleScroll} className={css.scrollArea}>
      <div className={isMobile ? css.containerMobile : css.container}>
        <div className={css.cardColumn}>

          {/* ── App Settings ── */}
          <FadeInDiv delay={stagger(staggerIndex++)}>
          <SectionHeader title={t('settings.appSettings')} description={t('settings.appSettingsHint')} />
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
                <div className={css.innerSectionTitle}>{t('settings.songRowVisualOrder')}</div>
                <div className={css.sectionHint}>
                  {t('settings.songRowVisualOrderDesc')}
                </div>
                <div className={css.reorderListWrap}>
                  <ReorderList
                    items={visualOrderItems}
                    /* v8 ignore start -- DnD reorder callback; can't fire in jsdom */
                    onReorder={items => updateSettings({ songRowVisualOrder: items.map(i => i.key) })}
                    /* v8 ignore stop */
                  />
                </div>
              </div>
            )}
            <ToggleRow
              label={t('settings.filterInvalidScores')}
              description={t('settings.filterInvalidScoresToggleDesc')}
              checked={settings.filterInvalidScores}
              onToggle={() => updateSettings({ filterInvalidScores: !settings.filterInvalidScores })}
              large={isMobile}
            />
            <div className={settings.filterInvalidScores ? css.collapseGridOpen : css.collapseGridClosed}>
              <div className={css.collapseInner}>
                <div className={css.leewayContent}>
                  <div className={css.innerSectionTitle}>{t('settings.maxScoreLeeway')}</div>
                  <div className={isMobile ? css.leewayDescMobile : css.leewayDesc}>
                    {t('settings.maxScoreLeewayDesc', { leeway: settings.filterInvalidScoresLeeway, maxScore: (100000 * (1 + settings.filterInvalidScoresLeeway / 100)).toLocaleString() })}
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
          <SectionHeader title={t('settings.showInstruments')} description={t('settings.showInstrumentsHint')} />
          <Card>
            {INSTRUMENT_SHOW_MAP.map(inst => (
              <ToggleRow
                key={inst.showKey}
                label={t(inst.i18nKey)}
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
          <SectionHeader title={t('settings.showMetadata')} description={t('settings.showMetadataHint')} />
          <Card>
            {METADATA_TOGGLES.map(m => (
              <ToggleRow
                key={m.key}
                label={t(m.i18nKey)}
                checked={settings[m.key]}
                onToggle={() => toggleMetadata(m.key)}
                large={isMobile}
              />
            ))}
          </Card>
          </FadeInDiv>

          {/* ── Version ── */}
          <FadeInDiv delay={stagger(staggerIndex++)}>
          <SectionHeader title={t('settings.versionTitle')} description={t('settings.versionHint')} />
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
            <div className={css.versionRow}>
              <span>{t('settings.themeVersion')}</span>
              <span className={css.versionValue}>{THEME_VERSION}</span>
            </div>
          </Card>
          </FadeInDiv>

          {/* ── Reset ── */}
          <FadeInDiv delay={stagger(staggerIndex++)}>
          <div className={isMobile ? css.resetRowMobile : css.resetRow}>
            <div>
              <SectionHeader title={t('settings.resetSection')} description={t('settings.resetDescription')} flush />
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
        /* v8 ignore start — confirm dialog callbacks */
        <ConfirmAlert
          title={t('settings.resetConfirmTitle')}
          message={t('settings.resetConfirmMessage')}
          onNo={() => setShowResetConfirm(false)}
          onYes={() => { setShowResetConfirm(false); resetSettings(); }}
        />
        /* v8 ignore stop */
      )}
    </div>
  );
}

function Card({ children }: { children: React.ReactNode }) {
  return <div className={css.card}>{children}</div>;
}

